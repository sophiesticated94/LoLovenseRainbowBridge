module LoLovenseRainbowBridge.Tests

open System.Text.Json.Nodes
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open Xunit

let scoringConfig =
    {
        KillWeight = 4.0
        AssistWeight = 1.5
        CreepScoreWeight = 0.04
        LevelWeight = 0.8
        WardScoreWeight = 0.15
        DeathWeight = 3.5
        NormalizedScoreWeight = 5.0
        EqualScoreNormalizedValue = 0.5
        ScoreEqualityEpsilon = 0.0001
        MinIntensity = 0
        MaxIntensity = 20
        BaseIntensityCap = 18
        HealthMinMultiplier = 0.5
        HealthPressureDropThresholdPercent = 5.0
        FullRegainPressureFactor = 0.8
        SingleKillPulseValue = 1
        SingleKillPulseDurationSec = 1.0
        ProvisionalSingleKillWindowSec = 2.0
        MinMultikillStreak = 2
        MaxMultikillStreak = 5
        EnableObjectiveWaves = true
        EnableTeamfightBurst = true
        EnableHeartbeatNearDeath = true
        EnableLaningPhaseTexture = true
        EnableJungleTensionRamp = true
        TeamfightWindowSec = 12.0
        TeamfightKillCountThreshold = 3
        LowHealthHeartbeatThreshold = 0.30
        CriticalHealthHeartbeatThreshold = 0.15
        LaningPhaseEndSec = 840.0
        DragonInitialSpawnSec = 300.0
        DragonRespawnSec = 300.0
        HeraldInitialSpawnSec = 480.0
        HeraldDespawnSec = 1140.0
        BaronInitialSpawnSec = 1200.0
        BaronRespawnSec = 360.0
        ObjectiveTensionWindowSec = 90.0
    }

let lovenseConfig =
    {
        AuthToken = None
        ToyId = None
        Platform = "tests"
        CommandTimeSec = 2.0
        DryRun = true
        ConnectTimeoutMs = 1000
        CommandAckTimeoutMs = 1000
        Mapping =
            {
                Mode = "MultiFunction"
                EnableComboActions = true
                EnableEventBursts = true
                EnableDeathStop = true
                EnableStrokeActions = false
                EnableCapabilityFiltering = true
                DefaultStopPrevious = true
                UnknownCapabilityMode = "SafeUniversal"
                ForceSupportedFunctions = []
                MaxActionIntensity = 20
                PumpMax = 3
                DepthMax = 3
                StrokeMax = 100
            }
    }

let player health : BridgePlayer =
    {
        Id = "active"
        Aliases = [ "Active#EUW" ]
        Kills = 20
        Deaths = 0
        Assists = 10
        CreepScore = 250
        WardScore = 30.0
        Level = 18
        CurrentHealth = health |> Option.map fst
        MaxHealth = health |> Option.map snd
    }

let snapshot health events : BridgeSnapshot =
    let active = player health

    {
        GameTime = 1000.0
        ActiveAliases = active.Aliases
        ActivePlayer = active
        Players = [ active; { active with Id = "other"; Aliases = [ "Other#EUW" ]; Kills = 0; Assists = 0; CreepScore = 10; Level = 1 } ]
        Events = events
    }

[<Fact>]
let ``live health multiplier interpolates for arbitrary HP`` () =
    Assert.Equal(0.5, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.0), 6)
    Assert.Equal(0.625, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.25), 6)
    Assert.Equal(0.75, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.5), 6)
    Assert.Equal(0.875, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.75), 6)
    Assert.Equal(1.0, LiveHealthMultiplierCalculator.compute scoringConfig (Some 1.0), 6)

[<Fact>]
let ``health pressure applies proportional incremental recovery scars`` () =
    let initial = initialState.HealthPressure
    let atFull = HealthPressureCalculator.update scoringConfig (Some 1.0) initial
    let atHalf = HealthPressureCalculator.update scoringConfig (Some 0.5) atFull
    let quarterRecovered = HealthPressureCalculator.update scoringConfig (Some 0.625) atHalf
    let halfRecovered = HealthPressureCalculator.update scoringConfig (Some 0.75) quarterRecovered
    let fullRecovered = HealthPressureCalculator.update scoringConfig (Some 1.0) halfRecovered

    Assert.Equal(0.95, quarterRecovered.PressureMultiplier, 6)
    Assert.Equal(0.9, halfRecovered.PressureMultiplier, 6)
    Assert.Equal(0.8, fullRecovered.PressureMultiplier, 6)

[<Fact>]
let ``health pressure compounds across separate recovered loss segments`` () =
    let first =
        initialState.HealthPressure
        |> HealthPressureCalculator.update scoringConfig (Some 1.0)
        |> HealthPressureCalculator.update scoringConfig (Some 0.5)
        |> HealthPressureCalculator.update scoringConfig (Some 1.0)

    let second =
        first
        |> HealthPressureCalculator.update scoringConfig (Some 0.5)
        |> HealthPressureCalculator.update scoringConfig (Some 1.0)

    Assert.Equal(0.8, first.PressureMultiplier, 6)
    Assert.Equal(0.64, second.PressureMultiplier, 6)

[<Fact>]
let ``base intensity caps at eighteen while temporary effects can reach twenty`` () =
    let dragon : BridgeEvent =
        {
            EventId = 10
            GameTime = 998.0
            ActorName = Some "Active#EUW"
            VictimName = None
            Assisters = []
            Kind = ObjectiveKill(Dragon(Some "Elder"), Some true)
        }

    let state = { initialState with MultikillCount = 30 }
    let breakdown = computeIntensityBreakdown scoringConfig (snapshot (Some(1000.0, 1000.0)) [ dragon ]) state

    Assert.True(breakdown.BaseIntensity <= 18)
    Assert.True(breakdown.TemporaryBoost > 0)
    Assert.Equal(20, breakdown.Intensity)

[<Fact>]
let ``parser reads active health and objective event fields`` () =
    let raw =
        """
        {
          "activePlayer": {
            "riotId": "Active#EUW",
            "riotIdGameName": "Active",
            "riotIdTagLine": "EUW",
            "championStats": { "currentHealth": 250.0, "maxHealth": 1000.0 }
          },
          "allPlayers": [
            {
              "riotId": "Active#EUW",
              "riotIdGameName": "Active",
              "riotIdTagLine": "EUW",
              "level": 6,
              "scores": { "kills": 1, "deaths": 0, "assists": 2, "creepScore": 44, "wardScore": 5.0 }
            }
          ],
          "gameData": { "gameTime": 600.0 },
          "events": {
            "Events": [
              { "EventID": 1, "EventName": "DragonKill", "EventTime": 590.0, "DragonType": "Earth", "Stolen": "True", "KillerName": "Active#EUW", "Assisters": ["Other#EUW"] }
            ]
          }
        }
        """

    let parsed = JsonNode.Parse(raw) |> Parser.parseGameSnapshotResult

    match parsed with
    | Error error ->
        failwithf "Parse failed: %A" error
    | Ok parsed ->
        Assert.Equal(Some 250.0, parsed.Snapshot.ActivePlayer.CurrentHealth)
        Assert.Equal(Some 1000.0, parsed.Snapshot.ActivePlayer.MaxHealth)
        Assert.Equal("DragonKill", parsed.Snapshot.Events.Head.EventName)
        Assert.Equal(Some "Earth", parsed.Snapshot.Events.Head.DragonType)
        Assert.Equal(Some true, parsed.Snapshot.Events.Head.Stolen)

[<Fact>]
let ``capability filtering removes unsupported actions and keeps safe fallback`` () =
    let plan =
        {
            Actions =
                [
                    { Function = Vibrate; Value = 10; MaxValue = 20; RangeStart = None }
                    { Function = Rotate; Value = 10; MaxValue = 20; RangeStart = None }
                ]
            Reasons = [ BasePerformance; TeamfightBurst ]
            TimeSec = 2.0
            StopPrevious = true
            ToyId = None
        }

    let filtered, dropped = Mapping.filterByCapabilities lovenseConfig (Some(set [ "Vibrate" ])) plan

    Assert.Single(dropped) |> ignore
    Assert.Equal("Rotate:10", dropped.Head)
    Assert.Equal("Vibrate:10", Mapping.planActionString filtered)
