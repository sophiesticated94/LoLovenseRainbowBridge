namespace LoLovenseRainbowBridge

open System
open System.Net.Http
open System.Globalization
open System.Reflection
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Microsoft.FSharp.Reflection

module Shared =

    let env name fallback =
        let value = Environment.GetEnvironmentVariable(name)
        if String.IsNullOrWhiteSpace value then fallback else value

    let envBool name fallback =
        match env name (string fallback) with
        | "1" | "true" | "TRUE" | "True" | "yes" | "YES" -> true
        | _ -> false

    let inline clamp minValue maxValue value =
        value |> max minValue |> min maxValue

    let clamp01 value =
        clamp 0.0 1.0 value

    let redactUrlSecrets (value: string) =
        if String.IsNullOrWhiteSpace value then
            value
        else
            Regex.Replace(value, "(?i)([?&]ntoken=)[^&\\s\"]+", $"$1{Constants.Lovense.AuthTokenRedacted}")

    let insecureHttpClient () =
        let handler = new HttpClientHandler()

        handler.ServerCertificateCustomValidationCallback <-
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator

        new HttpClient(handler)

module Json =

    let tryGet (name: string) (node: JsonNode) : JsonNode option =
        if isNull node then
            None
        else
            try
                let value = node.Item(name)
                if isNull value then None else Some value
            with _ ->
                None

    let tryString (name: string) (node: JsonNode) : string option =
        tryGet name node
        |> Option.bind (fun value ->
            try Some(value.GetValue<string>())
            with _ -> None)

    let tryInt (name: string) (node: JsonNode) : int option =
        tryGet name node
        |> Option.bind (fun value ->
            try
                Some(value.GetValue<int>())
            with _ ->
                try
                    Some(value.GetValue<float>() |> int)
                with _ ->
                    try
                        Some(value.GetValue<double>() |> int)
                    with _ ->
                        None)

    let tryFloat (name: string) (node: JsonNode) : float option =
        tryGet name node
        |> Option.bind (fun value ->
            try
                Some(value.GetValue<float>())
            with _ ->
                try
                    Some(value.GetValue<double>())
                with _ ->
                    try
                        Some(value.GetValue<int>() |> float)
                    with _ ->
                        None)

    let arrayItems (node: JsonNode) : JsonNode list =
        try
            node.AsArray()
            |> Seq.choose (fun item -> if isNull item then None else Some item)
            |> List.ofSeq
        with _ ->
            []

[<AttributeUsage(AttributeTargets.Field ||| AttributeTargets.Property, AllowMultiple = false)>]
type CalculatorVariableAttribute() =
    inherit Attribute()

    member val Name = "" with get, set

type CommandBuilderCacheState =
    {
        [<field: CalculatorVariable(Name = "IncarnationId")>]
        CurrentIncarnationId: int
        [<field: CalculatorVariable(Name = "PreviousIncarnationBase")>]
        PreviousIncarnationBase: float
        [<field: CalculatorVariable(Name = "CurrentBase")>]
        CurrentBase: float
        [<field: CalculatorVariable(Name = "MaxBaseThisIncarnation")>]
        MaxBaseThisIncarnation: float
        [<field: CalculatorVariable(Name = "MinBaseThisIncarnation")>]
        MinBaseThisIncarnation: float
        [<field: CalculatorVariable(Name = "LovenseIteration")>]
        LovenseIteration: int64
        LastFunctionState: Map<string, int>
        LastActionString: string option
    }

module AppCache =

    type CacheEnvelope<'T> =
        {
            Value: 'T
            Version: int64
            UpdatedAt: DateTimeOffset
        }

    let private tryProjectFloat (now: DateTimeOffset option) (value: obj) =
        let rec loop (current: obj) =
            match current with
            | null -> Some 0.0
            | :? bool as value -> Some(if value then 1.0 else 0.0)
            | :? byte as value -> Some(float value)
            | :? sbyte as value -> Some(float value)
            | :? int16 as value -> Some(float value)
            | :? uint16 as value -> Some(float value)
            | :? int as value -> Some(float value)
            | :? uint32 as value -> Some(float value)
            | :? int64 as value -> Some(float value)
            | :? uint64 as value -> Some(float value)
            | :? single as value -> Some(float value)
            | :? double as value -> Some value
            | :? decimal as value -> Some(float value)
            | :? string as value ->
                match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, parsed -> Some parsed
                | false, _ -> None
            | :? DateTimeOffset as value ->
                now
                |> Option.map (fun current -> max 0.0 (current - value).TotalMilliseconds)
            | _ ->
                let valueType = current.GetType()

                if FSharpType.IsUnion(valueType, true)
                   && valueType.FullName.StartsWith("Microsoft.FSharp.Core.FSharpOption`1", StringComparison.Ordinal) then
                    let case, fields = FSharpValue.GetUnionFields(current, valueType, true)

                    match case.Name, fields with
                    | "Some", [| inner |] -> loop inner
                    | _ -> None
                else
                    try
                        Some(Convert.ToDouble(current, CultureInfo.InvariantCulture))
                    with _ ->
                        None

        loop value

    let private projectMember (now: DateTimeOffset option) (memberInfo: MemberInfo) (getter: unit -> obj) =
        let attribute =
            memberInfo
                .GetCustomAttributes(typeof<CalculatorVariableAttribute>, true)
            |> Seq.cast<CalculatorVariableAttribute>
            |> Seq.tryHead

        match attribute with
        | None -> None
        | Some attr ->
            match tryProjectFloat now (getter ()) with
            | None -> None
            | Some value ->
                let name =
                    match attr.Name with
                    | name when not (String.IsNullOrWhiteSpace name) -> name
                    | _ -> memberInfo.Name

                Some(name, value)

    let projectAnnotated (now: DateTimeOffset option) (source: obj) =
        if isNull source then
            Map.empty
        else
            let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
            let members =
                [
                    for field in source.GetType().GetFields(flags) do
                        yield projectMember now field (fun () -> field.GetValue(source))

                    for propertyInfo in source.GetType().GetProperties(flags) do
                        if propertyInfo.GetIndexParameters().Length = 0 && propertyInfo.CanRead then
                            yield projectMember now propertyInfo (fun () -> propertyInfo.GetValue(source))
                ]
                |> List.choose id

            members |> Map.ofList

    let projectMany (now: DateTimeOffset option) (sources: obj seq) =
        sources
        |> Seq.fold (fun acc source -> projectAnnotated now source |> Map.fold (fun map key value -> map |> Map.add key value) acc) Map.empty

type IAppCache =
    abstract Read: unit -> obj
