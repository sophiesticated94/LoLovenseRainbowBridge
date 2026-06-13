namespace LoLovenseRainbowBridge.Lovense

open System
open System.Text.Json.Nodes
open LoLovenseRainbowBridge

module DeviceInfo =

    let knownFunctionNames =
        set
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

    let private tryBoolByNames names (node: JsonNode) =
        names
        |> List.tryPick (fun name ->
            Json.tryGet name node
            |> Option.bind (fun value ->
                try Some(value.GetValue<bool>())
                with _ ->
                    try
                        match value.GetValue<string>() with
                        | text when String.Equals(text, "true", StringComparison.OrdinalIgnoreCase) -> Some true
                        | text when String.Equals(text, "false", StringComparison.OrdinalIgnoreCase) -> Some false
                        | _ -> None
                    with _ ->
                        None))

    let private tryStringByNames names (node: JsonNode) =
        names |> List.tryPick (fun name -> Json.tryString name node)

    let private tryIntByNames names (node: JsonNode) =
        names |> List.tryPick (fun name -> Json.tryInt name node)

    let private findArrayByName name (root: JsonNode) =
        let rec find (node: JsonNode) =
            if isNull node then
                None
            else
                match node with
                | :? JsonObject as object ->
                    object
                    |> Seq.tryPick (fun pair ->
                        if String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) then
                            match pair.Value with
                            | :? JsonArray as array -> Some array
                            | _ -> None
                        else
                            find pair.Value)
                | :? JsonArray as array ->
                    array
                    |> Seq.tryPick (fun item -> if isNull item then None else find item)
                | _ ->
                    None

        find root

    let private toyFromNode (node: JsonNode) =
        {
            Id = tryStringByNames [ "id"; "toyId"; "toy" ] node
            Name = tryStringByNames [ "name" ] node
            ToyType = tryStringByNames [ "toyType"; "type" ] node
            Nickname = tryStringByNames [ "nickName"; "nickname" ] node
            Battery = tryIntByNames [ "battery" ] node
            Connected = tryBoolByNames [ "connected"; "status" ] node
        }

    let parseToyList (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)

            if isNull root then
                []
            else
                match findArrayByName "toyList" root with
                | None -> []
                | Some toyList ->
                    toyList
                    |> Seq.choose (fun item -> if isNull item then None else Some(toyFromNode item))
                    |> List.ofSeq
        with _ ->
            []

    let tryExtractSupportedFunctions (rawText: string) =
        let rec collect (node: JsonNode) =
            if isNull node then
                Set.empty
            else
                match node with
                | :? JsonValue as value ->
                    try
                        let text = value.GetValue<string>()

                        knownFunctionNames
                        |> Seq.filter (fun name -> text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        |> Set.ofSeq
                    with _ ->
                        Set.empty
                | :? JsonArray as array ->
                    array
                    |> Seq.choose (fun item -> if isNull item then None else Some item)
                    |> Seq.map collect
                    |> Seq.fold Set.union Set.empty
                | :? JsonObject as object ->
                    object
                    |> Seq.choose (fun pair -> if isNull pair.Value then None else Some pair.Value)
                    |> Seq.map collect
                    |> Seq.fold Set.union Set.empty
                | _ ->
                    Set.empty

        try
            let root = JsonNode.Parse(rawText)
            let functions = collect root
            if functions.IsEmpty then None else Some functions
        with _ ->
            None

    let parse (rawText: string) =
        let toyList = parseToyList rawText

        {
            ToyList = toyList
            SupportedFunctions = tryExtractSupportedFunctions rawText
        }
