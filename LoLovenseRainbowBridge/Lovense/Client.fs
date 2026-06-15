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
    publishSession: LovenseSessionSnapshot -> unit
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

    let sessionStore = ClientState.LovenseSessionStore(publishSession)

    let mutable standardCallbackServer: StandardApiCallbackServer option = None

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

    let handleDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        sessionStore.Update (ClientState.applyDeviceInfo deviceInfo) |> ignore

    let handleQrCode () =
        let current = sessionStore.Read()

        if not current.QrCodeLogged then
            sessionStore.Update ClientState.onQrCode |> ignore
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

    member _.CommandUrl =
        match sessionStore.Read().SocketInfo with
        | Some cached -> $"{cached.Value.SocketIoUrl} ({cached.Value.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = sessionStore.Read().LatestDeviceInfo

    member _.LatestStandardQrCode = sessionStore.Read().StandardQrCode

    member _.ApplyDeviceInfo(deviceInfo: LovenseDeviceInfo) =
        sessionStore.Update (ClientState.applyDeviceInfo deviceInfo) |> ignore

    member _.PrepareStandardApiAsync(ct: CancellationToken) =
        task {
            let! newCallbackServer = ClientConnection.ensureStandardApiReadyAsync http logger config sessionStore standardCallbackServer handleDeviceInfo ct
            standardCallbackServer <- newCallbackServer
            kickOffConnectionWarmup ()
        }

    member _.EnsureConnectedAsync(ct: CancellationToken) =
        task {
            do! this.PrepareStandardApiAsync(ct)

            let! connectionResult = ClientConnection.ensureConnectedAsync http logger config sessionStore standardCallbackServer connectGate handleDeviceInfo handleQrCode ct
            return connectionResult
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ruleTraces: LovenseRuleEvaluationTrace list, ct: CancellationToken) =
        task {
            let! newCallbackServer =
                ClientConnection.ensureStandardApiReadyAsync http logger config sessionStore standardCallbackServer handleDeviceInfo ct

            standardCallbackServer <- newCallbackServer

            let! commandResult =
                ClientCommand.sendCommandPlanAsync
                    http
                    localHttp
                    logger
                    config
                    scoringConfig
                    sessionStore
                    kickOffConnectionWarmup
                    plan
                    requestedValue
                    ruleTraces
                    ct
            return commandResult
        }

    member this.SendVibrateAsync(value: int, ct: CancellationToken) =
        let plan = Mapping.simpleVibratePlan config value
        this.SendCommandPlanAsync(plan, value, [], ct)

    member _.DisconnectAsync(ct: CancellationToken) =
        task {
            match sessionStore.Read().Socket with
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
            match sessionStore.Read().Socket with
            | None -> ()
            | Some client -> client.Dispose()

            connectGate.Dispose()
            warmupCts.Cancel()
            warmupCts.Dispose()
            http.Dispose()
            localHttp.Dispose()
            standardCallbackServer |> Option.iter (fun server -> server.Stop())
