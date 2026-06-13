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

type GeneratorState =
    {
        SeenEventIds: Set<int>
        Pulses: KillPulse list
        MultikillCount: int
        HealthPressure: HealthPressureState
        LastSent: (int * DateTimeOffset) option
        LastSentCommand: (string * DateTimeOffset) option
    }

and HealthPressureState =
    {
        PressureMultiplier: float
        DeathPressureActive: bool
        DeathPressureStartTime: float option
        BaseBeforeDeath: float option
        LastHpPercent: float option
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

    let ceilSqrt x =
        x |> float |> sqrt |> ceil |> int

    let deathPenalty deathCount =
        [ 1 .. deathCount ]
        |> List.sumBy ceilSqrt

    let private effectKindName kind =
        match kind with
        | KillPulseEffect -> "KillPulse"
        | ObjectiveWaveEffect -> "ObjectiveWave"
        | TeamfightBurstEffect -> "TeamfightBurst"
        | AceBurstEffect -> "AceBurst"
        | HeartbeatNearDeathEffect -> "HeartbeatNearDeath"
        | LaningTextureEffect -> "LaningTexture"
        | JungleTensionRampEffect -> "JungleTensionRamp"

    let temporaryEffectLog effect =
        {|
            kind = effectKindName effect.Kind
            value = effect.Value
            sourceEventIds = effect.SourceEventIds
            debug = effect.Debug
        |}

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
            |> List.exists (fun alias ->
                String.Equals(alias, name, StringComparison.OrdinalIgnoreCase))

    let private activeInvolved snapshot ev =
        nameMatches snapshot.ActiveAliases ev.ActorName
        || nameMatches snapshot.ActiveAliases ev.VictimName
        || ev.Assisters
           |> List.exists (fun assister ->
               snapshot.ActiveAliases
               |> List.exists (fun alias -> String.Equals(alias, assister, StringComparison.OrdinalIgnoreCase)))

    let removeRecentProvisionalSingleKills (config: ScoringConfig) eventTime pulses =
        pulses
        |> List.filter (fun pulse ->
            not (pulse.ProvisionalSingleKill && abs (pulse.CreatedAt - eventTime) <= config.ProvisionalSingleKillWindowSec))

    module HealthPressureCalculator =
        let handleDeath config currentTime (state: HealthPressureState) =
            {
                state with
                    DeathPressureActive = true
                    DeathPressureStartTime = Some currentTime
                    BaseBeforeDeath = Some state.PressureMultiplier
                    PressureMultiplier = state.PressureMultiplier * (1.0 - config.DeathPressureBaseLossPercent)
            }

        let handleKillDuringPressure (state: HealthPressureState) =
            match state.BaseBeforeDeath with
            | Some baseBeforeDeath ->
                {
                    state with
                        DeathPressureActive = false
                        DeathPressureStartTime = None
                        BaseBeforeDeath = None
                        PressureMultiplier = baseBeforeDeath
                }
            | None ->
                state

        let checkPressureWindowExpiry config currentTime (state: HealthPressureState) =
            if state.DeathPressureActive then
                match state.DeathPressureStartTime with
                | Some startTime ->
                    if currentTime - startTime >= config.DeathPressureWindowSec then
                        {
                            state with
                                DeathPressureActive = false
                                DeathPressureStartTime = None
                                BaseBeforeDeath = None
                                PressureMultiplier = state.PressureMultiplier * (1.0 - config.DeathPressureBaseLossPercent)
                        }
                    else
                        state
                | None ->
                    state
            else
                state

        let calculateBaseRecovery config healthPercent =
            if healthPercent <= 0.0 then
                config.BaseRecoveryFloor
            elif healthPercent >= 1.0 then
                config.BaseRecoveryTarget
            elif healthPercent <= 0.5 then
                let ratio = healthPercent / 0.5
                config.BaseRecoveryFloor + (config.BaseRecoveryTarget - config.BaseRecoveryFloor) * ratio
            else
                let ratio = (healthPercent - 0.5) / 0.5
                let baseAt50 = config.BaseRecoveryTarget
                let targetAt100 = (config.BaseRecoveryTarget + 1.0) / 2.0
                baseAt50 + (targetAt100 - baseAt50) * ratio

        let update config currentTime hp (state: HealthPressureState) =
            let stateWithWindowCheck = checkPressureWindowExpiry config currentTime state

            match hp with
            | None ->
                stateWithWindowCheck

            | Some currentHp ->
                let threshold = config.HpChangeThresholdPercent / 100.0

                match stateWithWindowCheck.LastHpPercent with
                | None ->
                    { stateWithWindowCheck with LastHpPercent = Some currentHp }

                | Some lastHp ->
                    let delta = currentHp - lastHp
                    let absDelta = abs delta

                    if absDelta >= threshold then
                        let recoveryMultiplier = calculateBaseRecovery config currentHp
                        {
                            stateWithWindowCheck with
                                PressureMultiplier = recoveryMultiplier
                                LastHpPercent = Some currentHp
                        }
                    else
                        { stateWithWindowCheck with LastHpPercent = Some currentHp }

    let private applyEvent config activeAliases state ev =
        if state.SeenEventIds.Contains ev.EventId then
            state
        else
            let stateWithSeen =
                { state with SeenEventIds = state.SeenEventIds.Add ev.EventId }

            let stateWithDeathPressure =
                if nameMatches activeAliases ev.VictimName then
                    { stateWithSeen with HealthPressure = HealthPressureCalculator.handleDeath config ev.GameTime stateWithSeen.HealthPressure }
                else
                    stateWithSeen

            if not (nameMatches activeAliases ev.ActorName) then
                stateWithDeathPressure
            else
                match ev.Kind with
                | ChampionKill ->
                    let stateWithKillRecovery =
                        if stateWithDeathPressure.HealthPressure.DeathPressureActive then
                            { stateWithDeathPressure with HealthPressure = HealthPressureCalculator.handleKillDuringPressure stateWithDeathPressure.HealthPressure }
                        else
                            stateWithDeathPressure

                    let pulse =
                        {
                            Value = config.SingleKillPulseValue
                            CreatedAt = ev.GameTime
                            ExpiresAt = ev.GameTime + config.SingleKillPulseDurationSec
                            ProvisionalSingleKill = true
                        }

                    { stateWithKillRecovery with Pulses = pulse :: stateWithKillRecovery.Pulses }

                | Multikill streak ->
                    let stateWithKillRecovery =
                        if stateWithDeathPressure.HealthPressure.DeathPressureActive then
                            { stateWithDeathPressure with HealthPressure = HealthPressureCalculator.handleKillDuringPressure stateWithDeathPressure.HealthPressure }
                        else
                            stateWithDeathPressure

                    let safeStreak = streak |> Shared.clamp config.MinMultikillStreak config.MaxMultikillStreak

                    let pulse =
                        {
                            Value = safeStreak * safeStreak
                            CreatedAt = ev.GameTime
                            ExpiresAt = ev.GameTime + float safeStreak
                            ProvisionalSingleKill = false
                        }

                    {
                        stateWithKillRecovery with
                            Pulses =
                                stateWithKillRecovery.Pulses
                                |> removeRecentProvisionalSingleKills config ev.GameTime
                                |> fun pulses -> pulse :: pulses

                            MultikillCount = stateWithKillRecovery.MultikillCount + 1
                    }

                | ObjectiveKill _
                | Ace _ ->
                    stateWithDeathPressure

                | Other _ ->
                    stateWithDeathPressure

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

    module LiveHealthMultiplierCalculator =
        let compute config hp =
            match hp with
            | Some healthPercent -> config.HealthMinMultiplier + (1.0 - config.HealthMinMultiplier) * healthPercent
            | None -> 1.0

    let updateHealthPressure config snapshot state =
        let nextHealthPressure =
            HealthPressureCalculator.update config snapshot.GameTime (healthPercent snapshot) state.HealthPressure

        { state with HealthPressure = nextHealthPressure }

    module TemporaryPulseCalculator =
        let private recentWithin windowSec (snapshot: BridgeSnapshot) (ev: BridgeEvent) =
            ev.GameTime <= snapshot.GameTime
            && snapshot.GameTime - ev.GameTime <= windowSec

        let private objectiveValue (ev: BridgeEvent) =
            match ev.Kind with
            | ObjectiveKill(Dragon dragonType, stolen) ->
                let baseValue =
                    match dragonType |> Option.map (fun value -> value.ToUpperInvariant()) with
                    | Some "ELDER" -> 5
                    | _ -> 3

                baseValue + if stolen = Some true then 1 else 0
            | ObjectiveKill(Baron, stolen) ->
                5 + if stolen = Some true then 1 else 0
            | ObjectiveKill(Herald, stolen) ->
                3 + if stolen = Some true then 1 else 0
            | ObjectiveKill(Turret _, _) ->
                1
            | ObjectiveKill(Inhibitor _, _) ->
                3
            | _ ->
                0

        let objectiveWaves config (snapshot: BridgeSnapshot) =
            if not config.EnableObjectiveWaves then
                []
            else
                snapshot.Events
                |> List.choose (fun ev ->
                    let value = objectiveValue ev

                    if value > 0 && recentWithin 8.0 snapshot ev then
                        Some
                            {
                                Kind = ObjectiveWaveEffect
                                Value = value
                                SourceEventIds = [ ev.EventId ]
                                Debug = [ "eventId", string ev.EventId; "value", string value ]
                            }
                    else
                        None)

        let teamfightBurst config (snapshot: BridgeSnapshot) =
            if not config.EnableTeamfightBurst then
                []
            else
                let kills =
                    snapshot.Events
                    |> List.filter (fun ev ->
                        match ev.Kind with
                        | ChampionKill -> recentWithin config.TeamfightWindowSec snapshot ev
                        | _ -> false)

                if kills.Length < config.TeamfightKillCountThreshold then
                    []
                else
                    let involvementBonus =
                        if kills |> List.exists (fun ev -> nameMatches snapshot.ActiveAliases ev.ActorName) then 3
                        elif kills |> List.exists (activeInvolved snapshot) then 2
                        else 1

                    [
                        {
                            Kind = TeamfightBurstEffect
                            Value = min 5 (kills.Length + involvementBonus)
                            SourceEventIds = kills |> List.map (fun ev -> ev.EventId)
                            Debug = [ "killCount", string kills.Length; "involvementBonus", string involvementBonus ]
                        }
                    ]

        let aceBurst config (snapshot: BridgeSnapshot) =
            if not config.EnableTeamfightBurst then
                []
            else
                snapshot.Events
                |> List.choose (fun ev ->
                    match ev.Kind with
                    | Ace _ when recentWithin 10.0 snapshot ev ->
                        Some
                            {
                                Kind = AceBurstEffect
                                Value = 5
                                SourceEventIds = [ ev.EventId ]
                                Debug = [ "eventId", string ev.EventId ]
                            }
                    | _ ->
                        None)

        module HeartbeatCalculator =
            let maxAmplitude = 6.0
            let private cycleSec = 1.0
            let private pulseStart = 0.72
            let private pulsePeak = 0.78
            let private pulseEnd = 0.98

            let private smoothStep edge0 edge1 value =
                if edge1 <= edge0 then
                    0.0
                else
                    let t = ((value - edge0) / (edge1 - edge0)) |> Shared.clamp01
                    t * t * (3.0 - 2.0 * t)

            let amplitude healthPercent =
                (1.0 - healthPercent)
                |> Shared.clamp01
                |> fun missingHealth -> missingHealth * maxAmplitude

            let pulseShape gameTime =
                let phase =
                    let normalized = gameTime / cycleSec
                    normalized - Math.Floor normalized

                if phase < pulseStart || phase > pulseEnd then
                    0.0
                elif phase <= pulsePeak then
                    smoothStep pulseStart pulsePeak phase
                else
                    1.0 - smoothStep pulsePeak pulseEnd phase

            let value gameTime healthPercent =
                amplitude healthPercent * pulseShape gameTime

        let heartbeat config (snapshot: BridgeSnapshot) =
            if not config.EnableHeartbeatNearDeath then
                []
            else
                match healthPercent snapshot with
                | Some hp when hp <= config.LowHealthHeartbeatThreshold ->
                    let rawPulse = HeartbeatCalculator.value snapshot.GameTime hp
                    let pulseValue =
                        rawPulse
                        |> Math.Round
                        |> int
                        |> Shared.clamp 0 (int HeartbeatCalculator.maxAmplitude)

                    if pulseValue <= 0 then
                        []
                    else
                        [
                            {
                                Kind = HeartbeatNearDeathEffect
                                Value = pulseValue
                                SourceEventIds = []
                                Debug =
                                    [
                                        "healthPercent", hp.ToString("0.###")
                                        "missingHealth", (1.0 - hp).ToString("0.###")
                                        "amplitude", (HeartbeatCalculator.amplitude hp).ToString("0.###")
                                        "pulseShape", (HeartbeatCalculator.pulseShape snapshot.GameTime).ToString("0.###")
                                        "rawPulse", rawPulse.ToString("0.###")
                                    ]
                            }
                        ]
                | _ ->
                    []

        let laningTexture config (snapshot: BridgeSnapshot) =
            if not config.EnableLaningPhaseTexture || snapshot.GameTime > config.LaningPhaseEndSec then
                []
            else
                let value =
                    int (float snapshot.ActivePlayer.CreepScore / 75.0)
                    + int (snapshot.ActivePlayer.WardScore / 25.0)
                    + int (float snapshot.ActivePlayer.Assists / 4.0)
                    |> Shared.clamp 0 2

                if value <= 0 then
                    []
                else
                    [
                        {
                            Kind = LaningTextureEffect
                            Value = value
                            SourceEventIds = []
                            Debug = [ "gameTime", snapshot.GameTime.ToString("0.###"); "value", string value ]
                        }
                    ]

        let private lastObjectiveTime predicate (snapshot: BridgeSnapshot) =
            snapshot.Events
            |> List.choose (fun ev -> if predicate ev then Some ev.GameTime else None)
            |> List.sortDescending
            |> List.tryHead

        let private tensionValue windowSec now spawnAt =
            if windowSec <= 0.0 || now > spawnAt || spawnAt - now > windowSec then
                0
            else
                let progress = 1.0 - ((spawnAt - now) / windowSec)
                Math.Ceiling(progress * 2.0) |> int |> Shared.clamp 0 2

        let jungleTension config (snapshot: BridgeSnapshot) =
            if not config.EnableJungleTensionRamp then
                []
            else
                let now = snapshot.GameTime

                let dragonNext =
                    match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Dragon _, _) -> true | _ -> false) snapshot with
                    | Some last -> last + config.DragonRespawnSec
                    | None -> config.DragonInitialSpawnSec

                let heraldNext =
                    if now <= config.HeraldDespawnSec then config.HeraldInitialSpawnSec else Double.PositiveInfinity

                let baronNext =
                    match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Baron, _) -> true | _ -> false) snapshot with
                    | Some last -> last + config.BaronRespawnSec
                    | None -> config.BaronInitialSpawnSec

                let value =
                    [
                        tensionValue config.ObjectiveTensionWindowSec now dragonNext
                        tensionValue config.ObjectiveTensionWindowSec now heraldNext
                        tensionValue config.ObjectiveTensionWindowSec now baronNext
                    ]
                    |> List.max

                if value <= 0 then
                    []
                else
                    [
                        {
                            Kind = JungleTensionRampEffect
                            Value = value
                            SourceEventIds = []
                            Debug =
                                [
                                    "dragonNext", dragonNext.ToString("0.###")
                                    "heraldNext", heraldNext.ToString("0.###")
                                    "baronNext", baronNext.ToString("0.###")
                                    "value", string value
                                ]
                        }
                    ]

        let compute config (snapshot: BridgeSnapshot) activePulseEffects =
            [
                yield! activePulseEffects
                yield! objectiveWaves config snapshot
                yield! teamfightBurst config snapshot
                yield! aceBurst config snapshot
                yield! heartbeat config snapshot
                yield! laningTexture config snapshot
                yield! jungleTension config snapshot
            ]

    let computeIntensityBreakdown config snapshot state =
        let normalizedScore = normalizeActivePlayerScore config snapshot

        let activePulseBoost =
            state.Pulses
            |> List.filter (fun pulse -> pulse.ExpiresAt > snapshot.GameTime)
            |> List.sumBy (fun pulse -> pulse.Value)

        let activePulseEffect =
            if activePulseBoost > 0 then
                [
                    {
                        Kind = KillPulseEffect
                        Value = activePulseBoost
                        SourceEventIds = []
                        Debug = [ "activePulseBoost", string activePulseBoost ]
                    }
                ]
            else
                []

        let deathPenaltyValue = deathPenalty snapshot.ActivePlayer.Deaths
        let performanceScore = normalizedScore * config.NormalizedScoreWeight
        let multikillBase = float state.MultikillCount

        let rawBaseValue =
            performanceScore
            + multikillBase
            - float deathPenaltyValue

        let healthPercent = healthPercent snapshot

        let liveHealthMultiplier =
            LiveHealthMultiplierCalculator.compute config healthPercent

        let healthAdjustedBaseValue =
            rawBaseValue * liveHealthMultiplier * state.HealthPressure.PressureMultiplier

        let baseIntensity =
            healthAdjustedBaseValue
            |> Math.Round
            |> int
            |> Shared.clamp config.MinIntensity config.BaseIntensityCap

        let temporaryEffects = TemporaryPulseCalculator.compute config snapshot activePulseEffect
        let temporaryBoost = temporaryEffects |> List.sumBy (fun effect -> effect.Value)
        let rawFinalValue = float baseIntensity + float temporaryBoost

        {
            PerformanceScore = performanceScore
            NormalizedScore = normalizedScore
            MultikillBase = multikillBase
            DeathPenalty = deathPenaltyValue
            RawBaseValue = rawBaseValue
            LiveHealthPercent = healthPercent
            LiveHealthMultiplier = liveHealthMultiplier
            HealthPressureMultiplier = state.HealthPressure.PressureMultiplier
            HealthAdjustedBaseValue = healthAdjustedBaseValue
            BaseIntensity = baseIntensity
            TemporaryBoost = temporaryBoost
            TemporaryEffects = temporaryEffects
            RawFinalValue = rawFinalValue
            Intensity = rawFinalValue |> Math.Round |> int |> Shared.clamp config.MinIntensity config.MaxIntensity
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
