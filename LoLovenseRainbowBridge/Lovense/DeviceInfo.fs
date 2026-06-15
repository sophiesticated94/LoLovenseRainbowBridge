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
                            try
                                let text = value.GetValue<string>()
                                if String.IsNullOrWhiteSpace text then toysNode else JsonNode.Parse(text)
                            with _ ->
                                toysNode
                        | _ ->
                            toysNode

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
            Domain = if isNull root then None else findValueByName "domain" root |> Option.bind (fun node -> try Some(node.GetValue<string>()) with _ -> None)
            HttpsPort = if isNull root then None else findValueByName "httpsPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
            HttpPort = if isNull root then None else findValueByName "httpPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
            WssPort = if isNull root then None else findValueByName "wssPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
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

    let parseStandardCallback (rawText: string) =
        let toyList = parseGetToysToyList rawText
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
            Domain = if isNull root then None else findValueByName "domain" root |> Option.bind (fun node -> try Some(node.GetValue<string>()) with _ -> None)
            HttpsPort = if isNull root then None else findValueByName "httpsPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
            HttpPort = if isNull root then None else findValueByName "httpPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
            WssPort = if isNull root then None else findValueByName "wssPort" root |> Option.bind (fun node -> try Some(node.GetValue<int>()) with _ -> try Some(int (node.GetValue<string>())) with _ -> None)
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
