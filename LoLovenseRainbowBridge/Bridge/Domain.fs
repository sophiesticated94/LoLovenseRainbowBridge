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
        CurrentHealth: float option
        MaxHealth: float option
    }

type ObjectiveKind =
    | Dragon of dragonType: string option
    | Herald
    | Baron
    | Turret of turretId: string option
    | Inhibitor of inhibitorId: string option

type BridgeEventKind =
    | ChampionKill
    | Multikill of streak: int
    | ObjectiveKill of ObjectiveKind * stolen: bool option
    | Ace of acingTeam: string option
    | Other of name: string

type BridgeEvent =
    {
        EventId: int
        GameTime: float
        ActorName: string option
        VictimName: string option
        Assisters: string list
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

type HealthPressureState =
    {
        PressureMultiplier: float
        DeathPressureActive: bool
        DeathPressureStartTime: float option
        BaseBeforeDeath: float option
        LastHpPercent: float option
    }

type GeneratorState =
    {
        SeenEventIds: Set<int>
        Pulses: KillPulse list
        MultikillCount: int
        HealthPressure: HealthPressureState
        LastSent: (int * DateTimeOffset) option
        LastSentCommand: (string * DateTimeOffset) option
    }

type TemporaryEffectKind =
    | KillPulseEffect
    | ObjectiveWaveEffect
    | TeamfightBurstEffect
    | AceBurstEffect
    | HeartbeatNearDeathEffect
    | LaningTextureEffect
    | JungleTensionRampEffect

type TemporaryEffect =
    {
        Kind: TemporaryEffectKind
        Value: int
        SourceEventIds: int list
        Debug: (string * string) list
    }

type IntensityBreakdown =
    {
        PerformanceScore: float
        NormalizedScore: float
        MultikillBase: float
        DeathPenalty: int
        RawBaseValue: float
        LiveHealthPercent: float option
        LiveHealthMultiplier: float
        HealthPressureMultiplier: float
        HealthAdjustedBaseValue: float
        BaseIntensity: int
        TemporaryBoost: int
        TemporaryEffects: TemporaryEffect list
        RawFinalValue: float
        Intensity: int
    }

module Scoring =

    let initialState =
        {
            SeenEventIds = Set.empty
            Pulses = []
            MultikillCount = 0
            HealthPressure =
                {
                    PressureMultiplier = 1.0
                    DeathPressureActive = false
                    DeathPressureStartTime = None
                    BaseBeforeDeath = None
                    LastHpPercent = None
                }
            LastSent = None
            LastSentCommand = None
        }

    let healthPercent (snapshot: BridgeSnapshot) =
        match snapshot.ActivePlayer.CurrentHealth, snapshot.ActivePlayer.MaxHealth with
        | Some currentHealth, Some maxHealth when maxHealth > 0.0 ->
            Some(currentHealth / maxHealth |> Shared.clamp01)
        | _ ->
            None

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
            |> List.exists (fun alias -> String.Equals(alias, name, StringComparison.OrdinalIgnoreCase))

    let activeInvolved snapshot ev =
        nameMatches snapshot.ActiveAliases ev.ActorName
        || nameMatches snapshot.ActiveAliases ev.VictimName
        || ev.Assisters
           |> List.exists (fun assister ->
               snapshot.ActiveAliases
               |> List.exists (fun alias -> String.Equals(alias, assister, StringComparison.OrdinalIgnoreCase)))

    let private countNewMultikills (previousState: GeneratorState) (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (fun ev -> not (previousState.SeenEventIds.Contains ev.EventId))
        |> List.choose (fun ev ->
            match ev.Kind with
            | Multikill streak when nameMatches snapshot.ActiveAliases ev.ActorName -> Some streak
            | _ -> None)
        |> List.length

    let evolve (_config: ScoringConfig) snapshot state =
        let seen =
            snapshot.Events
            |> List.fold (fun acc ev -> acc |> Set.add ev.EventId) state.SeenEventIds

        {
            state with
                SeenEventIds = seen
                MultikillCount = state.MultikillCount + countNewMultikills state snapshot
                Pulses = []
        }

    let emptyBreakdown =
        {
            PerformanceScore = 0.0
            NormalizedScore = 0.0
            MultikillBase = 0.0
            DeathPenalty = 0
            RawBaseValue = 0.0
            LiveHealthPercent = None
            LiveHealthMultiplier = 1.0
            HealthPressureMultiplier = 1.0
            HealthAdjustedBaseValue = 0.0
            BaseIntensity = 0
            TemporaryBoost = 0
            TemporaryEffects = []
            RawFinalValue = 0.0
            Intensity = 0
        }

    let temporaryEffectLog effect =
        {|
            kind = string effect.Kind
            value = effect.Value
            sourceEventIds = effect.SourceEventIds
            debug = effect.Debug
        |}

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
