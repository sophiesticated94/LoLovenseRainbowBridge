namespace LoLovenseRainbowBridge

open System
open Microsoft.Extensions.Configuration

type LeagueConfig =
    {
        BaseUrl: string
    }

type LovenseConfig =
    {
        ToyId: string option
        TransportMode: string
        Platform: string
        Developer: LovenseDeveloperConfig
        StandardApi: LovenseStandardApiConfig
        LocalApi: LovenseLocalApiConfig
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
        UserToken: string option
    }

and LovenseStandardApiConfig =
    {
        Enable: bool
        CallbackListenUrl: string
        PublicCallbackUrl: string option
        GenerateQrOnStartup: bool
        UseServerCommandFallback: bool
        PairingQrExpiresHours: float
    }

and LovenseLocalApiConfig =
    {
        EnableGetToys: bool
        EnableCommandFallback: bool
        Domain: string option
        HttpsPort: int option
        TimeoutMs: int
        AllowSelfSignedCertificate: bool
        HeaderPlatform: string
        CapabilityRefreshIntervalSec: int
    }

and LovenseMappingConfig =
    {
        Mode: string
        EnableComboActions: bool
        EnableEventBursts: bool
        EnableDeathStop: bool
        EnableStrokeActions: bool
        EnableCapabilityFiltering: bool
        EnableStereoVibration: bool
        DefaultStopPrevious: bool
        UnknownCapabilityMode: string
        StereoMode: string
        StereoFallback: string
        LogToyViability: bool
        ForceSupportedFunctions: string list
        MaxActionIntensity: int
        PumpMax: int
        DepthMax: int
        StrokeMax: int
        FunctionProfiles: LovenseFunctionProfileConfig list
        Rules: LovenseRuleConfig list
    }

and LovenseFunctionProfileConfig =
    {
        FunctionName: string
        Enabled: bool
        MinOutput: int
        MaxOutput: int
        BaseWeight: float
        TimedWeight: float
        EffectWeight: float
        Curve: string
        Smoothing: float
    }

and LovenseRuleConfig =
    {
        Name: string
        Kind: string
        Enabled: bool
        Trigger: string
        Condition: string
        TargetFunctions: string
        StateSlot: string
        Layer: string
        Operation: string
        Expression: string
        DurationSec: float
    }

type RuntimeConfig =
    {
        PollMs: int
        LeaguePollMs: int
        OcrPollMs: int
        LovensePollMs: int
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
        HeartbeatPulseMaxAmplitude: float
        HeartbeatPulseCycleSec: float
        HeartbeatPulseStartPhase: float
        HeartbeatPulsePeakPhase: float
        HeartbeatPulseEndPhase: float
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

    let private validate config =
        if config.Runtime.PollMs <= 0 then
            invalidArg "Runtime.PollMs" "Runtime.PollMs must be greater than zero."

        if config.Runtime.LeaguePollMs <= 0 then
            invalidArg "Runtime.LeaguePollMs" "Runtime.LeaguePollMs must be greater than zero."

        if config.Runtime.OcrPollMs <= 0 then
            invalidArg "Runtime.OcrPollMs" "Runtime.OcrPollMs must be greater than zero."

        if config.Runtime.LovensePollMs <= 0 then
            invalidArg "Runtime.LovensePollMs" "Runtime.LovensePollMs must be greater than zero."

        if config.Runtime.ResendEveryMs <= 0 then
            invalidArg "Runtime.ResendEveryMs" "Runtime.ResendEveryMs must be greater than zero."

        if config.Runtime.UnavailableRetryMs <= 0 then
            invalidArg "Runtime.UnavailableRetryMs" "Runtime.UnavailableRetryMs must be greater than zero."

        if config.Lovense.ConnectTimeoutMs <= 0 then
            invalidArg "Lovense.ConnectTimeoutMs" "Lovense.ConnectTimeoutMs must be greater than zero."

        if config.Lovense.CommandAckTimeoutMs <= 0 then
            invalidArg "Lovense.CommandAckTimeoutMs" "Lovense.CommandAckTimeoutMs must be greater than zero."

        let allowedTransportModes = set [ "SOCKETAPI"; "STANDARDAPILOCAL"; "STANDARDAPISERVER"; "AUTO" ]

        if not (allowedTransportModes.Contains(config.Lovense.TransportMode.ToUpperInvariant())) then
            invalidArg "Lovense.TransportMode" "Lovense.TransportMode must be SocketApi, StandardApiLocal, StandardApiServer, or Auto."

        if config.Lovense.StandardApi.Enable then
            if String.IsNullOrWhiteSpace config.Lovense.StandardApi.CallbackListenUrl then
                invalidArg "Lovense.StandardApi.CallbackListenUrl" "Lovense.StandardApi.CallbackListenUrl cannot be empty when Standard API is enabled."

            if config.Lovense.StandardApi.PairingQrExpiresHours <= 0.0 then
                invalidArg "Lovense.StandardApi.PairingQrExpiresHours" "Lovense.StandardApi.PairingQrExpiresHours must be greater than zero."

        if config.Lovense.LocalApi.TimeoutMs <= 0 then
            invalidArg "Lovense.LocalApi.TimeoutMs" "Lovense.LocalApi.TimeoutMs must be greater than zero."

        if config.Lovense.LocalApi.CapabilityRefreshIntervalSec <= 0 then
            invalidArg "Lovense.LocalApi.CapabilityRefreshIntervalSec" "Lovense.LocalApi.CapabilityRefreshIntervalSec must be greater than zero."

        if config.Lovense.LocalApi.EnableCommandFallback || config.Lovense.LocalApi.EnableGetToys then
            match config.Lovense.LocalApi.HttpsPort with
            | Some port when port <= 0 || port > 65535 ->
                invalidArg "Lovense.LocalApi.HttpsPort" "Lovense.LocalApi.HttpsPort must be in range 1..65535."
            | _ -> ()

        if String.IsNullOrWhiteSpace config.Lovense.LocalApi.HeaderPlatform then
            invalidArg "Lovense.LocalApi.HeaderPlatform" "Lovense.LocalApi.HeaderPlatform cannot be empty."

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

        let allowedStereoModes = set [ "AUTO"; "DISABLED"; "FORCE" ]

        if not (allowedStereoModes.Contains(config.Lovense.Mapping.StereoMode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.StereoMode" "Lovense.Mapping.StereoMode must be Auto, Disabled, or Force."

        let allowedStereoFallbacks = set [ "MAX"; "AVERAGE"; "LEFTONLY" ]

        if not (allowedStereoFallbacks.Contains(config.Lovense.Mapping.StereoFallback.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.StereoFallback" "Lovense.Mapping.StereoFallback must be Max, Average, or LeftOnly."

        let knownLovenseFunctions =
            set
                [
                    "VIBRATE"
                    "VIBRATE1"
                    "VIBRATE2"
                    "ROTATE"
                    "PUMP"
                    "THRUSTING"
                    "FINGERING"
                    "SUCTION"
                    "DEPTH"
                    "STROKE"
                    "OSCILLATE"
                    "ALL"
                    "STOP"
                ]

        let knownRuleKinds =
            set
                [
                    "BASEMODIFIER"
                    "THRESHOLDMODIFIER"
                    "TIMEDCONTRIBUTION"
                    "EFFECT"
                    "STATETRANSITION"
                    "CAPABILITYFALLBACK"
                    "POSITIONMODULATION"
                ]

        let knownRuleOperations =
            set
                [
                    "SET"
                    "ADD"
                    "SUBTRACT"
                    "MULTIPLY"
                    "CLAMPMIN"
                    "CLAMPMAX"
                    "TRACKMAX"
                    "TRACKMIN"
                    "CLEAR"
                    "STARTWINDOW"
                    "STARTINCARNATION"
                ]

        let knownRuleLayers =
            set [ "BASE"; "TIMED"; "EFFECT"; "OTHER"; "STATE" ]

        for profile in config.Lovense.Mapping.FunctionProfiles do
            if not (knownLovenseFunctions.Contains(profile.FunctionName.ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.FunctionProfiles.FunctionName" $"Unknown Lovense function profile: {profile.FunctionName}"

            if profile.MinOutput < 0 || profile.MaxOutput < profile.MinOutput then
                invalidArg "Lovense.Mapping.FunctionProfiles.Output" $"Invalid output range for {profile.FunctionName}."

            if profile.Smoothing < 0.0 || profile.Smoothing > 1.0 then
                invalidArg "Lovense.Mapping.FunctionProfiles.Smoothing" $"Smoothing for {profile.FunctionName} must be in range 0.0..1.0."

        for rule in config.Lovense.Mapping.Rules do
            if String.IsNullOrWhiteSpace rule.Name then
                invalidArg "Lovense.Mapping.Rules.Name" "Every Lovense rule must have a name."

            if not (knownRuleKinds.Contains(rule.Kind.ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Kind" $"Unknown Lovense rule kind: {rule.Kind}"

            if not (knownRuleOperations.Contains((rule.Operation |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Operation" $"Unknown Lovense rule operation in '{rule.Name}': {rule.Operation}"

            if not (knownRuleLayers.Contains((rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Layer" $"Unknown Lovense rule layer in '{rule.Name}': {rule.Layer}"

            if String.Equals(rule.Layer, "State", StringComparison.OrdinalIgnoreCase) then
                if String.IsNullOrWhiteSpace rule.StateSlot then
                    invalidArg "Lovense.Mapping.Rules.StateSlot" $"State rule '{rule.Name}' must declare StateSlot."
            else
                let functions =
                    rule.TargetFunctions.Split('|', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                    |> Array.toList

                if functions.IsEmpty then
                    invalidArg "Lovense.Mapping.Rules.TargetFunctions" $"Rule '{rule.Name}' must declare TargetFunctions."

                for functionName in functions do
                    if not (knownLovenseFunctions.Contains(functionName.ToUpperInvariant())) then
                        invalidArg "Lovense.Mapping.Rules.TargetFunctions" $"Unknown Lovense rule target function in '{rule.Name}': {functionName}"

            if String.IsNullOrWhiteSpace rule.Expression
               && not (String.Equals(rule.Operation, "Clear", StringComparison.OrdinalIgnoreCase)
                       || String.Equals(rule.Operation, "StartIncarnation", StringComparison.OrdinalIgnoreCase)) then
                invalidArg "Lovense.Mapping.Rules.Expression" $"Rule '{rule.Name}' must declare Expression."

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

        if config.Scoring.HeartbeatPulseMaxAmplitude < 0.0 then
            invalidArg "Scoring.HeartbeatPulseMaxAmplitude" "Scoring.HeartbeatPulseMaxAmplitude must be zero or greater."

        if config.Scoring.HeartbeatPulseCycleSec <= 0.0 then
            invalidArg "Scoring.HeartbeatPulseCycleSec" "Scoring.HeartbeatPulseCycleSec must be greater than zero."

        for key, value in
            [
                "Scoring.HeartbeatPulseStartPhase", config.Scoring.HeartbeatPulseStartPhase
                "Scoring.HeartbeatPulsePeakPhase", config.Scoring.HeartbeatPulsePeakPhase
                "Scoring.HeartbeatPulseEndPhase", config.Scoring.HeartbeatPulseEndPhase
            ] do
            if value < 0.0 || value > 1.0 then
                invalidArg key $"{key} must be in range 0.0..1.0."

        if not (config.Scoring.HeartbeatPulseStartPhase < config.Scoring.HeartbeatPulsePeakPhase
                && config.Scoring.HeartbeatPulsePeakPhase < config.Scoring.HeartbeatPulseEndPhase) then
            invalidArg "Scoring.HeartbeatPulsePhases" "Heartbeat pulse phases must satisfy StartPhase < PeakPhase < EndPhase."

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
        |> validate
