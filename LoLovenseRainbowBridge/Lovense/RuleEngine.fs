namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring

type LovensePlanningPosition =
    {
        NormalizedX: float
        NormalizedY: float
        Confidence: float
        Quadrant: string
        Zone: string
        DetectionMethod: string
    }

type LovenseCommandBuildInput =
    {
        PreviousState: GeneratorState
        Snapshot: BridgeSnapshot
        EvolvedState: GeneratorState
        Breakdown: IntensityBreakdown
        Position: LovensePlanningPosition option
        Now: DateTimeOffset
    }

type LovenseFunctionLayers =
    {
        Base: float
        Timed: float
        Effect: float
        Inherited: float
        Final: int
        Contributions: string list
    }

type LovenseCommandBuilderState =
    {
        CurrentIncarnationId: int
        PreviousIncarnationBase: float
        CurrentBase: float
        MaxBaseThisIncarnation: float
        MinBaseThisIncarnation: float
        LastFunctionState: Map<string, int>
        LastActionString: string option
    }

type LovenseCommandFrame =
    {
        Plan: LovenseCommandPlan
        ActionString: string
        FunctionStates: Map<LovenseActionFunction, LovenseFunctionLayers>
        StateDiff: (string * int) list
        BuilderState: LovenseCommandBuilderState
        Debug: (string * string) list
    }

type ILovenseRuleInterpreter =
    abstract Apply: LovenseCommandBuilderState -> LovenseCommandBuildInput -> LovenseRuleConfig list -> Map<LovenseActionFunction, LovenseFunctionLayers> * LovenseCommandBuilderState

type ILovenseCommandBuilder =
    abstract Build: LovenseCommandBuildInput -> LovenseCommandFrame

module LovenseRuleInternals =

    let emptyLayers =
        {
            Base = 0.0
            Timed = 0.0
            Effect = 0.0
            Inherited = 0.0
            Final = 0
            Contributions = []
        }

    let functionFromConfig value =
        if String.IsNullOrWhiteSpace value then None else LovenseActionCodec.functionFromName value

    let private activePlayerDeathEvents (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind, ev.VictimName with
            | ChampionKill, Some victim ->
                snapshot.ActiveAliases
                |> List.exists (fun alias -> String.Equals(alias, victim, StringComparison.OrdinalIgnoreCase))
            | _ -> false)

    let hasNewActiveDeath previousState snapshot =
        activePlayerDeathEvents snapshot
        |> List.exists (fun ev -> not (previousState.SeenEventIds.Contains ev.EventId))

    let effectKindToken kind =
        match kind with
        | KillPulseEffect -> "KillPulseEffect"
        | ObjectiveWaveEffect -> "ObjectiveWaveEffect"
        | TeamfightBurstEffect -> "TeamfightBurstEffect"
        | AceBurstEffect -> "AceBurstEffect"
        | HeartbeatNearDeathEffect -> "HeartbeatNearDeathEffect"
        | LaningTextureEffect -> "LaningTextureEffect"
        | JungleTensionRampEffect -> "JungleTensionRampEffect"

    let sourceValue (state: LovenseCommandBuilderState) (input: LovenseCommandBuildInput) (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) (source: string) =
        match source with
        | value when String.Equals(value, "Breakdown.BaseIntensity", StringComparison.OrdinalIgnoreCase) ->
            float input.Breakdown.BaseIntensity
        | value when String.Equals(value, "Breakdown.Intensity", StringComparison.OrdinalIgnoreCase) ->
            float input.Breakdown.Intensity
        | value when String.Equals(value, "Breakdown.TemporaryBoost", StringComparison.OrdinalIgnoreCase) ->
            float input.Breakdown.TemporaryBoost
        | value when String.Equals(value, "Breakdown.LiveHealthPercent", StringComparison.OrdinalIgnoreCase) ->
            input.Breakdown.LiveHealthPercent |> Option.defaultValue 1.0
        | value when String.Equals(value, "State.CurrentBase", StringComparison.OrdinalIgnoreCase) ->
            state.CurrentBase
        | value when String.Equals(value, "State.MaxBaseThisIncarnation", StringComparison.OrdinalIgnoreCase) ->
            state.MaxBaseThisIncarnation
        | value when String.Equals(value, "State.MinBaseThisIncarnation", StringComparison.OrdinalIgnoreCase) ->
            state.MinBaseThisIncarnation
        | value when value.StartsWith("FunctionBase:", StringComparison.OrdinalIgnoreCase) ->
            let name = value.Substring("FunctionBase:".Length)
            functionFromConfig name
            |> Option.bind (fun fn -> layers |> Map.tryFind fn)
            |> Option.map (fun layer -> layer.Base + layer.Inherited)
            |> Option.defaultValue 0.0
        | value when value.StartsWith("TemporaryEffect.", StringComparison.OrdinalIgnoreCase) ->
            let name = value.Substring("TemporaryEffect.".Length)
            input.Breakdown.TemporaryEffects
            |> List.filter (fun effect -> String.Equals(effectKindToken effect.Kind, name, StringComparison.OrdinalIgnoreCase))
            |> List.sumBy (fun effect -> float effect.Value)
        | _ ->
            0.0

    let private addContribution name existing =
        if existing.Contributions |> List.contains name then existing.Contributions else name :: existing.Contributions

    let updateLayer fn updater name (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) =
        let current = layers |> Map.tryFind fn |> Option.defaultValue emptyLayers
        layers |> Map.add fn { updater current with Contributions = addContribution name current }

    let applyCurve (curve: string) value =
        match curve.ToUpperInvariant() with
        | "SQUARE" -> value * value / 20.0
        | "SQRT" -> sqrt (max 0.0 value) * sqrt 20.0
        | _ -> value

type LovenseRuleInterpreter() =

    let applyBaseRule state input rule layers =
        match LovenseRuleInternals.functionFromConfig rule.TargetFunction with
        | None -> layers
        | Some fn ->
            let source = LovenseRuleInternals.sourceValue state input layers rule.Source
            let value = source * rule.Value

            LovenseRuleInternals.updateLayer
                fn
                (fun layer ->
                    match rule.Operation.ToUpperInvariant() with
                    | "SET" -> { layer with Base = value }
                    | "ADD" -> { layer with Base = layer.Base + value }
                    | "MULTIPLY" -> { layer with Base = layer.Base * value }
                    | "CLAMPMIN" -> { layer with Base = max layer.Base value }
                    | _ -> layer)
                rule.Name
                layers

    let applyThresholdRule state input rule layers =
        let source = LovenseRuleInternals.sourceValue state input layers rule.Source

        let nextState =
            match rule.Operation.ToUpperInvariant(), rule.StateSlot.ToUpperInvariant() with
            | "TRACKMAX", "MAXBASETHISINCARNATION" ->
                { state with MaxBaseThisIncarnation = max state.MaxBaseThisIncarnation source }
            | "MULTIPLY", "MINBASETHISINCARNATION" ->
                { state with MinBaseThisIncarnation = max state.MinBaseThisIncarnation (source * rule.Value) }
            | _ ->
                state

        let nextLayers =
            match rule.Operation.ToUpperInvariant(), LovenseRuleInternals.functionFromConfig rule.TargetFunction with
            | "CLAMPMIN", Some fn ->
                let minValue = LovenseRuleInternals.sourceValue nextState input layers rule.Source
                LovenseRuleInternals.updateLayer fn (fun layer -> { layer with Base = max layer.Base minValue }) rule.Name layers
            | _ ->
                layers

        nextLayers, nextState

    let applyContributionRule state input rule isEffect layers =
        match LovenseRuleInternals.functionFromConfig rule.TargetFunction with
        | None -> layers
        | Some fn ->
            let source = LovenseRuleInternals.sourceValue state input layers rule.Source
            let value = source * rule.Value

            LovenseRuleInternals.updateLayer
                fn
                (fun layer ->
                    if isEffect then
                        { layer with Effect = layer.Effect + value }
                    else
                        { layer with Timed = layer.Timed + value })
                rule.Name
                layers

    let applyInheritanceRule rule layers =
        match LovenseRuleInternals.functionFromConfig rule.TargetFunction, LovenseRuleInternals.functionFromConfig rule.SourceFunction with
        | Some target, Some source ->
            let sourceLayer = layers |> Map.tryFind source |> Option.defaultValue LovenseRuleInternals.emptyLayers
            LovenseRuleInternals.updateLayer
                target
                (fun layer -> { layer with Inherited = sourceLayer.Base + sourceLayer.Inherited })
                rule.Name
                layers
        | _ ->
            layers

    let positionMatches (position: LovensePlanningPosition option) rule =
        match position with
        | None -> false
        | Some position ->
            String.IsNullOrWhiteSpace rule.When
            || String.Equals(position.Quadrant, rule.When, StringComparison.OrdinalIgnoreCase)
            || String.Equals(position.Zone, rule.When, StringComparison.OrdinalIgnoreCase)

    let applyPositionRule input rule layers =
        if not (positionMatches input.Position rule) then
            layers
        else
            match LovenseRuleInternals.functionFromConfig rule.TargetFunction with
            | None -> layers
            | Some fn ->
                LovenseRuleInternals.updateLayer
                    fn
                    (fun layer ->
                        match rule.Operation.ToUpperInvariant() with
                        | "MULTIPLYINHERITED" ->
                            { layer with Inherited = layer.Inherited * rule.Value }
                        | "ADD" ->
                            { layer with Timed = layer.Timed + rule.Value }
                        | "SET" ->
                            { layer with Inherited = rule.Value }
                        | _ ->
                            layer)
                    rule.Name
                    layers

    let applyStateTransition state input rule =
        if String.Equals(rule.Trigger, "ActiveDeath", StringComparison.OrdinalIgnoreCase)
           && LovenseRuleInternals.hasNewActiveDeath input.PreviousState input.Snapshot then
            let nextBase = max 0.0 (state.CurrentBase - rule.Value)
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
        else
            state

    interface ILovenseRuleInterpreter with
        member _.Apply state input rules =
            let enabled = rules |> List.filter (fun rule -> rule.Enabled)

            let state =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "StateTransition", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun currentState rule -> applyStateTransition currentState input rule) state

            let baseLayers =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "BaseModifier", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun layers rule -> applyBaseRule state input rule layers) Map.empty

            let thresholdLayers, state =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "ThresholdModifier", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun (layers, currentState) rule -> applyThresholdRule currentState input rule layers) (baseLayers, state)

            let inheritedLayers =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "FunctionInheritance", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun layers rule -> applyInheritanceRule rule layers) thresholdLayers

            let timedLayers =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "TimedContribution", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun layers rule -> applyContributionRule state input rule false layers) inheritedLayers

            let effectLayers =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "Effect", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun layers rule -> applyContributionRule state input rule true layers) timedLayers

            let positionedLayers =
                enabled
                |> List.filter (fun rule -> String.Equals(rule.Kind, "PositionModulation", StringComparison.OrdinalIgnoreCase))
                |> List.fold (fun layers rule -> applyPositionRule input rule layers) effectLayers

            positionedLayers, state

type LovenseCommandBuilder(config: LovenseConfig, interpreter: ILovenseRuleInterpreter) =

    let mutable state =
        {
            CurrentIncarnationId = 1
            PreviousIncarnationBase = 0.0
            CurrentBase = 0.0
            MaxBaseThisIncarnation = 0.0
            MinBaseThisIncarnation = 0.0
            LastFunctionState = LovenseActionCodec.emptyState
            LastActionString = None
        }

    let profileFor fn =
        let name = LovenseActionCodec.actionName fn
        config.Mapping.FunctionProfiles
        |> List.tryFind (fun profile -> String.Equals(profile.FunctionName, name, StringComparison.OrdinalIgnoreCase))
        |> Option.defaultValue
            {
                FunctionName = name
                Enabled = fn = Vibrate
                InheritFrom = ""
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
                let layer = layers |> Map.tryFind fn |> Option.defaultValue LovenseRuleInternals.emptyLayers
                let raw =
                    (layer.Base + layer.Inherited) * profile.BaseWeight
                    + layer.Timed * profile.TimedWeight
                    + layer.Effect * profile.EffectWeight
                    |> LovenseRuleInternals.applyCurve profile.Curve

                let final =
                    raw
                    |> Math.Round
                    |> int
                    |> LovenseFunctionRanges.clampWithProfile fn profile.MinOutput profile.MaxOutput

                Some
                    (fn,
                     {
                         layer with
                             Final = final
                     }))
        |> Map.ofList

    let reasonsFromLayers (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) =
        layers
        |> Map.toList
        |> List.collect (fun (_, layer) -> layer.Contributions)
        |> List.distinct
        |> function
            | [] -> [ BasePerformance ]
            | names -> names |> List.map RuleContribution

    interface ILovenseCommandBuilder with
        member _.Build input =
            let layers, nextState = interpreter.Apply state input config.Mapping.Rules
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
                    Reasons = reasonsFromLayers functionStates
                    TimeSec = config.CommandTimeSec
                    StopPrevious = config.Mapping.DefaultStopPrevious
                    ToyId = config.ToyId
                }

            let actionString = LovenseActionCodec.planActionString plan
            let currentFlatState = LovenseActionCodec.stateFromActions plan.Actions
            let diff = LovenseActionCodec.diff state.LastFunctionState currentFlatState

            let vibrateBase =
                functionStates
                |> Map.tryFind Vibrate
                |> Option.map (fun layer -> float layer.Final)
                |> Option.defaultValue input.Breakdown.HealthAdjustedBaseValue

            state <-
                {
                    nextState with
                        CurrentBase = vibrateBase
                        LastFunctionState = currentFlatState
                        LastActionString = Some actionString
                }

            {
                Plan = plan
                ActionString = actionString
                FunctionStates = functionStates
                StateDiff = diff
                BuilderState = state
                Debug =
                    [
                        "incarnationId", string state.CurrentIncarnationId
                        "currentBase", state.CurrentBase.ToString("0.###")
                        "maxBaseThisIncarnation", state.MaxBaseThisIncarnation.ToString("0.###")
                        "minBaseThisIncarnation", state.MinBaseThisIncarnation.ToString("0.###")
                    ]
            }
