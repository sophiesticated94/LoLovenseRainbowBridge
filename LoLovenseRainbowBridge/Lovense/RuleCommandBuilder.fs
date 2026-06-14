namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge

type LovenseCommandValueBuilder(config: LovenseConfig, interpreter: ILovenseRuleInterpreter) =

    let mutable state =
        {
            CurrentIncarnationId = 1
            PreviousIncarnationBase = 0.0
            CurrentBase = 0.0
            MaxBaseThisIncarnation = 0.0
            MinBaseThisIncarnation = 0.0
            Variables = Map.empty
            LastFunctionState = LovenseActionCodec.emptyState
            LastActionString = None
        }

    let mutable lovenseIteration = 0L

    let profileFor fn =
        let name = LovenseActionCodec.actionName fn
        config.Mapping.FunctionProfiles
        |> List.tryFind (fun profile -> String.Equals(profile.FunctionName, name, StringComparison.OrdinalIgnoreCase))
        |> Option.defaultValue
            {
                FunctionName = name
                Enabled = fn = Vibrate
                MinOutput = (LovenseFunctionRanges.get fn).Min
                MaxOutput = (LovenseFunctionRanges.get fn).Max
                BaseWeight = 1.0
                TimedWeight = 1.0
                EffectWeight = 1.0
                Curve = "Linear"
                Smoothing = 0.0
            }

    let action fn value : LovenseAction =
        {
            Function = fn
            Value = LovenseFunctionRanges.clamp fn value
            MaxValue = LovenseFunctionRanges.maxValue fn
            RangeStart = None
        }

    let materializeLayers (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) =
        LovenseActionCodec.canonicalFunctions
        |> List.choose LovenseActionCodec.functionFromName
        |> List.choose (fun fn ->
            let profile = profileFor fn

            if not profile.Enabled || fn = Stop then
                None
            else
                let layer = layers |> Map.tryFind fn |> Option.defaultValue RuleInternals.emptyLayers
                let raw =
                    layer.Base
                    + layer.Timed
                    + layer.Effect
                    + layer.Other
                    |> RuleInternals.applyCurve profile.Curve

                let final =
                    raw
                    |> Math.Round
                    |> int
                    |> LovenseFunctionRanges.clampWithProfile fn 0 profile.MaxOutput

                Some(fn, { layer with Final = final }))
        |> Map.ofList

    let reasonsFromLayers (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) (diagnostics: LovenseRuleDiagnostic list) =
        let ruleReasons =
            layers
            |> Map.toList
            |> List.collect (fun (_, layer) -> layer.Contributions)
            |> List.distinct
            |> List.map RuleContribution

        let diagnosticReasons =
            diagnostics
            |> List.map (fun diagnostic -> RuleContribution $"{diagnostic.RuleName}:error")

        match ruleReasons @ diagnosticReasons with
        | [] -> [ BasePerformance ]
        | reasons -> reasons

    let breakdownFrom variables functionStates =
        let maxFinal =
            functionStates
            |> Map.toList
            |> List.map (fun (_, layer) -> layer.Final)
            |> function
                | [] -> 0
                | values -> values |> List.max

        let vibrateLayer =
            functionStates
            |> Map.tryFind Vibrate
            |> Option.defaultValue RuleInternals.emptyLayers

        let temporary =
            functionStates
            |> Map.toList
            |> List.sumBy (fun (_, layer) -> layer.Timed + layer.Effect + layer.Other)

        {
            PerformanceScore = variables |> Map.tryFind "PerformanceScore" |> Option.defaultValue 0.0
            NormalizedScore = variables |> Map.tryFind "NormalizedScore" |> Option.defaultValue 0.0
            MultikillBase = variables |> Map.tryFind "MultikillCount" |> Option.defaultValue 0.0
            DeathPenalty = variables |> Map.tryFind "DeathPenalty" |> Option.map int |> Option.defaultValue 0
            RawBaseValue = vibrateLayer.Base
            LiveHealthPercent = variables |> Map.tryFind "HealthPercent"
            LiveHealthMultiplier = variables |> Map.tryFind "LiveHealthMultiplier" |> Option.defaultValue 1.0
            HealthPressureMultiplier = variables |> Map.tryFind "HealthPressureMultiplier" |> Option.defaultValue 1.0
            HealthAdjustedBaseValue = vibrateLayer.Base
            BaseIntensity = vibrateLayer.Final
            TemporaryBoost = temporary |> Math.Round |> int
            TemporaryEffects = []
            RawFinalValue = float maxFinal
            Intensity = maxFinal
        }

    interface ILovenseCommandValueBuilder with
        member _.Build input =
            lovenseIteration <- lovenseIteration + 1L
            let stateBeforeBuild =
                {
                    state with
                        Variables =
                            state.Variables
                            |> Map.add "LovenseIteration" (float lovenseIteration)
                            |> Map.add "LoopIteration" (float lovenseIteration)
                }
            let (layers, nextState, diagnostics: LovenseRuleDiagnostic list, traces: LovenseRuleEvaluationTrace list) =
                interpreter.Apply stateBeforeBuild input config.Mapping.Rules
            let functionStates = materializeLayers layers

            let actions =
                functionStates
                |> Map.toList
                |> List.choose (fun (fn, layer) ->
                    if layer.Final <= 0 then None else Some(action fn layer.Final))

            let actions =
                match actions with
                | [] -> [ action Vibrate 0 ]
                | actions -> actions

            let plan =
                {
                    Actions = actions
                    Reasons = reasonsFromLayers functionStates diagnostics
                    TimeSec = config.CommandTimeSec
                    StopPrevious = config.Mapping.DefaultStopPrevious
                    ToyId = config.ToyId
                }

            let actionString = LovenseActionCodec.planActionString plan
            let currentFlatState = LovenseActionCodec.stateFromActions plan.Actions
            let diff = LovenseActionCodec.diff input.LastSentFunctionState currentFlatState
            let changedPlan =
                LovenseActionCodec.planFromStateDiff
                    config
                    (reasonsFromLayers functionStates diagnostics)
                    config.CommandTimeSec
                    config.Mapping.DefaultStopPrevious
                    config.ToyId
                    diff
            let changedActionString = changedPlan |> Option.map LovenseActionCodec.planActionString
            let vibrateBase =
                functionStates
                |> Map.tryFind Vibrate
                |> Option.map (fun layer -> layer.Base)
                |> Option.defaultValue nextState.CurrentBase

            let variables =
                nextState.Variables
                |> Map.add "CurrentBase" vibrateBase

            state <-
                {
                    nextState with
                        CurrentBase = vibrateBase
                        Variables = variables
                        LastFunctionState = input.LastSentFunctionState
                        LastActionString = Some actionString
                }

            {
                Plan = plan
                ChangedPlan = changedPlan
                ActionString = actionString
                ChangedActionString = changedActionString
                FullFunctionState = currentFlatState
                ChangedFunctionState = diff
                FunctionStates = functionStates
                StateDiff = diff
                BuilderState = state
                Breakdown = breakdownFrom variables functionStates
                RuleVariables = variables
                Diagnostics = diagnostics
                RuleTraces = traces
                Debug =
                    [
                        "incarnationId", string state.CurrentIncarnationId
                        "currentBase", state.CurrentBase.ToString("0.###")
                        "maxBaseThisIncarnation", state.MaxBaseThisIncarnation.ToString("0.###")
                        "minBaseThisIncarnation", state.MinBaseThisIncarnation.ToString("0.###")
                    ]
            }
