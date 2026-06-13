namespace LoLovenseRainbowBridge

open System
open Microsoft.Extensions.Configuration

type LeagueConfig =
    {
        BaseUrl: string
    }

type LovenseConfig =
    {
        AuthToken: string option
        ToyId: string option
        Platform: string
        Developer: LovenseDeveloperConfig
        CommandTimeSec: float
        DryRun: bool
        ConnectTimeoutMs: int
        CommandAckTimeoutMs: int
        Mapping: LovenseMappingConfig
    }

and LovenseDeveloperConfig =
    {
        Token: string option
        UserId: string option
        UserName: string option
        UserEmail: string option
        UserToken: string option
    }

and LovenseMappingConfig =
    {
        Mode: string
        EnableComboActions: bool
        EnableEventBursts: bool
        EnableDeathStop: bool
        EnableStrokeActions: bool
        EnableCapabilityFiltering: bool
        DefaultStopPrevious: bool
        UnknownCapabilityMode: string
        ForceSupportedFunctions: string list
        MaxActionIntensity: int
        PumpMax: int
        DepthMax: int
        StrokeMax: int
    }

type RuntimeConfig =
    {
        PollMs: int
        ResendEveryMs: int
        UnavailableRetryMs: int
    }

type ScoringConfig =
    {
        KillWeight: float
        AssistWeight: float
        CreepScoreWeight: float
        LevelWeight: float
        WardScoreWeight: float
        DeathWeight: float
        NormalizedScoreWeight: float
        EqualScoreNormalizedValue: float
        ScoreEqualityEpsilon: float
        MinIntensity: int
        MaxIntensity: int
        BaseIntensityCap: int
        HealthMinMultiplier: float
        HealthPressureDropThresholdPercent: float
        FullRegainPressureFactor: float
        SingleKillPulseValue: int
        SingleKillPulseDurationSec: float
        ProvisionalSingleKillWindowSec: float
        MinMultikillStreak: int
        MaxMultikillStreak: int
        EnableObjectiveWaves: bool
        EnableTeamfightBurst: bool
        EnableHeartbeatNearDeath: bool
        EnableLaningPhaseTexture: bool
        EnableJungleTensionRamp: bool
        TeamfightWindowSec: float
        TeamfightKillCountThreshold: int
        LowHealthHeartbeatThreshold: float
        CriticalHealthHeartbeatThreshold: float
        LaningPhaseEndSec: float
        DragonInitialSpawnSec: float
        DragonRespawnSec: float
        HeraldInitialSpawnSec: float
        HeraldDespawnSec: float
        BaronInitialSpawnSec: float
        BaronRespawnSec: float
        ObjectiveTensionWindowSec: float
        DeathPressureWindowSec: float
        DeathPressureBaseLossPercent: float
        HpChangeThresholdPercent: float
        BaseRecoveryFloor: float
        BaseRecoveryTarget: float
    }

type LoggingConfig =
    {
        BaseDirectory: string
        SessionDirectoryFormat: string
        TrackLogLevel: string
        LogRawLeague: bool
        LogRawLovense: bool
        RawLogPrettyPrint: bool
    }

type RecordingConfig =
    {
        Enabled: bool
        DatabasePath: string
        SliceMs: int
        RecordRawContext: bool
    }

type PositionBasedRotationConfig =
    {
        Enable: bool
        CaptureIntervalMs: int
        MinimapScreenX: int
        MinimapScreenY: int
        MinimapWidth: int
        MinimapHeight: int
        MappingMode: string
        RotationSensitivity: float
        TemplateImagePath: string option
        DebugMode: bool
    }

type AppConfig =
    {
        League: LeagueConfig
        Lovense: LovenseConfig
        Runtime: RuntimeConfig
        Scoring: ScoringConfig
        Logging: LoggingConfig
        Recording: RecordingConfig
        PositionBasedRotation: PositionBasedRotationConfig
    }

module Configuration =

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

    let private validate config =
        if config.Runtime.PollMs <= 0 then
            invalidArg "Runtime.PollMs" "Runtime.PollMs must be greater than zero."

        if config.Runtime.ResendEveryMs <= 0 then
            invalidArg "Runtime.ResendEveryMs" "Runtime.ResendEveryMs must be greater than zero."

        if config.Runtime.UnavailableRetryMs <= 0 then
            invalidArg "Runtime.UnavailableRetryMs" "Runtime.UnavailableRetryMs must be greater than zero."

        if config.Lovense.ConnectTimeoutMs <= 0 then
            invalidArg "Lovense.ConnectTimeoutMs" "Lovense.ConnectTimeoutMs must be greater than zero."

        if config.Lovense.CommandAckTimeoutMs <= 0 then
            invalidArg "Lovense.CommandAckTimeoutMs" "Lovense.CommandAckTimeoutMs must be greater than zero."

        let allowedMappingModes = set [ "SIMPLEVIBRATE"; "MULTIFUNCTION" ]

        if not (allowedMappingModes.Contains(config.Lovense.Mapping.Mode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.Mode" "Lovense.Mapping.Mode must be SimpleVibrate or MultiFunction."

        if config.Lovense.Mapping.MaxActionIntensity < 0 || config.Lovense.Mapping.MaxActionIntensity > 20 then
            invalidArg "Lovense.Mapping.MaxActionIntensity" "Lovense.Mapping.MaxActionIntensity must be in range 0..20."

        if config.Lovense.Mapping.PumpMax < 0 || config.Lovense.Mapping.PumpMax > 3 then
            invalidArg "Lovense.Mapping.PumpMax" "Lovense.Mapping.PumpMax must be in range 0..3."

        if config.Lovense.Mapping.DepthMax < 0 || config.Lovense.Mapping.DepthMax > 3 then
            invalidArg "Lovense.Mapping.DepthMax" "Lovense.Mapping.DepthMax must be in range 0..3."

        if config.Lovense.Mapping.StrokeMax < 0 || config.Lovense.Mapping.StrokeMax > 100 then
            invalidArg "Lovense.Mapping.StrokeMax" "Lovense.Mapping.StrokeMax must be in range 0..100."

        let allowedUnknownCapabilityModes = set [ "SAFEUNIVERSAL"; "PASSTHROUGH" ]

        if not (allowedUnknownCapabilityModes.Contains(config.Lovense.Mapping.UnknownCapabilityMode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.UnknownCapabilityMode" "Lovense.Mapping.UnknownCapabilityMode must be SafeUniversal or PassThrough."

        if config.Scoring.MinIntensity > config.Scoring.MaxIntensity then
            invalidArg "Scoring.MinIntensity" "Scoring.MinIntensity cannot be greater than Scoring.MaxIntensity."

        if config.Scoring.BaseIntensityCap < config.Scoring.MinIntensity || config.Scoring.BaseIntensityCap > config.Scoring.MaxIntensity then
            invalidArg "Scoring.BaseIntensityCap" "Scoring.BaseIntensityCap must be between Scoring.MinIntensity and Scoring.MaxIntensity."

        if config.Scoring.HealthMinMultiplier < 0.0 || config.Scoring.HealthMinMultiplier > 1.0 then
            invalidArg "Scoring.HealthMinMultiplier" "Scoring.HealthMinMultiplier must be in range 0.0..1.0."

        if config.Scoring.HealthPressureDropThresholdPercent < 0.0 || config.Scoring.HealthPressureDropThresholdPercent > 100.0 then
            invalidArg "Scoring.HealthPressureDropThresholdPercent" "Scoring.HealthPressureDropThresholdPercent must be in range 0.0..100.0."

        if config.Scoring.FullRegainPressureFactor < 0.0 || config.Scoring.FullRegainPressureFactor > 1.0 then
            invalidArg "Scoring.FullRegainPressureFactor" "Scoring.FullRegainPressureFactor must be in range 0.0..1.0."

        if config.Scoring.MinMultikillStreak > config.Scoring.MaxMultikillStreak then
            invalidArg "Scoring.MinMultikillStreak" "Scoring.MinMultikillStreak cannot be greater than Scoring.MaxMultikillStreak."

        if config.Scoring.TeamfightWindowSec < 0.0 then
            invalidArg "Scoring.TeamfightWindowSec" "Scoring.TeamfightWindowSec must be zero or greater."

        if config.Scoring.TeamfightKillCountThreshold <= 0 then
            invalidArg "Scoring.TeamfightKillCountThreshold" "Scoring.TeamfightKillCountThreshold must be greater than zero."

        if config.Scoring.LowHealthHeartbeatThreshold < 0.0 || config.Scoring.LowHealthHeartbeatThreshold > 1.0 then
            invalidArg "Scoring.LowHealthHeartbeatThreshold" "Scoring.LowHealthHeartbeatThreshold must be in range 0.0..1.0."

        if config.Scoring.CriticalHealthHeartbeatThreshold < 0.0 || config.Scoring.CriticalHealthHeartbeatThreshold > 1.0 then
            invalidArg "Scoring.CriticalHealthHeartbeatThreshold" "Scoring.CriticalHealthHeartbeatThreshold must be in range 0.0..1.0."

        if config.Scoring.CriticalHealthHeartbeatThreshold > config.Scoring.LowHealthHeartbeatThreshold then
            invalidArg "Scoring.CriticalHealthHeartbeatThreshold" "Critical health threshold cannot be greater than low health threshold."

        for key, value in
            [
                "Scoring.LaningPhaseEndSec", config.Scoring.LaningPhaseEndSec
                "Scoring.DragonInitialSpawnSec", config.Scoring.DragonInitialSpawnSec
                "Scoring.DragonRespawnSec", config.Scoring.DragonRespawnSec
                "Scoring.HeraldInitialSpawnSec", config.Scoring.HeraldInitialSpawnSec
                "Scoring.HeraldDespawnSec", config.Scoring.HeraldDespawnSec
                "Scoring.BaronInitialSpawnSec", config.Scoring.BaronInitialSpawnSec
                "Scoring.BaronRespawnSec", config.Scoring.BaronRespawnSec
                "Scoring.ObjectiveTensionWindowSec", config.Scoring.ObjectiveTensionWindowSec
                "Scoring.DeathPressureWindowSec", config.Scoring.DeathPressureWindowSec
            ] do
            if value < 0.0 then
                invalidArg key $"{key} must be zero or greater."

        if config.Scoring.DeathPressureBaseLossPercent < 0.0 || config.Scoring.DeathPressureBaseLossPercent > 1.0 then
            invalidArg "Scoring.DeathPressureBaseLossPercent" "Scoring.DeathPressureBaseLossPercent must be in range 0.0..1.0."

        if config.Scoring.HpChangeThresholdPercent < 0.0 || config.Scoring.HpChangeThresholdPercent > 100.0 then
            invalidArg "Scoring.HpChangeThresholdPercent" "Scoring.HpChangeThresholdPercent must be in range 0.0..100.0."

        if config.Scoring.BaseRecoveryFloor < 0.0 || config.Scoring.BaseRecoveryFloor > 1.0 then
            invalidArg "Scoring.BaseRecoveryFloor" "Scoring.BaseRecoveryFloor must be in range 0.0..1.0."

        if config.Scoring.BaseRecoveryTarget < 0.0 || config.Scoring.BaseRecoveryTarget > 1.0 then
            invalidArg "Scoring.BaseRecoveryTarget" "Scoring.BaseRecoveryTarget must be in range 0.0..1.0."

        if config.Scoring.BaseRecoveryTarget < config.Scoring.BaseRecoveryFloor then
            invalidArg "Scoring.BaseRecoveryTarget" "Scoring.BaseRecoveryTarget cannot be less than Scoring.BaseRecoveryFloor."

        if config.PositionBasedRotation.CaptureIntervalMs <= 0 then
            invalidArg "PositionBasedRotation.CaptureIntervalMs" "PositionBasedRotation.CaptureIntervalMs must be greater than zero."

        if config.PositionBasedRotation.MinimapWidth <= 0 || config.PositionBasedRotation.MinimapHeight <= 0 then
            invalidArg "PositionBasedRotation.Minimap" "PositionBasedRotation.Minimap dimensions must be greater than zero."

        let allowedMappingModes = set [ "QUADRANT"; "CONTINUOUS"; "ZONEBASED"; "COMBINED" ]

        if not (allowedMappingModes.Contains(config.PositionBasedRotation.MappingMode.ToUpperInvariant())) then
            invalidArg "PositionBasedRotation.MappingMode" "PositionBasedRotation.MappingMode must be Quadrant, Continuous, ZoneBased, or Combined."

        if config.PositionBasedRotation.RotationSensitivity < 0.0 || config.PositionBasedRotation.RotationSensitivity > 2.0 then
            invalidArg "PositionBasedRotation.RotationSensitivity" "PositionBasedRotation.RotationSensitivity must be in range 0.0..2.0."

        let allowedLogLevels = set [ "TRACE"; "DEBUG"; "INFO"; "WARN"; "ERROR" ]

        if not (allowedLogLevels.Contains(config.Logging.TrackLogLevel.ToUpperInvariant())) then
            invalidArg "Logging.TrackLogLevel" "Logging.TrackLogLevel must be one of: Trace, Debug, Info, Warn, Error."

        if config.Recording.SliceMs <= 0 then
            invalidArg "Recording.SliceMs" "Recording.SliceMs must be greater than zero."

        if String.IsNullOrWhiteSpace config.Recording.DatabasePath then
            invalidArg "Recording.DatabasePath" "Recording.DatabasePath cannot be empty."

        config

    let load () =
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
                    AuthToken = optionalString root "Lovense:AuthToken"
                    ToyId = optionalString root "Lovense:ToyId"
                    Platform = requiredValue root "Lovense:Platform"
                    Developer =
                        {
                            Token = optionalString root "Lovense:Developer:Token"
                            UserId = optionalString root "Lovense:Developer:UserId"
                            UserName = optionalString root "Lovense:Developer:UserName"
                            UserEmail = optionalString root "Lovense:Developer:UserEmail"
                            UserToken = optionalString root "Lovense:Developer:UserToken"
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
                            DefaultStopPrevious = boolValue root "Lovense:Mapping:DefaultStopPrevious"
                            UnknownCapabilityMode = requiredValue root "Lovense:Mapping:UnknownCapabilityMode"
                            ForceSupportedFunctions =
                                root.GetSection("Lovense:Mapping:ForceSupportedFunctions").Get<string[]>()
                                |> Option.ofObj
                                |> Option.map Array.toList
                                |> Option.defaultValue []
                            MaxActionIntensity = intValue root "Lovense:Mapping:MaxActionIntensity"
                            PumpMax = intValue root "Lovense:Mapping:PumpMax"
                            DepthMax = intValue root "Lovense:Mapping:DepthMax"
                            StrokeMax = intValue root "Lovense:Mapping:StrokeMax"
                        }
                }

            Runtime =
                {
                    PollMs = intValue root "Runtime:PollMs"
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
                            AuthToken = legacyEnv "LOVENSE_AUTH_TOKEN" |> Option.orElse loaded.Lovense.AuthToken
                            ToyId = legacyEnv "LOVENSE_TOY_ID" |> Option.orElse loaded.Lovense.ToyId
                            DryRun = legacyBool "DRY_RUN" loaded.Lovense.DryRun
                    }
                PositionBasedRotation =
                    {
                        loaded.PositionBasedRotation with
                            Enable = legacyBool "POSITION_ROTATION_ENABLE" loaded.PositionBasedRotation.Enable
                    }
        }
        |> validate
