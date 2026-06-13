namespace LoLovenseRainbowBridge.Lovense

open System
open System.Globalization
open System.Threading
open LoLovenseRainbowBridge
open SocketIOClient

type private CachedSessionValue<'T> =
    {
        Value: 'T
        AcquiredAt: DateTimeOffset
    }

type private LovenseSessionState =
    {
        Socket: SocketIO option
        SocketInfo: CachedSessionValue<SocketUrlInfo> option
        StandardQrCode: CachedSessionValue<StandardApiQrCodeInfo> option
        QrCodeLogged: bool
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        GeneratedAuthToken: CachedSessionValue<string> option
        LatestDeviceInfo: LovenseDeviceInfo option
    }

type private SessionRetryPolicy =
    | DoNotRetry
    | RetrySocketUrlOnly
    | RetryAuthAndSocketUrl

type LovenseClient(config: LovenseConfig, scoringConfig: ScoringConfig, logger: StructuredSessionLogger) =

    let http = Shared.insecureHttpClient ()
    let localHttp =
        if config.LocalApi.AllowSelfSignedCertificate then
            Shared.insecureHttpClient ()
        else
            new System.Net.Http.HttpClient()

    let connectGate = new Threading.SemaphoreSlim(1, 1)

    let mutable session =
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

    let invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let applyDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        session <-
            {
                session with
                    LatestDeviceInfo = Some deviceInfo
                    CapabilityProfiles = deviceInfo.CapabilityProfiles
                    SupportedFunctions = deviceInfo.SupportedFunctions |> Option.orElse session.SupportedFunctions
            }

    let onDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        task {
            applyDeviceInfo deviceInfo
            nextLocalCapabilityRefreshAt <- DateTimeOffset.MinValue
        }

    let onQrCode () =
        if not session.QrCodeLogged then
            session <- { session with QrCodeLogged = true }
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

    let tryRefreshLocalGetToysAsync (ct: CancellationToken) =
        task {
            let now = DateTimeOffset.UtcNow

            if not config.LocalApi.EnableGetToys || now < nextLocalCapabilityRefreshAt then
                return ()
            else
                nextLocalCapabilityRefreshAt <- now.AddSeconds(float config.LocalApi.CapabilityRefreshIntervalSec)

                let deviceInfo =
                    session.LatestDeviceInfo
                    |> Option.defaultValue
                        {
                            ToyList = []
                            SupportedFunctions = session.SupportedFunctions
                            CapabilityProfiles = session.CapabilityProfiles
                            Domain = config.LocalApi.Domain
                            HttpsPort = config.LocalApi.HttpsPort
                            HttpPort = None
                            WssPort = None
                        }

                let! result = LocalApi.getToysAsync localHttp logger config.LocalApi deviceInfo ct

                match result with
                | Ok localInfo when not localInfo.CapabilityProfiles.IsEmpty ->
                    let merged =
                        {
                            deviceInfo with
                                ToyList = localInfo.ToyList
                                SupportedFunctions = localInfo.SupportedFunctions |> Option.orElse deviceInfo.SupportedFunctions
                                CapabilityProfiles = localInfo.CapabilityProfiles
                        }

                    applyDeviceInfo merged
                    logger.Debug(
                        "lovense.local_get_toys.cache_refreshed",
                        "Lovense Local API capability cache refreshed.",
                        {| nextRefreshAt = nextLocalCapabilityRefreshAt; toyCount = merged.ToyList.Length |}
                    )
                | Error error ->
                    logger.Debug(
                        "lovense.local_get_toys.cache_refresh_skipped",
                        "Lovense Local API capability cache refresh failed; old cache remains active.",
                        {| nextRefreshAt = nextLocalCapabilityRefreshAt; error = string error |}
                    )
                | _ -> ()
        }

    let ensureStandardApiReadyAsync (ct: CancellationToken) =
        task {
            if config.StandardApi.Enable then
                if standardCallbackServer.IsNone then
                    let onStandardDeviceInfo deviceInfo =
                        applyDeviceInfo deviceInfo
                        nextLocalCapabilityRefreshAt <- DateTimeOffset.MinValue

                    standardCallbackServer <-
                        StandardApi.startCallbackListener logger config.StandardApi config.Developer onStandardDeviceInfo

                if config.StandardApi.GenerateQrOnStartup && session.StandardQrCode.IsNone then
                    let! qrResult = StandardApi.requestQrCodeAsync http logger config.StandardApi config.Developer ct

                    match qrResult with
                    | Ok qrInfo ->
                        session <- { session with StandardQrCode = Some { Value = qrInfo; AcquiredAt = DateTimeOffset.UtcNow } }
                        printfn "Lovense Standard API pairing code: %s" qrInfo.Code
                        printfn "Lovense Standard API QR: %s" qrInfo.Qr
                    | Error error ->
                        logger.Warn(
                            "lovense.standard.prepare_failed",
                            "Lovense Standard API QR/code preparation failed.",
                            {| error = string error |}
                        )
        }

    let createCommandPayload (plan: LovenseCommandPlan) correlationId =
        let toyPart =
            plan.ToyId
            |> Option.map (fun toyId -> $",\"toy\":\"{escapeJsonString toyId}\"")
            |> Option.defaultValue ""

        let stopPrevious = if plan.StopPrevious then 1 else 0
        let actionString = LovenseActionCodec.planActionString plan

        $"""{{"ackId":"{correlationId}","command":"{Constants.Lovense.CommandName}","action":"{escapeJsonString actionString}","timeSec":{invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""

    let traceActionString (trace: LovenseRuleEvaluationTrace) =
        $"{trace.ExpandedFunction}:{int (Math.Round(trace.AfterLayerValue))}"

    let ruleMappingSummary (traces: LovenseRuleEvaluationTrace list) (resolution: LovenseCapabilityResolution) =
        let finalSet = resolution.FinalActions |> Set.ofList
        let droppedSet = resolution.DroppedActions |> Set.ofList

        traces
        |> List.groupBy (fun trace -> trace.RuleName)
        |> List.map (fun (ruleName, ruleTraces) ->
            let expandedFunctions =
                ruleTraces
                |> List.map (fun trace -> trace.ExpandedFunction)
                |> List.distinct

            let candidateActions =
                ruleTraces
                |> List.map traceActionString
                |> List.distinct

            let viableActions =
                candidateActions
                |> List.filter (fun action -> finalSet.Contains action)

            let droppedActions =
                candidateActions
                |> List.filter (fun action -> droppedSet.Contains action)

            let traceDetails =
                ruleTraces
                |> List.map (fun trace ->
                    {|
                        expandedFunction = trace.ExpandedFunction
                        layer = trace.Layer
                        operation = trace.Operation
                        expression = trace.Expression
                        value = trace.Value
                        minValue = trace.MinValue
                        maxValue = trace.MaxValue
                        beforeLayerValue = trace.BeforeLayerValue
                        afterLayerValue = trace.AfterLayerValue
                    |})

            {|
                ruleName = ruleName
                kind = ruleTraces.Head.Kind
                condition = ruleTraces.Head.Condition
                targetFunctions = ruleTraces.Head.TargetFunctions
                expandedFunctions = expandedFunctions
                candidateActions = candidateActions
                viableActions = viableActions
                droppedActions = droppedActions
                hasAnyViableToyFunction = not viableActions.IsEmpty
                capabilitySource = resolution.CapabilitySource
                traces = traceDetails
            |})

    let toyProfileSummary (profiles: LovenseToyCapabilityProfile list) =
        profiles
        |> List.map (fun profile ->
            {|
                toyId =
                    profile.ToyId
                    |> Option.map (fun toyId -> if toyId.Length <= 4 then "***" else $"***{toyId.Substring(toyId.Length - 4)}")
                name = profile.Name
                toyType = profile.ToyType
                connected = profile.Connected
                explicitFunctions = profile.ExplicitFunctions |> Set.toList
                inferredFunctions = profile.InferredFunctions |> Set.toList
                supportedFunctions = profile.SupportedFunctions |> Set.toList
                stereoVibrationSupported = profile.StereoVibrationSupported
                capabilitySource = string profile.CapabilitySource
                notes = profile.Notes
            |})

    let invalidateSocketUrl reason =
        logger.Warn(
            "lovense.socket_url.cache_invalidated",
            "Lovense cached Socket.IO URL invalidated before retry.",
            {| reason = reason |}
        )

        session <-
            {
                session with
                    SocketInfo = None
            }

    let invalidateAuthAndSocketUrl reason =
        logger.Warn(
            "lovense.session.cache_invalidated",
            "Lovense cached auth token and Socket.IO URL invalidated before retry.",
            {| reason = reason |}
        )

        session <-
            {
                session with
                    GeneratedAuthToken = None
                    SocketInfo = None
            }

    let containsAny (needles: string list) (value: string) =
        let haystack = if isNull value then "" else value.ToUpperInvariant()
        needles |> List.exists (fun needle -> haystack.Contains(needle.ToUpperInvariant(), StringComparison.Ordinal))

    let retryPolicyFor error =
        match error with
        | MissingDeveloperCredentials _ ->
            DoNotRetry
        | SocketUrlRejected(_, _, message)
            when containsAny [ "AUTH"; "TOKEN"; "EXPIRED"; "INVALID"; "UNAUTHORIZED" ] message ->
            RetryAuthAndSocketUrl
        | SocketUrlRejected _ ->
            DoNotRetry
        | SocketUrlRequestFailed _ ->
            RetrySocketUrlOnly
        | SocketConnectFailed _ ->
            RetrySocketUrlOnly
        | SocketDisconnected _ ->
            RetrySocketUrlOnly
        | UnexpectedConnectionError(_, errorType)
            when containsAny [ "AUTH"; "TOKEN"; "UNAUTHORIZED" ] errorType ->
            RetryAuthAndSocketUrl
        | UnexpectedConnectionError _ ->
            RetrySocketUrlOnly

    let resolveAuthTokenAsync forceRefresh (ct: CancellationToken) =
        task {
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
                    session <- { session with GeneratedAuthToken = Some { Value = authToken; AcquiredAt = acquiredAt } }

                    logger.Info(
                        "lovense.auth.refresh",
                        "Lovense auth token stored in application state.",
                        {| acquiredAt = acquiredAt; authToken = Constants.Lovense.AuthTokenRedacted |}
                    )

                    return Ok authToken
                | Error error ->
                    return Error error
        }

    let resolveSocketUrlAsync authToken forceRefresh (ct: CancellationToken) =
        task {
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
                    session <- { session with SocketInfo = Some { Value = info; AcquiredAt = acquiredAt } }

                    logger.Info(
                        "lovense.socket_url.refresh",
                        "Lovense Socket.IO URL stored in application state.",
                        {| acquiredAt = acquiredAt; socketIoUrl = Shared.redactUrlSecrets info.SocketIoUrl; socketIoPath = info.SocketIoPath |}
                    )

                    return Ok info
        }

    member _.CommandUrl =
        match session.SocketInfo with
        | Some cached -> $"{cached.Value.SocketIoUrl} ({cached.Value.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = session.LatestDeviceInfo

    member _.PrepareStandardApiAsync(ct: CancellationToken) =
        ensureStandardApiReadyAsync ct

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
                                let! authTokenResult = resolveAuthTokenAsync forceAuthRefresh ct

                                match authTokenResult with
                                | Error error ->
                                    return Error error

                                | Ok authToken ->
                                    let! socketUrlResult = resolveSocketUrlAsync authToken forceSocketUrlRefresh ct

                                    match socketUrlResult with
                                    | Error error ->
                                        return Error error

                                    | Ok info ->
                                        let! connected = SocketRuntime.connectAsync config logger onDeviceInfo onQrCode info ct

                                        match connected with
                                        | Ok (client, state) ->
                                            session <- { session with Socket = Some client }
                                            return Ok state
                                        | Error error ->
                                            return Error error
                            }

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
                                match retryPolicyFor error with
                                | DoNotRetry ->
                                    return Error error
                                | RetrySocketUrlOnly ->
                                    invalidateSocketUrl (string error)
                                    let! secondAttempt = connectOnce false true
                                    return secondAttempt
                                | RetryAuthAndSocketUrl ->
                                    invalidateAuthAndSocketUrl (string error)
                                    let! secondAttempt = connectOnce true true
                                    return secondAttempt

                    finally
                        connectGate.Release() |> ignore
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ruleTraces: LovenseRuleEvaluationTrace list, ct: CancellationToken) =
        task {
            let safeValue = requestedValue |> Shared.clamp scoringConfig.MinIntensity scoringConfig.MaxIntensity
            let correlationId = Transport.newCorrelationId()
            do! ensureStandardApiReadyAsync ct
            do! tryRefreshLocalGetToysAsync ct
            let candidateActionString = LovenseActionCodec.planActionString plan
            let capabilityResolution = CapabilityResolver.resolve config session.CapabilityProfiles session.SupportedFunctions plan
            let filteredPlan = capabilityResolution.Plan
            let droppedActions = capabilityResolution.DroppedActions
            let actionString = LovenseActionCodec.planActionString filteredPlan
            let finalActionForLog = if capabilityResolution.NoSupportedActions then "" else actionString
            let commandReasons = filteredPlan.Reasons |> List.map LovenseActionCodec.reasonToString
            let capabilitySource = capabilityResolution.CapabilitySource
            let mappingSummary = ruleMappingSummary ruleTraces capabilityResolution
            let toyProfiles = toyProfileSummary capabilityResolution.ToyProfiles

            logger.Debug(
                "lovense.rule_mapping",
                "Mapped Lovense rules into capability-filtered command actions.",
                {|
                    correlationId = correlationId
                    candidateAction = candidateActionString
                    finalAction = finalActionForLog
                    payloadAction = finalActionForLog
                    droppedActions = droppedActions
                    finalActions = capabilityResolution.FinalActions
                    stereoApplied = capabilityResolution.StereoApplied
                    stereoFallbackApplied = capabilityResolution.StereoFallbackApplied
                    capabilitySource = capabilitySource
                    toyProfiles = toyProfiles
                    ruleMapping = mappingSummary
                |}
            )

            if capabilityResolution.NoSupportedActions then
                logger.Warn(
                    "lovense.command.no_supported_actions",
                    "No Lovense command emitted because no candidate action is supported by the selected toy capabilities.",
                    {|
                        correlationId = correlationId
                        requestedValue = requestedValue
                        safeValue = safeValue
                        candidateAction = candidateActionString
                        finalAction = finalActionForLog
                        payloadAction = ""
                        droppedActions = droppedActions
                        finalActions = capabilityResolution.FinalActions
                        reasons = commandReasons
                        capabilitySource = capabilitySource
                        toyProfiles = toyProfiles
                        ruleMapping = mappingSummary
                    |}
                )

                return
                    Ok
                        {
                            RequestedValue = requestedValue
                            SafeValue = safeValue
                            DryRun = config.DryRun
                            CorrelationId = correlationId
                            SocketConnected = false
                        }
            else
                let payload = createCommandPayload filteredPlan correlationId

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
                            finalAction = actionString
                            payloadAction = actionString
                            droppedActions = droppedActions
                            finalActions = capabilityResolution.FinalActions
                            stereoApplied = capabilityResolution.StereoApplied
                            stereoFallbackApplied = capabilityResolution.StereoFallbackApplied
                            reasons = commandReasons
                            capabilitySource = capabilitySource
                            ruleMapping = mappingSummary
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
                    let transportMode = config.TransportMode.ToUpperInvariant()

                    let sendLocalCommandAsync () =
                        LocalApi.sendCommandAsync localHttp logger config.LocalApi session.LatestDeviceInfo filteredPlan correlationId ct

                    let sendStandardServerCommandAsync () =
                        StandardApi.sendServerCommandAsync http logger config.Developer filteredPlan correlationId ct

                    if transportMode = "STANDARDAPILOCAL" then
                        let! localResult = sendLocalCommandAsync ()

                        match localResult with
                        | Ok _ ->
                            return
                                Ok
                                    {
                                        RequestedValue = requestedValue
                                        SafeValue = safeValue
                                        DryRun = false
                                        CorrelationId = correlationId
                                        SocketConnected = false
                                    }
                        | Error localError when config.StandardApi.UseServerCommandFallback ->
                            let! serverResult = sendStandardServerCommandAsync ()
                            match serverResult with
                            | Ok _ ->
                                return
                                    Ok
                                        {
                                            RequestedValue = requestedValue
                                            SafeValue = safeValue
                                            DryRun = false
                                            CorrelationId = correlationId
                                            SocketConnected = false
                                        }
                            | Error serverError -> return Error serverError
                        | Error localError -> return Error localError
                    elif transportMode = "STANDARDAPISERVER" then
                        let! serverResult = sendStandardServerCommandAsync ()
                        match serverResult with
                        | Ok _ ->
                            return
                                Ok
                                    {
                                        RequestedValue = requestedValue
                                        SafeValue = safeValue
                                        DryRun = false
                                        CorrelationId = correlationId
                                        SocketConnected = false
                                    }
                        | Error serverError -> return Error serverError
                    else
                        let! connected = this.EnsureConnectedAsync ct

                        match connected with
                        | Error error ->
                            if config.LocalApi.EnableCommandFallback || transportMode = "AUTO" then
                                logger.Warn(
                                    "lovense.socket.fallback_to_local",
                                    "Lovense Socket API connection failed; trying Local API command fallback.",
                                    {|
                                        correlationId = correlationId
                                        socketError = string error
                                        localDomain = session.LatestDeviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.LocalApi.Domain
                                        localHttpsPort = session.LatestDeviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.LocalApi.HttpsPort
                                        action = actionString
                                    |}
                                )

                                let! localResult = sendLocalCommandAsync ()

                                match localResult with
                                | Ok _ ->
                                    return
                                        Ok
                                            {
                                                RequestedValue = requestedValue
                                                SafeValue = safeValue
                                                DryRun = false
                                                CorrelationId = correlationId
                                                SocketConnected = false
                                            }
                                | Error localError when config.StandardApi.UseServerCommandFallback && transportMode = "AUTO" ->
                                    let! serverResult = sendStandardServerCommandAsync ()
                                    match serverResult with
                                    | Ok _ ->
                                        return
                                            Ok
                                                {
                                                    RequestedValue = requestedValue
                                                    SafeValue = safeValue
                                                    DryRun = false
                                                    CorrelationId = correlationId
                                                    SocketConnected = false
                                                }
                                    | Error serverError -> return Error serverError
                                | Error localError ->
                                    return Error localError
                            else
                                return Error(NotConnected error)

                        | Ok _ ->
                            match session.Socket with
                            | None ->
                                return Error(NotConnected(SocketDisconnected "Socket was not available after successful connection."))

                            | Some client when not client.Connected ->
                                return Error(NotConnected(SocketDisconnected "Socket disconnected before command emit."))

                            | Some client ->
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
                                        finalAction = actionString
                                        payloadAction = actionString
                                        droppedActions = droppedActions
                                        finalActions = capabilityResolution.FinalActions
                                        stereoApplied = capabilityResolution.StereoApplied
                                        stereoFallbackApplied = capabilityResolution.StereoFallbackApplied
                                        reasons = commandReasons
                                        capabilitySource = capabilitySource
                                        ruleMapping = mappingSummary
                                        payloadLength = payload.Length
                                        rawLogged = logger.IsRawLovenseEnabled
                                    |}
                                )

                                let! emitResult =
                                    Transport.emitJsonAsync
                                        client
                                        logger
                                        Constants.Lovense.SendToyCommandEmit
                                        payload
                                        config.CommandAckTimeoutMs
                                        ct

                                match emitResult with
                                | Error error ->
                                    return Error error
                                | Ok _ ->
                                    return
                                        Ok
                                            {
                                                RequestedValue = requestedValue
                                                SafeValue = safeValue
                                                DryRun = false
                                                CorrelationId = correlationId
                                                SocketConnected = client.Connected
                                            }
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
