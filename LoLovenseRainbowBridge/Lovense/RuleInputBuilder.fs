namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring

type RuleInputBuilder(scoringConfig: ScoringConfig, ?cache: IAppCache) =

    let add key value variables = variables |> Map.add key value

    let cacheVariables now =
        match cache with
        | None -> Map.empty
        | Some cache ->
            let snapshot = cache.Read()
            let snapshotType = snapshot.GetType()

            let projectChild name =
                let property = snapshotType.GetProperty(name)

                if isNull property then
                    Map.empty
                else
                    property.GetValue(snapshot)
                    |> AppCache.projectAnnotated (Some now)

            [
                projectChild "League"
                projectChild "Ocr"
                projectChild "Lovense"
                projectChild "Toys"
                projectChild "RuntimeContext"
                projectChild "CommandBuilder"
            ]
            |> List.fold RuleInternals.mergeVariables Map.empty

    let boolValue value = if value then 1.0 else 0.0

    let positionWeights position =
        match position |> Option.map (fun value -> value.Quadrant) with
        | Some value when String.Equals(value, "Center", StringComparison.OrdinalIgnoreCase) -> 1.0, 1.0
        | Some value when String.Equals(value, "TopLeft", StringComparison.OrdinalIgnoreCase) -> 1.35, 0.35
        | Some value when String.Equals(value, "TopRight", StringComparison.OrdinalIgnoreCase) -> 0.35, 1.35
        | Some value when String.Equals(value, "BottomLeft", StringComparison.OrdinalIgnoreCase) -> 0.0, 0.0
        | Some value when String.Equals(value, "BottomRight", StringComparison.OrdinalIgnoreCase) -> 0.55, 1.05
        | Some value when String.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) -> 1.15, 0.65
        | Some value when String.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) -> 0.65, 1.15
        | Some _ ->
            match position with
            | Some p -> CapabilityResolver.stereoWeightsFromNormalizedX 100 p.NormalizedX |> fun (l, r) -> float l / 100.0, float r / 100.0
            | None -> 1.0, 1.0
        | None -> 1.0, 1.0

    let recentWithin windowSec (snapshot: BridgeSnapshot) (ev: BridgeEvent) =
        ev.GameTime <= snapshot.GameTime && snapshot.GameTime - ev.GameTime <= windowSec

    let newEvents (input: LovenseCommandBuildInput) =
        input.Snapshot.Events
        |> List.filter (fun ev -> not (input.PreviousState.SeenEventIds.Contains ev.EventId))

    let activeKillEvents input =
        newEvents input
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches input.Snapshot.ActiveAliases ev.ActorName
            | _ -> false)

    let activeDeathEvents input =
        newEvents input
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches input.Snapshot.ActiveAliases ev.VictimName
            | _ -> false)

    let activeMultikillStreak input =
        newEvents input
        |> List.choose (fun ev ->
            match ev.Kind with
            | Multikill streak when nameMatches input.Snapshot.ActiveAliases ev.ActorName -> Some streak
            | _ -> None)
        |> function
            | [] -> 0
            | streaks -> streaks |> List.max

    let objectiveValue (ev: BridgeEvent) =
        match ev.Kind with
        | ObjectiveKill(Dragon dragonType, stolen) ->
            let baseValue =
                match dragonType |> Option.map (fun value -> value.ToUpperInvariant()) with
                | Some "ELDER" -> 5
                | _ -> 3

            baseValue + if stolen = Some true then 1 else 0
        | ObjectiveKill(Baron, stolen) -> 5 + if stolen = Some true then 1 else 0
        | ObjectiveKill(Herald, stolen) -> 3 + if stolen = Some true then 1 else 0
        | ObjectiveKill(Turret _, _) -> 1
        | ObjectiveKill(Inhibitor _, _) -> 3
        | _ -> 0

    let objectiveWaveValue input =
        input.Snapshot.Events
        |> List.filter (recentWithin 8.0 input.Snapshot)
        |> List.sumBy objectiveValue

    let teamfightKillCount input =
        input.Snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> recentWithin scoringConfig.TeamfightWindowSec input.Snapshot ev
            | _ -> false)

    let teamfightValue input =
        let kills = teamfightKillCount input

        if kills.Length < scoringConfig.TeamfightKillCountThreshold then
            0
        else
            let involvementBonus =
                if kills |> List.exists (fun ev -> nameMatches input.Snapshot.ActiveAliases ev.ActorName) then 3
                elif kills |> List.exists (activeInvolved input.Snapshot) then 2
                else 1

            min 5 (kills.Length + involvementBonus)

    let aceValue input =
        input.Snapshot.Events
        |> List.exists (fun ev ->
            match ev.Kind with
            | Ace _ -> recentWithin 10.0 input.Snapshot ev
            | _ -> false)
        |> fun hasAce -> if hasAce then 5 else 0

    let lastObjectiveTime predicate (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.choose (fun ev -> if predicate ev then Some ev.GameTime else None)
        |> List.sortDescending
        |> List.tryHead

    let tensionValue windowSec now spawnAt =
        if windowSec <= 0.0 || now > spawnAt || spawnAt - now > windowSec then
            0.0
        else
            let progress = 1.0 - ((spawnAt - now) / windowSec)
            Math.Ceiling(progress * 2.0) |> float |> min 2.0 |> max 0.0

    let jungleTensionValue snapshot =
        let now = snapshot.GameTime

        let dragonNext =
            match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Dragon _, _) -> true | _ -> false) snapshot with
            | Some last -> last + scoringConfig.DragonRespawnSec
            | None -> scoringConfig.DragonInitialSpawnSec

        let heraldNext =
            if now <= scoringConfig.HeraldDespawnSec then scoringConfig.HeraldInitialSpawnSec else Double.PositiveInfinity

        let baronNext =
            match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Baron, _) -> true | _ -> false) snapshot with
            | Some last -> last + scoringConfig.BaronRespawnSec
            | None -> scoringConfig.BaronInitialSpawnSec

        [
            tensionValue scoringConfig.ObjectiveTensionWindowSec now dragonNext
            tensionValue scoringConfig.ObjectiveTensionWindowSec now heraldNext
            tensionValue scoringConfig.ObjectiveTensionWindowSec now baronNext
        ]
        |> List.max

    interface IRuleInputBuilder with
        member _.Build state input layers =
            let snapshot = input.Snapshot
            let active = snapshot.ActivePlayer
            let hp = healthPercent snapshot
            let missingHealth = hp |> Option.map (fun value -> 1.0 - value) |> Option.defaultValue 0.0
            let rawScore = rawPerformanceScore scoringConfig active
            let normalizedScore = normalizeActivePlayerScore scoringConfig snapshot
            let activeKills = activeKillEvents input
            let activeDeaths = activeDeathEvents input
            let multikillStreak = activeMultikillStreak input
            let teamfightKills = teamfightKillCount input
            let heartbeatAmplitude = missingHealth * scoringConfig.HeartbeatPulseMaxAmplitude
            let safePollMs = max 1 input.RuntimePollMs
            let iterationsPerSecond = max 1.0 (Math.Round(1000.0 / float safePollMs))
            let loopIteration = float input.LoopIteration
            let loopIterationWithinSecond = float ((int64 loopIteration) % int64 iterationsPerSecond)
            let loopTimeSec = loopIteration * float safePollMs / 1000.0
            let projectedCacheVariables = cacheVariables input.Now
            let projectedStateVariables = AppCache.projectAnnotated None state
            let positionLeftWeight, positionRightWeight = positionWeights input.Position
            let positionAvailable = input.Position.IsSome

            Map.empty
            |> add "Kills" (float active.Kills)
            |> add "Deaths" (float active.Deaths)
            |> add "Assists" (float active.Assists)
            |> add "CreepScore" (float active.CreepScore)
            |> add "CS" (float active.CreepScore)
            |> add "WardScore" active.WardScore
            |> add "Level" (float active.Level)
            |> add "CurrentHealth" (active.CurrentHealth |> Option.defaultValue 0.0)
            |> add "MaxHealth" (active.MaxHealth |> Option.defaultValue 0.0)
            |> add "HealthPercent" (hp |> Option.defaultValue 1.0)
            |> add "MissingHealth" missingHealth
            |> add "GameTime" snapshot.GameTime
            |> add "RawPerformanceScore" rawScore
            |> add "NormalizedScore" normalizedScore
            |> add "PerformanceScore" (normalizedScore * scoringConfig.NormalizedScoreWeight)
            |> add "DeathPenalty" (float active.Deaths * scoringConfig.DeathWeight)
            |> add "LiveHealthMultiplier" (scoringConfig.HealthMinMultiplier + (1.0 - scoringConfig.HealthMinMultiplier) * (hp |> Option.defaultValue 1.0))
            |> add "HealthPressureMultiplier" input.EvolvedState.HealthPressure.PressureMultiplier
            |> add "ActiveKill" (boolValue (not activeKills.IsEmpty))
            |> add "ActiveKillCount" (float activeKills.Length)
            |> add "ActiveDeath" (boolValue (not activeDeaths.IsEmpty))
            |> add "ActiveDeathCount" (float activeDeaths.Length)
            |> add "ActiveMultikill" (boolValue (multikillStreak > 0))
            |> add "MultikillCount" (float multikillStreak)
            |> add "TotalMultikillCount" (float input.EvolvedState.MultikillCount)
            |> add "ObjectiveWaveValue" (objectiveWaveValue input |> float)
            |> add "TeamfightKillCount" (float teamfightKills.Length)
            |> add "TeamfightBurstValue" (teamfightValue input |> float)
            |> add "AceBurstValue" (aceValue input |> float)
            |> add "HeartbeatAmplitude" heartbeatAmplitude
            |> add "LowHealthHeartbeatThreshold" scoringConfig.LowHealthHeartbeatThreshold
            |> add "CriticalHealthHeartbeatThreshold" scoringConfig.CriticalHealthHeartbeatThreshold
            |> add "HeartbeatPulseMaxAmplitude" scoringConfig.HeartbeatPulseMaxAmplitude
            |> add "HeartbeatPulseCycleSec" scoringConfig.HeartbeatPulseCycleSec
            |> add "HeartbeatPulseStartPhase" scoringConfig.HeartbeatPulseStartPhase
            |> add "HeartbeatPulsePeakPhase" scoringConfig.HeartbeatPulsePeakPhase
            |> add "HeartbeatPulseEndPhase" scoringConfig.HeartbeatPulseEndPhase
            |> add "LaningTextureValue" (if snapshot.GameTime <= scoringConfig.LaningPhaseEndSec then min 2.0 (float active.CreepScore / 75.0 + active.WardScore / 25.0 + float active.Assists / 4.0) else 0.0)
            |> add "JungleTensionValue" (jungleTensionValue snapshot)
            |> add "LoopIteration" loopIteration
            |> add "LoopIterationWithinSecond" loopIterationWithinSecond
            |> add "LoopIterationsPerSecond" iterationsPerSecond
            |> add "LoopTimeSec" loopTimeSec
            |> add "RuntimePollMs" (float safePollMs)
            |> RuleInternals.mergeVariables projectedCacheVariables
            |> RuleInternals.mergeVariables projectedStateVariables
            |> add "PositionAvailable" (boolValue positionAvailable)
            |> add "PositionLeftWeight" positionLeftWeight
            |> add "PositionRightWeight" positionRightWeight
            |> add "PositionX" (input.Position |> Option.map (fun p -> p.NormalizedX) |> Option.defaultValue 0.0)
            |> add "PositionY" (input.Position |> Option.map (fun p -> p.NormalizedY) |> Option.defaultValue 0.0)
            |> add "PositionConfidence" (input.Position |> Option.map (fun p -> p.Confidence) |> Option.defaultValue 0.0)
            |> add "Pi" Math.PI
            |> RuleInternals.mergeVariables (RuleInternals.layerVariables layers)
            |> RuleInternals.mergeVariables (RuleInternals.functionRangeVariables ())
            |> fun variables ->
                LovenseActionCodec.emptyState
                |> Map.fold (fun acc name value ->
                    let previous = input.LastSentFunctionState |> Map.tryFind name |> Option.defaultValue value
                    acc |> Map.add $"PreviousFunction_{name}" (float previous)) variables
