namespace LoLovenseRainbowBridge.Lovense

type LovenseActionFunction =
    | Vibrate
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
    | StopCommand

type LovenseCommandPlan =
    {
        Actions: LovenseAction list
        Reasons: LovenseCommandReason list
        TimeSec: float
        StopPrevious: bool
        ToyId: string option
    }
