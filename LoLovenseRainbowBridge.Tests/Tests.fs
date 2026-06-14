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
open LoLovenseRainbowBridge.App
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
        TransportMode = "Auto"
        Platform = "tests"
        Developer =
            {
                Token = None
                UserId = None
                UserName = None
                UserToken = None
            }
        StandardApi =
            {
                Enable = false
                CallbackListenUrl = "http://localhost:17878/lovense/callback/"
                PublicCallbackUrl = None
                GenerateQrOnStartup = false
                UseServerCommandFallback = true
                PairingQrExpiresHours = 4.0
            }
        LocalApi =
            {
                EnableGetToys = true
                EnableCommandFallback = true
                Domain = Some "127.0.0.1"
                HttpsPort = Some 30010
                HttpPort = Some 20010
                TimeoutMs = 3000
                AllowSelfSignedCertificate = true
                HeaderPlatform = "GameRender"
                CapabilityRefreshIntervalSec = 60
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

let functionProfile name enabled =
    {
        FunctionName = name
        Enabled = enabled
        MinOutput = 0
        MaxOutput = if name = "Pump" || name = "Depth" then 3 elif name = "Stroke" then 100 else 20
        BaseWeight = 1.0
        TimedWeight = 1.0
        EffectWeight = 1.0
        Curve = "Linear"
        Smoothing = 0.0
    }

let functionRule name kind trigger condition targetFunctions layer operation expression =
    {
        Name = name
        Kind = kind
        Enabled = true
        Trigger = trigger
        Condition = condition
        TargetFunctions = targetFunctions
        StateSlot = ""
        Layer = layer
        Operation = operation
        Expression = expression
        DurationSec = 0.0
    }

let stateRule name kind trigger condition stateSlot operation expression =
    {
        Name = name
        Kind = kind
        Enabled = true
        Trigger = trigger
        Condition = condition
        TargetFunctions = ""
        StateSlot = stateSlot
        Layer = "State"
        Operation = operation
        Expression = expression
        DurationSec = 0.0
    }

let ruleEngineLovenseConfig =
    {
        lovenseConfig with
            Mapping =
                {
                    lovenseConfig.Mapping with
                        FunctionProfiles =
                            [
                                functionProfile "Vibrate" true
                                functionProfile "Vibrate1" true
                                functionProfile "Vibrate2" true
                                functionProfile "All" true
                            ]
                        Rules =
                            [
                                functionRule "base-vibrate" "BaseModifier" "" "" "Vibrate" "Base" "Set" "Kills"
                                functionRule "base-vibrate1" "BaseModifier" "" "" "Vibrate1" "Base" "Set" "Kills * PositionLeftWeight"
                                functionRule "base-vibrate2" "BaseModifier" "" "" "Vibrate2" "Base" "Set" "Kills * PositionRightWeight"
                                stateRule "track-max" "ThresholdModifier" "" "" "MaxBaseThisIncarnation" "TrackMax" "FunctionBase_Vibrate"
                                stateRule "floor" "ThresholdModifier" "" "" "MinBaseThisIncarnation" "TrackMax" "MaxBaseThisIncarnation * 0.5"
                                functionRule "clamp-floor" "ThresholdModifier" "" "" "Vibrate" "Base" "ClampMin" "MinBaseThisIncarnation"
                                functionRule "multikill-growth" "BaseModifier" "ActiveMultikill" "" "Vibrate" "Base" "Add" "MultikillCount^2 - (MultikillCount - 1)^2"
                                functionRule "kill-all" "TimedContribution" "ActiveKill" "" "All" "Timed" "Add" "ActiveKillCount"
                            ]
                }
    }

let ruleInterpreter () =
    LovenseRuleInterpreter(RuleInputBuilder(scoringConfig), RuleExpressionEvaluator())

let emptyBuilderState =
    {
        CurrentIncarnationId = 1
        PreviousIncarnationBase = 0.0
        CurrentBase = 0.0
        MaxBaseThisIncarnation = 0.0
        MinBaseThisIncarnation = 0.0
        Variables = Map.empty
        LastFunctionState = LovenseActionCodec.emptyState
        LastActionString = None
    }

let defaultRuntimeRuleContext =
    {
        LolDataAcquired = true
        OcrDataAcquired = true
        LovenseDataAcquired = true
        LolUnavailableElapsedMs = 0L
        OcrUnavailableElapsedMs = 0L
        LovenseUnavailableElapsedMs = 0L
        LolFailureAttemptsSinceSuccess = 0
        OcrFailureAttemptsSinceSuccess = 0
        LovenseFailureAttemptsSinceSuccess = 0
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
let ``lol unavailable plan uses discrete 10 or 15 fallback vibration`` () =
    Assert.Equal(10, Mapping.lolNotRunningIntensity 0L)
    Assert.Equal(15, Mapping.lolNotRunningIntensity 1000L)
    Assert.Equal(10, Mapping.lolNotRunningIntensity 40000L)

[<Fact>]
let ``lol unavailable plan uses configured command timing and source reason`` () =
    let plan = Mapping.lolNotRunningPlan lovenseConfig 1000L

    Assert.Equal(lovenseConfig.CommandTimeSec, plan.TimeSec)
    Assert.Equal(lovenseConfig.Mapping.DefaultStopPrevious, plan.StopPrevious)
    Assert.Equal(SourceNotConnected, Assert.Single(plan.Reasons))
    Assert.Equal("Vibrate:15", LovenseActionCodec.planActionString plan)
    Assert.Equal("SourceNotConnected", LovenseActionCodec.reasonToString SourceNotConnected)

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
          "type": "OK",
          "code": 200,
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
          "type": "OK",
          "code": 200,
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
            EnableCommandFallback = true
            Domain = Some "127.0.0.1"
            HttpsPort = Some 30010
            HttpPort = Some 20010
            TimeoutMs = 3000
            AllowSelfSignedCertificate = true
            HeaderPlatform = "GameRender"
            CapabilityRefreshIntervalSec = 60
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
        Assert.Equal("GameRender", capturedPlatform)
        Assert.Equal("""{"command":"GetToys"}""", capturedBody)
        Assert.True(profile.StereoVibrationSupported)

[<Fact>]
let ``lovense local get toys falls back from https to http endpoint`` () =
    let requestedUrls = ResizeArray<string>()

    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) =
                task {
                    requestedUrls.Add(request.RequestUri.ToString())

                    let! _ =
                        if isNull request.Content then
                            Task.FromResult("")
                        else
                            request.Content.ReadAsStringAsync(ct)

                    if String.Equals(request.RequestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) then
                        return
                            new HttpResponseMessage(
                                HttpStatusCode.BadGateway,
                                Content = new StringContent("""{"code":500,"message":"https unavailable"}""")
                            )
                    else
                        return
                            new HttpResponseMessage(
                                HttpStatusCode.OK,
                                Content = new StringContent("""{"code":200,"type":"OK","data":{"toys":"{\"toy-a\":{\"id\":\"toy-a\",\"name\":\"Ferri\",\"status\":1,\"battery\":90,\"fullFunctionNames\":[\"Vibrate\"]}}"}}""")
                            )
                } }

    use http = new HttpClient(handler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "local-api-http-fallback-logs"))
    let localConfig =
        {
            EnableGetToys = true
            EnableCommandFallback = true
            Domain = Some "192.168.0.110"
            HttpsPort = Some 30010
            HttpPort = Some 20010
            TimeoutMs = 3000
            AllowSelfSignedCertificate = true
            HeaderPlatform = "GameRender"
            CapabilityRefreshIntervalSec = 60
        }

    let result =
        LocalApi.getConfiguredToysAsync http logger localConfig CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error error ->
        failwithf "Expected HTTP fallback GetToys to succeed, got %A" error
    | Ok parsed ->
        Assert.Equal(2, requestedUrls.Count)
        Assert.Equal("https://192-168-0-110.lovense.club:30010/command", requestedUrls[0])
        Assert.Equal("http://192.168.0.110:20010/command", requestedUrls[1])
        let profile = Assert.Single(parsed.CapabilityProfiles)
        Assert.Contains(Constants.Lovense.VibrateAction, profile.SupportedFunctions)

[<Fact>]
let ``lovense local get toys treats endpoint timeout as fallback error`` () =
    let requestedUrls = ResizeArray<string>()

    use handler =
        { new HttpMessageHandler() with
            override _.SendAsync(request: HttpRequestMessage, ct: CancellationToken) =
                task {
                    requestedUrls.Add(request.RequestUri.ToString())

                    if String.Equals(request.RequestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) then
                        return raise (OperationCanceledException())
                    else
                        return
                            new HttpResponseMessage(
                                HttpStatusCode.OK,
                                Content = new StringContent("""{"code":200,"type":"OK","data":{"toys":"{\"toy-a\":{\"id\":\"toy-a\",\"name\":\"Ferri\",\"fullFunctionNames\":[\"Vibrate\"]}}"}}""")
                            )
                } }

    use http = new HttpClient(handler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "local-api-timeout-fallback-logs"))
    let localConfig =
        {
            EnableGetToys = true
            EnableCommandFallback = true
            Domain = Some "192.168.0.110"
            HttpsPort = Some 30010
            HttpPort = Some 20010
            TimeoutMs = 3000
            AllowSelfSignedCertificate = true
            HeaderPlatform = "GameRender"
            CapabilityRefreshIntervalSec = 60
        }

    let result =
        LocalApi.getConfiguredToysAsync http logger localConfig CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error error ->
        failwithf "Expected HTTP fallback after timeout to succeed, got %A" error
    | Ok parsed ->
        Assert.Equal(2, requestedUrls.Count)
        Assert.Equal("https://192-168-0-110.lovense.club:30010/command", requestedUrls[0])
        Assert.Equal("http://192.168.0.110:20010/command", requestedUrls[1])
        Assert.Single(parsed.CapabilityProfiles) |> ignore

[<Fact>]
let ``lovense local command fallback posts final function request`` () =
    let mutable capturedUrl = ""
    let mutable capturedPlatform = ""
    let mutable capturedBody = ""

    use handler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"code":200,"type":"OK","data":{}}""",
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
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "local-command-logs"))
    let localConfig =
        {
            EnableGetToys = true
            EnableCommandFallback = true
            Domain = Some "127.0.0.1"
            HttpsPort = Some 30010
            HttpPort = Some 20010
            TimeoutMs = 3000
            AllowSelfSignedCertificate = true
            HeaderPlatform = "GameRender"
            CapabilityRefreshIntervalSec = 60
        }

    let plan = Mapping.simpleVibratePlan lovenseConfig 16
    let result =
        LocalApi.sendCommandAsync http logger localConfig None plan "corr-local" CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error error ->
        failwithf "Local command failed: %A" error
    | Ok response ->
        Assert.Equal(200, response.StatusCode)
        Assert.Equal("https://127-0-0-1.lovense.club:30010/command", capturedUrl)
        Assert.Equal("GameRender", capturedPlatform)
        Assert.Contains("\"command\":\"Function\"", capturedBody)
        Assert.Contains("\"action\":\"Vibrate:16\"", capturedBody)
        Assert.Contains("\"timeSec\":2", capturedBody)

[<Fact>]
let ``lovense auto command falls back instead of cold not connected`` () =
    let mutable capturedLocalBody = ""

    use apiHandler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"code":0,"message":"Success","data":{}}""",
            fun _ _ -> ()
        )

    use localHandler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"code":200,"type":"OK","data":{}}""",
            fun _ body -> capturedLocalBody <- body
        )

    use http = new HttpClient(apiHandler)
    use localHttp = new HttpClient(localHandler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "auto-fallback-logs"))
    use connectGate = new SemaphoreSlim(1, 1)

    let config =
        {
            lovenseConfig with
                DryRun = false
                TransportMode = "Auto"
                StandardApi = { lovenseConfig.StandardApi with Enable = false; UseServerCommandFallback = false }
                LocalApi = { lovenseConfig.LocalApi with EnableGetToys = false; EnableCommandFallback = true }
                Developer = { Token = None; UserId = None; UserName = None; UserToken = None }
        }

    let result, _, _, _ =
        ClientCommand.sendCommandPlanAsync
            http
            localHttp
            logger
            config
            scoringConfig
            {
                Socket = None
                SocketInfo = None
                StandardQrCode = None
                QrCodeLogged = false
                SupportedFunctions = None
                CapabilityProfiles = []
                GeneratedAuthToken = None
                LatestDeviceInfo = None
            }
            None
            DateTimeOffset.MinValue
            connectGate
            ignore
            ignore
            (Mapping.simpleVibratePlan config 8)
            8
            []
            CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Ok response ->
        Assert.False(response.SocketConnected)
        Assert.Contains("\"action\":\"Vibrate:8\"", capturedLocalBody)
    | Error error ->
        failwithf "Expected Auto local fallback to succeed, got %A" error

[<Fact>]
let ``lovense standard qr request contains developer fields and redacts token`` () =
    let developer =
        {
            Token = Some "dev-secret"
            UserId = Some "uid-1"
            UserName = Some "sophie"
            UserToken = Some "user-token"
        }

    let body =
        match StandardApi.buildQrCodeRequestBody developer with
        | Ok body -> body
        | Error error -> failwithf "Unexpected QR body error: %A" error

    let redacted =
        match StandardApi.buildRedactedQrCodeRequestBody developer with
        | Ok body -> body
        | Error error -> failwithf "Unexpected redacted QR body error: %A" error

    Assert.Contains("\"token\":\"dev-secret\"", body)
    Assert.Contains("\"uid\":\"uid-1\"", body)
    Assert.Contains("\"uname\":\"sophie\"", body)
    Assert.Contains("\"utoken\":\"user-token\"", body)
    Assert.Contains("\"v\":2", body)
    Assert.DoesNotContain("dev-secret", redacted)
    Assert.DoesNotContain("user-token", redacted)

[<Fact>]
let ``lovense standard callback parser extracts local endpoint and toys`` () =
    let raw =
        """{"uid":"uid-1","utoken":"u-token","domain":"192-168-1-44.lovense.club","httpsPort":"34568","httpPort":"34567","wssPort":"34568","toys":{"toy-a":{"id":"toy-a","name":"gemini","status":1,"battery":88,"fullFunctionNames":["Vibrate1","Vibrate2"]}}}"""

    let developer =
        {
            Token = Some "dev-token"
            UserId = Some "uid-1"
            UserName = Some "sophie"
            UserToken = Some "u-token"
        }

    match StandardApi.validateCallback developer raw with
    | Error error -> failwithf "Callback rejected unexpectedly: %s" error
    | Ok deviceInfo ->
        Assert.Equal(Some "192-168-1-44.lovense.club", deviceInfo.Domain)
        Assert.Equal(Some 34568, deviceInfo.HttpsPort)
        Assert.Equal(Some 34567, deviceInfo.HttpPort)
        let profile = Assert.Single(deviceInfo.CapabilityProfiles)
        Assert.True(profile.StereoVibrationSupported)
        Assert.Contains(Constants.Lovense.Vibrate1Action, profile.SupportedFunctions)
        Assert.Contains(Constants.Lovense.Vibrate2Action, profile.SupportedFunctions)

[<Fact>]
let ``lovense standard callback listener forwards device info to owner callback`` () =
    let listener = new System.Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()

    let mutable received: LovenseDeviceInfo option = None
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "standard-callback-listener-logs"))
    let callbackUrl = $"http://localhost:{port}/lovense/callback/"
    let server =
        StandardApi.startCallbackListener
            logger
            { lovenseConfig.StandardApi with Enable = true; CallbackListenUrl = callbackUrl; GenerateQrOnStartup = false }
            { Token = Some "dev-token"; UserId = Some "uid-1"; UserName = None; UserToken = Some "u-token" }
            (fun deviceInfo -> received <- Some deviceInfo)

    match server with
    | None -> failwith "Callback listener did not start."
    | Some server ->
        try
            use http = new HttpClient()
            let body =
                """{"uid":"uid-1","utoken":"u-token","domain":"127-0-0-1.lovense.club","httpsPort":30010,"toys":{"toy-a":{"id":"toy-a","name":"Gemini","fullFunctionNames":["Vibrate1","Vibrate2"]}}}"""
            let response =
                http.PostAsync(callbackUrl, new StringContent(body, Text.Encoding.UTF8, Constants.Lovense.JsonMediaType))
                |> fun task -> task.GetAwaiter().GetResult()

            Assert.Equal(HttpStatusCode.OK, response.StatusCode)

            let deadline = DateTimeOffset.UtcNow.AddSeconds(3.0)
            while received.IsNone && DateTimeOffset.UtcNow < deadline do
                Thread.Sleep(25)

            Assert.True(received.IsSome, "Callback listener did not forward device info.")
            Assert.Equal(Some "127-0-0-1.lovense.club", received.Value.Domain)
            Assert.True((Assert.Single(received.Value.CapabilityProfiles)).StereoVibrationSupported)
        finally
            server.Stop()

[<Fact>]
let ``lovense standard callback rejects unexpected uid`` () =
    let developer =
        {
            Token = Some "dev-token"
            UserId = Some "expected"
            UserName = None
            UserToken = None
        }

    match StandardApi.validateCallback developer """{"uid":"actual","toys":{}}""" with
    | Error error -> Assert.Contains("Unexpected uid", error)
    | Ok _ -> failwith "Callback should have been rejected."

[<Fact>]
let ``lovense standard server command redacts developer token and posts function request`` () =
    let mutable capturedUrl = ""
    let mutable capturedBody = ""

    use handler =
        new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """{"code":0,"message":"Success","data":{}}""",
            fun request body ->
                capturedUrl <- request.RequestUri.ToString()
                capturedBody <- body
        )

    use http = new HttpClient(handler)
    use logger = new StructuredSessionLogger(loggingConfig (tempPath "standard-server-command-logs"))
    let developer =
        {
            Token = Some "dev-secret"
            UserId = Some "uid-1"
            UserName = None
            UserToken = None
        }

    let plan = Mapping.simpleVibratePlan lovenseConfig 10
    let result =
        StandardApi.sendServerCommandAsync http logger developer plan "corr-standard" CancellationToken.None
        |> fun task -> task.GetAwaiter().GetResult()

    match result with
    | Error error -> failwithf "Standard server command failed: %A" error
    | Ok response ->
        Assert.Equal(200, response.StatusCode)
        Assert.Equal(Constants.Lovense.StandardServerCommand, capturedUrl)
        Assert.Contains("\"token\":\"dev-secret\"", capturedBody)
        Assert.Contains("\"uid\":\"uid-1\"", capturedBody)
        Assert.Contains("\"command\":\"Function\"", capturedBody)
        Assert.Contains("\"action\":\"Vibrate:10\"", capturedBody)

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
let ``rule input builder exposes live health and heartbeat variables`` () =
    let input =
        {
            PreviousState = initialState
            Snapshot = snapshotAt 1000.78 (Some(100.0, 1000.0)) []
            EvolvedState = initialState
            Position = None
            Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
            LoopIteration = 1L
            LastSentFunctionState = LovenseActionCodec.emptyState
            RuntimeContext = defaultRuntimeRuleContext
            RuntimePollMs = 250
        }

    let variables = (RuleInputBuilder(scoringConfig) :> IRuleInputBuilder).Build emptyBuilderState input Map.empty

    Assert.Equal(0.1, variables["HealthPercent"], 6)
    Assert.Equal(0.55, variables["LiveHealthMultiplier"], 6)
    Assert.Equal(1.0, variables["LoopIteration"], 6)
    Assert.Equal(0.25, variables["LoopTimeSec"], 6)
    Assert.Equal(5.4, variables["HeartbeatAmplitude"], 6)
    Assert.False(variables.ContainsKey("HeartbeatPulseValue"))

[<Fact>]
let ``ncalc expression evaluator reads state variables`` () =
    let evaluator = RuleExpressionEvaluator() :> IRuleExpressionEvaluator

    match evaluator.Evaluate "MultikillCount^2 - (MultikillCount - 1)^2" (Map.ofList [ "MultikillCount", 3.0 ]) with
    | Ok value -> Assert.Equal(5.0, value, 6)
    | Error error -> failwithf "Expected expression success, got %s" error

[<Fact>]
let ``ncalc expression evaluator supports trigonometric loop expressions`` () =
    let evaluator = RuleExpressionEvaluator() :> IRuleExpressionEvaluator
    let variables =
        Map.ofList
            [
                "LoopTimeSec", 0.25
                "HeartbeatPulseCycleSec", 1.0
                "Pi", Math.PI
                "HeartbeatAmplitude", 6.0
            ]

    match evaluator.Evaluate "HeartbeatAmplitude * Pow(Max(0, Sin((LoopTimeSec / HeartbeatPulseCycleSec) * 2 * Pi)), 8)" variables with
    | Ok value -> Assert.Equal(6.0, value, 6)
    | Error error -> failwithf "Expected trigonometric expression success, got %s" error

[<Fact>]
let ``rule command builder applies heartbeat as effect without mutating base`` () =
    let heartbeatConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            Rules =
                                [
                                    functionRule "base" "BaseModifier" "" "" "Vibrate" "Base" "Set" "10"
                                    functionRule "heartbeat" "Effect" "" "HealthPercent <= 0.30" "Vibrate" "Effect" "Add" "HeartbeatAmplitude * Pow(Max(0, Sin((LoopTimeSec / HeartbeatPulseCycleSec) * 2 * Pi)), 8)"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(heartbeatConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshotAt 1000.78 (Some(100.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    let vibrate = frame.FunctionStates[Vibrate]

    Assert.Equal(10.0, vibrate.Base, 6)
    Assert.True(vibrate.Effect > 0.0)
    Assert.True(vibrate.Final > 10)

[<Fact>]
let ``rule condition skips target evaluation and state mutation`` () =
    let conditionalConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            Rules =
                                [
                                    functionRule "base" "BaseModifier" "" "" "Vibrate" "Base" "Set" "10"
                                    functionRule "skipped" "Effect" "" "HealthPercent < 0.50" "Vibrate" "Effect" "Add" "UnknownVariable + 20"
                                    stateRule "skipped-state" "Effect" "" "HealthPercent < 0.50" "MaxBaseThisIncarnation" "Set" "99"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(conditionalConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    let vibrate = frame.FunctionStates[Vibrate]

    Assert.Equal(10.0, vibrate.Base, 6)
    Assert.Equal(0.0, vibrate.Effect, 6)
    Assert.Equal(10, vibrate.Final)
    Assert.Equal(0.0, frame.BuilderState.MaxBaseThisIncarnation, 6)
    Assert.Empty(frame.Diagnostics)

[<Fact>]
let ``rule value aggregation allows negative contributions and clamps final sum`` () =
    let aggregationConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            Rules =
                                [
                                    functionRule "base" "BaseModifier" "" "" "Vibrate" "Base" "Set" "10"
                                    functionRule "negative" "Effect" "" "" "Vibrate" "Effect" "Add" "-15"
                                    functionRule "positive-overflow" "Effect" "" "" "Vibrate1" "Effect" "Add" "FunctionMax_Vibrate1 + 25"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(aggregationConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    Assert.Equal(10.0, frame.FunctionStates[Vibrate].Base, 6)
    Assert.Equal(-15.0, frame.FunctionStates[Vibrate].Effect, 6)
    Assert.Equal(0, frame.FunctionStates[Vibrate].Final)
    Assert.Equal(45.0, frame.FunctionStates[Vibrate1].Effect, 6)
    Assert.Equal(20, frame.FunctionStates[Vibrate1].Final)

[<Fact>]
let ``conditional heartbeat can use function range variables for asymmetric stereo percentage`` () =
    let heartbeatConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            Rules =
                                [
                                    functionRule "heartbeat-left-off" "Effect" "" "HealthPercent < 0.50" "Vibrate1" "Effect" "Set" "0"
                                    functionRule "heartbeat-right-half" "Effect" "" "HealthPercent < 0.50" "Vibrate2" "Effect" "Set" "MaxValue * 0.5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(heartbeatConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(400.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    Assert.Equal(0, frame.FunctionStates[Vibrate1].Final)
    Assert.Equal(10, frame.FunctionStates[Vibrate2].Final)

[<Fact>]
let ``pipe separated target functions use target scoped max value`` () =
    let rangeConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            FunctionProfiles =
                                [
                                    functionProfile "Vibrate" true
                                    functionProfile "Pump" true
                                    functionProfile "Stroke" true
                                ]
                            Rules =
                                [
                                    functionRule "half-range" "Effect" "" "" "Vibrate|Pump|Stroke" "Effect" "Add" "MaxValue * 0.5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(rangeConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    Assert.Equal(10.0, frame.FunctionStates[Vibrate].Effect, 6)
    Assert.Equal(1.5, frame.FunctionStates[Pump].Effect, 6)
    Assert.Equal(50.0, frame.FunctionStates[Stroke].Effect, 6)

[<Fact>]
let ``function rule condition can use target scoped max value`` () =
    let rangeConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            FunctionProfiles =
                                [
                                    functionProfile "Vibrate" true
                                    functionProfile "Pump" true
                                ]
                            Rules =
                                [
                                    functionRule "skip-small-ranges" "Effect" "" "MaxValue > 3" "Vibrate|Pump" "Effect" "Add" "MaxValue * 0.5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(rangeConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    Assert.Equal(10.0, frame.FunctionStates[Vibrate].Effect, 6)
    Assert.Equal(0.0, frame.FunctionStates[Pump].Effect, 6)

[<Fact>]
let ``rule trace records expression and evaluated value separately`` () =
    let traceConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            FunctionProfiles =
                                [
                                    functionProfile "Vibrate" true
                                ]
                            Rules =
                                [
                                    functionRule "trace-rule" "Effect" "" "" "Vibrate" "Effect" "Add" "MaxValue * 0.5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(traceConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    let trace = Assert.Single(frame.RuleTraces)

    Assert.Equal("trace-rule", trace.RuleName)
    Assert.Equal("MaxValue * 0.5", trace.Expression)
    Assert.Equal(10.0, trace.Value, 6)
    Assert.Equal(0.0, trace.BeforeLayerValue, 6)
    Assert.Equal(10.0, trace.AfterLayerValue, 6)
    Assert.Equal(20.0, trace.MaxValue, 6)

[<Fact>]
let ``function max remains available for explicit cross function expressions`` () =
    let rangeConfig =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            Rules =
                                [
                                    functionRule "explicit-range" "Effect" "" "" "Vibrate2" "Effect" "Set" "FunctionMax_Vibrate2 * 0.5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(rangeConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(400.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 250
            }

    Assert.Equal(10, frame.FunctionStates[Vibrate2].Final)

[<Fact>]
let ``multikill expression grows by odd deltas`` () =
    let event streak =
        {
            EventId = streak
            GameTime = 999.0
            ActorName = Some "Active#EUW"
            VictimName = Some "Other#EUW"
            Assisters = []
            Kind = Multikill streak
        }

    let build streak =
        let builder = LovenseCommandValueBuilder(ruleEngineLovenseConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
        builder.Build
            {
                PreviousState = initialState
                Snapshot = { snapshot (Some(1000.0, 1000.0)) [ event streak ] with ActivePlayer = { player (Some(1000.0, 1000.0)) with Kills = 0 } }
                EvolvedState = { initialState with MultikillCount = 1 }
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 500
            }

    Assert.Equal(1.0, (build 1).FunctionStates[Vibrate].Base, 6)
    Assert.Equal(3.0, (build 2).FunctionStates[Vibrate].Base, 6)
    Assert.Equal(5.0, (build 3).FunctionStates[Vibrate].Base, 6)

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
    Assert.DoesNotContain("Rotate", LovenseActionCodec.planActionString resolution.Plan)
    Assert.False(resolution.NoSupportedActions)

[<Fact>]
let ``capability resolver falls back only to supported safe command`` () =
    let plan =
        {
            Actions =
                [
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

    Assert.Contains("Rotate:7", resolution.DroppedActions)
    Assert.Equal("Vibrate:0", LovenseActionCodec.planActionString resolution.Plan)
    Assert.False(resolution.NoSupportedActions)

[<Fact>]
let ``capability resolver reports no supported actions when no fallback is supported`` () =
    let plan =
        {
            Actions =
                [
                    { Function = Rotate; Value = 7; MaxValue = 20; RangeStart = None }
                ]
            Reasons = [ BasePerformance ]
            TimeSec = 2.0
            StopPrevious = true
            ToyId = None
        }

    let suctionOnly =
        {
            ToyId = Some "custom"
            Name = Some "Custom"
            ToyType = Some "custom"
            Nickname = None
            Battery = None
            Connected = Some true
            ExplicitFunctions = set [ "Suction" ]
            InferredFunctions = Set.empty
            SupportedFunctions = set [ "Suction" ]
            StereoVibrationSupported = false
            CapabilitySource = Explicit
            Notes = []
        }

    let resolution = CapabilityResolver.resolve lovenseConfig [ suctionOnly ] None plan

    Assert.Contains("Rotate:7", resolution.DroppedActions)
    Assert.Empty(resolution.Plan.Actions)
    Assert.True(resolution.NoSupportedActions)

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
    let builder = LovenseCommandValueBuilder(ruleEngineLovenseConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let build baseIntensity =
        let baseSnapshot = snapshot (Some(1000.0, 1000.0)) []
        let active = { baseSnapshot.ActivePlayer with Kills = baseIntensity }

        builder.Build
            {
                PreviousState = initialState
                Snapshot = { baseSnapshot with ActivePlayer = active; Players = [ active ] }
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 500
            }

    let first = build 18
    let second = build 4

    Assert.Equal(18.0, first.BuilderState.MaxBaseThisIncarnation)
    Assert.Equal(9.0, second.BuilderState.MinBaseThisIncarnation)
    let secondState = LovenseActionCodec.stateFromActions second.Plan.Actions
    Assert.Equal(9, secondState["Vibrate"])

[<Fact>]
let ``rule command builder applies minimap stereo position modulation`` () =
    let builder = LovenseCommandValueBuilder(ruleEngineLovenseConfig, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let baseSnapshot = snapshot (Some(1000.0, 1000.0)) []
    let active = { baseSnapshot.ActivePlayer with Kills = 10 }
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = { baseSnapshot with ActivePlayer = active; Players = [ active ] }
                EvolvedState = initialState
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
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext = defaultRuntimeRuleContext
                RuntimePollMs = 500
            }

    let state = LovenseActionCodec.stateFromActions frame.Plan.Actions

    Assert.Equal(10, state["Vibrate"])
    Assert.Equal(14, state["Vibrate1"])
    Assert.Equal(4, state["Vibrate2"])

[<Fact>]
let ``default configuration enables position rotation`` () =
    let config = Loader.load ()

    Assert.True(config.PositionBasedRotation.Enable)
    Assert.Equal("Combined", config.PositionBasedRotation.MappingMode)
    Assert.Equal(None, config.PositionBasedRotation.TemplateImagePath)
    Assert.False(config.PositionBasedRotation.DebugMode)
    Assert.True(config.Recording.Enabled)
    Assert.Equal("data/gameplay.sqlite", config.Recording.DatabasePath.Replace('\\', '/'))
    Assert.Equal(100, config.Recording.SliceMs)
    Assert.True(config.Lovense.LocalApi.EnableGetToys)
    Assert.True(config.Lovense.LocalApi.EnableCommandFallback)
    Assert.True(config.Lovense.LocalApi.Domain.IsSome)
    Assert.Equal(Some 30010, config.Lovense.LocalApi.HttpsPort)
    Assert.Equal(Some 20010, config.Lovense.LocalApi.HttpPort)
    Assert.Equal(60, config.Lovense.LocalApi.CapabilityRefreshIntervalSec)
    Assert.Equal("Auto", config.Lovense.TransportMode)
    Assert.True(config.Lovense.StandardApi.Enable)
    Assert.True(config.Lovense.StandardApi.GenerateQrOnStartup)
    Assert.True(config.Lovense.StandardApi.UseServerCommandFallback)
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
let ``rule input builder exposes runtime availability variables`` () =
    let input =
        {
            PreviousState = initialState
            Snapshot = snapshot (Some(1000.0, 1000.0)) []
            EvolvedState = initialState
            Position = None
            Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
            LoopIteration = 1L
            LastSentFunctionState = LovenseActionCodec.emptyState
            RuntimeContext =
                {
                    defaultRuntimeRuleContext with
                        LolDataAcquired = false
                        OcrDataAcquired = false
                        LovenseDataAcquired = false
                        LolUnavailableElapsedMs = 12345L
                        OcrUnavailableElapsedMs = 23456L
                        LovenseUnavailableElapsedMs = 34567L
                        LolFailureAttemptsSinceSuccess = 3
                        OcrFailureAttemptsSinceSuccess = 4
                        LovenseFailureAttemptsSinceSuccess = 5
                }
            RuntimePollMs = 100
        }

    let variables = (RuleInputBuilder(scoringConfig) :> IRuleInputBuilder).Build emptyBuilderState input Map.empty

    Assert.Equal(0.0, variables["LolDataAcquired"])
    Assert.Equal(0.0, variables["OcrDataAcquired"])
    Assert.Equal(0.0, variables["LovenseDataAcquired"])
    Assert.Equal(12345.0, variables["LolUnavailableElapsedMs"])
    Assert.Equal(3.0, variables["LolFailureAttemptsSinceSuccess"])

[<Fact>]
let ``ocr disabled cache state does not increment detection failures`` () =
    let cache = RuntimeState.RuntimeStateCache()

    cache.UpdateOcrDisabled()
    cache.UpdateOcrDisabled()

    let state = cache.Read().Ocr

    Assert.False(state.DataAcquired)
    Assert.Equal(0, state.DetectionFailures)
    Assert.Equal(Some "Position-based rotation is disabled.", state.LastError)

[<Fact>]
let ``lovense command builder creates changed plan only for function diffs`` () =
    let config =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            FunctionProfiles = [ functionProfile "Vibrate" true; functionProfile "Rotate" true ]
                            Rules =
                                [
                                    functionRule "vibrate-set" "BaseModifier" "" "" "Vibrate" "Base" "Set" "10"
                                    functionRule "rotate-set" "BaseModifier" "" "" "Rotate" "Base" "Set" "4"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(config, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let baseInput =
        {
            PreviousState = initialState
            Snapshot = snapshot (Some(1000.0, 1000.0)) []
            EvolvedState = initialState
            Position = None
            Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
            LoopIteration = 1L
            LastSentFunctionState = LovenseActionCodec.emptyState
            RuntimeContext = defaultRuntimeRuleContext
            RuntimePollMs = 100
        }

    let firstFrame = builder.Build baseInput
    Assert.Equal(Some "Vibrate:10,Rotate:4", firstFrame.ChangedActionString)

    let secondFrame =
        builder.Build
            {
                baseInput with
                    LastSentFunctionState = firstFrame.FullFunctionState
            }

    Assert.True(secondFrame.ChangedPlan.IsNone)
    Assert.Empty(secondFrame.ChangedFunctionState)

[<Fact>]
let ``lol unavailable pulse is expressed as configurable rule`` () =
    let config =
        {
            ruleEngineLovenseConfig with
                Mapping =
                    {
                        ruleEngineLovenseConfig.Mapping with
                            FunctionProfiles = [ functionProfile "Vibrate" true ]
                            Rules =
                                [
                                    functionRule
                                        "lol-unavailable-pulse"
                                        "Effect"
                                        ""
                                        "LolDataAcquired == 0"
                                        "Vibrate"
                                        "Effect"
                                        "Set"
                                        "10 + Ceiling(Sin(LolUnavailableElapsedMs / 10000.0)) * 5"
                                ]
                    }
        }

    let builder = LovenseCommandValueBuilder(config, ruleInterpreter()) :> ILovenseCommandValueBuilder
    let frame =
        builder.Build
            {
                PreviousState = initialState
                Snapshot = snapshot (Some(1000.0, 1000.0)) []
                EvolvedState = initialState
                Position = None
                Now = DateTimeOffset.Parse("2026-06-13T10:00:00Z")
                LoopIteration = 1L
                LastSentFunctionState = LovenseActionCodec.emptyState
                RuntimeContext =
                    {
                        defaultRuntimeRuleContext with
                            LolDataAcquired = false
                            LolUnavailableElapsedMs = 20000L
                    }
                RuntimePollMs = 100
            }

    Assert.Equal("Vibrate:15", frame.ActionString)

[<Fact>]
let ``sqlite recorder opens closes and skips unchanged slices`` () =
    let dbPath = tempPath "gameplay.sqlite"
    let logDir = Path.Combine(Path.GetDirectoryName dbPath, "log")
    use logger = new StructuredSessionLogger(loggingConfig logDir)
    let recorder = new GameplayRecorder(recordingConfig dbPath, logger)
    let bridgeSnapshot = snapshot (Some(1000.0, 1000.0)) []
    let breakdown = { emptyBreakdown with Intensity = 10; BaseIntensity = 10; RawFinalValue = 10.0 }
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
let ``duplicate private helper guard detects repeated helper names in sample`` () =
    let duplicatePrivateNames (sources: (string * string) list) =
        let regex = Text.RegularExpressions.Regex(@"let\s+private\s+([A-Za-z_][A-Za-z0-9_]*)\b")

        sources
        |> List.collect (fun (path, text) ->
            regex.Matches(text)
            |> Seq.cast<Text.RegularExpressions.Match>
            |> Seq.map (fun m -> m.Groups[1].Value, path)
            |> Seq.toList)
        |> List.groupBy fst
        |> List.choose (fun (name, matches) ->
            let paths = matches |> List.map snd |> List.distinct
            if paths.Length > 1 then Some(name, paths) else None)

    let duplicates =
        duplicatePrivateNames
            [
                "A.fs", "module A\nlet private sameName value = value\n"
                "B.fs", "module B\nlet private sameName value = value + 1\n"
            ]

    Assert.Contains(duplicates, fun (name, _) -> name = "sameName")

[<Fact>]
let ``source files do not duplicate important private helper names`` () =
    let sourceRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "LoLovenseRainbowBridge"))
    let regex = Text.RegularExpressions.Regex(@"let\s+private\s+([A-Za-z_][A-Za-z0-9_]*)\b")
    let allowedDuplicates = set [ "action" ]

    let duplicates =
        Directory.GetFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
        |> Array.toList
        |> List.collect (fun path ->
            let text = File.ReadAllText(path)

            regex.Matches(text)
            |> Seq.cast<Text.RegularExpressions.Match>
            |> Seq.map (fun m -> m.Groups[1].Value, Path.GetRelativePath(sourceRoot, path))
            |> Seq.toList)
        |> List.groupBy fst
        |> List.choose (fun (name, matches) ->
            let paths = matches |> List.map snd |> List.distinct |> List.sort

            if paths.Length > 1 && not (allowedDuplicates.Contains name) then
                Some(sprintf "%s: %s" name (String.Join(", ", paths)))
            else
                None)

    let duplicateMessage = String.Join("; ", duplicates)
    Assert.True(duplicates.IsEmpty, $"Duplicate private helpers found: {duplicateMessage}")

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
