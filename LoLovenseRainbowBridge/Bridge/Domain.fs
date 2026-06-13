namespace LoLovenseRainbowBridge.Bridge

open System
open LoLovenseRainbowBridge

type BridgePlayer =
    {
        Id: string
        Aliases: string list
        Kills: int
        Deaths: int
        Assists: int
        CreepScore: int
        WardScore: float
        Level: int
    }

type BridgeEventKind =
    | ChampionKill
    | Multikill of streak: int
    | Other of name: string

type BridgeEvent =
    {
        EventId: int
        GameTime: float
        ActorName: string option
        VictimName: string option
        Kind: BridgeEventKind
    }

type BridgeSnapshot =
    {
        GameTime: float
        ActiveAliases: string list
        ActivePlayer: BridgePlayer
        Players: BridgePlayer list
        Events: BridgeEvent list
    }

type KillPulse =
    {
        Value: int
        CreatedAt: float
        ExpiresAt: float
        ProvisionalSingleKill: bool
    }

type GeneratorState =
    {
        SeenEventIds: Set<int>
        Pulses: KillPulse list
        MultikillCount: int
        LastSent: (int * DateTimeOffset) option
        LastSentCommand: (string * DateTimeOffset) option
    }

type IntensityBreakdown =
    {
        NormalizedScore: float
        ActivePulseBoost: int
        DeathPenalty: int
        BaseValue: float
        RawValue: float
        RoundedValue: int
        Intensity: int
    }

module Scoring =

    let initialState =
        {
            SeenEventIds = Set.empty
            Pulses = []
            MultikillCount = 0
            LastSent = None
            LastSentCommand = None
        }

    let ceilSqrt x =
        x |> float |> sqrt |> ceil |> int

    let deathPenalty deathCount =
        [ 1 .. deathCount ]
        |> List.sumBy ceilSqrt

    let rawPerformanceScore (config: ScoringConfig) (p: BridgePlayer) =
        float p.Kills * config.KillWeight
        + float p.Assists * config.AssistWeight
        + float p.CreepScore * config.CreepScoreWeight
        + float p.Level * config.LevelWeight
        + p.WardScore * config.WardScoreWeight
        - float p.Deaths * config.DeathWeight

    let normalizeActivePlayerScore (config: ScoringConfig) (snapshot: BridgeSnapshot) =
        match snapshot.Players with
        | [] ->
            config.EqualScoreNormalizedValue

        | players ->
            let scores = players |> List.map (rawPerformanceScore config)
            let minScore = scores |> List.min
            let maxScore = scores |> List.max

            if abs (maxScore - minScore) < config.ScoreEqualityEpsilon then
                config.EqualScoreNormalizedValue
            else
                let activeRaw = rawPerformanceScore config snapshot.ActivePlayer

                (activeRaw - minScore) / (maxScore - minScore)
                |> Shared.clamp01

    let nameMatches aliases candidate =
        match candidate with
        | None -> false
        | Some name ->
            aliases
            |> List.exists (fun alias ->
                String.Equals(alias, name, StringComparison.OrdinalIgnoreCase))

    let removeRecentProvisionalSingleKills (config: ScoringConfig) eventTime pulses =
        pulses
        |> List.filter (fun pulse ->
            not (pulse.ProvisionalSingleKill && abs (pulse.CreatedAt - eventTime) <= config.ProvisionalSingleKillWindowSec))

    let private applyEvent config activeAliases state ev =
        if state.SeenEventIds.Contains ev.EventId then
            state
        else
            let stateWithSeen =
                { state with SeenEventIds = state.SeenEventIds.Add ev.EventId }

            if not (nameMatches activeAliases ev.ActorName) then
                stateWithSeen
            else
                match ev.Kind with
                | ChampionKill ->
                    let pulse =
                        {
                            Value = config.SingleKillPulseValue
                            CreatedAt = ev.GameTime
                            ExpiresAt = ev.GameTime + config.SingleKillPulseDurationSec
                            ProvisionalSingleKill = true
                        }

                    { stateWithSeen with Pulses = pulse :: stateWithSeen.Pulses }

                | Multikill streak ->
                    let safeStreak = streak |> Shared.clamp config.MinMultikillStreak config.MaxMultikillStreak

                    let pulse =
                        {
                            Value = safeStreak * safeStreak
                            CreatedAt = ev.GameTime
                            ExpiresAt = ev.GameTime + float safeStreak
                            ProvisionalSingleKill = false
                        }

                    {
                        stateWithSeen with
                            Pulses =
                                stateWithSeen.Pulses
                                |> removeRecentProvisionalSingleKills config ev.GameTime
                                |> fun pulses -> pulse :: pulses

                            MultikillCount = stateWithSeen.MultikillCount + 1
                    }

                | Other _ ->
                    stateWithSeen

    let evolve config snapshot state =
        let withoutExpiredPulses =
            {
                state with
                    Pulses =
                        state.Pulses
                        |> List.filter (fun pulse -> pulse.ExpiresAt > snapshot.GameTime)
            }

        snapshot.Events
        |> List.sortBy (fun ev -> ev.EventId)
        |> List.fold (applyEvent config snapshot.ActiveAliases) withoutExpiredPulses

    let computeIntensityBreakdown config snapshot state =
        let normalizedScore = normalizeActivePlayerScore config snapshot

        let activePulseBoost =
            state.Pulses
            |> List.filter (fun pulse -> pulse.ExpiresAt > snapshot.GameTime)
            |> List.sumBy (fun pulse -> pulse.Value)

        let deathPenaltyValue = deathPenalty snapshot.ActivePlayer.Deaths

        let baseValue =
            normalizedScore * config.NormalizedScoreWeight
            + float state.MultikillCount
            - float deathPenaltyValue

        let rawValue = baseValue + float activePulseBoost
        let roundedValue = rawValue |> Math.Round |> int

        {
            NormalizedScore = normalizedScore
            ActivePulseBoost = activePulseBoost
            DeathPenalty = deathPenaltyValue
            BaseValue = baseValue
            RawValue = rawValue
            RoundedValue = roundedValue
            Intensity = roundedValue |> Shared.clamp config.MinIntensity config.MaxIntensity
        }

    let computeIntensity config snapshot state =
        (computeIntensityBreakdown config snapshot state).Intensity

    let shouldSend resendEveryMs now value state =
        match state.LastSent with
        | None -> true
        | Some (previousValue, previousAt) ->
            value <> previousValue
            || (now - previousAt).TotalMilliseconds >= float resendEveryMs

    let shouldSendCommand resendEveryMs now command state =
        match state.LastSentCommand with
        | None -> true
        | Some (previousCommand, previousAt) ->
            not (String.Equals(previousCommand, command, StringComparison.Ordinal))
            || (now - previousAt).TotalMilliseconds >= float resendEveryMs
