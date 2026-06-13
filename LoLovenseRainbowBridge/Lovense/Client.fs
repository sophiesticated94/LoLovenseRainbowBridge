namespace LoLovenseRainbowBridge.Lovense

open System
open System.Globalization
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient
open SocketIOClient.Common

type LovenseConnectionState =
    {
        Connected: bool
        DryRun: bool
        SocketIoUrl: string option
        SocketIoPath: string option
        SocketId: string option
    }

type LovenseCommandResult =
    {
        RequestedValue: int
        SafeValue: int
        DryRun: bool
        CorrelationId: string
        SocketConnected: bool
    }

type LovenseConnectionError =
    | MissingAuthToken
    | SocketUrlRequestFailed of url: string * message: string
    | SocketUrlRejected of url: string * code: int option * message: string
    | SocketConnectFailed of socketIoUrl: string * socketIoPath: string * message: string
    | SocketDisconnected of reason: string
    | UnexpectedConnectionError of message: string * errorType: string

type LovenseCommandError =
    | NotConnected of LovenseConnectionError
    | CommandEmitFailed of eventName: string * message: string
    | CommandRejected of eventName: string * message: string
    | CommandTimeout of eventName: string * timeoutMs: int
    | UnexpectedCommandError of eventName: string * message: string * errorType: string

type private SocketUrlInfo =
    {
        SocketIoUrl: string
        SocketIoPath: string
    }

type LovenseClient(config: LovenseConfig, scoringConfig: ScoringConfig, logger: StructuredSessionLogger) =

    let http = Shared.insecureHttpClient ()
    let connectGate = new Threading.SemaphoreSlim(1, 1)

    let mutable socket: SocketIO option = None
    let mutable socketInfo: SocketUrlInfo option = None
    let mutable qrCodeLogged = false
    let mutable supportedFunctions: Set<string> option = None

    let invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

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

    let createCommandPayload (plan: LovenseCommandPlan) correlationId =
        let toyPart =
            plan.ToyId
            |> Option.map (fun toyId -> $",\"toy\":\"{escapeJsonString toyId}\"")
            |> Option.defaultValue ""

        let stopPrevious = if plan.StopPrevious then 1 else 0
        let actionString = Mapping.planActionString plan

        $"""{{"ackId":"{correlationId}","command":"{Constants.Lovense.CommandName}","action":"{escapeJsonString actionString}","timeSec":{invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""

    let redactedSocketUrlRequestBody =
        $"""{{"{Constants.Lovense.PlatformField}":"{escapeJsonString config.Platform}","{Constants.Lovense.AuthTokenField}":"{Constants.Lovense.AuthTokenRedacted}"}}"""

    let socketUrlRequestBody authToken =
        $"""{{"{Constants.Lovense.PlatformField}":"{escapeJsonString config.Platform}","{Constants.Lovense.AuthTokenField}":"{escapeJsonString authToken}"}}"""

    let parseSocketUrlResponse (body: string) =
        try
            let root = JsonNode.Parse(body)

            if isNull root then
                Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, None, "Lovense returned empty socket URL response."))
            else
                let code = Json.tryInt Constants.Lovense.CodeField root
                let message = Json.tryString Constants.Lovense.MessageField root |> Option.defaultValue ""

                match code with
                | Some Constants.Lovense.SuccessCode ->
                    let data = Json.tryGet Constants.Lovense.DataField root

                    match
                        data |> Option.bind (Json.tryString Constants.Lovense.SocketIoUrlField),
                        data |> Option.bind (Json.tryString Constants.Lovense.SocketIoPathField)
                    with
                    | Some socketIoUrl, Some socketIoPath
                        when not (String.IsNullOrWhiteSpace socketIoUrl)
                             && not (String.IsNullOrWhiteSpace socketIoPath) ->
                        Ok
                            {
                                SocketIoUrl = socketIoUrl
                                SocketIoPath = socketIoPath
                            }

                    | _ ->
                        Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, code, "Socket URL response did not include socketIoUrl/socketIoPath."))

                | _ ->
                    Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, code, message))
        with ex ->
            Error(SocketUrlRequestFailed(Constants.Lovense.GetSocketUrl, $"Could not parse socket URL response: {ex.Message}"))

    let requestSocketUrlAsync (authToken: string) (ct: CancellationToken) =
        task {
            let correlationId = Guid.NewGuid().ToString("N")
            let body = socketUrlRequestBody authToken

            logger.Info(
                "lovense.socket_url.request",
                "Requesting Lovense Socket.IO URL.",
                {|
                    correlationId = correlationId
                    url = Constants.Lovense.GetSocketUrl
                    platform = config.Platform
                    authToken = Constants.Lovense.AuthTokenRedacted
                    rawLogged = logger.IsRawLovenseEnabled
                |}
            )

            logger.RawLovenseSocketHttp(correlationId, Constants.Lovense.GetSocketUrl, "request", None, redactedSocketUrlRequestBody)

            try
                use request = new HttpRequestMessage(HttpMethod.Post, Constants.Lovense.GetSocketUrl)
                request.Content <- new StringContent(body, Encoding.UTF8, Constants.Lovense.JsonMediaType)

                let! response = http.SendAsync(request, ct)
                let! responseBody = response.Content.ReadAsStringAsync(ct)

                logger.RawLovenseSocketHttp(correlationId, Constants.Lovense.GetSocketUrl, "response", Some(int response.StatusCode), responseBody)

                if not response.IsSuccessStatusCode then
                    return Error(SocketUrlRequestFailed(Constants.Lovense.GetSocketUrl, $"HTTP {(int response.StatusCode)}: {responseBody}"))
                else
                    match parseSocketUrlResponse responseBody with
                    | Ok info ->
                        logger.Info(
                            "lovense.socket_url.success",
                            "Received Lovense Socket.IO connection details.",
                            {|
                                correlationId = correlationId
                                socketIoUrl = info.SocketIoUrl
                                socketIoPath = info.SocketIoPath
                            |}
                        )

                        return Ok info

                    | Error error ->
                        logger.Warn(
                            "lovense.socket_url.rejected",
                            "Lovense rejected or returned unusable Socket.IO connection details.",
                            {|
                                correlationId = correlationId
                                error = string error
                            |}
                        )

                        return Error error
            with
            | :? OperationCanceledException ->
                return raise (OperationCanceledException())
            | ex ->
                return Error(SocketUrlRequestFailed(Constants.Lovense.GetSocketUrl, ex.Message))
        }

    let logSocketEvent eventName correlationId (ctx: IEventContext) =
        let raw = ctx.RawText

        if eventName = Constants.Lovense.DeviceInfoListen then
            match tryExtractSupportedFunctions raw with
            | Some functions ->
                supportedFunctions <- Some functions
                logger.Info(
                    "lovense.capabilities.updated",
                    "Updated Lovense supported function set from device info.",
                    {|
                        correlationId = correlationId
                        supportedFunctions = functions |> Set.toList
                    |}
                )
            | None ->
                logger.Warn(
                    "lovense.capabilities.unknown",
                    "Lovense device info did not expose supported function names.",
                    {| correlationId = correlationId |}
                )

        logger.RawLovenseSocketEvent(correlationId, eventName, "receive", raw)

        logger.Info(
            $"lovense.socket.{eventName}",
            "Received Lovense Socket.IO event.",
            {|
                correlationId = correlationId
                eventName = eventName
                rawLength = if isNull raw then 0 else raw.Length
                rawLogged = logger.IsRawLovenseEnabled
            |}
        )

    let configureSocket (info: SocketUrlInfo) =
        let client = new SocketIO(Uri(info.SocketIoUrl))

        client.Options.Path <- info.SocketIoPath
        client.Options.Transport <- TransportProtocol.WebSocket
        client.Options.EIO <- EngineIO.V3
        client.Options.ConnectionTimeout <- TimeSpan.FromMilliseconds(float config.ConnectTimeoutMs)
        client.Options.Reconnection <- true
        client.Options.ReconnectionDelayMax <- config.ConnectTimeoutMs

        client.OnConnected.Add(fun _ ->
            logger.Info(
                "lovense.socket.connected",
                "Connected to Lovense Socket.IO.",
                {|
                    socketIoUrl = info.SocketIoUrl
                    socketIoPath = info.SocketIoPath
                    socketId = client.Id
                    socketIoVersion = Constants.Lovense.SocketIoVersion
                |}
            ))

        client.OnDisconnected.Add(fun reason ->
            logger.Warn(
                "lovense.socket.disconnected",
                "Disconnected from Lovense Socket.IO.",
                {| reason = reason |}
            ))

        client.OnError.Add(fun message ->
            logger.Error(
                "lovense.socket.error",
                "Lovense Socket.IO error.",
                {| message = message |}
            ))

        client.OnReconnectAttempt.Add(fun attempt ->
            logger.Warn(
                "lovense.socket.reconnect_attempt",
                "Lovense Socket.IO reconnect attempt.",
                {| attempt = attempt |}
            ))

        for eventName in
            [
                Constants.Lovense.DeviceInfoListen
                Constants.Lovense.AppStatusListen
                Constants.Lovense.AppOnlineListen
            ] do
            client.On(
                eventName,
                Func<IEventContext, Task>(fun ctx ->
                    logSocketEvent eventName (Guid.NewGuid().ToString("N")) ctx
                    Task.CompletedTask)
            )

        client.On(
            Constants.Lovense.GetQrCodeListen,
            Func<IEventContext, Task>(fun ctx ->
                let raw = ctx.RawText
                let correlationId = Guid.NewGuid().ToString("N")
                logger.RawLovenseSocketEvent(correlationId, Constants.Lovense.GetQrCodeListen, "receive", raw)

                if not qrCodeLogged then
                    qrCodeLogged <- true
                    printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

                logger.Info(
                    "lovense.socket.qrcode",
                    "Lovense QR code information received.",
                    {|
                        correlationId = correlationId
                        rawLength = if isNull raw then 0 else raw.Length
                        rawLogged = logger.IsRawLovenseEnabled
                    |}
                )

                Task.CompletedTask)
        )

        client

    let connectSocketAsync (info: SocketUrlInfo) (ct: CancellationToken) =
        task {
            try
                let client = configureSocket info
                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(config.ConnectTimeoutMs)

                do! client.ConnectAsync(timeoutCts.Token)

                socket <- Some client
                socketInfo <- Some info

                let qrAckId = Guid.NewGuid().ToString("N")
                let qrPayload = $"""{{"{Constants.Lovense.AckIdField}":"{qrAckId}"}}"""

                logger.RawLovenseSocketEvent(qrAckId, Constants.Lovense.GetQrCodeEmit, "emit", qrPayload)

                let qrData: obj seq =
                    [ JsonSerializer.Deserialize<JsonElement>(qrPayload) :> obj ]

                do! client.EmitAsync(Constants.Lovense.GetQrCodeEmit, qrData, timeoutCts.Token)

                return
                    Ok
                        {
                            Connected = client.Connected
                            DryRun = false
                            SocketIoUrl = Some info.SocketIoUrl
                            SocketIoPath = Some info.SocketIoPath
                            SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                        }
            with
            | :? OperationCanceledException ->
                return raise (OperationCanceledException())
            | ex ->
                return Error(SocketConnectFailed(info.SocketIoUrl, info.SocketIoPath, ex.Message))
        }

    member _.CommandUrl =
        match socketInfo with
        | Some info -> $"{info.SocketIoUrl} ({info.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.EnsureConnectedAsync(ct: CancellationToken) =
        task {
            if config.DryRun then
                return
                    Ok
                        {
                            Connected = false
                            DryRun = true
                            SocketIoUrl = None
                            SocketIoPath = None
                            SocketId = None
                        }
            else
                match config.AuthToken with
                | None ->
                    return Error MissingAuthToken

                | Some authToken ->
                    match socket with
                    | Some client when client.Connected ->
                        return
                            Ok
                                {
                                    Connected = true
                                    DryRun = false
                                    SocketIoUrl = socketInfo |> Option.map (fun info -> info.SocketIoUrl)
                                    SocketIoPath = socketInfo |> Option.map (fun info -> info.SocketIoPath)
                                    SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                                }

                    | _ ->
                        do! connectGate.WaitAsync(ct)

                        try
                            match socket with
                            | Some client when client.Connected ->
                                return
                                    Ok
                                        {
                                            Connected = true
                                            DryRun = false
                                            SocketIoUrl = socketInfo |> Option.map (fun info -> info.SocketIoUrl)
                                            SocketIoPath = socketInfo |> Option.map (fun info -> info.SocketIoPath)
                                            SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                                        }

                            | _ ->
                                let! socketUrlResult = requestSocketUrlAsync authToken ct

                                match socketUrlResult with
                                | Error error ->
                                    return Error error

                                | Ok info ->
                                    let! connected = connectSocketAsync info ct
                                    return connected
                        finally
                            connectGate.Release() |> ignore
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ct: CancellationToken) =
        task {
            let safeValue = requestedValue |> Shared.clamp scoringConfig.MinIntensity scoringConfig.MaxIntensity
            let correlationId = Guid.NewGuid().ToString("N")
            let candidateActionString = Mapping.planActionString plan
            let filteredPlan, droppedActions = Mapping.filterByCapabilities config supportedFunctions plan
            let payload = createCommandPayload filteredPlan correlationId
            let actionString = Mapping.planActionString filteredPlan
            let commandReasons = filteredPlan.Reasons |> List.map Mapping.reasonToString

            if config.DryRun then
                logger.Info(
                    "lovense.command.dry_run",
                    "Lovense Socket API command skipped because DryRun is enabled.",
                    {|
                        correlationId = correlationId
                        requestedValue = requestedValue
                        safeValue = safeValue
                        eventName = Constants.Lovense.SendToyCommandEmit
                        action = actionString
                        candidateAction = candidateActionString
                        droppedActions = droppedActions
                        reasons = commandReasons
                        capabilitySource = if supportedFunctions.IsSome then "deviceInfo" elif config.Mapping.ForceSupportedFunctions.IsEmpty then "unknown" else "config"
                        rawLogged = logger.IsRawLovenseEnabled
                    |}
                )

                logger.RawLovenseSocketEvent(correlationId, Constants.Lovense.SendToyCommandEmit, "emit", payload)
                printfn "[DRY] Lovense Socket %s" actionString

                return
                    Ok
                        {
                            RequestedValue = requestedValue
                            SafeValue = safeValue
                            DryRun = true
                            CorrelationId = correlationId
                            SocketConnected = false
                        }
            else
                let! connected = this.EnsureConnectedAsync ct

                match connected with
                | Error error ->
                    return Error(NotConnected error)

                | Ok _ ->
                    match socket with
                    | None ->
                        return Error(NotConnected(SocketDisconnected "Socket was not available after successful connection."))

                    | Some client when not client.Connected ->
                        return Error(NotConnected(SocketDisconnected "Socket disconnected before command emit."))

                    | Some client ->
                        try
                            use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                            timeoutCts.CancelAfter(config.CommandAckTimeoutMs)

                            logger.Info(
                                "lovense.command.emit",
                                "Emitting Lovense Socket API toy command.",
                                {|
                                    correlationId = correlationId
                                    requestedValue = requestedValue
                                    safeValue = safeValue
                                    eventName = Constants.Lovense.SendToyCommandEmit
                                    action = actionString
                                    candidateAction = candidateActionString
                                    droppedActions = droppedActions
                                    reasons = commandReasons
                                    capabilitySource = if supportedFunctions.IsSome then "deviceInfo" elif config.Mapping.ForceSupportedFunctions.IsEmpty then "unknown" else "config"
                                    payloadLength = payload.Length
                                    rawLogged = logger.IsRawLovenseEnabled
                                |}
                            )

                            logger.RawLovenseSocketEvent(correlationId, Constants.Lovense.SendToyCommandEmit, "emit", payload)

                            let payloadElement = JsonSerializer.Deserialize<JsonElement>(payload)

                            let commandData: obj seq =
                                [ payloadElement :> obj ]

                            do! client.EmitAsync(Constants.Lovense.SendToyCommandEmit, commandData, timeoutCts.Token)

                            return
                                Ok
                                    {
                                        RequestedValue = requestedValue
                                        SafeValue = safeValue
                                        DryRun = false
                                        CorrelationId = correlationId
                                        SocketConnected = client.Connected
                                    }
                        with
                        | :? OperationCanceledException when not ct.IsCancellationRequested ->
                            return Error(CommandTimeout(Constants.Lovense.SendToyCommandEmit, config.CommandAckTimeoutMs))
                        | :? OperationCanceledException ->
                            return raise (OperationCanceledException())
                        | ex ->
                            return Error(CommandEmitFailed(Constants.Lovense.SendToyCommandEmit, ex.Message))
        }

    member this.SendVibrateAsync(value: int, ct: CancellationToken) =
        let plan = Mapping.simpleVibratePlan config value
        this.SendCommandPlanAsync(plan, value, ct)

    member _.DisconnectAsync(ct: CancellationToken) =
        task {
            match socket with
            | None ->
                return Ok()

            | Some client ->
                try
                    do! client.DisconnectAsync(ct)
                    logger.Info("lovense.socket.disconnect", "Lovense Socket.IO disconnected by application.")
                    return Ok()
                with
                | :? OperationCanceledException ->
                    return raise (OperationCanceledException())
                | ex ->
                    return Error(UnexpectedConnectionError(ex.Message, ex.GetType().FullName))
        }

    interface IDisposable with
        member _.Dispose() =
            match socket with
            | None -> ()
            | Some client -> client.Dispose()

            connectGate.Dispose()
            http.Dispose()
