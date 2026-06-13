namespace LoLovenseRainbowBridge.Lovense

open LoLovenseRainbowBridge

module Mapping =

    let private action fn value : LovenseAction =
        {
            Function = fn
            Value = LovenseFunctionRanges.clamp fn value
            MaxValue = LovenseFunctionRanges.maxValue fn
            RangeStart = None
        }

    let simpleVibratePlan (config: LovenseConfig) intensity =
        {
            Actions = [ action Vibrate intensity ]
            Reasons = [ CompatibilityVibrate ]
            TimeSec = config.CommandTimeSec
            StopPrevious = config.Mapping.DefaultStopPrevious
            ToyId = config.ToyId
        }

    let stopPlan (config: LovenseConfig) reason =
        {
            Actions = [ action Stop 0 ]
            Reasons = [ reason ]
            TimeSec = 0.0
            StopPrevious = true
            ToyId = config.ToyId
        }
