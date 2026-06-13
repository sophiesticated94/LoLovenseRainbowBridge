namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge

module LovenseActionCodec =

    let canonicalFunctions =
        [
            Constants.Lovense.VibrateAction
            Constants.Lovense.RotateAction
            Constants.Lovense.PumpAction
            Constants.Lovense.ThrustingAction
            Constants.Lovense.FingeringAction
            Constants.Lovense.SuctionAction
            Constants.Lovense.DepthAction
            Constants.Lovense.StrokeAction
            Constants.Lovense.OscillateAction
            Constants.Lovense.AllAction
            Constants.Lovense.StopAction
        ]

    let emptyState =
        canonicalFunctions
        |> List.map (fun name -> name, 0)
        |> Map.ofList

    let actionName fn =
        match fn with
        | Vibrate -> Constants.Lovense.VibrateAction
        | Rotate -> Constants.Lovense.RotateAction
        | Pump -> Constants.Lovense.PumpAction
        | Thrusting -> Constants.Lovense.ThrustingAction
        | Fingering -> Constants.Lovense.FingeringAction
        | Suction -> Constants.Lovense.SuctionAction
        | Depth -> Constants.Lovense.DepthAction
        | Stroke -> Constants.Lovense.StrokeAction
        | Oscillate -> Constants.Lovense.OscillateAction
        | All -> Constants.Lovense.AllAction
        | Stop -> Constants.Lovense.StopAction

    let private maxValue fn =
        match fn with
        | Pump
        | Depth -> 3
        | Stroke -> 100
        | _ -> 20

    let actionToString action =
        match action.Function, action.RangeStart with
        | Stop, _ ->
            Constants.Lovense.StopAction
        | Stroke, Some startValue ->
            $"{actionName action.Function}:{startValue}-{action.Value}"
        | _ ->
            $"{actionName action.Function}:{action.Value}"

    let reasonToString reason =
        match reason with
        | CompatibilityVibrate -> "CompatibilityVibrate"
        | BasePerformance -> "BasePerformance"
        | KillBurst eventId -> $"KillBurst:{eventId}"
        | MultikillBurst(eventId, streak) -> $"MultikillBurst:{eventId}:{streak}"
        | DeathReset -> "DeathReset"
        | AssistSupportTexture -> "AssistSupportTexture"
        | HighMomentumTexture -> "HighMomentumTexture"
        | ObjectiveWave -> "ObjectiveWave"
        | TeamfightBurst -> "TeamfightBurst"
        | AceBurst -> "AceBurst"
        | HeartbeatNearDeath -> "HeartbeatNearDeath"
        | LaningTexture -> "LaningTexture"
        | JungleTensionRamp -> "JungleTensionRamp"
        | CapabilityFiltered droppedActions ->
            let joined = String.concat "|" droppedActions
            $"CapabilityFiltered:{joined}"
        | StopCommand -> "StopCommand"

    let planActionString plan =
        match plan.Actions with
        | [] -> Constants.Lovense.StopAction
        | actions ->
            actions
            |> List.map actionToString
            |> String.concat ","

    let functionFromName name =
        match name with
        | value when String.Equals(value, Constants.Lovense.VibrateAction, StringComparison.OrdinalIgnoreCase) -> Some Vibrate
        | value when String.Equals(value, Constants.Lovense.RotateAction, StringComparison.OrdinalIgnoreCase) -> Some Rotate
        | value when String.Equals(value, Constants.Lovense.PumpAction, StringComparison.OrdinalIgnoreCase) -> Some Pump
        | value when String.Equals(value, Constants.Lovense.ThrustingAction, StringComparison.OrdinalIgnoreCase) -> Some Thrusting
        | value when String.Equals(value, Constants.Lovense.FingeringAction, StringComparison.OrdinalIgnoreCase) -> Some Fingering
        | value when String.Equals(value, Constants.Lovense.SuctionAction, StringComparison.OrdinalIgnoreCase) -> Some Suction
        | value when String.Equals(value, Constants.Lovense.DepthAction, StringComparison.OrdinalIgnoreCase) -> Some Depth
        | value when String.Equals(value, Constants.Lovense.StrokeAction, StringComparison.OrdinalIgnoreCase) -> Some Stroke
        | value when String.Equals(value, Constants.Lovense.OscillateAction, StringComparison.OrdinalIgnoreCase) -> Some Oscillate
        | value when String.Equals(value, Constants.Lovense.AllAction, StringComparison.OrdinalIgnoreCase) -> Some All
        | value when String.Equals(value, Constants.Lovense.StopAction, StringComparison.OrdinalIgnoreCase) -> Some Stop
        | _ -> None

    let actionFromToken (token: string) =
        let trimmed = token.Trim()

        if String.IsNullOrWhiteSpace trimmed then
            None
        elif String.Equals(trimmed, Constants.Lovense.StopAction, StringComparison.OrdinalIgnoreCase) then
            Some
                {
                    Function = Stop
                    Value = 0
                    MaxValue = 20
                    RangeStart = None
                }
        else
            let parts = trimmed.Split(':', 2, StringSplitOptions.TrimEntries)

            if parts.Length <> 2 then
                None
            else
                match functionFromName parts[0] with
                | None -> None
                | Some fn ->
                    if fn = Stroke && parts[1].Contains('-', StringComparison.Ordinal) then
                        let rangeParts = parts[1].Split('-', 2, StringSplitOptions.TrimEntries)

                        match Int32.TryParse(rangeParts[0]), Int32.TryParse(rangeParts[1]) with
                        | (true, startValue), (true, endValue) ->
                            let maxValue = maxValue fn

                            Some
                                {
                                    Function = fn
                                    Value = Shared.clamp 0 maxValue endValue
                                    MaxValue = maxValue
                                    RangeStart = Some(Shared.clamp 0 maxValue startValue)
                                }
                        | _ ->
                            None
                    else
                        match Int32.TryParse(parts[1]) with
                        | true, value ->
                            let maxValue = maxValue fn

                            Some
                                {
                                    Function = fn
                                    Value = Shared.clamp 0 maxValue value
                                    MaxValue = maxValue
                                    RangeStart = None
                                }
                        | false, _ ->
                            None

    let parseActionString actionString =
        if String.IsNullOrWhiteSpace actionString then
            []
        else
            actionString.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.choose actionFromToken
            |> Array.toList

    let stateFromActions actions =
        if actions |> List.exists (fun action -> action.Function = Stop) then
            emptyState
            |> Map.add Constants.Lovense.StopAction 1
        else
            actions
            |> List.fold (fun state action ->
                state
                |> Map.add (actionName action.Function) action.Value
                |> Map.add Constants.Lovense.StopAction 0) emptyState

    let stateFromActionString actionString =
        actionString
        |> parseActionString
        |> stateFromActions

    let planFromActionString (config: LovenseConfig) actionString =
        let actions = parseActionString actionString

        if actions |> List.exists (fun action -> action.Function = Stop) then
            {
                Actions = [ { Function = Stop; Value = 0; MaxValue = config.Mapping.MaxActionIntensity; RangeStart = None } ]
                Reasons = [ StopCommand ]
                TimeSec = 0.0
                StopPrevious = true
                ToyId = config.ToyId
            }
        else
            {
                Actions = actions
                Reasons = [ BasePerformance ]
                TimeSec = config.CommandTimeSec
                StopPrevious = config.Mapping.DefaultStopPrevious
                ToyId = config.ToyId
            }

    let diff previous current =
        canonicalFunctions
        |> List.choose (fun name ->
            let oldValue = previous |> Map.tryFind name |> Option.defaultValue 0
            let newValue = current |> Map.tryFind name |> Option.defaultValue 0

            if oldValue <> newValue then
                Some(name, newValue)
            else
                None)

    let applyDiff state changes =
        changes
        |> List.fold (fun acc (name, value) -> acc |> Map.add name value) state
