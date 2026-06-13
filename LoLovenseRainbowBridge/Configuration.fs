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
        CommandTimeSec: float
        DryRun: bool
        ConnectTimeoutMs: int
        CommandAckTimeoutMs: int
        Mapping: LovenseMappingConfig
    }

and LovenseMappingConfig =
    {
        Mode: string
        EnableComboActions: bool
        EnableEventBursts: bool
        EnableDeathStop: bool
        EnableStrokeActions: bool
        DefaultStopPrevious: bool
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
        SingleKillPulseValue: int
        SingleKillPulseDurationSec: float
        ProvisionalSingleKillWindowSec: float
        MinMultikillStreak: int
        MaxMultikillStreak: int
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

type AppConfig =
    {
        League: LeagueConfig
        Lovense: LovenseConfig
        Runtime: RuntimeConfig
        Scoring: ScoringConfig
        Logging: LoggingConfig
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

        if config.Scoring.MinIntensity > config.Scoring.MaxIntensity then
            invalidArg "Scoring.MinIntensity" "Scoring.MinIntensity cannot be greater than Scoring.MaxIntensity."

        if config.Scoring.MinMultikillStreak > config.Scoring.MaxMultikillStreak then
            invalidArg "Scoring.MinMultikillStreak" "Scoring.MinMultikillStreak cannot be greater than Scoring.MaxMultikillStreak."

        let allowedLogLevels = set [ "TRACE"; "DEBUG"; "INFO"; "WARN"; "ERROR" ]

        if not (allowedLogLevels.Contains(config.Logging.TrackLogLevel.ToUpperInvariant())) then
            invalidArg "Logging.TrackLogLevel" "Logging.TrackLogLevel must be one of: Trace, Debug, Info, Warn, Error."

        config

    let load () =
        let root =
            ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional = false, reloadOnChange = false)
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
                            DefaultStopPrevious = boolValue root "Lovense:Mapping:DefaultStopPrevious"
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
                    SingleKillPulseValue = intValue root "Scoring:SingleKillPulseValue"
                    SingleKillPulseDurationSec = floatValue root "Scoring:SingleKillPulseDurationSec"
                    ProvisionalSingleKillWindowSec = floatValue root "Scoring:ProvisionalSingleKillWindowSec"
                    MinMultikillStreak = intValue root "Scoring:MinMultikillStreak"
                    MaxMultikillStreak = intValue root "Scoring:MaxMultikillStreak"
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
        }
        |> validate
