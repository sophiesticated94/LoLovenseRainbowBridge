namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge

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
