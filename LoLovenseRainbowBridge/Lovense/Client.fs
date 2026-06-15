namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open SocketIOClient

type LovenseClient(
    config: LovenseConfig,
    scoringConfig: ScoringConfig,
    logger: StructuredSessionLogger,
    readSession: unit -> LovenseSessionSnapshot,
    updateSession: (LovenseSessionSnapshot -> LovenseSessionSnapshot) -> unit
) as this =

    let http = Shared.insecureHttpClient ()
    let localHttp =
        if config.LocalApi.AllowSelfSignedCertificate then
            Shared.insecureHttpClient ()
        else
            new System.Net.Http.HttpClient()

    let connectGate = new Threading.SemaphoreSlim(1, 1)
    let warmupGate = obj()
    let warmupCts = new CancellationTokenSource()
    let mutable connectionWarmupTask: Task option = None

    let mutable session: LovenseSessionState =
        {
            Socket = None
            SocketInfo = None
            SocketConnected = false
            SocketReadyAt = None
            LastConnectAttemptAt = None
            NextConnectRetryAt = None
            LocalCommandCooldownUntil = None
            ServerCommandCooldownUntil = None
            StandardQrCode = None
            QrCodeLogged = false
            SupportedFunctions = None
            CapabilityProfiles = []
            GeneratedAuthToken = None
            LatestDeviceInfo = None
        }

    let mutable standardCallbackServer: StandardApiCallbackServer option = None

    let syncLovenseSessionCache (update: LovenseSessionSnapshot -> LovenseSessionSnapshot) = updateSession update

    let kickOffConnectionWarmup () =
        lock warmupGate (fun () ->
            match connectionWarmupTask with
            | Some task when not task.IsCompleted -> ()
            | _ ->
                connectionWarmupTask <-
                    Some
                        (Task.Run(
                            Func<Task>(fun () ->
                                task {
                                    try
                                        let! _ = this.EnsureConnectedAsync(warmupCts.Token)
                                        return ()
                                    with
                                    | :? OperationCanceledException -> ()
                                    | _ -> ()
                                }
                            )
                        )))

    let syncLovenseSessionCacheFromState () =
        let socketInfo = session.SocketInfo |> Option.map (fun cached -> cached.Value)

        syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
            {
                current with
                    SocketInfo = socketInfo
                    SocketConnected = session.SocketConnected
                    SocketReadyAt = session.SocketReadyAt
                    LastConnectAttemptAt = session.LastConnectAttemptAt
                    NextConnectRetryAt = session.NextConnectRetryAt
                    LocalCommandCooldownUntil = session.LocalCommandCooldownUntil
                    ServerCommandCooldownUntil = session.ServerCommandCooldownUntil
                    StandardQrCode = session.StandardQrCode |> Option.map (fun cached -> cached.Value)
                    QrCodeLogged = session.QrCodeLogged
                    LatestDeviceInfo = session.LatestDeviceInfo
                    SupportedFunctions = session.SupportedFunctions
                    CapabilityProfiles = session.CapabilityProfiles
            })

    let handleDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        session <- ClientState.applyDeviceInfo deviceInfo session
        syncLovenseSessionCacheFromState ()

    let handleQrCode () =
        if not session.QrCodeLogged then
            session <- ClientState.onQrCode session
            syncLovenseSessionCacheFromState ()
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

    member _.CommandUrl =
        match readSession().SocketInfo with
        | Some cached -> $"{cached.SocketIoUrl} ({cached.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = readSession().LatestDeviceInfo

    member _.LatestStandardQrCode = readSession().StandardQrCode

    member _.ApplyDeviceInfo(deviceInfo: LovenseDeviceInfo) =
        session <- ClientState.applyDeviceInfo deviceInfo session
        syncLovenseSessionCacheFromState ()

    member _.PrepareStandardApiAsync(ct: CancellationToken) =
        task {
            let! (updatedSession, newCallbackServer) = ClientConnection.ensureStandardApiReadyAsync http logger config session standardCallbackServer handleDeviceInfo ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            syncLovenseSessionCacheFromState ()
            kickOffConnectionWarmup ()
        }

    member _.EnsureConnectedAsync(ct: CancellationToken) =
        task {
            let! (connectionResult, updatedSession, newCallbackServer) = ClientConnection.ensureConnectedAsync http logger config session standardCallbackServer connectGate handleDeviceInfo handleQrCode ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            syncLovenseSessionCacheFromState ()
            return connectionResult
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ruleTraces: LovenseRuleEvaluationTrace list, ct: CancellationToken) =
        task {
            let! (commandResult, updatedSession, newCallbackServer) =
                ClientCommand.sendCommandPlanAsync
                    http
                    localHttp
                    logger
                    config
                    scoringConfig
                    session
                    standardCallbackServer
                    connectGate
                    kickOffConnectionWarmup
                    handleDeviceInfo
                    handleQrCode
                    plan
                    requestedValue
                    ruleTraces
                    ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            syncLovenseSessionCacheFromState ()
            return commandResult
        }

    member this.SendVibrateAsync(value: int, ct: CancellationToken) =
        let plan = Mapping.simpleVibratePlan config value
        this.SendCommandPlanAsync(plan, value, [], ct)

    member _.DisconnectAsync(ct: CancellationToken) =
        task {
            match session.Socket with
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
            match session.Socket with
            | None -> ()
            | Some client -> client.Dispose()

            connectGate.Dispose()
            warmupCts.Cancel()
            warmupCts.Dispose()
            http.Dispose()
            localHttp.Dispose()
            standardCallbackServer |> Option.iter (fun server -> server.Stop())
