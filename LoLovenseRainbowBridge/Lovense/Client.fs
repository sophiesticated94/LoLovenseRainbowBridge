namespace LoLovenseRainbowBridge.Lovense

open System
open System.Threading
open LoLovenseRainbowBridge
open SocketIOClient

type LovenseClient(
    config: LovenseConfig,
    scoringConfig: ScoringConfig,
    logger: StructuredSessionLogger,
    readSession: unit -> LovenseSessionSnapshot,
    updateSession: (LovenseSessionSnapshot -> LovenseSessionSnapshot) -> unit
) =

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

    let mutable standardCallbackServer: StandardApiCallbackServer option = None

    let syncLovenseSessionCache (update: LovenseSessionSnapshot -> LovenseSessionSnapshot) = updateSession update

    let handleDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        session <- ClientState.applyDeviceInfo deviceInfo session
        syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
            {
                current with
                    LatestDeviceInfo = session.LatestDeviceInfo
                    SupportedFunctions = session.SupportedFunctions
                    CapabilityProfiles = session.CapabilityProfiles
            })

    let handleQrCode () =
        if not session.QrCodeLogged then
            session <- ClientState.onQrCode session
            syncLovenseSessionCache (fun current -> { current with QrCodeLogged = true })
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

    member _.CommandUrl =
        match readSession().SocketInfo with
        | Some cached -> $"{cached.SocketIoUrl} ({cached.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = readSession().LatestDeviceInfo

    member _.LatestStandardQrCode = readSession().StandardQrCode

    member _.ApplyDeviceInfo(deviceInfo: LovenseDeviceInfo) =
        session <- ClientState.applyDeviceInfo deviceInfo session
        syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
            {
                current with
                    LatestDeviceInfo = session.LatestDeviceInfo
                    SupportedFunctions = session.SupportedFunctions
                    CapabilityProfiles = session.CapabilityProfiles
            })

    member _.PrepareStandardApiAsync(ct: CancellationToken) =
        task {
            let! (updatedSession, newCallbackServer) = ClientConnection.ensureStandardApiReadyAsync http logger config session standardCallbackServer handleDeviceInfo ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            let socketInfo = session.SocketInfo |> Option.map (fun cached -> cached.Value)
            let standardQrCode = session.StandardQrCode |> Option.map (fun cached -> cached.Value)
            syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
                {
                    current with
                        StandardQrCode = standardQrCode
                        QrCodeLogged = session.QrCodeLogged
                        LatestDeviceInfo = session.LatestDeviceInfo
                        SupportedFunctions = session.SupportedFunctions
                        CapabilityProfiles = session.CapabilityProfiles
                        SocketInfo = socketInfo
                })
        }

    member _.EnsureConnectedAsync(ct: CancellationToken) =
        task {
            let! (connectionResult, updatedSession, newCallbackServer) = ClientConnection.ensureConnectedAsync http logger config session standardCallbackServer connectGate handleDeviceInfo handleQrCode ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            let socketInfo = session.SocketInfo |> Option.map (fun cached -> cached.Value)
            syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
                {
                    current with
                        SocketInfo = socketInfo
                        LatestDeviceInfo = session.LatestDeviceInfo
                        SupportedFunctions = session.SupportedFunctions
                        CapabilityProfiles = session.CapabilityProfiles
                })
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
                    handleDeviceInfo
                    handleQrCode
                    plan
                    requestedValue
                    ruleTraces
                    ct
            session <- updatedSession
            standardCallbackServer <- newCallbackServer
            let socketInfo = session.SocketInfo |> Option.map (fun cached -> cached.Value)
            syncLovenseSessionCache (fun (current: LovenseSessionSnapshot) ->
                {
                    current with
                        SocketInfo = socketInfo
                        LatestDeviceInfo = session.LatestDeviceInfo
                        SupportedFunctions = session.SupportedFunctions
                        CapabilityProfiles = session.CapabilityProfiles
                })
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
