namespace LoLovenseRainbowBridge.Lovense

open System
open System.Globalization
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

    let mutable socket: SocketIO option = None
    let mutable socketInfo: SocketUrlInfo option = None
    let mutable qrCodeLogged = false
    let mutable supportedFunctions: Set<string> option = None
    let mutable capabilityProfiles: LovenseToyCapabilityProfile list = []
    let mutable generatedAuthToken: string option = None
    let mutable latestDeviceInfo: LovenseDeviceInfo option = None

    let invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let applyDeviceInfo (deviceInfo: LovenseDeviceInfo) =
        latestDeviceInfo <- Some deviceInfo
        capabilityProfiles <- deviceInfo.CapabilityProfiles

        match deviceInfo.SupportedFunctions with
        | Some functions -> supportedFunctions <- Some functions
        | None -> ()

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
        if not qrCodeLogged then
            qrCodeLogged <- true
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

    let resolveAuthTokenAsync (ct: CancellationToken) =
        task {
            match generatedAuthToken with
            | Some authToken when not (String.IsNullOrWhiteSpace authToken) ->
                return Ok authToken

            | _ ->
                let! tokenResult = Auth.requestAuthTokenAsync http logger config.Developer ct

                match tokenResult with
                | Ok authToken ->
                    generatedAuthToken <- Some authToken
                    return Ok authToken
                | Error error ->
                    return Error error
        }

    member _.CommandUrl =
        match socketInfo with
        | Some info -> $"{info.SocketIoUrl} ({info.SocketIoPath})"
        | None -> Constants.Lovense.GetSocketUrl

    member _.LatestDeviceInfo = latestDeviceInfo

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
                            let! authTokenResult = resolveAuthTokenAsync ct

                            match authTokenResult with
                            | Error error ->
                                return Error error

                            | Ok authToken ->
                                let! socketUrlResult = SocketUrl.requestSocketUrlAsync http logger config.Platform authToken ct

                                match socketUrlResult with
                                | Error error ->
                                    return Error error

                                | Ok info ->
                                    let! connected = SocketRuntime.connectAsync config logger onDeviceInfo onQrCode info ct

                                    match connected with
                                    | Ok (client, state) ->
                                        socket <- Some client
                                        socketInfo <- Some info
                                        return Ok state
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
            let capabilityResolution = CapabilityResolver.resolve config capabilityProfiles supportedFunctions plan
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
                        match socket with
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
            localHttp.Dispose()
