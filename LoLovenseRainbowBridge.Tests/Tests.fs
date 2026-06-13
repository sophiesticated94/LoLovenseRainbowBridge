module LoLovenseRainbowBridge.Tests

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Text.Json.Nodes
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge.Recording
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
        Developer =
            {
                Token = None
                UserId = None
                UserName = None
                UserEmail = None
                UserToken = None
            }
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

let recordingConfig databasePath =
    {
        Enabled = true
        DatabasePath = databasePath
        SliceMs = 100
        RecordRawContext = false
    }

let loggingConfig directory =
    {
        BaseDirectory = directory
        SessionDirectoryFormat = "yyyy-MM-dd_HH-mm-ss_fffffff"
        TrackLogLevel = "Trace"
        LogRawLeague = false
        LogRawLovense = false
        RawLogPrettyPrint = false
    }

let testAssetPath fileName =
    let outputPath = Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName)

    if File.Exists outputPath then
        outputPath
    else
        Path.Combine(__SOURCE_DIRECTORY__, "TestAssets", fileName)

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

let snapshotAt gameTime health events =
    { snapshot health events with GameTime = gameTime }

let tempPath fileName =
    Path.Combine(Path.GetTempPath(), "LoLovenseRainbowBridge.Tests", Guid.NewGuid().ToString("N"), fileName)

[<Fact>]
let ``live health multiplier interpolates for arbitrary HP`` () =
    Assert.Equal(0.5, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.0), 6)
    Assert.Equal(0.625, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.25), 6)
    Assert.Equal(0.75, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.5), 6)
    Assert.Equal(0.875, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.75), 6)
    Assert.Equal(1.0, LiveHealthMultiplierCalculator.compute scoringConfig (Some 1.0), 6)

[<Fact>]
let ``heartbeat amplitude follows missing health in zero to six range`` () =
    Assert.Equal(0.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude 1.0, 6)
    Assert.Equal(1.5, TemporaryPulseCalculator.HeartbeatCalculator.amplitude 0.75, 6)
    Assert.Equal(3.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude 0.5, 6)
    Assert.Equal(4.5, TemporaryPulseCalculator.HeartbeatCalculator.amplitude 0.25, 6)
    Assert.Equal(6.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude 0.0, 6)

[<Fact>]
let ``heartbeat pulse shape stays near zero most of cycle and peaks briefly`` () =
    let quiet = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape 1000.10
    let rising = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape 1000.75
    let peak = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape 1000.78
    let falling = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape 1000.90
    let ended = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape 1000.99

    Assert.Equal(0.0, quiet, 6)
    Assert.InRange(rising, 0.01, 0.99)
    Assert.Equal(1.0, peak, 6)
    Assert.InRange(falling, 0.01, 0.99)
    Assert.Equal(0.0, ended, 6)
    Assert.True(rising > falling)

[<Fact>]
let ``low health heartbeat adds stronger positive pulse to final intensity`` () =
    let lowHpPeak = computeIntensityBreakdown scoringConfig (snapshotAt 1000.78 (Some(100.0, 1000.0)) []) initialState
    let higherHpPeak = computeIntensityBreakdown scoringConfig (snapshotAt 1000.78 (Some(250.0, 1000.0)) []) initialState
    let lowHpQuiet = computeIntensityBreakdown scoringConfig (snapshotAt 1000.10 (Some(100.0, 1000.0)) []) initialState

    let heartbeatValue breakdown =
        breakdown.TemporaryEffects
        |> List.tryFind (fun effect -> effect.Kind = HeartbeatNearDeathEffect)
        |> Option.map (fun effect -> effect.Value)
        |> Option.defaultValue 0

    Assert.Equal(5, heartbeatValue lowHpPeak)
    Assert.Equal(4, heartbeatValue higherHpPeak)
    Assert.Equal(0, heartbeatValue lowHpQuiet)
    Assert.True(lowHpPeak.Intensity > lowHpQuiet.Intensity)

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
let ``default configuration enables position rotation`` () =
    let config = Configuration.load ()

    Assert.True(config.PositionBasedRotation.Enable)
    Assert.Equal("Combined", config.PositionBasedRotation.MappingMode)
    Assert.Equal(None, config.PositionBasedRotation.TemplateImagePath)
    Assert.False(config.PositionBasedRotation.DebugMode)
    Assert.True(config.Recording.Enabled)
    Assert.Equal("data/gameplay.sqlite", config.Recording.DatabasePath.Replace('\\', '/'))
    Assert.Equal(100, config.Recording.SliceMs)

[<Fact>]
let ``lovense action string parses to normalized function state`` () =
    let state = LovenseActionCodec.stateFromActionString "Vibrate:10,Rotate:7,Pump:3,Stroke:0-80"

    Assert.Equal(10, state["Vibrate"])
    Assert.Equal(7, state["Rotate"])
    Assert.Equal(3, state["Pump"])
    Assert.Equal(80, state["Stroke"])
    Assert.Equal(0, state["Stop"])

[<Fact>]
let ``lovense state diff includes only changed functions`` () =
    let previous = LovenseActionCodec.stateFromActionString "Vibrate:10,Rotate:7"
    let current = LovenseActionCodec.stateFromActionString "Vibrate:10,Rotate:9,All:4"
    let diff = LovenseActionCodec.diff previous current |> Map.ofList

    Assert.False(diff.ContainsKey("Vibrate"))
    Assert.Equal(9, diff["Rotate"])
    Assert.Equal(4, diff["All"])

[<Fact>]
let ``sqlite recorder opens closes and skips unchanged slices`` () =
    let dbPath = tempPath "gameplay.sqlite"
    let logDir = Path.Combine(Path.GetDirectoryName dbPath, "log")
    use logger = new StructuredSessionLogger(loggingConfig logDir)
    let recorder = new GameplayRecorder(recordingConfig dbPath, logger)
    let bridgeSnapshot = snapshot (Some(1000.0, 1000.0)) []
    let state = initialState
    let breakdown = computeIntensityBreakdown scoringConfig bridgeSnapshot state
    let plan = Mapping.simpleVibratePlan lovenseConfig breakdown.Intensity
    let action = Mapping.planActionString plan
    let now = DateTimeOffset.Parse("2026-06-13T10:00:00.0000000+00:00")
    let configSummary = {| test = true |}

    recorder.RecordPlan(now, configSummary, bridgeSnapshot, breakdown, plan, action, { Attempted = false; Success = None; Error = None })
    recorder.RecordPlan(now.AddMilliseconds(100.0), configSummary, bridgeSnapshot, breakdown, plan, action, { Attempted = false; Success = None; Error = None })
    recorder.CloseActiveGame(now.AddSeconds(1.0))

    let games = recorder.ListGames()
    Assert.Single(games) |> ignore
    Assert.True(games.Head.EndedAt.IsSome)

    let records = recorder.ReadRecords(games.Head.GameId)
    Assert.Single(records) |> ignore
    let context = JsonNode.Parse(records.Head.ContextDiffJson)
    Assert.Equal(action, context["action"].GetValue<string>())
    Assert.NotNull(context["functions"])

[<Fact>]
let ``replay can reconstruct command plan from recorded action`` () =
    let plan = LovenseActionCodec.planFromActionString lovenseConfig "Vibrate:11,Rotate:4,All:12"
    let action = Mapping.planActionString plan

    Assert.Equal("Vibrate:11,Rotate:4,All:12", action)
    Assert.Equal(3, plan.Actions.Length)

[<Fact>]
let ``minimap detection uses real screenshot fixture template`` () =
    use screenshot = new Bitmap(testAssetPath "screenshot.jpg")
    use minimap = screenshot.Clone(Rectangle(992, 552, 208, 196), PixelFormat.Format24bppRgb)
    use playerTemplate = minimap.Clone(Rectangle(94, 82, 48, 48), PixelFormat.Format24bppRgb)

    let template = MinimapDetector.createTemplateFromBitmap playerTemplate
    Assert.True(template.IsSome, "Expected a template created from the real screenshot crop.")

    let captureResult =
        {
            Bitmap = minimap
            Timestamp = DateTimeOffset.Now
        }

    let result = MinimapDetector.detectPlayerPosition captureResult template

    Assert.Equal("TemplateMatching", result.DetectionMethod)
    Assert.True(result.Position.IsSome, "Player marker should be detected from screenshot.jpg.")

    let position = result.Position.Value
    Assert.InRange(position.NormalizedX, 0.45, 0.70)
    Assert.InRange(position.NormalizedY, 0.40, 0.70)
    Assert.True(position.Confidence >= 0.7, sprintf "Template confidence too low: %f" position.Confidence)

[<Fact>]
let ``minimap detection works without generated default template`` () =
    use screenshot = new Bitmap(testAssetPath "screenshot.jpg")
    use minimap = screenshot.Clone(Rectangle(992, 552, 208, 196), PixelFormat.Format24bppRgb)

    let captureResult =
        {
            Bitmap = minimap
            Timestamp = DateTimeOffset.Now
        }

    let result = MinimapDetector.detectPlayerPosition captureResult None

    Assert.True(result.DetectionMethod <> "TemplateMatching", $"Unexpected template matching path: {result.DetectionMethod}")
    Assert.True(result.Position.IsSome, "Color/contour detector should find a marker in the real screenshot minimap.")

    let position = result.Position.Value
    Assert.InRange(position.NormalizedX, 0.0, 1.0)
    Assert.InRange(position.NormalizedY, 0.0, 1.0)
    Assert.True(position.Confidence > 0.1, sprintf "Color detector confidence too low: %f" position.Confidence)
