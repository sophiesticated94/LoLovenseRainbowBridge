namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient
open SocketIOClient.Common

module SocketRuntime =

    let private logDeviceInfo (config: LovenseConfig) (logger: StructuredSessionLogger) correlationId raw =
        let deviceInfo = DeviceInfo.parse raw

        if config.Mapping.LogToyViability then
            logger.Info(
                "lovense.toys.available",
                "Lovense toy capability profiles updated.",
                {|
                    correlationId = correlationId
                    toyCount = deviceInfo.CapabilityProfiles.Length
                    toys =
                        deviceInfo.CapabilityProfiles
                        |> List.map (fun profile ->
                            {|
                                id = profile.ToyId |> Option.map (fun _ -> "<configured>")
                                name = profile.Name
                                toyType = profile.ToyType
                                nickname = profile.Nickname
                                battery = profile.Battery
                                connected = profile.Connected
                                explicitFunctions = profile.ExplicitFunctions |> Set.toList
                                inferredFunctions = profile.InferredFunctions |> Set.toList
                                supportedFunctions = profile.SupportedFunctions |> Set.toList
                                stereoVibrationSupported = profile.StereoVibrationSupported
                                capabilitySource = string profile.CapabilitySource
                                notes = profile.Notes
                            |})
                |}
            )

        match deviceInfo.SupportedFunctions with
        | Some functions ->
            logger.Info(
                "lovense.capabilities.updated",
                "Updated Lovense supported function set from device info.",
                {|
                    correlationId = correlationId
                    supportedFunctions = functions |> Set.toList
                    toyCount = deviceInfo.ToyList.Length
                    toyList =
                        deviceInfo.ToyList
                        |> List.map (fun toy ->
                            {|
                                id = toy.Id |> Option.map (fun _ -> "<configured>")
                                name = toy.Name
                                toyType = toy.ToyType
                                nickname = toy.Nickname
                                battery = toy.Battery
                                connected = toy.Connected
                            |})
                |}
            )
        | None ->
            logger.Warn(
                "lovense.capabilities.unknown",
                "Lovense device info did not expose supported function names.",
                {|
                    correlationId = correlationId
                    toyCount = deviceInfo.ToyList.Length
                    toyList =
                        deviceInfo.ToyList
                        |> List.map (fun toy ->
                            {|
                                id = toy.Id |> Option.map (fun _ -> "<configured>")
                                name = toy.Name
                                toyType = toy.ToyType
                                nickname = toy.Nickname
                                battery = toy.Battery
                                connected = toy.Connected
                            |})
                |}
            )

        deviceInfo

    let private logSocketEvent eventName (config: LovenseConfig) (logger: StructuredSessionLogger) onDeviceInfo correlationId (ctx: IEventContext) =
        task {
            let raw = ctx.RawText

            logger.RawLovenseSocketEvent(correlationId, eventName, "receive", raw)

            if eventName = Constants.Lovense.DeviceInfoListen then
                let deviceInfo = logDeviceInfo config logger correlationId raw
                onDeviceInfo deviceInfo

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
        }

    let configure (config: LovenseConfig) (logger: StructuredSessionLogger) onDeviceInfo onQrCode (info: SocketUrlInfo) =
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
                    socketIoUrl = Shared.redactUrlSecrets info.SocketIoUrl
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
                    logSocketEvent eventName config logger onDeviceInfo (Guid.NewGuid().ToString("N")) ctx)
            )

        client.On(
            Constants.Lovense.GetQrCodeListen,
            Func<IEventContext, Task>(fun ctx ->
                let raw = ctx.RawText
                let correlationId = Guid.NewGuid().ToString("N")
                logger.RawLovenseSocketEvent(correlationId, Constants.Lovense.GetQrCodeListen, "receive", raw)
                onQrCode()

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

    let connectAsync (config: LovenseConfig) (logger: StructuredSessionLogger) onDeviceInfo onQrCode (info: SocketUrlInfo) (ct: CancellationToken) =
        task {
            try
                let client = configure config logger onDeviceInfo onQrCode info
                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(config.ConnectTimeoutMs)

                do! client.ConnectAsync(timeoutCts.Token)

                let qrAckId = Guid.NewGuid().ToString("N")
                let qrPayload = $"""{{"{Constants.Lovense.AckIdField}":"{qrAckId}"}}"""

                let! qrEmitResult =
                    Transport.emitJsonAsync
                        client
                        logger
                        Constants.Lovense.GetQrCodeEmit
                        qrPayload
                        config.ConnectTimeoutMs
                        timeoutCts.Token

                match qrEmitResult with
                | Error error ->
                    return Error(SocketConnectFailed(info.SocketIoUrl, info.SocketIoPath, string error))
                | Ok _ ->
                    return
                        Ok
                            (client,
                             {
                                 Connected = client.Connected
                                 DryRun = false
                                 SocketIoUrl = Some info.SocketIoUrl
                                 SocketIoPath = Some info.SocketIoPath
                                 SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                             })
            with
            | :? OperationCanceledException ->
                return raise (OperationCanceledException())
            | ex ->
                return Error(SocketConnectFailed(info.SocketIoUrl, info.SocketIoPath, ex.Message))
        }
