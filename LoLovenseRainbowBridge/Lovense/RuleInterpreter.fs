namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge

type LovenseRuleInterpreter(inputBuilder: IRuleInputBuilder, evaluator: IRuleExpressionEvaluator) =

    let triggerMatches input variables trigger =
        match trigger |> Option.ofObj |> Option.defaultValue "" with
        | value when String.IsNullOrWhiteSpace value -> true
        | value ->
            RuleInternals.boolVariable variables value

    let conditionMatches input variables (conditionValue: string) =
        if String.IsNullOrWhiteSpace conditionValue then
            true
        else
            match evaluator.Evaluate conditionValue variables with
            | Ok value -> value <> 0.0
            | Error _ -> false

    let ruleTargetName (rule: LovenseRuleConfig) =
        if String.Equals(rule.Layer, "State", StringComparison.OrdinalIgnoreCase) then
            rule.StateSlot
        else
            rule.TargetFunctions

    let targetFunctions (rule: LovenseRuleConfig) =
        rule.TargetFunctions.Split('|', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.choose LovenseActionCodec.functionFromName
        |> Array.toList

    let targetScopedVariables fn variables =
        let range = LovenseFunctionRanges.get fn
        variables
        |> Map.add "MinValue" (float range.Min)
        |> Map.add "MaxValue" (float range.Max)

    let applyLayerOperation operation value layer =
        match operation |> Option.ofObj |> Option.defaultValue "" |> fun v -> v.ToUpperInvariant() with
        | "SET" -> value
        | "ADD" -> layer + value
        | "SUBTRACT" -> layer - value
        | "MULTIPLY" -> layer * value
        | "CLAMPMIN" -> max layer value
        | "CLAMPMAX" -> min layer value
        | "CLEAR" -> 0.0
        | _ -> layer

    let layerValue layerName (layer: LovenseFunctionLayers) =
        match (layerName |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
        | "BASE" -> layer.Base
        | "TIMED" -> layer.Timed
        | "EFFECT" -> layer.Effect
        | "OTHER" -> layer.Other
        | _ -> 0.0

    let applyFunctionTarget (rule: LovenseRuleConfig) fn value layers =
        let current = layers |> Map.tryFind fn |> Option.defaultValue RuleInternals.emptyLayers
        let before = layerValue rule.Layer current
        let updated =
            match (rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
            | "BASE" -> { current with Base = applyLayerOperation rule.Operation value current.Base }
            | "TIMED" -> { current with Timed = applyLayerOperation rule.Operation value current.Timed }
            | "EFFECT" -> { current with Effect = applyLayerOperation rule.Operation value current.Effect }
            | "OTHER" -> { current with Other = applyLayerOperation rule.Operation value current.Other }
            | _ -> current
        let after = layerValue rule.Layer updated
        let updated = { updated with Contributions = RuleInternals.addContribution rule.Name current }
        layers |> Map.add fn updated, before, after

    let evaluationTrace (rule: LovenseRuleConfig) fn value minValue maxValue before after =
        {
            RuleName = rule.Name
            Kind = rule.Kind
            Trigger = rule.Trigger
            Condition = rule.Condition
            TargetFunctions = rule.TargetFunctions
            ExpandedFunction = LovenseActionCodec.actionName fn
            Layer = rule.Layer
            Operation = rule.Operation
            Expression = rule.Expression
            Value = value
            MinValue = minValue
            MaxValue = maxValue
            BeforeLayerValue = before
            AfterLayerValue = after
        }

    let applyStateTarget (rule: LovenseRuleConfig) value state =
        let current = RuleInternals.stateSlotValue rule.StateSlot state

        match (rule.Operation |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
        | "SET" -> RuleInternals.setStateSlot rule.StateSlot value state
        | "ADD" -> RuleInternals.setStateSlot rule.StateSlot (current + value) state
        | "SUBTRACT" -> RuleInternals.setStateSlot rule.StateSlot (current - value) state
        | "MULTIPLY" -> RuleInternals.setStateSlot rule.StateSlot (current * value) state
        | "TRACKMAX" -> RuleInternals.setStateSlot rule.StateSlot (max current value) state
        | "TRACKMIN" -> RuleInternals.setStateSlot rule.StateSlot (min current value) state
        | "CLAMPMIN" -> RuleInternals.setStateSlot rule.StateSlot (max current value) state
        | "CLAMPMAX" -> RuleInternals.setStateSlot rule.StateSlot (min current value) state
        | "CLEAR" -> RuleInternals.setStateSlot rule.StateSlot 0.0 state
        | "STARTINCARNATION" ->
            let nextBase = max 0.0 value
            {
                state with
                    CurrentIncarnationId = state.CurrentIncarnationId + 1
                    PreviousIncarnationBase = state.CurrentBase
                    CurrentBase = nextBase
                    MaxBaseThisIncarnation = nextBase
                    MinBaseThisIncarnation = 0.0
                    LastFunctionState = LovenseActionCodec.emptyState
                    LastActionString = None
            }
        | _ -> state

    interface ILovenseRuleInterpreter with
        member _.Apply state input rules =
            let enabled =
                rules
                |> List.filter (fun rule -> rule.Enabled)
                |> List.sortBy (fun rule ->
                    match (rule.Kind |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
                    | "STATETRANSITION" -> 0
                    | "BASEMODIFIER" -> 1
                    | "THRESHOLDMODIFIER" -> 2
                    | _ -> 3)

            let folder
                (layers: Map<LovenseActionFunction, LovenseFunctionLayers>,
                 state: LovenseCommandBuilderState,
                 diagnostics: LovenseRuleDiagnostic list,
                 traces: LovenseRuleEvaluationTrace list,
                 variables: Map<string, float>)
                (rule: LovenseRuleConfig)
                =
                let projectedVariables = inputBuilder.Build state input layers
                let variables =
                    RuleInternals.mergeVariables
                        projectedVariables
                        (RuleInternals.evaluationVariables layers input.LastSentFunctionState)

                if not (triggerMatches input variables rule.Trigger) then
                    layers, state, diagnostics, traces, variables
                else
                    match (rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
                    | "STATE" ->
                        if not (conditionMatches input variables rule.Condition) then
                            layers, state, diagnostics, traces, variables
                        else
                            match evaluator.Evaluate rule.Expression variables with
                            | Error message ->
                                let diagnostic =
                                    {
                                        RuleName = rule.Name
                                        Target = ruleTargetName rule
                                        Message = message
                                    }

                                layers, state, diagnostic :: diagnostics, traces, variables
                            | Ok value ->
                                layers, applyStateTarget rule value state, diagnostics, traces, variables
                    | _ ->
                        let folder
                            (layers: Map<LovenseActionFunction, LovenseFunctionLayers>,
                             state: LovenseCommandBuilderState,
                             diagnostics: LovenseRuleDiagnostic list,
                             traces: LovenseRuleEvaluationTrace list,
                             _)
                            fn =
                            let projectedVariables = inputBuilder.Build state input layers
                            let variables =
                                RuleInternals.mergeVariables
                                    projectedVariables
                                    (RuleInternals.evaluationVariables layers input.LastSentFunctionState)
                                |> targetScopedVariables fn

                            if not (conditionMatches input variables rule.Condition) then
                                layers, state, diagnostics, traces, variables
                            else
                                match evaluator.Evaluate rule.Expression variables with
                                | Error message ->
                                    let diagnostic =
                                        {
                                            RuleName = rule.Name
                                            Target = LovenseActionCodec.actionName fn
                                            Message = message
                                        }

                                    layers, state, diagnostic :: diagnostics, traces, variables
                                | Ok value ->
                                    let nextLayers, before, after = applyFunctionTarget rule fn value layers
                                    let minValue = variables |> Map.tryFind "MinValue" |> Option.defaultValue 0.0
                                    let maxValue = variables |> Map.tryFind "MaxValue" |> Option.defaultValue 0.0
                                    let trace = evaluationTrace rule fn value minValue maxValue before after
                                    nextLayers, state, diagnostics, trace :: traces, variables

                        targetFunctions rule |> List.fold folder (layers, state, diagnostics, traces, variables)

            enabled
            |> List.fold folder (Map.empty, state, ([]: LovenseRuleDiagnostic list), ([]: LovenseRuleEvaluationTrace list), Map.empty)
            |> fun (layers, state, diagnostics, traces, variables) -> layers, state, (diagnostics |> List.rev), (traces |> List.rev), variables
