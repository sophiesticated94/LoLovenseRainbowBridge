namespace LoLovenseRainbowBridge

open System
open Microsoft.Extensions.Configuration

module Loader =
    let private requiredValue (config: IConfiguration) key =
        let raw = config[key]
        if String.IsNullOrWhiteSpace raw then
            invalidArg key $"Missing required configuration value: {key}"
        else
            raw

    let private intValue (config: IConfiguration) key =
        match Int32.TryParse(requiredValue config key) with
        | true, parsed -> parsed
        | false, _ -> invalidArg key $"Configuration value must be an integer: {key}"

    let private floatValue (config: IConfiguration) key =
        match Double.TryParse(requiredValue config key, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, parsed -> parsed
        | false, _ -> invalidArg key $"Configuration value must be a floating point number: {key}"

    let private boolValue (config: IConfiguration) key =
        match requiredValue config key with
        | "1" | "true" | "TRUE" | "True" | "yes" | "YES" -> true
        | "0" | "false" | "FALSE" | "False" | "no" | "NO" -> false
        | _ -> invalidArg key $"Configuration value must be a boolean: {key}"

    let private optionalString (config: IConfiguration) key =
        let raw = config[key]
        if String.IsNullOrWhiteSpace raw then None else Some raw

    let private legacyEnv name =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then None else Some value

    let private legacyInt name current =
        legacyEnv name
        |> Option.bind (fun raw ->
            match Int32.TryParse raw with
            | true, parsed -> Some parsed
            | false, _ -> None)
        |> Option.defaultValue current

    let private legacyBool name current =
        legacyEnv name
        |> Option.map (function
            | "1" | "true" | "TRUE" | "True" | "yes" | "YES" -> true
            | "0" | "false" | "FALSE" | "False" | "no" | "NO" -> false
            | _ -> current)
        |> Option.defaultValue current

    let private sectionString (section: IConfigurationSection) key =
        let value = section[key]
        if String.IsNullOrWhiteSpace value then "" else value

    let private sectionBool defaultValue (section: IConfigurationSection) key =
        let value = section[key]
        if String.IsNullOrWhiteSpace value then
            defaultValue
        else
            match value with
            | "1" | "true" | "TRUE" | "True" | "yes" | "YES" -> true
            | "0" | "false" | "FALSE" | "False" | "no" | "NO" -> false
            | _ -> defaultValue

    let private sectionFloat defaultValue (section: IConfigurationSection) key =
        let value = section[key]
        if String.IsNullOrWhiteSpace value then
            defaultValue
        else
            match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, parsed -> parsed
            | false, _ -> defaultValue

    let private loadRule (section: IConfigurationSection) =
        {
            Name = sectionString section "Name"
            Kind = sectionString section "Kind"
            Enabled = sectionBool true section "Enabled"
            Trigger = sectionString section "Trigger"
            Condition = sectionString section "Condition"
            TargetFunctions = sectionString section "TargetFunctions"
            StateSlot = sectionString section "StateSlot"
            Layer = sectionString section "Layer"
            Operation = sectionString section "Operation"
            Expression = sectionString section "Expression"
            DurationSec = sectionFloat 0.0 section "DurationSec"
        }

    let load () : AppConfig =
        let root =
            ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional = false, reloadOnChange = false)
                .AddJsonFile("appsettings.Local.json", optional = true, reloadOnChange = false)
                .AddEnvironmentVariables()
                .Build()

        let loaded =
            {
            League =
                {
                    BaseUrl = requiredValue root "League:BaseUrl"
                }

            Lovense =
                {
                    ToyId = optionalString root "Lovense:ToyId"
                    TransportMode = requiredValue root "Lovense:TransportMode"
                    Platform = requiredValue root "Lovense:Platform"
                    Developer =
                        {
                            Token = optionalString root "Lovense:Developer:Token"
                            UserId = optionalString root "Lovense:Developer:UserId"
                            UserName = optionalString root "Lovense:Developer:UserName"
                            UserToken = optionalString root "Lovense:Developer:UserToken"
                        }
                    StandardApi =
                        {
                            Enable = boolValue root "Lovense:StandardApi:Enable"
                            CallbackListenUrl = requiredValue root "Lovense:StandardApi:CallbackListenUrl"
                            PublicCallbackUrl = optionalString root "Lovense:StandardApi:PublicCallbackUrl"
                            GenerateQrOnStartup = boolValue root "Lovense:StandardApi:GenerateQrOnStartup"
                            UseServerCommandFallback = boolValue root "Lovense:StandardApi:UseServerCommandFallback"
                            PairingQrExpiresHours = floatValue root "Lovense:StandardApi:PairingQrExpiresHours"
                        }
                    LocalApi =
                        {
                            EnableGetToys = boolValue root "Lovense:LocalApi:EnableGetToys"
                            EnableCommandFallback = boolValue root "Lovense:LocalApi:EnableCommandFallback"
                            Domain = optionalString root "Lovense:LocalApi:Domain"
                            HttpsPort = optionalString root "Lovense:LocalApi:HttpsPort" |> Option.bind (fun raw -> match Int32.TryParse raw with | true, value -> Some value | _ -> None)
                            HttpPort = optionalString root "Lovense:LocalApi:HttpPort" |> Option.bind (fun raw -> match Int32.TryParse raw with | true, value -> Some value | _ -> None)
                            TimeoutMs = intValue root "Lovense:LocalApi:TimeoutMs"
                            AllowSelfSignedCertificate = boolValue root "Lovense:LocalApi:AllowSelfSignedCertificate"
                            HeaderPlatform = requiredValue root "Lovense:LocalApi:HeaderPlatform"
                            CapabilityRefreshIntervalSec = intValue root "Lovense:LocalApi:CapabilityRefreshIntervalSec"
                        }
                    CommandTimeSec = floatValue root "Lovense:CommandTimeSec"
                    DryRun = boolValue root "Lovense:DryRun"
                    ConnectTimeoutMs = intValue root "Lovense:ConnectTimeoutMs"
                    CommandAckTimeoutMs = intValue root "Lovense:CommandAckTimeoutMs"
                    Mapping =
                        {
                            Mode = requiredValue root "Lovense:Mapping:Mode"
                            EnableComboActions = boolValue root "Lovense:Mapping:EnableComboActions"
                            EnableEventBursts = boolValue root "Lovense:Mapping:EnableEventBursts"
                            EnableDeathStop = boolValue root "Lovense:Mapping:EnableDeathStop"
                            EnableStrokeActions = boolValue root "Lovense:Mapping:EnableStrokeActions"
                            EnableCapabilityFiltering = boolValue root "Lovense:Mapping:EnableCapabilityFiltering"
                            EnableStereoVibration = boolValue root "Lovense:Mapping:EnableStereoVibration"
                            DefaultStopPrevious = boolValue root "Lovense:Mapping:DefaultStopPrevious"
                            UnknownCapabilityMode = requiredValue root "Lovense:Mapping:UnknownCapabilityMode"
                            StereoMode = requiredValue root "Lovense:Mapping:StereoMode"
                            StereoFallback = requiredValue root "Lovense:Mapping:StereoFallback"
                            LogToyViability = boolValue root "Lovense:Mapping:LogToyViability"
                            ForceSupportedFunctions =
                                root.GetSection("Lovense:Mapping:ForceSupportedFunctions").Get<string[]>()
                                |> Option.ofObj
                                |> Option.map Array.toList
                                |> Option.defaultValue []
                            MaxActionIntensity = intValue root "Lovense:Mapping:MaxActionIntensity"
                            PumpMax = intValue root "Lovense:Mapping:PumpMax"
                            DepthMax = intValue root "Lovense:Mapping:DepthMax"
                            StrokeMax = intValue root "Lovense:Mapping:StrokeMax"
                            FunctionProfiles =
                                root.GetSection("Lovense:Mapping:FunctionProfiles").Get<LovenseFunctionProfileConfig[]>()
                                |> Option.ofObj
                                |> Option.map Array.toList
                                |> Option.defaultValue []
                            Rules =
                                root.GetSection("Lovense:Mapping:Rules").GetChildren()
                                |> Seq.map loadRule
                                |> Seq.toList
                        }
                }

            Runtime =
                {
                    PollMs = intValue root "Runtime:PollMs"
                    LeaguePollMs = optionalString root "Runtime:LeaguePollMs" |> Option.bind (fun raw -> match Int32.TryParse raw with | true, value -> Some value | _ -> None) |> Option.defaultValue (intValue root "Runtime:PollMs")
                    OcrPollMs = optionalString root "Runtime:OcrPollMs" |> Option.bind (fun raw -> match Int32.TryParse raw with | true, value -> Some value | _ -> None) |> Option.defaultValue (intValue root "Runtime:PollMs")
                    LovensePollMs = optionalString root "Runtime:LovensePollMs" |> Option.bind (fun raw -> match Int32.TryParse raw with | true, value -> Some value | _ -> None) |> Option.defaultValue (intValue root "Runtime:PollMs")
                    ResendEveryMs = intValue root "Runtime:ResendEveryMs"
                    UnavailableRetryMs = intValue root "Runtime:UnavailableRetryMs"
                }

            Scoring =
                {
                    KillWeight = floatValue root "Scoring:KillWeight"
                    AssistWeight = floatValue root "Scoring:AssistWeight"
                    CreepScoreWeight = floatValue root "Scoring:CreepScoreWeight"
                    LevelWeight = floatValue root "Scoring:LevelWeight"
                    WardScoreWeight = floatValue root "Scoring:WardScoreWeight"
                    DeathWeight = floatValue root "Scoring:DeathWeight"
                    NormalizedScoreWeight = floatValue root "Scoring:NormalizedScoreWeight"
                    EqualScoreNormalizedValue = floatValue root "Scoring:EqualScoreNormalizedValue"
                    ScoreEqualityEpsilon = floatValue root "Scoring:ScoreEqualityEpsilon"
                    MinIntensity = intValue root "Scoring:MinIntensity"
                    MaxIntensity = intValue root "Scoring:MaxIntensity"
                    BaseIntensityCap = intValue root "Scoring:BaseIntensityCap"
                    HealthMinMultiplier = floatValue root "Scoring:HealthMinMultiplier"
                    HealthPressureDropThresholdPercent = floatValue root "Scoring:HealthPressureDropThresholdPercent"
                    FullRegainPressureFactor = floatValue root "Scoring:FullRegainPressureFactor"
                    SingleKillPulseValue = intValue root "Scoring:SingleKillPulseValue"
                    SingleKillPulseDurationSec = floatValue root "Scoring:SingleKillPulseDurationSec"
                    ProvisionalSingleKillWindowSec = floatValue root "Scoring:ProvisionalSingleKillWindowSec"
                    MinMultikillStreak = intValue root "Scoring:MinMultikillStreak"
                    MaxMultikillStreak = intValue root "Scoring:MaxMultikillStreak"
                    EnableObjectiveWaves = boolValue root "Scoring:EnableObjectiveWaves"
                    EnableTeamfightBurst = boolValue root "Scoring:EnableTeamfightBurst"
                    EnableHeartbeatNearDeath = boolValue root "Scoring:EnableHeartbeatNearDeath"
                    EnableLaningPhaseTexture = boolValue root "Scoring:EnableLaningPhaseTexture"
                    EnableJungleTensionRamp = boolValue root "Scoring:EnableJungleTensionRamp"
                    TeamfightWindowSec = floatValue root "Scoring:TeamfightWindowSec"
                    TeamfightKillCountThreshold = intValue root "Scoring:TeamfightKillCountThreshold"
                    LowHealthHeartbeatThreshold = floatValue root "Scoring:LowHealthHeartbeatThreshold"
                    CriticalHealthHeartbeatThreshold = floatValue root "Scoring:CriticalHealthHeartbeatThreshold"
                    HeartbeatPulseMaxAmplitude = floatValue root "Scoring:HeartbeatPulseMaxAmplitude"
                    HeartbeatPulseCycleSec = floatValue root "Scoring:HeartbeatPulseCycleSec"
                    HeartbeatPulseStartPhase = floatValue root "Scoring:HeartbeatPulseStartPhase"
                    HeartbeatPulsePeakPhase = floatValue root "Scoring:HeartbeatPulsePeakPhase"
                    HeartbeatPulseEndPhase = floatValue root "Scoring:HeartbeatPulseEndPhase"
                    LaningPhaseEndSec = floatValue root "Scoring:LaningPhaseEndSec"
                    DragonInitialSpawnSec = floatValue root "Scoring:DragonInitialSpawnSec"
                    DragonRespawnSec = floatValue root "Scoring:DragonRespawnSec"
                    HeraldInitialSpawnSec = floatValue root "Scoring:HeraldInitialSpawnSec"
                    HeraldDespawnSec = floatValue root "Scoring:HeraldDespawnSec"
                    BaronInitialSpawnSec = floatValue root "Scoring:BaronInitialSpawnSec"
                    BaronRespawnSec = floatValue root "Scoring:BaronRespawnSec"
                    ObjectiveTensionWindowSec = floatValue root "Scoring:ObjectiveTensionWindowSec"
                    DeathPressureWindowSec = floatValue root "Scoring:DeathPressureWindowSec"
                    DeathPressureBaseLossPercent = floatValue root "Scoring:DeathPressureBaseLossPercent"
                    HpChangeThresholdPercent = floatValue root "Scoring:HpChangeThresholdPercent"
                    BaseRecoveryFloor = floatValue root "Scoring:BaseRecoveryFloor"
                    BaseRecoveryTarget = floatValue root "Scoring:BaseRecoveryTarget"
                }

            Logging =
                {
                    BaseDirectory = requiredValue root "Logging:BaseDirectory"
                    SessionDirectoryFormat = requiredValue root "Logging:SessionDirectoryFormat"
                    TrackLogLevel = requiredValue root "Logging:TrackLogLevel"
                    LogRawLeague = boolValue root "Logging:LogRawLeague"
                    LogRawLovense = boolValue root "Logging:LogRawLovense"
                    RawLogPrettyPrint = boolValue root "Logging:RawLogPrettyPrint"
                }

            Recording =
                {
                    Enabled = boolValue root "Recording:Enabled"
                    DatabasePath = requiredValue root "Recording:DatabasePath"
                    SliceMs = intValue root "Recording:SliceMs"
                    RecordRawContext = boolValue root "Recording:RecordRawContext"
                }

            PositionBasedRotation =
                {
                    Enable = boolValue root "PositionBasedRotation:Enable"
                    CaptureIntervalMs = intValue root "PositionBasedRotation:CaptureIntervalMs"
                    MinimapScreenX = intValue root "PositionBasedRotation:MinimapScreenX"
                    MinimapScreenY = intValue root "PositionBasedRotation:MinimapScreenY"
                    MinimapWidth = intValue root "PositionBasedRotation:MinimapWidth"
                    MinimapHeight = intValue root "PositionBasedRotation:MinimapHeight"
                    MappingMode = requiredValue root "PositionBasedRotation:MappingMode"
                    RotationSensitivity = floatValue root "PositionBasedRotation:RotationSensitivity"
                    TemplateImagePath = optionalString root "PositionBasedRotation:TemplateImagePath"
                    DebugMode = boolValue root "PositionBasedRotation:DebugMode"
                }
        }

        {
            loaded with
                Lovense =
                    {
                        loaded.Lovense with
                            ToyId = legacyEnv "LOVENSE_TOY_ID" |> Option.orElse loaded.Lovense.ToyId
                            DryRun = legacyBool "DRY_RUN" loaded.Lovense.DryRun
                    }
                PositionBasedRotation =
                    {
                        loaded.PositionBasedRotation with
                            Enable = legacyBool "POSITION_ROTATION_ENABLE" loaded.PositionBasedRotation.Enable
                    }
        }
        |> Validation.validate
