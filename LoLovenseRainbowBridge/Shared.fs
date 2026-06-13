namespace LoLovenseRainbowBridge

open System
open System.Net.Http
open System.Text.Json.Nodes

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
