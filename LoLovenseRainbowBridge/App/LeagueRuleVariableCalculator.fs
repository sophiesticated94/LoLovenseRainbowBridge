namespace LoLovenseRainbowBridge.App

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.App.RuntimeState

module LeagueRuleVariableCalculator =

    let private recentWithin windowSec (snapshot: BridgeSnapshot) (ev: BridgeEvent) =
        ev.GameTime <= snapshot.GameTime && snapshot.GameTime - ev.GameTime <= windowSec

    let private newEvents (state: GeneratorState) (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (fun ev -> not (state.SeenEventIds.Contains ev.EventId))

    let private activeKillEvents (state: GeneratorState) (snapshot: BridgeSnapshot) =
        newEvents state snapshot
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches snapshot.ActiveAliases ev.ActorName
            | _ -> false)

    let private activeDeathEvents (state: GeneratorState) (snapshot: BridgeSnapshot) =
        newEvents state snapshot
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches snapshot.ActiveAliases ev.VictimName
            | _ -> false)

    let private activeMultikillStreak (state: GeneratorState) (snapshot: BridgeSnapshot) =
        newEvents state snapshot
        |> List.choose (fun ev ->
            match ev.Kind with
            | Multikill streak when nameMatches snapshot.ActiveAliases ev.ActorName -> Some streak
            | _ -> None)
        |> function
            | [] -> 0
            | streaks -> streaks |> List.max

    let private objectiveValue (ev: BridgeEvent) =
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

    let private objectiveWaveValue (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (recentWithin 8.0 snapshot)
        |> List.sumBy objectiveValue

    let private teamfightKillCount (scoringConfig: ScoringConfig) (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> recentWithin scoringConfig.TeamfightWindowSec snapshot ev
            | _ -> false)

    let private teamfightValue (scoringConfig: ScoringConfig) (snapshot: BridgeSnapshot) =
        let kills = teamfightKillCount scoringConfig snapshot

        if kills.Length < scoringConfig.TeamfightKillCountThreshold then
            0
        else
            let involvementBonus =
                if kills |> List.exists (fun ev -> nameMatches snapshot.ActiveAliases ev.ActorName) then 3
                elif kills |> List.exists (activeInvolved snapshot) then 2
                else 1

            min 5 (kills.Length + involvementBonus)

    let private aceValue (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.exists (fun ev ->
            match ev.Kind with
            | Ace _ -> recentWithin 10.0 snapshot ev
            | _ -> false)
        |> fun hasAce -> if hasAce then 5 else 0

    let private lastObjectiveTime predicate (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.choose (fun ev -> if predicate ev then Some ev.GameTime else None)
        |> List.sortDescending
        |> List.tryHead

    let private tensionValue windowSec now spawnAt =
        if windowSec <= 0.0 || now > spawnAt || spawnAt - now > windowSec then
            0.0
        else
            let progress = 1.0 - ((spawnAt - now) / windowSec)
            Math.Ceiling(progress * 2.0) |> float |> min 2.0 |> max 0.0

    let private jungleTensionValue (scoringConfig: ScoringConfig) (snapshot: BridgeSnapshot) =
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

    let calculate (scoringConfig: ScoringConfig) (snapshot: BridgeSnapshot) (state: GeneratorState) : LeagueRuleCacheState =
        let active = snapshot.ActivePlayer
        let hp = healthPercent snapshot
        let missingHealth = hp |> Option.map (fun value -> 1.0 - value) |> Option.defaultValue 0.0
        let rawScore = rawPerformanceScore scoringConfig active
        let normalizedScore = normalizeActivePlayerScore scoringConfig snapshot
        let activeKills = activeKillEvents state snapshot
        let activeDeaths = activeDeathEvents state snapshot
        let multikillStreak = activeMultikillStreak state snapshot
        let teamfightKills = teamfightKillCount scoringConfig snapshot
        let heartbeatAmplitude = missingHealth * scoringConfig.HeartbeatPulseMaxAmplitude

        {
            Kills = float active.Kills
            Deaths = float active.Deaths
            Assists = float active.Assists
            CreepScore = float active.CreepScore
            CS = float active.CreepScore
            WardScore = active.WardScore
            Level = float active.Level
            CurrentHealth = active.CurrentHealth |> Option.defaultValue 0.0
            MaxHealth = active.MaxHealth |> Option.defaultValue 0.0
            HealthPercent = hp |> Option.defaultValue 1.0
            MissingHealth = missingHealth
            GameTime = snapshot.GameTime
            RawPerformanceScore = rawScore
            NormalizedScore = normalizedScore
            PerformanceScore = normalizedScore * scoringConfig.NormalizedScoreWeight
            DeathPenalty = float active.Deaths * scoringConfig.DeathWeight
            LiveHealthMultiplier = scoringConfig.HealthMinMultiplier + (1.0 - scoringConfig.HealthMinMultiplier) * (hp |> Option.defaultValue 1.0)
            HealthPressureMultiplier = state.HealthPressure.PressureMultiplier
            ActiveKill = if activeKills.IsEmpty then 0.0 else 1.0
            ActiveKillCount = float activeKills.Length
            ActiveDeath = if activeDeaths.IsEmpty then 0.0 else 1.0
            ActiveDeathCount = float activeDeaths.Length
            ActiveMultikill = if multikillStreak > 0 then 1.0 else 0.0
            MultikillCount = float multikillStreak
            TotalMultikillCount = float state.MultikillCount
            ObjectiveWaveValue = float (objectiveWaveValue snapshot)
            TeamfightKillCount = float teamfightKills.Length
            TeamfightBurstValue = float (teamfightValue scoringConfig snapshot)
            AceBurstValue = float (aceValue snapshot)
            HeartbeatAmplitude = heartbeatAmplitude
            LowHealthHeartbeatThreshold = scoringConfig.LowHealthHeartbeatThreshold
            CriticalHealthHeartbeatThreshold = scoringConfig.CriticalHealthHeartbeatThreshold
            HeartbeatPulseMaxAmplitude = scoringConfig.HeartbeatPulseMaxAmplitude
            HeartbeatPulseCycleSec = scoringConfig.HeartbeatPulseCycleSec
            HeartbeatPulseStartPhase = scoringConfig.HeartbeatPulseStartPhase
            HeartbeatPulsePeakPhase = scoringConfig.HeartbeatPulsePeakPhase
            HeartbeatPulseEndPhase = scoringConfig.HeartbeatPulseEndPhase
            LaningTextureValue =
                if snapshot.GameTime <= scoringConfig.LaningPhaseEndSec then
                    min 2.0 (float active.CreepScore / 75.0 + active.WardScore / 25.0 + float active.Assists / 4.0)
                else
                    0.0
            JungleTensionValue = jungleTensionValue scoringConfig snapshot
        }
