module LoLovenseRainbowBridge.Tests

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge.Recording
open LoLovenseRainbowBridge.ScreenCapture
open Xunit

type StubHttpMessageHandler(statusCode: HttpStatusCode, responseBody: string, onRequest: HttpRequestMessage -> string -> unit) =
    inherit HttpMessageHandler()

    override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) =
        task {
            let! requestBody =
                if isNull request.Content then
                    Task.FromResult("")
                else
                    request.Content.ReadAsStringAsync(ct)

            onRequest request requestBody

            return
                new HttpResponseMessage(
                    statusCode,
                    Content = new StringContent(responseBody)
                )
        }

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
        HeartbeatPulseMaxAmplitude = 6.0
        HeartbeatPulseCycleSec = 1.0
        HeartbeatPulseStartPhase = 0.72
        HeartbeatPulsePeakPhase = 0.78
        HeartbeatPulseEndPhase = 0.98
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
        ToyId = None
        Platform = "tests"
        Developer =
            {
                Token = None
                UserId = None
                UserName = None
                UserToken = None
            }
        LocalApi =
            {
                EnableGetToys = true
                TimeoutMs = 3000
                AllowSelfSignedCertificate = true
                HeaderPlatform = "LoL Lovense Bridge"
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
                EnableStereoVibration = true
                DefaultStopPrevious = true
                UnknownCapabilityMode = "SafeUniversal"
                StereoMode = "Auto"
                StereoFallback = "Max"
                LogToyViability = true
                ForceSupportedFunctions = []
                MaxActionIntensity = 20
                PumpMax = 3
                DepthMax = 3
                StrokeMax = 100
                FunctionProfiles = []
                Rules = []
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

let functionProfile name inheritFrom enabled =
    {
        FunctionName = name
        Enabled = enabled
        InheritFrom = inheritFrom
        MinOutput = 0
        MaxOutput = if name = "Pump" || name = "Depth" then 3 elif name = "Stroke" then 100 else 20
        BaseWeight = 1.0
        TimedWeight = 1.0
        EffectWeight = 1.0
        Curve = "Linear"
        Smoothing = 0.0
    }

let rule name kind target sourceFunction source operation value stateSlot trigger whenValue =
    {
        Name = name
        Kind = kind
        Enabled = true
        TargetFunction = target
        SourceFunction = sourceFunction
        Source = source
        Operation = operation
        Value = value
        DurationSec = 0.0
        StateSlot = stateSlot
        Trigger = trigger
        When = whenValue
        Layer = ""
        Min = 0.0
        Max = 20.0
    }

let ruleEngineLovenseConfig =
    {
        lovenseConfig with
            Mapping =
                {
                    lovenseConfig.Mapping with
                        FunctionProfiles =
                            [
                                functionProfile "Vibrate" "" true
                                functionProfile "Vibrate1" "Vibrate" true
                                functionProfile "Vibrate2" "Vibrate" true
                                functionProfile "All" "" true
                            ]
                        Rules =
                            [
                                rule "base" "BaseModifier" "Vibrate" "" "Breakdown.BaseIntensity" "Set" 1.0 "" "" ""
                                rule "track-max" "ThresholdModifier" "" "" "FunctionBase:Vibrate" "TrackMax" 1.0 "MaxBaseThisIncarnation" "" ""
                                rule "floor" "ThresholdModifier" "" "" "State.MaxBaseThisIncarnation" "Multiply" 0.5 "MinBaseThisIncarnation" "" ""
                                rule "clamp-floor" "ThresholdModifier" "Vibrate" "" "State.MinBaseThisIncarnation" "ClampMin" 1.0 "" "" ""
                                rule "inherit-left" "FunctionInheritance" "Vibrate1" "Vibrate" "" "Set" 1.0 "" "" ""
                                rule "inherit-right" "FunctionInheritance" "Vibrate2" "Vibrate" "" "Set" 1.0 "" "" ""
                                rule "top-left-left" "PositionModulation" "Vibrate1" "" "" "MultiplyInherited" 1.35 "" "" "TopLeft"
                                rule "top-left-right" "PositionModulation" "Vibrate2" "" "" "MultiplyInherited" 0.35 "" "" "TopLeft"
                                rule "kill-all" "TimedContribution" "All" "" "TemporaryEffect.KillPulseEffect" "Add" 1.0 "" "" ""
                            ]
                }
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
let ``lovense transport post json sends body and returns response`` () =
    let mutable capturedUrl = ""
    let mutable capturedBody = ""

    use handler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"code":0,"data":{"ok":true}}""",
            fun request body ->
                capturedUrl <- request.RequestUri.ToString()
                capturedBody <- body
        )

    use http = new HttpClient(handler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "transport-logs"))

    let result =
        Transport.postJsonAsync
            http
            logger
            "corr-test"
            "https://example.test/lovense"
            """{"token":"<redacted>"}"""
            """{"token":"real"}"""
            CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Ok response ->
        Assert.Equal("corr-test", response.CorrelationId)
        Assert.Equal(200, response.StatusCode)
        Assert.Equal("""{"code":0,"data":{"ok":true}}""", response.Body)
        Assert.Equal("https://example.test/lovense", capturedUrl)
        Assert.Equal("""{"token":"real"}""", capturedBody)
    | Error error ->
        failwithf "Expected transport success, got %A" error

[<Fact>]
let ``lovense action codec round trips action strings`` () =
    let actionString = "Vibrate:11,Rotate:4,Stroke:0-80,All:12"
    let plan = LovenseActionCodec.planFromActionString lovenseConfig actionString

    Assert.Equal(actionString, LovenseActionCodec.planActionString plan)

[<Fact>]
let ``lovense getToken payload includes documented fields`` () =
    let developer =
        {
            Token = Some "developer-secret"
            UserId = Some "user-123"
            UserName = Some "display-name"
            UserToken = Some "app-user-token"
        }

    let payload = Auth.buildTokenRequestBody developer |> JsonNode.Parse

    Assert.Equal("developer-secret", payload[Constants.Lovense.DeveloperTokenField].GetValue<string>())
    Assert.Equal("user-123", payload[Constants.Lovense.UserIdField].GetValue<string>())
    Assert.Equal("display-name", payload[Constants.Lovense.UserNameField].GetValue<string>())
    Assert.Equal("app-user-token", payload[Constants.Lovense.UserTokenField].GetValue<string>())

[<Fact>]
let ``lovense token request redaction hides developer and user token`` () =
    let developer =
        {
            Token = Some "developer-secret"
            UserId = Some "user-123"
            UserName = Some "display-name"
            UserToken = Some "app-user-token"
        }

    let payload = Auth.buildRedactedTokenRequestBody developer
    let parsed = JsonNode.Parse(payload)

    Assert.DoesNotContain("developer-secret", payload)
    Assert.DoesNotContain("app-user-token", payload)
    Assert.Equal(Constants.Lovense.AuthTokenRedacted, parsed[Constants.Lovense.DeveloperTokenField].GetValue<string>())
    Assert.Equal(Constants.Lovense.AuthTokenRedacted, parsed[Constants.Lovense.UserTokenField].GetValue<string>())
    Assert.Contains("user-123", payload)

[<Fact>]
let ``lovense token response parser returns runtime auth token`` () =
    let response = """{"code":0,"message":"OK","data":{"authToken":"runtime-token"}}"""

    match Auth.parseTokenResponse response with
    | Ok token -> Assert.Equal("runtime-token", token)
    | Error error -> failwithf "Expected token, got %A" error

[<Fact>]
let ``lovense socket url parser reads url and path`` () =
    let response = """{"code":0,"message":"OK","data":{"socketIoUrl":"https://socket.example","socketIoPath":"/socket.io"}}"""

    match SocketUrl.parseSocketUrlResponse response with
    | Ok info ->
        Assert.Equal("https://socket.example", info.SocketIoUrl)
        Assert.Equal("/socket.io", info.SocketIoPath)
    | Error error ->
        failwithf "Expected socket url info, got %A" error

[<Fact>]
let ``lovense device info parser extracts toys and functions`` () =
    let raw =
        """
        {
          "code": 0,
          "data": {
            "toyList": [
              { "id": "toy-1", "name": "Nora", "toyType": "nora", "nickName": "main", "battery": 88, "connected": true }
            ],
            "function": "Vibrate,Rotate,Pump"
          }
        }
        """

    let deviceInfo = DeviceInfo.parse raw

    Assert.Single(deviceInfo.ToyList) |> ignore
    Assert.Equal(Some "toy-1", deviceInfo.ToyList.Head.Id)
    Assert.Equal(Some 88, deviceInfo.ToyList.Head.Battery)
    Assert.Equal(Some true, deviceInfo.ToyList.Head.Connected)
    Assert.True(deviceInfo.SupportedFunctions.Value.Contains("Vibrate"))
    Assert.True(deviceInfo.SupportedFunctions.Value.Contains("Rotate"))
    Assert.True(deviceInfo.SupportedFunctions.Value.Contains("Pump"))
    Assert.Single(deviceInfo.CapabilityProfiles) |> ignore
    Assert.True(deviceInfo.CapabilityProfiles.Head.SupportedFunctions.Contains("Rotate"))

[<Fact>]
let ``lovense device info parser extracts per toy explicit functions`` () =
    let raw =
        """
        {
          "code": 0,
          "data": {
            "toyList": [
              {
                "id": "toy-1",
                "name": "Gemini",
                "toyType": "gemini",
                "battery": 88,
                "connected": true,
                "fullFunctionNames": ["Vibrate1", "Vibrate2"],
                "shortFunctionNames": ["Vibrate"]
              }
            ]
          }
        }
        """

    let deviceInfo = DeviceInfo.parse raw
    let profile = Assert.Single(deviceInfo.CapabilityProfiles)

    Assert.True(profile.ExplicitFunctions.Contains("Vibrate"))
    Assert.True(profile.ExplicitFunctions.Contains("Vibrate1"))
    Assert.True(profile.ExplicitFunctions.Contains("Vibrate2"))
    Assert.True(profile.StereoVibrationSupported)

[<Fact>]
let ``lovense local get toys parser handles toys json string`` () =
    let raw =
        """
        {
          "code": 200,
          "data": {
            "toys": "{\"toy-1\":{\"id\":\"toy-1\",\"name\":\"Gemini\",\"nickName\":\"left-right\",\"status\":1,\"battery\":77,\"version\":\"1.0\",\"shortFunctionNames\":[\"Vibrate\"],\"fullFunctionNames\":[\"Vibrate1\",\"Vibrate2\"]}}"
          }
        }
        """

    let parsed = DeviceInfo.parseGetToys raw
    let profile = Assert.Single(parsed.CapabilityProfiles)

    Assert.Equal(Some "toy-1", profile.ToyId)
    Assert.Equal(Some "Gemini", profile.Name)
    Assert.True(profile.ExplicitFunctions.Contains("Vibrate1"))
    Assert.True(profile.ExplicitFunctions.Contains("Vibrate2"))
    Assert.True(profile.StereoVibrationSupported)

[<Fact>]
let ``lovense local get toys parser handles toys object`` () =
    let raw =
        """
        {
          "data": {
            "toys": {
              "toy-1": {
                "id": "toy-1",
                "name": "Ferri",
                "fullFunctionNames": ["Vibrate"],
                "shortFunctionNames": []
              }
            }
          }
        }
        """

    let parsed = DeviceInfo.parseGetToys raw
    let profile = Assert.Single(parsed.CapabilityProfiles)

    Assert.True(profile.ExplicitFunctions.Contains("Vibrate"))
    Assert.False(profile.StereoVibrationSupported)

[<Fact>]
let ``lovense local get toys client posts command and parses response`` () =
    let mutable capturedUrl = ""
    let mutable capturedPlatform = ""
    let mutable capturedBody = ""
    let response =
        """
        {
          "data": {
            "toys": {
              "toy-1": {
                "id": "toy-1",
                "name": "Gemini",
                "fullFunctionNames": ["Vibrate1", "Vibrate2"]
              }
            }
          }
        }
        """

    use handler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            response,
            fun request body ->
                capturedUrl <- request.RequestUri.ToString()
                capturedPlatform <-
                    if request.Headers.Contains(Constants.Lovense.PlatformHeader) then
                        request.Headers.GetValues(Constants.Lovense.PlatformHeader) |> Seq.head
                    else
                        ""
                capturedBody <- body
        )

    use http = new HttpClient(handler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "local-api-logs"))
    let localConfig =
        {
            EnableGetToys = true
            TimeoutMs = 3000
            AllowSelfSignedCertificate = true
            HeaderPlatform = "LoL Lovense Bridge"
        }

    let deviceInfo =
        {
            ToyList = []
            SupportedFunctions = None
            CapabilityProfiles = []
            Domain = Some "127-0-0-1.lovense.club"
            HttpsPort = Some 34567
            HttpPort = None
            WssPort = None
        }

    let result =
        LocalApi.getToysAsync http logger localConfig deviceInfo CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error error ->
        failwithf "GetToys failed: %A" error
    | Ok parsed ->
        let profile = Assert.Single(parsed.CapabilityProfiles)
        Assert.Equal("https://127-0-0-1.lovense.club:34567/command", capturedUrl)
        Assert.Equal("LoL Lovense Bridge", capturedPlatform)
        Assert.Equal("""{"command":"GetToys"}""", capturedBody)
        Assert.True(profile.StereoVibrationSupported)

[<Fact>]
let ``lovense toy capability inference detects stereo gemini and single ferri`` () =
    let gemini =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "gemini"
                Name = Some "Gemini"
                ToyType = Some "gemini"
                Nickname = None
                Battery = Some 90
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let ferri =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "ferri"
                Name = Some "Ferri"
                ToyType = Some "ferri"
                Nickname = None
                Battery = Some 90
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    Assert.True(gemini.StereoVibrationSupported)
    Assert.True(gemini.SupportedFunctions.Contains(Constants.Lovense.Vibrate1Action))
    Assert.True(gemini.SupportedFunctions.Contains(Constants.Lovense.Vibrate2Action))
    Assert.False(ferri.StereoVibrationSupported)
    Assert.True(ferri.SupportedFunctions.Contains(Constants.Lovense.VibrateAction))
    Assert.False(ferri.SupportedFunctions.Contains(Constants.Lovense.RotateAction))

[<Fact>]
let ``lovense toy capability inference supports nora rotation`` () =
    let nora =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "nora"
                Name = Some "Nora"
                ToyType = Some "nora"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    Assert.True(nora.SupportedFunctions.Contains(Constants.Lovense.VibrateAction))
    Assert.True(nora.SupportedFunctions.Contains(Constants.Lovense.RotateAction))
    Assert.False(nora.StereoVibrationSupported)

[<Fact>]
let ``missing lovense developer credentials return result error`` () =
    let developer =
        {
            Token = None
            UserId = None
            UserName = None
            UserToken = None
        }

    use http = new HttpClient()
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "logs"))

    let result =
        Auth.requestAuthTokenAsync http logger developer CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error(MissingDeveloperCredentials missingFields) ->
        Assert.Contains("Lovense.Developer.Token", missingFields)
        Assert.Contains("Lovense.Developer.UserId", missingFields)
    | other ->
        failwithf "Expected missing credentials error, got %A" other

[<Fact>]
let ``lovense live socket api e2e can fetch device info and send guarded command`` () =
    let enabled =
        String.Equals(Environment.GetEnvironmentVariable("RUN_LOVENSE_E2E"), "true", StringComparison.OrdinalIgnoreCase)
        && String.Equals(Environment.GetEnvironmentVariable("LOVENSE_E2E_SEND_COMMAND"), "true", StringComparison.OrdinalIgnoreCase)

    if enabled then
        let localConfigPath =
            Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "LoLovenseRainbowBridge", "appsettings.Local.json"))

        Assert.True(File.Exists localConfigPath, "Opted into Lovense E2E, but appsettings.Local.json was not found.")

        let localRoot = JsonNode.Parse(File.ReadAllText localConfigPath)
        let lovenseRoot = localRoot["Lovense"]
        let developerRoot = lovenseRoot["Developer"]

        let localLovenseConfig =
            {
                lovenseConfig with
                    Platform = lovenseRoot["Platform"].GetValue<string>()
                    DryRun = false
                    Developer =
                        {
                            Token = Some(developerRoot["Token"].GetValue<string>())
                            UserId = Some(developerRoot["UserId"].GetValue<string>())
                            UserName =
                                if isNull developerRoot["UserName"] then None else Some(developerRoot["UserName"].GetValue<string>())
                            UserToken =
                                if isNull developerRoot["UserToken"] then None else Some(developerRoot["UserToken"].GetValue<string>())
                        }
            }

        use http = Shared.insecureHttpClient ()
        use logger = new StructuredSessionLogger({ loggingConfig (tempPath "lovense-e2e-logs") with LogRawLovense = true })
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds(60.0))

        let authToken =
            match Auth.requestAuthTokenAsync http logger localLovenseConfig.Developer cts.Token |> fun task -> task.GetAwaiter().GetResult() with
            | Ok token -> token
            | Error error -> failwithf "Lovense getToken failed: %A" error

        match SocketUrl.requestSocketUrlAsync http logger localLovenseConfig.Platform authToken cts.Token |> fun task -> task.GetAwaiter().GetResult() with
        | Ok info ->
            Assert.False(String.IsNullOrWhiteSpace info.SocketIoUrl)
            Assert.False(String.IsNullOrWhiteSpace info.SocketIoPath)
        | Error error ->
            failwithf "Lovense getSocketUrl failed: %A" error

        use client = new LovenseClient(localLovenseConfig, scoringConfig, logger)

        try
            match client.EnsureConnectedAsync(cts.Token) |> fun task -> task.GetAwaiter().GetResult() with
            | Ok state -> Assert.True(state.Connected)
            | Error error -> failwithf "Lovense socket connection failed: %A" error

            let deadline = DateTimeOffset.UtcNow.AddSeconds(30.0)
            while client.LatestDeviceInfo.IsNone && DateTimeOffset.UtcNow < deadline do
                Thread.Sleep(250)

            Assert.True(client.LatestDeviceInfo.IsSome, "Lovense device info event was not received.")
            Assert.NotEmpty(client.LatestDeviceInfo.Value.ToyList)

            match client.SendVibrateAsync(10, cts.Token) |> fun task -> task.GetAwaiter().GetResult() with
            | Ok result -> Assert.False(result.DryRun)
            | Error error -> failwithf "Lovense vibrate command failed: %A" error
        finally
            client.SendVibrateAsync(0, CancellationToken.None)
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

            client.DisconnectAsync(CancellationToken.None)
            |> fun task -> task.GetAwaiter().GetResult()
            |> ignore

[<Fact>]
let ``live health multiplier interpolates for arbitrary HP`` () =
    Assert.Equal(0.5, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.0), 6)
    Assert.Equal(0.625, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.25), 6)
    Assert.Equal(0.75, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.5), 6)
    Assert.Equal(0.875, LiveHealthMultiplierCalculator.compute scoringConfig (Some 0.75), 6)
    Assert.Equal(1.0, LiveHealthMultiplierCalculator.compute scoringConfig (Some 1.0), 6)

[<Fact>]
let ``heartbeat amplitude follows missing health in zero to six range`` () =
    Assert.Equal(0.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude scoringConfig 1.0, 6)
    Assert.Equal(1.5, TemporaryPulseCalculator.HeartbeatCalculator.amplitude scoringConfig 0.75, 6)
    Assert.Equal(3.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude scoringConfig 0.5, 6)
    Assert.Equal(4.5, TemporaryPulseCalculator.HeartbeatCalculator.amplitude scoringConfig 0.25, 6)
    Assert.Equal(6.0, TemporaryPulseCalculator.HeartbeatCalculator.amplitude scoringConfig 0.0, 6)

[<Fact>]
let ``heartbeat pulse shape stays near zero most of cycle and peaks briefly`` () =
    let quiet = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape scoringConfig 1000.10
    let rising = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape scoringConfig 1000.75
    let peak = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape scoringConfig 1000.78
    let falling = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape scoringConfig 1000.90
    let ended = TemporaryPulseCalculator.HeartbeatCalculator.pulseShape scoringConfig 1000.99

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
let ``capability resolver splits vibrate into stereo channels for gemini`` () =
    let plan = Mapping.simpleVibratePlan lovenseConfig 12
    let gemini =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "gemini"
                Name = Some "Gemini"
                ToyType = Some "gemini"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let resolution = CapabilityResolver.resolve lovenseConfig [ gemini ] None plan

    Assert.True(resolution.StereoApplied)
    Assert.Equal("Vibrate1:12,Vibrate2:12", LovenseActionCodec.planActionString resolution.Plan)

[<Fact>]
let ``capability resolver keeps ferri single vibrate and drops rotate`` () =
    let plan =
        {
            Actions =
                [
                    { Function = Vibrate; Value = 12; MaxValue = 20; RangeStart = None }
                    { Function = Rotate; Value = 7; MaxValue = 20; RangeStart = None }
                ]
            Reasons = [ BasePerformance ]
            TimeSec = 2.0
            StopPrevious = true
            ToyId = None
        }

    let ferri =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "ferri"
                Name = Some "Ferri"
                ToyType = Some "ferri"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let resolution = CapabilityResolver.resolve lovenseConfig [ ferri ] None plan

    Assert.False(resolution.StereoApplied)
    Assert.Contains("Rotate:7", resolution.DroppedActions)
    Assert.Equal("Vibrate:12", LovenseActionCodec.planActionString resolution.Plan)

[<Fact>]
let ``stereo split maps left center and right position weights`` () =
    Assert.Equal((10, 5), CapabilityResolver.stereoWeightsFromNormalizedX 10 0.0)
    Assert.Equal((10, 10), CapabilityResolver.stereoWeightsFromNormalizedX 10 0.5)
    Assert.Equal((5, 10), CapabilityResolver.stereoWeightsFromNormalizedX 10 1.0)

[<Fact>]
let ``stereo fallback collapses dual channels to vibrate`` () =
    let stereoPlan =
        {
            Actions =
                [
                    { Function = Vibrate1; Value = 8; MaxValue = 20; RangeStart = None }
                    { Function = Vibrate2; Value = 14; MaxValue = 20; RangeStart = None }
                ]
            Reasons = [ BasePerformance ]
            TimeSec = 2.0
            StopPrevious = true
            ToyId = None
        }

    let disabledConfig =
        {
            lovenseConfig with
                Mapping =
                    {
                        lovenseConfig.Mapping with
                            StereoMode = "Disabled"
                            StereoFallback = "Average"
                    }
        }

    let ferri =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "ferri"
                Name = Some "Ferri"
                ToyType = Some "ferri"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let resolution = CapabilityResolver.resolve disabledConfig [ ferri ] None stereoPlan

    Assert.True(resolution.StereoFallbackApplied)
    Assert.Equal("Vibrate:11", LovenseActionCodec.planActionString resolution.Plan)

[<Fact>]
let ``stereo force mode emits dual channels without device info`` () =
    let forcedConfig =
        {
            lovenseConfig with
                Mapping =
                    {
                        lovenseConfig.Mapping with
                            StereoMode = "Force"
                    }
        }

    let resolution = CapabilityResolver.resolve forcedConfig [] None (Mapping.simpleVibratePlan forcedConfig 9)

    Assert.True(resolution.StereoApplied)
    Assert.Equal("Vibrate1:9,Vibrate2:9", LovenseActionCodec.planActionString resolution.Plan)

[<Fact>]
let ``capability resolver honors configured toy id`` () =
    let plan =
        {
            Actions =
                [
                    { Function = Vibrate; Value = 12; MaxValue = 20; RangeStart = None }
                    { Function = Rotate; Value = 7; MaxValue = 20; RangeStart = None }
                ]
            Reasons = [ BasePerformance ]
            TimeSec = 2.0
            StopPrevious = true
            ToyId = None
        }

    let config =
        {
            lovenseConfig with
                ToyId = Some "ferri"
        }

    let nora =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "nora"
                Name = Some "Nora"
                ToyType = Some "nora"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let ferri =
        DeviceInfo.inferToyCapabilityProfile
            {
                Id = Some "ferri"
                Name = Some "Ferri"
                ToyType = Some "ferri"
                Nickname = None
                Battery = None
                Connected = Some true
                ExplicitFunctions = Set.empty
            }

    let resolution = CapabilityResolver.resolve config [ nora; ferri ] None plan

    Assert.Contains("Rotate:7", resolution.DroppedActions)
    Assert.Equal("Vibrate:12", LovenseActionCodec.planActionString resolution.Plan)

[<Fact>]
let ``lovense function ranges clamp protocol values`` () =
    Assert.Equal(20, LovenseFunctionRanges.clamp Vibrate 99)
    Assert.Equal(3, LovenseFunctionRanges.clamp Pump 99)
    Assert.Equal(100, LovenseFunctionRanges.clamp Stroke 999)
    Assert.Equal(0, LovenseFunctionRanges.clamp Rotate -4)

[<Fact>]
let ``rule command builder tracks max base and clamps to incarnation floor`` () =
    let breakdown baseIntensity =
        {
            PerformanceScore = 0.0
            NormalizedScore = 0.0
            MultikillBase = 0.0
            DeathPenalty = 0
            RawBaseValue = float baseIntensity
            LiveHealthPercent = Some 1.0
            LiveHealthMultiplier = 1.0
            HealthPressureMultiplier = 1.0
            HealthAdjustedBaseValue = float baseIntensity
            BaseIntensity = baseIntensity
            TemporaryBoost = 0
            TemporaryEffects = []
            RawFinalValue = float baseIntensity
            Intensity = baseIntensity
        }

    let builder = LovenseCommandBuilder(ruleEngineLovenseConfig, LovenseRuleInterpreter()) :> ILovenseCommandBuilder
    let build baseIntensity =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Breakdown = breakdown baseIntensity
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
            }

    let first = build 18
    let second = build 4

    Assert.Equal(18.0, first.BuilderState.MaxBaseThisIncarnation)
    Assert.Equal(9.0, second.BuilderState.MinBaseThisIncarnation)
    let secondState = LovenseActionCodec.stateFromActions second.Plan.Actions
    Assert.Equal(9, secondState["Vibrate"])

[<Fact>]
let ``rule command builder applies minimap stereo position modulation`` () =
    let breakdown =
        {
            PerformanceScore = 0.0
            NormalizedScore = 0.0
            MultikillBase = 0.0
            DeathPenalty = 0
            RawBaseValue = 10.0
            LiveHealthPercent = Some 1.0
            LiveHealthMultiplier = 1.0
            HealthPressureMultiplier = 1.0
            HealthAdjustedBaseValue = 10.0
            BaseIntensity = 10
            TemporaryBoost = 0
            TemporaryEffects = []
            RawFinalValue = 10.0
            Intensity = 10
        }

    let builder = LovenseCommandBuilder(ruleEngineLovenseConfig, LovenseRuleInterpreter()) :> ILovenseCommandBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Breakdown = breakdown
                Position =
                    Some
                        {
                            NormalizedX = 0.1
                            NormalizedY = 0.1
                            Confidence = 0.9
                            Quadrant = "TopLeft"
                            Zone = "TopLane"
                            DetectionMethod = "test"
                        }
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
            }

    let state = LovenseActionCodec.stateFromActions frame.Plan.Actions

    Assert.Equal(10, state["Vibrate"])
    Assert.Equal(14, state["Vibrate1"])
    Assert.Equal(4, state["Vibrate2"])

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
    Assert.True(config.Lovense.LocalApi.EnableGetToys)
    Assert.NotEmpty(config.Lovense.Mapping.FunctionProfiles)
    Assert.NotEmpty(config.Lovense.Mapping.Rules)

[<Fact>]
let ``lovense action string parses to normalized function state`` () =
    let state = LovenseActionCodec.stateFromActionString "Vibrate:10,Vibrate1:12,Vibrate2:14,Rotate:7,Pump:3,Stroke:0-80"

    Assert.Equal(10, state["Vibrate"])
    Assert.Equal(12, state["Vibrate1"])
    Assert.Equal(14, state["Vibrate2"])
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
    let action = LovenseActionCodec.planActionString plan
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
    let action = LovenseActionCodec.planActionString plan

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
