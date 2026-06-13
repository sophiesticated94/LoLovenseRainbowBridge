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
        QrCodeLogged: bool
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        GeneratedAuthToken: CachedSessionValue<string> option
        LatestDeviceInfo: LovenseDeviceInfo option
    }

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
            QrCodeLogged = false
            SupportedFunctions = None
            CapabilityProfiles = []
            GeneratedAuthToken = None
            LatestDeviceInfo = None
        }

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

            if config.LocalApi.EnableGetToys then
                let! localResult = LocalApi.getToysAsync localHttp logger config.LocalApi deviceInfo CancellationToken.None

                match localResult with
                | Ok localInfo when not localInfo.CapabilityProfiles.IsEmpty ->
                    let merged =
                        {
                            deviceInfo with
                                ToyList = localInfo.ToyList
                                SupportedFunctions = localInfo.SupportedFunctions |> Option.orElse deviceInfo.SupportedFunctions
                                CapabilityProfiles = localInfo.CapabilityProfiles
                        }

                    applyDeviceInfo merged

                | _ ->
                    ()
        }

    let onQrCode () =
        if not session.QrCodeLogged then
            session <- { session with QrCodeLogged = true }
            printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."

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

    let invalidateSessionCache reason =
        logger.Warn(
            "lovense.session.cache_invalidated",
            "Lovense cached auth/socket session data invalidated before retry.",
            {| reason = reason |}
        )

        session <-
            {
                session with
                    GeneratedAuthToken = None
                    SocketInfo = None
            }

    let shouldRetryWithFreshSession error =
        match error with
        | MissingDeveloperCredentials _ -> false
        | _ -> true

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
                    {| acquiredAt = cached.AcquiredAt; socketIoUrl = cached.Value.SocketIoUrl; socketIoPath = cached.Value.SocketIoPath |}
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
                        {| acquiredAt = acquiredAt; socketIoUrl = info.SocketIoUrl; socketIoPath = info.SocketIoPath |}
                    )

                    return Ok info
        }

    member _.CommandUrl =
        match session.SocketInfo with
        | Some cached -> $"{cached.Value.SocketIoUrl} ({cached.Value.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = session.LatestDeviceInfo

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
                        let connectOnce forceRefresh =
                            task {
                                let! authTokenResult = resolveAuthTokenAsync forceRefresh ct

                                match authTokenResult with
                                | Error error ->
                                    return Error error

                                | Ok authToken ->
                                    let! socketUrlResult = resolveSocketUrlAsync authToken forceRefresh ct

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
                            let! firstAttempt = connectOnce false

                            match firstAttempt with
                            | Ok state ->
                                return Ok state

                            | Error error when shouldRetryWithFreshSession error ->
                                invalidateSessionCache (string error)
                                let! secondAttempt = connectOnce true
                                return secondAttempt

                            | Error error ->
                                return Error error
                    finally
                        connectGate.Release() |> ignore
        }

    member this.SendCommandPlanAsync(plan: LovenseCommandPlan, requestedValue: int, ruleTraces: LovenseRuleEvaluationTrace list, ct: CancellationToken) =
        task {
            let safeValue = requestedValue |> Shared.clamp scoringConfig.MinIntensity scoringConfig.MaxIntensity
            let correlationId = Transport.newCorrelationId()
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
                    let! connected = this.EnsureConnectedAsync ct

                    match connected with
                    | Error error ->
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
