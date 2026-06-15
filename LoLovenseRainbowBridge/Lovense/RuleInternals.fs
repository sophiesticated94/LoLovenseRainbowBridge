namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge

module RuleInternals =

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
        match slotName with
        | "CurrentBase" -> { state with CurrentBase = value }
        | "MaxBaseThisIncarnation" -> { state with MaxBaseThisIncarnation = value }
        | "MinBaseThisIncarnation" -> { state with MinBaseThisIncarnation = value }
        | "PreviousIncarnationBase" -> { state with PreviousIncarnationBase = value }
        | _ -> state

    let stateSlotValue slot state =
        let slotName = stateVariableName slot

        match slotName with
        | "CurrentBase" -> state.CurrentBase
        | "MaxBaseThisIncarnation" -> state.MaxBaseThisIncarnation
        | "MinBaseThisIncarnation" -> state.MinBaseThisIncarnation
        | "PreviousIncarnationBase" -> state.PreviousIncarnationBase
        | _ -> 0.0
