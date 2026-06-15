namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient

module ClientConnection =
    let private resolveAuthTokenAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (sessionStore: ClientState.LovenseSessionStore) forceRefresh (ct: CancellationToken) =
        task {
            let session = sessionStore.Read()
            match session.GeneratedAuthToken, forceRefresh with
            | Some cached, false when not (String.IsNullOrWhiteSpace cached.Value) ->
                logger.Debug(
                    "lovense.auth.cache_hit",
                    "Using cached Lovense auth token from application state.",
                    {| acquiredAt = cached.AcquiredAt; authToken = Constants.Lovense.AuthTokenRedacted |}
                )

                return Ok cached.Value

            | _ ->
                logger.Info(
                    "lovense.auth.cache_miss",
                    "Lovense auth token is missing or was invalidated; requesting a fresh token.",
                    {| forceRefresh = forceRefresh |}
                )

                let! tokenResult = Auth.requestAuthTokenAsync http logger config.Developer ct

                match tokenResult with
                | Ok authToken ->
                    let acquiredAt = DateTimeOffset.UtcNow
                    sessionStore.Update (fun current -> { current with GeneratedAuthToken = Some { Value = authToken; AcquiredAt = acquiredAt } }) |> ignore

                    logger.Info(
                        "lovense.auth.refresh",
                        "Lovense auth token stored in application state.",
                        {| acquiredAt = acquiredAt; authToken = Constants.Lovense.AuthTokenRedacted |}
                    )

                    return Ok authToken
                | Error error ->
                    return Error error
        }

    let private resolveSocketUrlAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (sessionStore: ClientState.LovenseSessionStore) authToken forceRefresh (ct: CancellationToken) =
        task {
            let session = sessionStore.Read()
            match session.SocketInfo, forceRefresh with
            | Some cached, false ->
                logger.Debug(
                    "lovense.socket_url.cache_hit",
                    "Using cached Lovense Socket.IO URL from application state.",
                    {| acquiredAt = cached.AcquiredAt; socketIoUrl = Shared.redactUrlSecrets cached.Value.SocketIoUrl; socketIoPath = cached.Value.SocketIoPath |}
                )

                return Ok cached.Value

            | _ ->
                logger.Info(
                    "lovense.socket_url.cache_miss",
                    "Lovense Socket.IO URL is missing or was invalidated; requesting fresh connection details.",
                    {| forceRefresh = forceRefresh; platform = config.Platform |}
                )

                let! socketUrlResult = SocketUrl.requestSocketUrlAsync http logger config.Platform authToken ct

                match socketUrlResult with
                | Error error ->
                    return Error error
                | Ok info ->
                    let acquiredAt = DateTimeOffset.UtcNow
                    sessionStore.Update (fun current -> { current with SocketInfo = Some { Value = info; AcquiredAt = acquiredAt } }) |> ignore

                    logger.Info(
                        "lovense.socket_url.refresh",
                        "Lovense Socket.IO URL stored in application state.",
                        {| acquiredAt = acquiredAt; socketIoUrl = Shared.redactUrlSecrets info.SocketIoUrl; socketIoPath = info.SocketIoPath |}
                    )

                    return Ok info
        }

    let ensureStandardApiReadyAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (sessionStore: ClientState.LovenseSessionStore) (standardCallbackServer: StandardApiCallbackServer option) (onDeviceInfo: LovenseDeviceInfo -> unit) (ct: CancellationToken) =
        task {
            if config.StandardApi.Enable then
                let newCallbackServer =
                    match standardCallbackServer with
                    | Some server -> Some server
                    | None -> StandardApi.startCallbackListener logger config.StandardApi config.Developer onDeviceInfo

                let session = sessionStore.Read()

                if config.StandardApi.GenerateQrOnStartup && session.StandardQrCode.IsNone then
                    let! qrResult = StandardApi.requestQrCodeAsync http logger config.StandardApi config.Developer ct

                    match qrResult with
                    | Ok qrInfo ->
                        sessionStore.Update (fun current -> { current with StandardQrCode = Some { Value = qrInfo; AcquiredAt = DateTimeOffset.UtcNow } }) |> ignore
                        printfn "Lovense Standard API pairing code: %s" qrInfo.Code
                        printfn "Lovense Standard API QR: %s" qrInfo.Qr
                        return newCallbackServer
                    | Error error ->
                        logger.Warn(
                            "lovense.standard.prepare_failed",
                            "Lovense Standard API QR/code preparation failed.",
                            {| error = string error |}
                        )
                        return newCallbackServer
                else
                    return newCallbackServer
            else
                return standardCallbackServer
        }

    let ensureConnectedAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (sessionStore: ClientState.LovenseSessionStore) (standardCallbackServer: StandardApiCallbackServer option) (connectGate: Threading.SemaphoreSlim) (onDeviceInfo: LovenseDeviceInfo -> unit) (onQrCode: unit -> unit) (ct: CancellationToken) =
        task {
            if config.DryRun then
                return Ok
                        {
                            Connected = false
                            DryRun = true
                            SocketIoUrl = None
                            SocketIoPath = None
                            SocketId = None
                        }
            else
                let session = sessionStore.Read()

                match session.Socket with
                | Some client when client.Connected ->
                    return
                        Ok
                            {
                                Connected = true
                                DryRun = false
                                SocketIoUrl = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoUrl)
                                SocketIoPath = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoPath)
                                SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                            }

                | _ ->
                    do! connectGate.WaitAsync(ct)

                    try
                        let connectOnce forceAuthRefresh forceSocketUrlRefresh =
                            task {
                                let startedAt = DateTimeOffset.UtcNow
                                sessionStore.Update (fun current -> { current with LastConnectAttemptAt = Some startedAt }) |> ignore
                                let! authTokenResult = resolveAuthTokenAsync http logger config sessionStore forceAuthRefresh ct

                                match authTokenResult with
                                | Error error ->
                                    let retryAt = Some(startedAt.AddMilliseconds(float config.ConnectTimeoutMs))
                                    sessionStore.Update (fun current -> { current with SocketConnected = false; NextConnectRetryAt = retryAt }) |> ignore
                                    return Error error

                                | Ok authToken ->
                                    let! socketUrlResult = resolveSocketUrlAsync http logger config sessionStore authToken forceSocketUrlRefresh ct

                                    match socketUrlResult with
                                    | Error error ->
                                        let retryAt = Some(startedAt.AddMilliseconds(float config.ConnectTimeoutMs))
                                        sessionStore.Update (fun current -> { current with SocketConnected = false; NextConnectRetryAt = retryAt }) |> ignore
                                        return Error error

                                    | Ok info ->
                                        let! connectedResult = SocketRuntime.connectAsync config logger onDeviceInfo onQrCode info ct

                                        match connectedResult with
                                        | Ok (client, state) ->
                                            sessionStore.Update (fun current ->
                                                {
                                                    current with
                                                        Socket = Some client
                                                        SocketConnected = true
                                                        SocketReadyAt = Some DateTimeOffset.UtcNow
                                                        LastConnectAttemptAt = Some startedAt
                                                        NextConnectRetryAt = None
                                                })
                                            |> ignore
                                            return Ok state
                                        | Error error ->
                                            let retryAt = Some(startedAt.AddMilliseconds(float config.ConnectTimeoutMs))
                                            sessionStore.Update (fun current -> { current with SocketConnected = false; NextConnectRetryAt = retryAt }) |> ignore
                                            return Error error
                            }

                        let session = sessionStore.Read()

                        match session.Socket with
                        | Some client when client.Connected ->
                            return
                                Ok
                                    {
                                        Connected = true
                                        DryRun = false
                                        SocketIoUrl = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoUrl)
                                        SocketIoPath = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoPath)
                                        SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                                    }

                        | _ ->
                            let! firstAttempt = connectOnce false false

                            match firstAttempt with
                            | Ok state ->
                                return Ok state

                            | Error error ->
                                match ClientState.retryPolicyFor error with
                                | DoNotRetry ->
                                    return Error error
                                | RetrySocketUrlOnly ->
                                    sessionStore.Update (fun current -> ClientState.invalidateSocketUrl (string error) current logger) |> ignore
                                    let! secondAttempt = connectOnce false true
                                    return secondAttempt
                                | RetryAuthAndSocketUrl ->
                                    sessionStore.Update (fun current -> ClientState.invalidateAuthAndSocketUrl (string error) current logger) |> ignore
                                    let! secondAttempt = connectOnce true true
                                    return secondAttempt

                    finally
                        connectGate.Release() |> ignore
        }
