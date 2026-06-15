namespace LoLovenseRainbowBridge.Lovense

open System
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
            TimeSec = 0.0
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

    let lolNotRunningIntensity (elapsedMs: int64) =
        let intensity =
            10.0 + Math.Ceiling(Math.Sin(float elapsedMs / 10000.0)) * 5.0
            |> int

        LovenseFunctionRanges.clamp Vibrate intensity

    let lolNotRunningPlan (config: LovenseConfig) (elapsedMs: int64) =
        let intensity = lolNotRunningIntensity elapsedMs

        {
            Actions = [ action Vibrate intensity ]
            Reasons = [ SourceNotConnected ]
            TimeSec = 0.0
            StopPrevious = config.Mapping.DefaultStopPrevious
            ToyId = config.ToyId
        }
