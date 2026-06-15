namespace LoLovenseRainbowBridge

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
        HttpPort: int option
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

type PositionWeightPairConfig =
    {
        Left: float
        Right: float
    }

type PositionWeightTableConfig =
    {
        Center: PositionWeightPairConfig
        TopLeft: PositionWeightPairConfig
        TopRight: PositionWeightPairConfig
        BottomLeft: PositionWeightPairConfig
        BottomRight: PositionWeightPairConfig
        Left: PositionWeightPairConfig
        Right: PositionWeightPairConfig
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
        PositionWeights: PositionWeightTableConfig
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
