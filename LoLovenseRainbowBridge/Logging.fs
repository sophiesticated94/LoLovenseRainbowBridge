namespace LoLovenseRainbowBridge

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open System.Text.Json
open System.Text.Json.Nodes

type LogLevel =
    | Trace = 0
    | Debug = 1
    | Info = 2
    | Warn = 3
    | Error = 4

type StructuredSessionLogger(config: LoggingConfig) =

    let parseLevel value =
        match value with
        | null -> LogLevel.Debug
        | value when String.Equals(value, "Trace", StringComparison.OrdinalIgnoreCase) -> LogLevel.Trace
        | value when String.Equals(value, "Debug", StringComparison.OrdinalIgnoreCase) -> LogLevel.Debug
        | value when String.Equals(value, "Info", StringComparison.OrdinalIgnoreCase) -> LogLevel.Info
        | value when String.Equals(value, "Warn", StringComparison.OrdinalIgnoreCase) -> LogLevel.Warn
        | value when String.Equals(value, "Error", StringComparison.OrdinalIgnoreCase) -> LogLevel.Error
        | _ -> LogLevel.Debug

    let trackLevel = parseLevel config.TrackLogLevel
    let sessionName = DateTime.Now.ToString(config.SessionDirectoryFormat)
    let sessionDirectory = Path.Combine(Path.GetFullPath config.BaseDirectory, sessionName)
    let trackPath = Path.Combine(sessionDirectory, "track.log")
    let leaguePath = Path.Combine(sessionDirectory, "lol.log")
    let lovensePath = Path.Combine(sessionDirectory, "lovense.log")

    let compactOptions = JsonSerializerOptions(WriteIndented = false)
    let prettyOptions = JsonSerializerOptions(WriteIndented = config.RawLogPrettyPrint)
    let gate = obj ()

    let trackWriter =
        Directory.CreateDirectory(sessionDirectory) |> ignore
        new StreamWriter(File.Open(trackPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))

    let leagueWriter =
        if config.LogRawLeague then
            Some(new StreamWriter(File.Open(leaguePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
        else
            None

    let lovenseWriter =
        if config.LogRawLovense then
            Some(new StreamWriter(File.Open(lovensePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)))
        else
            None

    let dataToNode (data: obj option) =
        match data with
        | None -> null
        | Some value ->
            try
                JsonSerializer.SerializeToNode(value, compactOptions)
            with ex ->
                JsonObject(
                    [
                        KeyValuePair<string, JsonNode>("loggingSerializationError", JsonValue.Create ex.Message)
                        KeyValuePair<string, JsonNode>("valueType", JsonValue.Create(value.GetType().FullName))
                        KeyValuePair<string, JsonNode>("value", JsonValue.Create(string value))
                    ]
                )

    let normalizeRawJson (rawText: string) =
        if not config.RawLogPrettyPrint then
            rawText
        else
            try
                use document = JsonDocument.Parse(rawText)
                JsonSerializer.Serialize(document.RootElement, prettyOptions)
            with _ ->
                rawText

    let redactSensitiveJson (rawText: string) =
        let sensitiveKeys = set [ "authtoken"; "token"; "utoken" ]

        let rec redactNode (node: JsonNode) =
            if isNull node then
                ()
            else
                match node with
                | :? JsonObject as object ->
                    let keys = object |> Seq.map (fun pair -> pair.Key) |> List.ofSeq

                    for key in keys do
                        if sensitiveKeys.Contains(key.ToLowerInvariant()) then
                            object[key] <- JsonValue.Create(Constants.Lovense.AuthTokenRedacted)
                        else
                            redactNode object[key]
                | :? JsonArray as array ->
                    for item in array do
                        redactNode item
                | _ ->
                    ()

        try
            let root = JsonNode.Parse(rawText)

            if isNull root then
                rawText
            else
                redactNode root
                root.ToJsonString(compactOptions)
        with _ ->
            Regex.Replace(
                rawText,
                """(?i)(""(?:authToken|token|utoken)""\s*:\s*"")[^""]*("")""",
                $"$1{Constants.Lovense.AuthTokenRedacted}$2"
            )

    let writeLine (writer: StreamWriter) (entry: JsonObject) =
        writer.WriteLine(entry.ToJsonString(compactOptions))
        writer.Flush()

    let writeRaw (writerOption: StreamWriter option) (eventName: string) (message: string) (data: obj) =
        try
            match writerOption with
            | None -> ()
            | Some writer ->
                let entry =
                    JsonObject(
                        [
                            KeyValuePair<string, JsonNode>("timestamp", JsonValue.Create(DateTimeOffset.Now.ToString("O")))
                            KeyValuePair<string, JsonNode>("event", JsonValue.Create(eventName))
                            KeyValuePair<string, JsonNode>("message", JsonValue.Create(message))
                            KeyValuePair<string, JsonNode>("data", dataToNode (Some data))
                        ]
                    )

                lock gate (fun () -> writeLine writer entry)
        with _ ->
            ()

    member _.SessionDirectory = sessionDirectory
    member _.TrackPath = trackPath
    member _.LeaguePath = leaguePath
    member _.LovensePath = lovensePath
    member _.IsRawLeagueEnabled = config.LogRawLeague
    member _.IsRawLovenseEnabled = config.LogRawLovense

    member _.Log(level: LogLevel, eventName: string, message: string, ?data: obj) =
        try
            if int level >= int trackLevel then
                let entry =
                    JsonObject(
                        [
                            KeyValuePair<string, JsonNode>("timestamp", JsonValue.Create(DateTimeOffset.Now.ToString("O")))
                            KeyValuePair<string, JsonNode>("level", JsonValue.Create(level.ToString().ToUpperInvariant()))
                            KeyValuePair<string, JsonNode>("event", JsonValue.Create(eventName))
                            KeyValuePair<string, JsonNode>("message", JsonValue.Create(message))
                            KeyValuePair<string, JsonNode>("data", dataToNode data)
                        ]
                    )

                lock gate (fun () -> writeLine trackWriter entry)
        with _ ->
            ()

    member this.Trace(eventName: string, message: string, ?data: obj) =
        this.Log(LogLevel.Trace, eventName, message, ?data = data)

    member this.Debug(eventName: string, message: string, ?data: obj) =
        this.Log(LogLevel.Debug, eventName, message, ?data = data)

    member this.Info(eventName: string, message: string, ?data: obj) =
        this.Log(LogLevel.Info, eventName, message, ?data = data)

    member this.Warn(eventName: string, message: string, ?data: obj) =
        this.Log(LogLevel.Warn, eventName, message, ?data = data)

    member this.Error(eventName: string, message: string, ?data: obj) =
        this.Log(LogLevel.Error, eventName, message, ?data = data)

    member _.RawLeagueResponse(url: string, statusCode: int, isSuccessStatusCode: bool, rawText: string) =
        writeRaw
            leagueWriter
            "league.raw.response"
            "Raw League Live Client response."
            {|
                url = url
                statusCode = statusCode
                isSuccessStatusCode = isSuccessStatusCode
                rawJson = normalizeRawJson rawText
            |}

    member _.RawLovenseSocketHttp(correlationId: string, url: string, direction: string, statusCode: int option, body: string) =
        writeRaw
            lovenseWriter
            "lovense.raw.socket_http"
            "Raw Lovense Socket API HTTP exchange."
            {|
                correlationId = correlationId
                url = url
                direction = direction
                statusCode = statusCode
                body = body |> redactSensitiveJson |> normalizeRawJson
            |}

    member _.RawLovenseSocketEvent(correlationId: string, eventName: string, direction: string, rawText: string) =
        writeRaw
            lovenseWriter
            "lovense.raw.socket_event"
            "Raw Lovense Socket.IO event."
            {|
                correlationId = correlationId
                eventName = eventName
                direction = direction
                rawText = rawText |> redactSensitiveJson |> normalizeRawJson
            |}

    interface IDisposable with
        member _.Dispose() =
            try
                trackWriter.Flush()
                trackWriter.Dispose()
            with _ ->
                ()

            for writer in [ leagueWriter; lovenseWriter ] do
                match writer with
                | None -> ()
                | Some writer ->
                    try
                        writer.Flush()
                        writer.Dispose()
                    with _ ->
                        ()
