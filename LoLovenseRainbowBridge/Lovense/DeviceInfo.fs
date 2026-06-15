namespace LoLovenseRainbowBridge.Lovense

open System
open System.Text.Json.Nodes
open LoLovenseRainbowBridge

module DeviceInfo =

    let knownFunctionNames =
        set
            [
                Constants.Lovense.VibrateAction
                Constants.Lovense.Vibrate1Action
                Constants.Lovense.Vibrate2Action
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

    let private tryByNames reader names (node: JsonNode) =
        names
        |> List.tryPick (fun name -> Json.tryGet name node |> Option.bind reader)

    let private tryStringByNames = tryByNames Json.tryStringValue
    let private tryIntByNames = tryByNames Json.tryIntValue
    let private tryBoolByNames = tryByNames Json.tryBoolValue

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

    let private findValueByName name (root: JsonNode) =
        let rec find (node: JsonNode) =
            if isNull node then
                None
            else
                match node with
                | :? JsonObject as object ->
                    object
                    |> Seq.tryPick (fun pair ->
                        if String.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) then
                            if isNull pair.Value then None else Some pair.Value
                        else
                            find pair.Value)
                | :? JsonArray as array ->
                    array
                    |> Seq.tryPick (fun item -> if isNull item then None else find item)
                | _ ->
                    None

        find root

    let private collectSupportedFunctionsFromNode (node: JsonNode) =
        let rec collect (node: JsonNode) =
            if isNull node then
                Set.empty
            else
                match node with
                | :? JsonValue as value ->
                    let text = value.ToJsonString()

                    if text.Length >= 2 && text.StartsWith("\"", StringComparison.Ordinal) && text.EndsWith("\"", StringComparison.Ordinal) then
                        let text = text.Substring(1, text.Length - 2)

                        knownFunctionNames
                        |> Seq.filter (fun name -> text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                        |> Set.ofSeq
                    else
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

        collect node

    let private toyFromNode (node: JsonNode) =
        {
            Id = tryStringByNames [ "id"; "toyId"; "toy" ] node
            Name = tryStringByNames [ "name" ] node
            ToyType = tryStringByNames [ "toyType"; "type" ] node
            Nickname = tryStringByNames [ "nickName"; "nickname" ] node
            Battery = tryIntByNames [ "battery" ] node
            Connected = tryBoolByNames [ "connected"; "status" ] node
            ExplicitFunctions = collectSupportedFunctionsFromNode node
        }

    let private parseToyCollectionFromNode (toysNode: JsonNode) =
        match toysNode with
        | :? JsonObject as toysObject ->
            toysObject
            |> Seq.choose (fun pair -> if isNull pair.Value then None else Some(toyFromNode pair.Value))
            |> List.ofSeq
        | :? JsonArray as toysArray ->
            toysArray
            |> Seq.choose (fun item -> if isNull item then None else Some(toyFromNode item))
            |> List.ofSeq
        | _ ->
            []

    let parseToyList (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)

            if isNull root then
                []
            else
                match findArrayByName "toyList" root with
                | Some toyList ->
                    toyList
                    |> Seq.choose (fun item -> if isNull item then None else Some(toyFromNode item))
                    |> List.ofSeq
                | None ->
                    match findValueByName "toys" root with
                    | Some toysNode ->
                        let toysNode =
                            match toysNode with
                            | :? JsonValue as value ->
                                match Json.tryStringValue value with
                                | Some text when not (String.IsNullOrWhiteSpace text) ->
                                    try JsonNode.Parse(text) with _ -> toysNode
                                | _ -> toysNode
                            | _ ->
                                toysNode

                        parseToyCollectionFromNode toysNode
                    | None -> []
        with _ ->
            []

    let parseGetToysToyList (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)

            if isNull root then
                []
            else
                match findValueByName "toys" root with
                | None -> []
                | Some toysNode ->
                    let toysNode =
                        match toysNode with
                        | :? JsonValue as value ->
                            match Json.tryStringValue value with
                            | Some text when not (String.IsNullOrWhiteSpace text) ->
                                try JsonNode.Parse(text) with _ -> toysNode
                            | _ -> toysNode
                        | _ ->
                            toysNode

                    parseToyCollectionFromNode toysNode
        with _ ->
            []

    let tryExtractSupportedFunctions (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)
            let functions = collectSupportedFunctionsFromNode root
            if functions.IsEmpty then None else Some functions
        with _ ->
            None

    let private supportedFunctionsFromProfiles (profiles: LovenseToyCapabilityProfile list) =
        let functions =
            profiles
            |> List.collect (fun profile -> profile.SupportedFunctions |> Set.toList)
            |> Set.ofList

        if functions.IsEmpty then None else Some functions

    let private textContains (needle: string) (values: string option list) =
        values
        |> List.exists (fun value ->
            value
            |> Option.exists (fun text -> text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))

    let inferToyCapabilityProfile (toy: LovenseDeviceToy) =
        let identity = [ toy.Name; toy.ToyType; toy.Nickname ]

        let inferred, source, notes =
            if textContains "Gemini" identity || textContains "Edge" identity then
                set
                    [
                        Constants.Lovense.VibrateAction
                        Constants.Lovense.Vibrate1Action
                        Constants.Lovense.Vibrate2Action
                        Constants.Lovense.AllAction
                        Constants.Lovense.StopAction
                    ],
                Inferred,
                [ "Dual vibration inferred from known dual-motor toy family." ]
            elif textContains "Ferri" identity then
                set [ Constants.Lovense.VibrateAction; Constants.Lovense.AllAction; Constants.Lovense.StopAction ],
                Inferred,
                [ "Single vibration inferred from Ferri toy family." ]
            elif textContains "Nora" identity then
                set
                    [
                        Constants.Lovense.VibrateAction
                        Constants.Lovense.RotateAction
                        Constants.Lovense.AllAction
                        Constants.Lovense.StopAction
                    ],
                Inferred,
                [ "Vibrate and Rotate inferred from Nora toy family." ]
            else
                set [ Constants.Lovense.VibrateAction; Constants.Lovense.AllAction; Constants.Lovense.StopAction ],
                SafeFallback,
                [ "Unknown toy type; using safe universal functions." ]

        let supported =
            toy.ExplicitFunctions
            |> Set.union inferred
            |> Set.add Constants.Lovense.AllAction
            |> Set.add Constants.Lovense.StopAction

        let source =
            if not toy.ExplicitFunctions.IsEmpty then Explicit else source

        {
            ToyId = toy.Id
            Name = toy.Name
            ToyType = toy.ToyType
            Nickname = toy.Nickname
            Battery = toy.Battery
            Connected = toy.Connected
            ExplicitFunctions = toy.ExplicitFunctions
            InferredFunctions = inferred
            SupportedFunctions = supported
            StereoVibrationSupported =
                supported.Contains(Constants.Lovense.Vibrate1Action)
                && supported.Contains(Constants.Lovense.Vibrate2Action)
            CapabilitySource = source
            Notes = notes
        }

    let parse (rawText: string) =
        let toyList = parseToyList rawText
        let capabilityProfiles = toyList |> List.map inferToyCapabilityProfile
        let profileFunctions = supportedFunctionsFromProfiles capabilityProfiles
        let legacyFunctions = tryExtractSupportedFunctions rawText
        let supportedFunctions =
            match profileFunctions, legacyFunctions with
            | Some left, Some right -> Some(Set.union left right)
            | Some functions, None
            | None, Some functions -> Some functions
            | None, None -> None
        let root =
            try JsonNode.Parse(rawText)
            with _ -> null

        {
            ToyList = toyList
            CapabilityProfiles = capabilityProfiles
            SupportedFunctions = supportedFunctions
            Domain = if isNull root then None else findValueByName "domain" root |> Option.bind Json.tryStringValue
            HttpsPort = if isNull root then None else findValueByName "httpsPort" root |> Option.bind Json.tryIntValue
            HttpPort = if isNull root then None else findValueByName "httpPort" root |> Option.bind Json.tryIntValue
            WssPort = if isNull root then None else findValueByName "wssPort" root |> Option.bind Json.tryIntValue
        }

    let parseGetToys (rawText: string) =
        let toyList = parseGetToysToyList rawText
        let capabilityProfiles = toyList |> List.map inferToyCapabilityProfile
        {
            ToyList = toyList
            CapabilityProfiles = capabilityProfiles
            SupportedFunctions = supportedFunctionsFromProfiles capabilityProfiles
            Domain = None
            HttpsPort = None
            HttpPort = None
            WssPort = None
        }

    let callbackUid (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)
            if isNull root then None else Json.tryString Constants.Lovense.UserIdField root
        with _ ->
            None

    let callbackUserToken (rawText: string) =
        try
            let root = JsonNode.Parse(rawText)
            if isNull root then None else Json.tryString Constants.Lovense.UserTokenField root
        with _ ->
            None
