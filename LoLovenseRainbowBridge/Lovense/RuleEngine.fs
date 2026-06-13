namespace LoLovenseRainbowBridge.Lovense

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open NCalc
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
        Position: LovensePlanningPosition option
        Now: DateTimeOffset
        LoopIteration: int64
        LastSentFunctionState: Map<string, int>
        RuntimeContext: LovenseRuntimeRuleContext
        RuntimePollMs: int
    }

and LovenseRuntimeRuleContext =
    {
        LolDataAcquired: bool
        OcrDataAcquired: bool
        LovenseDataAcquired: bool
        LolUnavailableElapsedMs: int64
        OcrUnavailableElapsedMs: int64
        LovenseUnavailableElapsedMs: int64
        LolFailureAttemptsSinceSuccess: int
        OcrFailureAttemptsSinceSuccess: int
        LovenseFailureAttemptsSinceSuccess: int
    }

type LovenseFunctionLayers =
    {
        Base: float
        Timed: float
        Effect: float
        Other: float
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
        Variables: Map<string, float>
        LastFunctionState: Map<string, int>
        LastActionString: string option
    }

type LovenseRuleDiagnostic =
    {
        RuleName: string
        Target: string
        Message: string
    }

type LovenseRuleEvaluationTrace =
    {
        RuleName: string
        Kind: string
        Trigger: string
        Condition: string
        TargetFunctions: string
        ExpandedFunction: string
        Layer: string
        Operation: string
        Expression: string
        Value: float
        MinValue: float
        MaxValue: float
        BeforeLayerValue: float
        AfterLayerValue: float
    }

type LovenseCommandValueFrame =
    {
        Plan: LovenseCommandPlan
        ChangedPlan: LovenseCommandPlan option
        ActionString: string
        ChangedActionString: string option
        FullFunctionState: Map<string, int>
        ChangedFunctionState: (string * int) list
        FunctionStates: Map<LovenseActionFunction, LovenseFunctionLayers>
        StateDiff: (string * int) list
        BuilderState: LovenseCommandBuilderState
        Breakdown: IntensityBreakdown
        RuleVariables: Map<string, float>
        Diagnostics: LovenseRuleDiagnostic list
        RuleTraces: LovenseRuleEvaluationTrace list
        Debug: (string * string) list
    }

type IRuleExpressionEvaluator =
    abstract Evaluate: expression: string -> variables: Map<string, float> -> Result<float, string>

type IRuleInputBuilder =
    abstract Build: state: LovenseCommandBuilderState -> input: LovenseCommandBuildInput -> layers: Map<LovenseActionFunction, LovenseFunctionLayers> -> Map<string, float>

type ILovenseRuleInterpreter =
    abstract Apply: LovenseCommandBuilderState -> LovenseCommandBuildInput -> LovenseRuleConfig list -> Map<LovenseActionFunction, LovenseFunctionLayers> * LovenseCommandBuilderState * LovenseRuleDiagnostic list * LovenseRuleEvaluationTrace list

type ILovenseCommandValueBuilder =
    abstract Build: LovenseCommandBuildInput -> LovenseCommandValueFrame

module LovenseRuleInternals =

    let emptyLayers =
        {
            Base = 0.0
            Timed = 0.0
            Effect = 0.0
            Other = 0.0
            Final = 0
            Contributions = []
        }

    let private keyEquals (left: string) (right: string) =
        String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let functionFromConfig value =
        if String.IsNullOrWhiteSpace value then None else LovenseActionCodec.functionFromName value

    let private activeDeathEvents (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind, ev.VictimName with
            | ChampionKill, Some victim ->
                snapshot.ActiveAliases
                |> List.exists (fun alias -> keyEquals alias victim)
            | _ -> false)

    let hasNewActiveDeath previousState snapshot =
        activeDeathEvents snapshot
        |> List.exists (fun ev -> not (previousState.SeenEventIds.Contains ev.EventId))

    let addContribution name existing =
        if existing.Contributions |> List.contains name then existing.Contributions else name :: existing.Contributions

    let updateLayer fn updater name (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) =
        let current = layers |> Map.tryFind fn |> Option.defaultValue emptyLayers
        layers |> Map.add fn { updater current with Contributions = addContribution name current }

    let applyCurve (curve: string) value =
        match (curve |> Option.ofObj |> Option.defaultValue "Linear").ToUpperInvariant() with
        | "SQUARE" -> value * value / 20.0
        | "SQRT" -> sqrt (max 0.0 value) * sqrt 20.0
        | _ -> value

    let boolVariable variables name =
        variables |> Map.tryFind name |> Option.defaultValue 0.0 |> fun value -> value > 0.0

    let layerVariables (layers: Map<LovenseActionFunction, LovenseFunctionLayers>) =
        layers
        |> Map.toList
        |> List.collect (fun (fn, layer) ->
            let name = LovenseActionCodec.actionName fn
            [
                $"Function.{name}.Base", layer.Base
                $"Function.{name}.Timed", layer.Timed
                $"Function.{name}.Effect", layer.Effect
                $"Function.{name}.Other", layer.Other
                $"Function.{name}.Final", float layer.Final
                $"FunctionBase_{name}", layer.Base
                $"FunctionTimed_{name}", layer.Timed
                $"FunctionEffect_{name}", layer.Effect
                $"FunctionOther_{name}", layer.Other
                $"FunctionFinal_{name}", float layer.Final
            ])
        |> Map.ofList

    let functionRangeVariables () =
        LovenseActionCodec.canonicalFunctions
        |> List.choose LovenseActionCodec.functionFromName
        |> List.collect (fun fn ->
            let name = LovenseActionCodec.actionName fn
            let range = LovenseFunctionRanges.get fn
            [
                $"FunctionMin_{name}", float range.Min
                $"FunctionMax_{name}", float range.Max
            ])
        |> Map.ofList

    let mergeVariables left right =
        right |> Map.fold (fun acc key value -> acc |> Map.add key value) left

    let stateVariableName slot =
        match slot |> Option.ofObj |> Option.defaultValue "" with
        | value when keyEquals value "CurrentBase" -> "CurrentBase"
        | value when keyEquals value "MaxBaseThisIncarnation" -> "MaxBaseThisIncarnation"
        | value when keyEquals value "MinBaseThisIncarnation" -> "MinBaseThisIncarnation"
        | value -> value

    let setStateSlot slot value state =
        let slotName = stateVariableName slot
        let variables = state.Variables |> Map.add slotName value

        match slotName with
        | "CurrentBase" -> { state with CurrentBase = value; Variables = variables }
        | "MaxBaseThisIncarnation" -> { state with MaxBaseThisIncarnation = value; Variables = variables }
        | "MinBaseThisIncarnation" -> { state with MinBaseThisIncarnation = value; Variables = variables }
        | "PreviousIncarnationBase" -> { state with PreviousIncarnationBase = value; Variables = variables }
        | _ -> { state with Variables = variables }

    let stateSlotValue slot state =
        let slotName = stateVariableName slot

        match slotName with
        | "CurrentBase" -> state.CurrentBase
        | "MaxBaseThisIncarnation" -> state.MaxBaseThisIncarnation
        | "MinBaseThisIncarnation" -> state.MinBaseThisIncarnation
        | "PreviousIncarnationBase" -> state.PreviousIncarnationBase
        | _ -> state.Variables |> Map.tryFind slotName |> Option.defaultValue 0.0

type RuleExpressionEvaluator() =
    let powerPattern =
        Regex(@"(\([^()]+\)|[A-Za-z_][A-Za-z0-9_.]*|\d+(?:\.\d+)?)\s*\^\s*(\([^()]+\)|[A-Za-z_][A-Za-z0-9_.]*|\d+(?:\.\d+)?)", RegexOptions.Compiled)

    let normalizePowerOperator expression =
        let rec loop current =
            let next =
                powerPattern.Replace(
                    current,
                    fun (m: Match) ->
                        let left = m.Groups[1].Value
                        let right = m.Groups[2].Value
                        $"Pow({left},{right})"
                )

            if String.Equals(next, current, StringComparison.Ordinal) then next else loop next

        loop expression

    interface IRuleExpressionEvaluator with
        member _.Evaluate expression variables =
            if String.IsNullOrWhiteSpace expression then
                Ok 0.0
            else
                try
                    let e = Expression(normalizePowerOperator expression)

                    for KeyValue(name, value) in variables do
                        e.Parameters[name] <- value

                    let raw = e.Evaluate()

                    match raw with
                    | :? bool as value -> Ok(if value then 1.0 else 0.0)
                    | :? byte as value -> Ok(float value)
                    | :? int16 as value -> Ok(float value)
                    | :? int as value -> Ok(float value)
                    | :? int64 as value -> Ok(float value)
                    | :? single as value -> Ok(float value)
                    | :? double as value -> Ok value
                    | :? decimal as value -> Ok(float value)
                    | null -> Error "Expression evaluated to null."
                    | value ->
                        match Double.TryParse(Convert.ToString(value, Globalization.CultureInfo.InvariantCulture), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                        | true, parsed -> Ok parsed
                        | false, _ -> Error $"Expression evaluated to unsupported value '{value}'."
                with ex ->
                    Error ex.Message

type RuleInputBuilder(scoringConfig: ScoringConfig) =

    let add key value variables = variables |> Map.add key value

    let boolValue value = if value then 1.0 else 0.0

    let recentWithin windowSec (snapshot: BridgeSnapshot) (ev: BridgeEvent) =
        ev.GameTime <= snapshot.GameTime && snapshot.GameTime - ev.GameTime <= windowSec

    let newEvents (input: LovenseCommandBuildInput) =
        input.Snapshot.Events
        |> List.filter (fun ev -> not (input.PreviousState.SeenEventIds.Contains ev.EventId))

    let activeKillEvents input =
        newEvents input
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches input.Snapshot.ActiveAliases ev.ActorName
            | _ -> false)

    let activeDeathEvents input =
        newEvents input
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> nameMatches input.Snapshot.ActiveAliases ev.VictimName
            | _ -> false)

    let activeMultikillStreak input =
        newEvents input
        |> List.choose (fun ev ->
            match ev.Kind with
            | Multikill streak when nameMatches input.Snapshot.ActiveAliases ev.ActorName -> Some streak
            | _ -> None)
        |> function
            | [] -> 0
            | streaks -> streaks |> List.max

    let objectiveValue (ev: BridgeEvent) =
        match ev.Kind with
        | ObjectiveKill(Dragon dragonType, stolen) ->
            let baseValue =
                match dragonType |> Option.map (fun value -> value.ToUpperInvariant()) with
                | Some "ELDER" -> 5
                | _ -> 3

            baseValue + if stolen = Some true then 1 else 0
        | ObjectiveKill(Baron, stolen) -> 5 + if stolen = Some true then 1 else 0
        | ObjectiveKill(Herald, stolen) -> 3 + if stolen = Some true then 1 else 0
        | ObjectiveKill(Turret _, _) -> 1
        | ObjectiveKill(Inhibitor _, _) -> 3
        | _ -> 0

    let objectiveWaveValue input =
        input.Snapshot.Events
        |> List.filter (recentWithin 8.0 input.Snapshot)
        |> List.sumBy objectiveValue

    let teamfightKillCount input =
        input.Snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind with
            | ChampionKill -> recentWithin scoringConfig.TeamfightWindowSec input.Snapshot ev
            | _ -> false)

    let teamfightValue input =
        let kills = teamfightKillCount input

        if kills.Length < scoringConfig.TeamfightKillCountThreshold then
            0
        else
            let involvementBonus =
                if kills |> List.exists (fun ev -> nameMatches input.Snapshot.ActiveAliases ev.ActorName) then 3
                elif kills |> List.exists (activeInvolved input.Snapshot) then 2
                else 1

            min 5 (kills.Length + involvementBonus)

    let aceValue input =
        input.Snapshot.Events
        |> List.exists (fun ev ->
            match ev.Kind with
            | Ace _ -> recentWithin 10.0 input.Snapshot ev
            | _ -> false)
        |> fun hasAce -> if hasAce then 5 else 0

    let lastObjectiveTime predicate (snapshot: BridgeSnapshot) =
        snapshot.Events
        |> List.choose (fun ev -> if predicate ev then Some ev.GameTime else None)
        |> List.sortDescending
        |> List.tryHead

    let tensionValue windowSec now spawnAt =
        if windowSec <= 0.0 || now > spawnAt || spawnAt - now > windowSec then
            0.0
        else
            let progress = 1.0 - ((spawnAt - now) / windowSec)
            Math.Ceiling(progress * 2.0) |> float |> min 2.0 |> max 0.0

    let jungleTensionValue snapshot =
        let now = snapshot.GameTime

        let dragonNext =
            match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Dragon _, _) -> true | _ -> false) snapshot with
            | Some last -> last + scoringConfig.DragonRespawnSec
            | None -> scoringConfig.DragonInitialSpawnSec

        let heraldNext =
            if now <= scoringConfig.HeraldDespawnSec then scoringConfig.HeraldInitialSpawnSec else Double.PositiveInfinity

        let baronNext =
            match lastObjectiveTime (fun ev -> match ev.Kind with | ObjectiveKill(Baron, _) -> true | _ -> false) snapshot with
            | Some last -> last + scoringConfig.BaronRespawnSec
            | None -> scoringConfig.BaronInitialSpawnSec

        [
            tensionValue scoringConfig.ObjectiveTensionWindowSec now dragonNext
            tensionValue scoringConfig.ObjectiveTensionWindowSec now heraldNext
            tensionValue scoringConfig.ObjectiveTensionWindowSec now baronNext
        ]
        |> List.max

    let positionWeights position =
        match position |> Option.map (fun value -> value.Quadrant) with
        | Some value when String.Equals(value, "Center", StringComparison.OrdinalIgnoreCase) -> 1.0, 1.0
        | Some value when String.Equals(value, "TopLeft", StringComparison.OrdinalIgnoreCase) -> 1.35, 0.35
        | Some value when String.Equals(value, "TopRight", StringComparison.OrdinalIgnoreCase) -> 0.35, 1.35
        | Some value when String.Equals(value, "BottomLeft", StringComparison.OrdinalIgnoreCase) -> 0.0, 0.0
        | Some value when String.Equals(value, "BottomRight", StringComparison.OrdinalIgnoreCase) -> 0.55, 1.05
        | Some value when String.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) -> 1.15, 0.65
        | Some value when String.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) -> 0.65, 1.15
        | Some _ ->
            match position with
            | Some p -> CapabilityResolver.stereoWeightsFromNormalizedX 100 p.NormalizedX |> fun (l, r) -> float l / 100.0, float r / 100.0
            | None -> 1.0, 1.0
        | None -> 1.0, 1.0

    interface IRuleInputBuilder with
        member _.Build state input layers =
            let snapshot = input.Snapshot
            let active = snapshot.ActivePlayer
            let hp = healthPercent snapshot
            let missingHealth = hp |> Option.map (fun value -> 1.0 - value) |> Option.defaultValue 0.0
            let rawScore = rawPerformanceScore scoringConfig active
            let normalizedScore = normalizeActivePlayerScore scoringConfig snapshot
            let activeKills = activeKillEvents input
            let activeDeaths = activeDeathEvents input
            let multikillStreak = activeMultikillStreak input
            let teamfightKills = teamfightKillCount input
            let leftWeight, rightWeight = positionWeights input.Position
            let heartbeatAmplitude = missingHealth * scoringConfig.HeartbeatPulseMaxAmplitude
            let safePollMs = max 1 input.RuntimePollMs
            let iterationsPerSecond = max 1.0 (Math.Round(1000.0 / float safePollMs))
            let loopIteration =
                state.Variables
                |> Map.tryFind "LovenseIteration"
                |> Option.defaultValue (float input.LoopIteration)
            let loopIterationWithinSecond = float ((int64 loopIteration) % int64 iterationsPerSecond)
            let loopTimeSec = loopIteration * float safePollMs / 1000.0

            Map.empty
            |> add "Kills" (float active.Kills)
            |> add "Deaths" (float active.Deaths)
            |> add "Assists" (float active.Assists)
            |> add "CreepScore" (float active.CreepScore)
            |> add "CS" (float active.CreepScore)
            |> add "WardScore" active.WardScore
            |> add "Level" (float active.Level)
            |> add "CurrentHealth" (active.CurrentHealth |> Option.defaultValue 0.0)
            |> add "MaxHealth" (active.MaxHealth |> Option.defaultValue 0.0)
            |> add "HealthPercent" (hp |> Option.defaultValue 1.0)
            |> add "MissingHealth" missingHealth
            |> add "GameTime" snapshot.GameTime
            |> add "RawPerformanceScore" rawScore
            |> add "NormalizedScore" normalizedScore
            |> add "PerformanceScore" (normalizedScore * scoringConfig.NormalizedScoreWeight)
            |> add "DeathPenalty" (float active.Deaths * scoringConfig.DeathWeight)
            |> add "LiveHealthMultiplier" (scoringConfig.HealthMinMultiplier + (1.0 - scoringConfig.HealthMinMultiplier) * (hp |> Option.defaultValue 1.0))
            |> add "HealthPressureMultiplier" input.EvolvedState.HealthPressure.PressureMultiplier
            |> add "ActiveKill" (boolValue (not activeKills.IsEmpty))
            |> add "ActiveKillCount" (float activeKills.Length)
            |> add "ActiveDeath" (boolValue (not activeDeaths.IsEmpty))
            |> add "ActiveDeathCount" (float activeDeaths.Length)
            |> add "ActiveMultikill" (boolValue (multikillStreak > 0))
            |> add "MultikillCount" (float multikillStreak)
            |> add "TotalMultikillCount" (float input.EvolvedState.MultikillCount)
            |> add "ObjectiveWaveValue" (objectiveWaveValue input |> float)
            |> add "TeamfightKillCount" (float teamfightKills.Length)
            |> add "TeamfightBurstValue" (teamfightValue input |> float)
            |> add "AceBurstValue" (aceValue input |> float)
            |> add "HeartbeatAmplitude" heartbeatAmplitude
            |> add "LowHealthHeartbeatThreshold" scoringConfig.LowHealthHeartbeatThreshold
            |> add "CriticalHealthHeartbeatThreshold" scoringConfig.CriticalHealthHeartbeatThreshold
            |> add "HeartbeatPulseMaxAmplitude" scoringConfig.HeartbeatPulseMaxAmplitude
            |> add "HeartbeatPulseCycleSec" scoringConfig.HeartbeatPulseCycleSec
            |> add "HeartbeatPulseStartPhase" scoringConfig.HeartbeatPulseStartPhase
            |> add "HeartbeatPulsePeakPhase" scoringConfig.HeartbeatPulsePeakPhase
            |> add "HeartbeatPulseEndPhase" scoringConfig.HeartbeatPulseEndPhase
            |> add "LaningTextureValue" (if snapshot.GameTime <= scoringConfig.LaningPhaseEndSec then min 2.0 (float active.CreepScore / 75.0 + active.WardScore / 25.0 + float active.Assists / 4.0) else 0.0)
            |> add "JungleTensionValue" (jungleTensionValue snapshot)
            |> add "LoopIteration" loopIteration
            |> add "LoopIterationWithinSecond" loopIterationWithinSecond
            |> add "LoopIterationsPerSecond" iterationsPerSecond
            |> add "LoopTimeSec" loopTimeSec
            |> add "LovenseIteration" loopIteration
            |> add "RuntimePollMs" (float safePollMs)
            |> add "LolDataAcquired" (boolValue input.RuntimeContext.LolDataAcquired)
            |> add "OcrDataAcquired" (boolValue input.RuntimeContext.OcrDataAcquired)
            |> add "LovenseDataAcquired" (boolValue input.RuntimeContext.LovenseDataAcquired)
            |> add "LolUnavailableElapsedMs" (float input.RuntimeContext.LolUnavailableElapsedMs)
            |> add "OcrUnavailableElapsedMs" (float input.RuntimeContext.OcrUnavailableElapsedMs)
            |> add "LovenseUnavailableElapsedMs" (float input.RuntimeContext.LovenseUnavailableElapsedMs)
            |> add "LolFailureAttemptsSinceSuccess" (float input.RuntimeContext.LolFailureAttemptsSinceSuccess)
            |> add "OcrFailureAttemptsSinceSuccess" (float input.RuntimeContext.OcrFailureAttemptsSinceSuccess)
            |> add "LovenseFailureAttemptsSinceSuccess" (float input.RuntimeContext.LovenseFailureAttemptsSinceSuccess)
            |> add "Pi" Math.PI
            |> add "CurrentBase" state.CurrentBase
            |> add "MaxBaseThisIncarnation" state.MaxBaseThisIncarnation
            |> add "MinBaseThisIncarnation" state.MinBaseThisIncarnation
            |> add "PreviousIncarnationBase" state.PreviousIncarnationBase
            |> add "IncarnationId" (float state.CurrentIncarnationId)
            |> add "PositionAvailable" (boolValue input.Position.IsSome)
            |> add "PositionX" (input.Position |> Option.map (fun p -> p.NormalizedX) |> Option.defaultValue 0.5)
            |> add "PositionY" (input.Position |> Option.map (fun p -> p.NormalizedY) |> Option.defaultValue 0.5)
            |> add "PositionConfidence" (input.Position |> Option.map (fun p -> p.Confidence) |> Option.defaultValue 0.0)
            |> add "PositionLeftWeight" leftWeight
            |> add "PositionRightWeight" rightWeight
            |> LovenseRuleInternals.mergeVariables state.Variables
            |> LovenseRuleInternals.mergeVariables (LovenseRuleInternals.layerVariables layers)
            |> LovenseRuleInternals.mergeVariables (LovenseRuleInternals.functionRangeVariables ())
            |> fun variables ->
                LovenseActionCodec.emptyState
                |> Map.fold (fun acc name value ->
                    let previous = input.LastSentFunctionState |> Map.tryFind name |> Option.defaultValue value
                    acc |> Map.add $"PreviousFunction_{name}" (float previous)) variables

type LovenseRuleInterpreter(inputBuilder: IRuleInputBuilder, evaluator: IRuleExpressionEvaluator) =

    let triggerMatches input variables trigger =
        match trigger |> Option.ofObj |> Option.defaultValue "" with
        | value when String.IsNullOrWhiteSpace value -> true
        | value when String.Equals(value, "ActiveDeath", StringComparison.OrdinalIgnoreCase) ->
            LovenseRuleInternals.hasNewActiveDeath input.PreviousState input.Snapshot
        | value ->
            LovenseRuleInternals.boolVariable variables value

    let conditionMatches input variables (conditionValue: string) =
        if String.IsNullOrWhiteSpace conditionValue then
            true
        else
            match input.Position with
            | Some position when String.Equals(position.Quadrant, conditionValue, StringComparison.OrdinalIgnoreCase)
                                 || String.Equals(position.Zone, conditionValue, StringComparison.OrdinalIgnoreCase) ->
                true
            | _ ->
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
        let current = layers |> Map.tryFind fn |> Option.defaultValue LovenseRuleInternals.emptyLayers
        let before = layerValue rule.Layer current
        let updated =
            match (rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
            | "BASE" -> { current with Base = applyLayerOperation rule.Operation value current.Base }
            | "TIMED" -> { current with Timed = applyLayerOperation rule.Operation value current.Timed }
            | "EFFECT" -> { current with Effect = applyLayerOperation rule.Operation value current.Effect }
            | "OTHER" -> { current with Other = applyLayerOperation rule.Operation value current.Other }
            | _ -> current
        let after = layerValue rule.Layer updated
        let updated = { updated with Contributions = LovenseRuleInternals.addContribution rule.Name current }
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
        let current = LovenseRuleInternals.stateSlotValue rule.StateSlot state

        match (rule.Operation |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
        | "SET" -> LovenseRuleInternals.setStateSlot rule.StateSlot value state
        | "ADD" -> LovenseRuleInternals.setStateSlot rule.StateSlot (current + value) state
        | "SUBTRACT" -> LovenseRuleInternals.setStateSlot rule.StateSlot (current - value) state
        | "MULTIPLY" -> LovenseRuleInternals.setStateSlot rule.StateSlot (current * value) state
        | "TRACKMAX" -> LovenseRuleInternals.setStateSlot rule.StateSlot (max current value) state
        | "TRACKMIN" -> LovenseRuleInternals.setStateSlot rule.StateSlot (min current value) state
        | "CLAMPMIN" -> LovenseRuleInternals.setStateSlot rule.StateSlot (max current value) state
        | "CLAMPMAX" -> LovenseRuleInternals.setStateSlot rule.StateSlot (min current value) state
        | "CLEAR" -> LovenseRuleInternals.setStateSlot rule.StateSlot 0.0 state
        | "STARTINCARNATION" ->
            let nextBase = max 0.0 value
            {
                state with
                    CurrentIncarnationId = state.CurrentIncarnationId + 1
                    PreviousIncarnationBase = state.CurrentBase
                    CurrentBase = nextBase
                    MaxBaseThisIncarnation = nextBase
                    MinBaseThisIncarnation = 0.0
                    Variables =
                        state.Variables
                        |> Map.add "CurrentBase" nextBase
                        |> Map.add "PreviousIncarnationBase" state.CurrentBase
                        |> Map.add "MaxBaseThisIncarnation" nextBase
                        |> Map.add "MinBaseThisIncarnation" 0.0
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
                 traces: LovenseRuleEvaluationTrace list)
                (rule: LovenseRuleConfig)
                =
                let variables =
                    inputBuilder.Build state input layers

                if not (triggerMatches input variables rule.Trigger) then
                    layers, { state with Variables = variables }, diagnostics, traces
                else
                    match (rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant() with
                    | "STATE" ->
                        if not (conditionMatches input variables rule.Condition) then
                            layers, { state with Variables = variables }, diagnostics, traces
                        else
                        match evaluator.Evaluate rule.Expression variables with
                        | Error message ->
                            let diagnostic =
                                {
                                    RuleName = rule.Name
                                    Target = ruleTargetName rule
                                    Message = message
                                }

                            layers, { state with Variables = variables }, diagnostic :: diagnostics, traces
                        | Ok value ->
                            layers, applyStateTarget rule value { state with Variables = variables }, diagnostics, traces
                    | _ ->
                        targetFunctions rule
                        |> List.fold (fun (layers, state, diagnostics: LovenseRuleDiagnostic list, traces: LovenseRuleEvaluationTrace list) fn ->
                            let variables =
                                inputBuilder.Build state input layers
                                |> targetScopedVariables fn

                            if not (conditionMatches input variables rule.Condition) then
                                layers, { state with Variables = variables }, diagnostics, traces
                            else
                            match evaluator.Evaluate rule.Expression variables with
                            | Error message ->
                                let diagnostic =
                                    {
                                        RuleName = rule.Name
                                        Target = LovenseActionCodec.actionName fn
                                        Message = message
                                    }

                                layers, { state with Variables = variables }, diagnostic :: diagnostics, traces
                            | Ok value ->
                                let nextLayers, before, after = applyFunctionTarget rule fn value layers
                                let minValue = variables |> Map.tryFind "MinValue" |> Option.defaultValue 0.0
                                let maxValue = variables |> Map.tryFind "MaxValue" |> Option.defaultValue 0.0
                                let trace = evaluationTrace rule fn value minValue maxValue before after
                                nextLayers, { state with Variables = variables }, diagnostics, trace :: traces)
                            (layers, state, diagnostics, traces)

            enabled
            |> List.fold folder (Map.empty, state, ([]: LovenseRuleDiagnostic list), ([]: LovenseRuleEvaluationTrace list))
            |> fun (layers, state, diagnostics, traces) -> layers, state, (diagnostics |> List.rev), (traces |> List.rev)

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
                let layer = layers |> Map.tryFind fn |> Option.defaultValue LovenseRuleInternals.emptyLayers
                let raw =
                    layer.Base
                    + layer.Timed
                    + layer.Effect
                    + layer.Other
                    |> LovenseRuleInternals.applyCurve profile.Curve

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
            |> Option.defaultValue LovenseRuleInternals.emptyLayers

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
