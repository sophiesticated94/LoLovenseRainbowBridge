namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge

module CapabilityResolver =

    let private action fn maxValue value : LovenseAction =
        {
            Function = fn
            Value = Shared.clamp 0 maxValue value
            MaxValue = maxValue
            RangeStart = None
        }

    let private normalizeFunctionSet (values: Set<string>) =
        values |> Set.map (fun value -> value.ToUpperInvariant())

    let stereoWeightsFromNormalizedX (baseValue: int) (normalizedX: float) =
        let x = Shared.clamp01 normalizedX
        let minValue = max 0 (int (Math.Round(float baseValue * 0.5)))

        if x < 0.5 then
            let right = minValue + int (Math.Round((float (baseValue - minValue)) * (x / 0.5)))
            baseValue, right
        elif x > 0.5 then
            let left = baseValue - int (Math.Round((float (baseValue - minValue)) * ((x - 0.5) / 0.5)))
            left, baseValue
        else
            baseValue, baseValue

    let private fallbackVibrateValue (fallback: string) left right =
        match fallback.ToUpperInvariant() with
        | "AVERAGE" -> int (Math.Round((float left + float right) / 2.0))
        | "LEFTONLY" -> left
        | _ -> max left right

    let private supportedFunctionsFromProfiles (profiles: LovenseToyCapabilityProfile list) =
        profiles
        |> List.collect (fun profile -> profile.SupportedFunctions |> Set.toList)
        |> Set.ofList

    let private selectedProfiles (toyId: string option) (profiles: LovenseToyCapabilityProfile list) =
        match toyId |> Option.bind (fun value -> if String.IsNullOrWhiteSpace value then None else Some value) with
        | None -> profiles
        | Some configuredToyId ->
            let matching =
                profiles
                |> List.filter (fun profile ->
                    profile.ToyId
                    |> Option.exists (fun profileToyId -> String.Equals(profileToyId, configuredToyId, StringComparison.OrdinalIgnoreCase)))

            if matching.IsEmpty then profiles else matching

    let private capabilitySource (profiles: LovenseToyCapabilityProfile list) (forced: Set<string>) (supported: Set<string> option) =
        if not forced.IsEmpty then
            "config"
        elif not profiles.IsEmpty then
            "deviceInfo"
        elif supported.IsSome then
            "legacyDeviceInfo"
        else
            "unknown"

    let resolve (config: LovenseConfig) (profiles: LovenseToyCapabilityProfile list) (legacySupportedFunctions: Set<string> option) (plan: LovenseCommandPlan) =
        if not config.Mapping.EnableCapabilityFiltering then
            {
                Plan = plan
                CandidateActions = plan.Actions |> List.map LovenseActionCodec.actionToString
                FinalActions = plan.Actions |> List.map LovenseActionCodec.actionToString
                DroppedActions = []
                StereoApplied = false
                StereoFallbackApplied = false
                CapabilitySource = "disabled"
                ToyProfiles = profiles
                NoSupportedActions = false
            }
        else
            let activeProfiles = selectedProfiles config.ToyId profiles

            let forced =
                config.Mapping.ForceSupportedFunctions
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> Set.ofList

            let stereoForced =
                String.Equals(config.Mapping.StereoMode, "Force", StringComparison.OrdinalIgnoreCase)

            let forced =
                if stereoForced then
                    Set.union forced (set [ Constants.Lovense.Vibrate1Action; Constants.Lovense.Vibrate2Action ])
                else
                    forced

            let supported =
                if not forced.IsEmpty then
                    forced
                elif not activeProfiles.IsEmpty then
                    supportedFunctionsFromProfiles activeProfiles
                else
                    legacySupportedFunctions |> Option.defaultValue Set.empty

            let supported =
                if supported.IsEmpty
                   && String.Equals(config.Mapping.UnknownCapabilityMode, "SafeUniversal", StringComparison.OrdinalIgnoreCase) then
                    set [ Constants.Lovense.VibrateAction; Constants.Lovense.AllAction; Constants.Lovense.StopAction ]
                elif supported.IsEmpty
                     && String.Equals(config.Mapping.UnknownCapabilityMode, "PassThrough", StringComparison.OrdinalIgnoreCase) then
                    plan.Actions
                    |> List.map (fun action -> LovenseActionCodec.actionName action.Function)
                    |> Set.ofList
                else
                    supported

            let supportedUpper = normalizeFunctionSet supported
            let stereoSupported =
                config.Mapping.EnableStereoVibration
                && not (String.Equals(config.Mapping.StereoMode, "Disabled", StringComparison.OrdinalIgnoreCase))
                && (stereoForced
                    || activeProfiles |> List.exists (fun profile -> profile.StereoVibrationSupported)
                    || (supportedUpper.Contains(Constants.Lovense.Vibrate1Action.ToUpperInvariant())
                        && supportedUpper.Contains(Constants.Lovense.Vibrate2Action.ToUpperInvariant())))

            let stereoExpanded =
                plan.Actions
                |> List.collect (fun action ->
                    match action.Function with
                    | Vibrate when stereoSupported ->
                        [
                            { action with Function = Vibrate1; Value = action.Value }
                            { action with Function = Vibrate2; Value = action.Value }
                        ]
                    | _ ->
                        [ action ])

            let stereoFallbackApplied =
                not stereoSupported
                && stereoExpanded |> List.exists (fun action -> action.Function = Vibrate1 || action.Function = Vibrate2)

            let collapsed =
                if stereoSupported then
                    stereoExpanded
                else
                    let left =
                        stereoExpanded
                        |> List.tryFind (fun action -> action.Function = Vibrate1)
                        |> Option.map (fun action -> action.Value)

                    let right =
                        stereoExpanded
                        |> List.tryFind (fun action -> action.Function = Vibrate2)
                        |> Option.map (fun action -> action.Value)

                    let withoutStereo =
                        stereoExpanded
                        |> List.filter (fun action -> action.Function <> Vibrate1 && action.Function <> Vibrate2)

                    match left, right with
                    | Some left, Some right ->
                        action Vibrate config.Mapping.MaxActionIntensity (fallbackVibrateValue config.Mapping.StereoFallback left right) :: withoutStereo
                    | Some left, None ->
                        action Vibrate config.Mapping.MaxActionIntensity left :: withoutStereo
                    | None, Some right ->
                        action Vibrate config.Mapping.MaxActionIntensity right :: withoutStereo
                    | None, None ->
                        withoutStereo

            let kept, dropped =
                collapsed
                |> List.partition (fun action -> supportedUpper.Contains((LovenseActionCodec.actionName action.Function).ToUpperInvariant()))

            let fallbackActions =
                if supportedUpper.Contains(Constants.Lovense.VibrateAction.ToUpperInvariant()) then
                    [ action Vibrate config.Mapping.MaxActionIntensity 0 ]
                elif supportedUpper.Contains(Constants.Lovense.StopAction.ToUpperInvariant()) then
                    [ action Stop 0 0 ]
                else
                    []

            let finalActions =
                match kept with
                | [] -> fallbackActions
                | kept -> kept

            let droppedNames = dropped |> List.map LovenseActionCodec.actionToString
            let finalPlan =
                {
                    plan with
                        Actions = finalActions
                        Reasons = if droppedNames.IsEmpty then plan.Reasons else CapabilityFiltered droppedNames :: plan.Reasons
                }

            {
                Plan = finalPlan
                CandidateActions = plan.Actions |> List.map LovenseActionCodec.actionToString
                FinalActions = finalActions |> List.map LovenseActionCodec.actionToString
                DroppedActions = droppedNames
                StereoApplied = stereoSupported && stereoExpanded <> plan.Actions
                StereoFallbackApplied = stereoFallbackApplied
                CapabilitySource = capabilitySource profiles forced legacySupportedFunctions
                ToyProfiles = activeProfiles
                NoSupportedActions = finalActions.IsEmpty
            }
