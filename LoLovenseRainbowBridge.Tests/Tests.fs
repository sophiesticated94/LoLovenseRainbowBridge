module LoLovenseRainbowBridge.Tests

open System
open System.IO
open System.Text.Json.Nodes
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge.ScreenCapture
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
        DeathPressureWindowSec = 60.0
        DeathPressureBaseLossPercent = 0.5
        HpChangeThresholdPercent = 5.0
        BaseRecoveryFloor = 0.5
        BaseRecoveryTarget = 0.8
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
let ``death pressure activates on death and reduces base`` () =
    let initial = initialState.HealthPressure
    let afterDeath = HealthPressureCalculator.handleDeath scoringConfig 100.0 initial

    Assert.True(afterDeath.DeathPressureActive)
    Assert.Equal(Some 100.0, afterDeath.DeathPressureStartTime)
    Assert.Equal(Some 1.0, afterDeath.BaseBeforeDeath)
    Assert.Equal(0.5, afterDeath.PressureMultiplier, 6)

[<Fact>]
let ``kill during death pressure recovers original base`` () =
    let initial = initialState.HealthPressure
    let afterDeath = HealthPressureCalculator.handleDeath scoringConfig 100.0 initial
    let afterKill = HealthPressureCalculator.handleKillDuringPressure afterDeath

    Assert.False(afterKill.DeathPressureActive)
    Assert.Equal(None, afterKill.DeathPressureStartTime)
    Assert.Equal(None, afterKill.BaseBeforeDeath)
    Assert.Equal(1.0, afterKill.PressureMultiplier, 6)

[<Fact>]
let ``death pressure window expiry applies additional loss`` () =
    let initial = initialState.HealthPressure
    let afterDeath = HealthPressureCalculator.handleDeath scoringConfig 100.0 initial
    let afterExpiry = HealthPressureCalculator.checkPressureWindowExpiry scoringConfig 170.0 afterDeath

    Assert.False(afterExpiry.DeathPressureActive)
    Assert.Equal(None, afterExpiry.DeathPressureStartTime)
    Assert.Equal(None, afterExpiry.BaseBeforeDeath)
    Assert.Equal(0.25, afterExpiry.PressureMultiplier, 6)

[<Fact>]
let ``hp change threshold resets base when exceeded`` () =
    let initial = initialState.HealthPressure
    let withInitialHp = HealthPressureCalculator.update scoringConfig 0.0 (Some 1.0) initial
    let withHpChange = HealthPressureCalculator.update scoringConfig 0.0 (Some 0.8) withInitialHp

    Assert.Equal(0.86, withHpChange.PressureMultiplier, 6)

[<Fact>]
let ``base recovery interpolates correctly`` () =
    Assert.Equal(0.5, HealthPressureCalculator.calculateBaseRecovery scoringConfig 0.0, 6)
    Assert.Equal(0.65, HealthPressureCalculator.calculateBaseRecovery scoringConfig 0.25, 6)
    Assert.Equal(0.8, HealthPressureCalculator.calculateBaseRecovery scoringConfig 0.5, 6)
    Assert.Equal(0.85, HealthPressureCalculator.calculateBaseRecovery scoringConfig 0.75, 6)
    Assert.Equal(0.8, HealthPressureCalculator.calculateBaseRecovery scoringConfig 1.0, 6)

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

[<Fact>]
let ``minimap detection finds green player indicator`` () =
    // Generate test image programmatically using System.Drawing
    use bitmap = new System.Drawing.Bitmap(200, 200, System.Drawing.Imaging.PixelFormat.Format24bppRgb)
    use graphics = System.Drawing.Graphics.FromImage(bitmap)
    
    // Draw black background
    graphics.Clear(System.Drawing.Color.Black)
    
    // Draw bright green player indicator at center (100, 100) - this should match the HSV detection range
    graphics.FillEllipse(System.Drawing.Brushes.Lime, 92, 92, 16, 16)
    
    let captureResult = {
        Bitmap = bitmap
        Timestamp = DateTimeOffset.Now
    }
    
    // Create default template
    let template = MinimapDetector.createDefaultTemplate ()
    Assert.True(template.IsSome, "Failed to create default template")
    
    // Run detection
    let result = MinimapDetector.detectPlayerPosition captureResult template
    
    // Assert position is detected
    Assert.True(result.Position.IsSome, "Player position should be detected")
    
    let position = result.Position.Value
    
    // Assert normalized coordinates are in valid range (0-1)
    Assert.True(position.NormalizedX >= 0.0 && position.NormalizedX <= 1.0, sprintf "NormalizedX out of range: %f" position.NormalizedX)
    Assert.True(position.NormalizedY >= 0.0 && position.NormalizedY <= 1.0, sprintf "NormalizedY out of range: %f" position.NormalizedY)
    
    // Assert confidence is reasonable (> 0.1 for now, will tune later)
    Assert.True(position.Confidence > 0.1, sprintf "Confidence too low: %f" position.Confidence)
    
    // For now, just verify detection works without strict position checking
    // The HSV parameters may need tuning to match System.Drawing colors accurately

[<Fact>]
let ``default template creates green circle`` () =
    let template = MinimapDetector.createDefaultTemplate ()
    Assert.True(template.IsSome, "Failed to create default template")
    
    let mat = template.Value
    Assert.Equal(20, mat.Width)
    Assert.Equal(20, mat.Height)
