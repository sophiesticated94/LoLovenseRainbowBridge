namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open LoLovenseRainbowBridge
open SocketIOClient

type LovenseClient(config: LovenseConfig, scoringConfig: ScoringConfig, logger: StructuredSessionLogger) =

    let http = Shared.insecureHttpClient ()
    let localHttp =
        if config.LocalApi.AllowSelfSignedCertificate then
            Shared.insecureHttpClient ()
        else
            new System.Net.Http.HttpClient()

    let connectGate = new Threading.SemaphoreSlim(1, 1)

    let mutable session: LovenseSessionState =
        {
            Socket = None
            SocketInfo = None
            StandardQrCode = None
            QrCodeLogged = false
            SupportedFunctions = None
            CapabilityProfiles = []
            GeneratedAuthToken = None
            LatestDeviceInfo = None
        }

    let mutable nextLocalCapabilityRefreshAt = DateTimeOffset.MinValue
    let mutable standardCallbackServer: StandardApiCallbackServer option = None

    let handleDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        let updatedSession = ClientState.applyDeviceInfo deviceInfo session
        nextLocalCapabilityRefreshAt <- DateTimeOffset.MinValue
        session <- updatedSession

    let handleQrCode () =
        if not session.QrCodeLogged then
            session <- ClientState.onQrCode session
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

    member _.CommandUrl =
        match session.SocketInfo with
        | Some cached -> $"{cached.Value.SocketIoUrl} ({cached.Value.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = session.LatestDeviceInfo

    member _.PrepareStandardApiAsync(ct: CancellationToken) =
        task {
            let! (updatedSession, newCallbackServer) = ClientConnection.ensureStandardApiReadyAsync http logger config session standardCallbackServer ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
        }

    member _.EnsureConnectedAsync(ct: CancellationToken) =
        task {
            let! (connectionResult, updatedSession, newCallbackServer) = ClientConnection.ensureConnectedAsync http logger config session standardCallbackServer connectGate handleDeviceInfo handleQrCode ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            return connectionResult
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ruleTraces: LovenseRuleEvaluationTrace list, ct: CancellationToken) =
        task {
            let! (commandResult, updatedSession, newCallbackServer, newNextRefresh) = ClientCommand.sendCommandPlanAsync http localHttp logger config scoringConfig session standardCallbackServer nextLocalCapabilityRefreshAt plan requestedValue ruleTraces ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            nextLocalCapabilityRefreshAt <- newNextRefresh
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
            http.Dispose()
            localHttp.Dispose()
            standardCallbackServer |> Option.iter (fun server -> server.Stop())
