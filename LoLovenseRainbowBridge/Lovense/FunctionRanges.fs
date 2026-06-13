namespace LoLovenseRainbowBridge.Lovense

open LoLovenseRainbowBridge

type LovenseFunctionRange =
    {
        Min: int
        Max: int
        CommandOnly: bool
    }

module LovenseFunctionRanges =

    let private range minValue maxValue =
        {
            Min = minValue
            Max = maxValue
            CommandOnly = false
        }

    let private commandOnly =
        {
            Min = 0
            Max = 0
            CommandOnly = true
        }

    let all =
        [
            Vibrate, range 0 20
            Vibrate1, range 0 20
            Vibrate2, range 0 20
            Rotate, range 0 20
            Pump, range 0 3
            Thrusting, range 0 20
            Fingering, range 0 20
            Suction, range 0 20
            Depth, range 0 3
            Stroke, range 0 100
            Oscillate, range 0 20
            All, range 0 20
            Stop, commandOnly
        ]
        |> Map.ofList

    let get fn =
        all
        |> Map.tryFind fn
        |> Option.defaultValue (range 0 20)

    let maxValue fn =
        (get fn).Max

    let clamp fn value =
        let bounds = get fn
        Shared.clamp bounds.Min bounds.Max value

    let clampWithProfile fn minOutput maxOutput value =
        let bounds = get fn
        let minValue = max bounds.Min minOutput
        let maxValue = min bounds.Max maxOutput
        Shared.clamp minValue maxValue value
