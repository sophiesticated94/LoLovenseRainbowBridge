namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient

module ClientConnection =
    let private resolveAuthTokenAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (session: LovenseSessionState) forceRefresh (ct: CancellationToken) =
        task {
            match session.GeneratedAuthToken, forceRefresh with
            | Some cached, false when not (String.IsNullOrWhiteSpace cached.Value) ->
                logger.Debug(
                    "lovense.auth.cache_hit",
                    "Using cached Lovense auth token from application state.",
                    {| acquiredAt = cached.AcquiredAt; authToken = Constants.Lovense.AuthTokenRedacted |}
                )

                return (Ok cached.Value, session)

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
                    let updatedSession = { session with GeneratedAuthToken = Some { Value = authToken; AcquiredAt = acquiredAt } }

                    logger.Info(
                        "lovense.auth.refresh",
                        "Lovense auth token stored in application state.",
                        {| acquiredAt = acquiredAt; authToken = Constants.Lovense.AuthTokenRedacted |}
                    )

                    return (Ok authToken, updatedSession)
                | Error error ->
                    return (Error error, session)
        }

    let private resolveSocketUrlAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (session: LovenseSessionState) authToken forceRefresh (ct: CancellationToken) =
        task {
            match session.SocketInfo, forceRefresh with
            | Some cached, false ->
                logger.Debug(
                    "lovense.socket_url.cache_hit",
                    "Using cached Lovense Socket.IO URL from application state.",
                    {| acquiredAt = cached.AcquiredAt; socketIoUrl = Shared.redactUrlSecrets cached.Value.SocketIoUrl; socketIoPath = cached.Value.SocketIoPath |}
                )

                return (Ok cached.Value, session)

            | _ ->
                logger.Info(
                    "lovense.socket_url.cache_miss",
                    "Lovense Socket.IO URL is missing or was invalidated; requesting fresh connection details.",
                    {| forceRefresh = forceRefresh; platform = config.Platform |}
                )

                let! socketUrlResult = SocketUrl.requestSocketUrlAsync http logger config.Platform authToken ct

                match socketUrlResult with
                | Error error ->
                    return (Error error, session)
                | Ok info ->
                    let acquiredAt = DateTimeOffset.UtcNow
                    let updatedSession = { session with SocketInfo = Some { Value = info; AcquiredAt = acquiredAt } }

                    logger.Info(
                        "lovense.socket_url.refresh",
                        "Lovense Socket.IO URL stored in application state.",
                        {| acquiredAt = acquiredAt; socketIoUrl = Shared.redactUrlSecrets info.SocketIoUrl; socketIoPath = info.SocketIoPath |}
                    )

                    return (Ok info, updatedSession)
        }

    let ensureStandardApiReadyAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (session: LovenseSessionState) (standardCallbackServer: StandardApiCallbackServer option) (onDeviceInfo: LovenseDeviceInfo -> unit) (ct: CancellationToken) =
        task {
            if config.StandardApi.Enable then
                let newCallbackServer =
                    match standardCallbackServer with
                    | Some server -> Some server
                    | None -> StandardApi.startCallbackListener logger config.StandardApi config.Developer onDeviceInfo

                if config.StandardApi.GenerateQrOnStartup && session.StandardQrCode.IsNone then
                    let! qrResult = StandardApi.requestQrCodeAsync http logger config.StandardApi config.Developer ct

                    match qrResult with
                    | Ok qrInfo ->
                        let finalSession = { session with StandardQrCode = Some { Value = qrInfo; AcquiredAt = DateTimeOffset.UtcNow } }
                        printfn "Lovense Standard API pairing code: %s" qrInfo.Code
                        printfn "Lovense Standard API QR: %s" qrInfo.Qr
                        return (finalSession, newCallbackServer)
                    | Error error ->
                        logger.Warn(
                            "lovense.standard.prepare_failed",
                            "Lovense Standard API QR/code preparation failed.",
                            {| error = string error |}
                        )
                        return (session, newCallbackServer)
                else
                    return (session, newCallbackServer)
            else
                return (session, standardCallbackServer)
        }

    let ensureConnectedAsync (http: System.Net.Http.HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (session: LovenseSessionState) (standardCallbackServer: StandardApiCallbackServer option) (connectGate: Threading.SemaphoreSlim) (onDeviceInfo: LovenseDeviceInfo -> unit) (onQrCode: unit -> unit) (ct: CancellationToken) =
        task {
            if config.DryRun then
                return
                    (Ok
                        {
                            Connected = false
                            DryRun = true
                            SocketIoUrl = None
                            SocketIoPath = None
                            SocketId = None
                        }, session, standardCallbackServer)
            else
                match session.Socket with
                | Some client when client.Connected ->
                    return
                        (Ok
                            {
                                Connected = true
                                DryRun = false
                                SocketIoUrl = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoUrl)
                                SocketIoPath = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoPath)
                                SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                            }, session, standardCallbackServer)

                | _ ->
                    do! connectGate.WaitAsync(ct)

                    try
                        let connectOnce forceAuthRefresh forceSocketUrlRefresh currentSession =
                            task {
                                let! (authTokenResult, updatedSession1) = resolveAuthTokenAsync http logger config currentSession forceAuthRefresh ct

                                match authTokenResult with
                                | Error error ->
                                    return (Error error, updatedSession1)

                                | Ok authToken ->
                                    let! (socketUrlResult, updatedSession2) = resolveSocketUrlAsync http logger config updatedSession1 authToken forceSocketUrlRefresh ct

                                    match socketUrlResult with
                                    | Error error ->
                                        return (Error error, updatedSession2)

                                    | Ok info ->
                                        let! connectedResult = SocketRuntime.connectAsync config logger onDeviceInfo onQrCode info ct

                                        match connectedResult with
                                        | Ok (client, state) ->
                                            let finalSession = { updatedSession2 with Socket = Some client }
                                            return (Ok state, finalSession)
                                        | Error error ->
                                            return (Error error, updatedSession2)
                            }

                        match session.Socket with
                        | Some client when client.Connected ->
                            return
                                (Ok
                                    {
                                        Connected = true
                                        DryRun = false
                                        SocketIoUrl = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoUrl)
                                        SocketIoPath = session.SocketInfo |> Option.map (fun cached -> cached.Value.SocketIoPath)
                                        SocketId = if String.IsNullOrWhiteSpace client.Id then None else Some client.Id
                                    }, session, standardCallbackServer)

                        | _ ->
                            let! (firstAttempt, updatedSession1) = connectOnce false false session

                            match firstAttempt with
                            | Ok state ->
                                return (Ok state, updatedSession1, standardCallbackServer)

                            | Error error ->
                                match ClientState.retryPolicyFor error with
                                | DoNotRetry ->
                                    return (Error error, updatedSession1, standardCallbackServer)
                                | RetrySocketUrlOnly ->
                                    let updatedSession2 = ClientState.invalidateSocketUrl (string error) updatedSession1 logger
                                    let! (secondAttempt, finalSession) = connectOnce false true updatedSession2
                                    return (secondAttempt, finalSession, standardCallbackServer)
                                | RetryAuthAndSocketUrl ->
                                    let updatedSession2 = ClientState.invalidateAuthAndSocketUrl (string error) updatedSession1 logger
                                    let! (secondAttempt, finalSession) = connectOnce true true updatedSession2
                                    return (secondAttempt, finalSession, standardCallbackServer)

                    finally
                        connectGate.Release() |> ignore
        }
