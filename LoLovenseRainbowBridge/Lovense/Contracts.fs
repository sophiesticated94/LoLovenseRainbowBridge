namespace LoLovenseRainbowBridge.Lovense

open System

type LovenseActionFunction =
    | Vibrate
    | Vibrate1
    | Vibrate2
    | Rotate
    | Pump
    | Thrusting
    | Fingering
    | Suction
    | Depth
    | Stroke
    | Oscillate
    | All
    | Stop

type LovenseAction =
    {
        Function: LovenseActionFunction
        Value: int
        MaxValue: int
        RangeStart: int option
    }

type LovenseCommandReason =
    | CompatibilityVibrate
    | BasePerformance
    | KillBurst of eventId: int
    | MultikillBurst of eventId: int * streak: int
    | DeathReset
    | AssistSupportTexture
    | HighMomentumTexture
    | ObjectiveWave
    | TeamfightBurst
    | AceBurst
    | HeartbeatNearDeath
    | LaningTexture
    | JungleTensionRamp
    | CapabilityFiltered of droppedActions: string list
    | RuleContribution of ruleName: string
    | StopCommand
    | SourceNotConnected

type LovenseCommandPlan =
    {
        Actions: LovenseAction list
        Reasons: LovenseCommandReason list
        TimeSec: float
        StopPrevious: bool
        ToyId: string option
    }

type LovenseConnectionState =
    {
        Connected: bool
        DryRun: bool
        SocketIoUrl: string option
        SocketIoPath: string option
        SocketId: string option
    }

type LovenseCommandResult =
    {
        RequestedValue: int
        SafeValue: int
        DryRun: bool
        CorrelationId: string
        SocketConnected: bool
    }

type LovenseConnectionError =
    | MissingDeveloperCredentials of missingFields: string list
    | SocketUrlRequestFailed of url: string * message: string
    | SocketUrlRejected of url: string * code: int option * message: string
    | SocketConnectFailed of socketIoUrl: string * socketIoPath: string * message: string
    | SocketDisconnected of reason: string
    | UnexpectedConnectionError of message: string * errorType: string

type LovenseCommandError =
    | NotConnected of LovenseConnectionError
    | CommandEmitFailed of eventName: string * message: string
    | CommandRejected of eventName: string * message: string
    | CommandTimeout of eventName: string * timeoutMs: int
    | UnexpectedCommandError of eventName: string * message: string * errorType: string

type SocketUrlInfo =
    {
        SocketIoUrl: string
        SocketIoPath: string
    }

type LovenseDeviceToy =
    {
        Id: string option
        Name: string option
        ToyType: string option
        Nickname: string option
        Battery: int option
        Connected: bool option
        ExplicitFunctions: Set<string>
    }

type LovenseCapabilitySource =
    | Explicit
    | Inferred
    | SafeFallback
    | Forced

type LovenseToyCapabilityProfile =
    {
        ToyId: string option
        Name: string option
        ToyType: string option
        Nickname: string option
        Battery: int option
        Connected: bool option
        ExplicitFunctions: Set<string>
        InferredFunctions: Set<string>
        SupportedFunctions: Set<string>
        StereoVibrationSupported: bool
        CapabilitySource: LovenseCapabilitySource
        Notes: string list
    }

type LovenseDeviceInfo =
    {
        ToyList: LovenseDeviceToy list
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        Domain: string option
        HttpsPort: int option
        HttpPort: int option
        WssPort: int option
    }

type LovenseEndpointSelection =
    {
        Url: string option
        ExpiresAt: DateTimeOffset option
        LastFailedUrl: string option
        LastSelectedAt: DateTimeOffset option
    }

type LovenseQrCodeSnapshot =
    {
        Qr: string
        Code: string
        ExpiresAt: DateTimeOffset
    }

type LovenseSessionSnapshot =
    {
        SocketInfo: SocketUrlInfo option
        SocketConnected: bool
        SocketReadyAt: DateTimeOffset option
        LastConnectAttemptAt: DateTimeOffset option
        NextConnectRetryAt: DateTimeOffset option
        LocalCommandCooldownUntil: DateTimeOffset option
        ServerCommandCooldownUntil: DateTimeOffset option
        StandardQrCode: LovenseQrCodeSnapshot option
        QrCodeLogged: bool
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        LatestDeviceInfo: LovenseDeviceInfo option
    }

type LovenseCapabilityResolution =
    {
        Plan: LovenseCommandPlan
        CandidateActions: string list
        FinalActions: string list
        DroppedActions: string list
        StereoApplied: bool
        StereoFallbackApplied: bool
        CapabilitySource: string
        ToyProfiles: LovenseToyCapabilityProfile list
        NoSupportedActions: bool
    }
