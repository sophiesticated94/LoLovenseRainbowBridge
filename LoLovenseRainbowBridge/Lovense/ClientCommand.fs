namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Threading
open LoLovenseRainbowBridge
open SocketIOClient

module ClientCommand =

    let private createCommandPayload (plan: LovenseCommandPlan) correlationId =
        let toyPart =
            plan.ToyId
            |> Option.map (fun toyId -> $",\"toy\":\"{LovenseFormatting.escapeJsonString toyId}\"")
            |> Option.defaultValue ""

        let stopPrevious = if plan.StopPrevious then 1 else 0
        let actionString = LovenseActionCodec.planActionString plan

        $"""{{"ackId":"{correlationId}","command":"{Constants.Lovense.CommandName}","action":"{LovenseFormatting.escapeJsonString actionString}","timeSec":{LovenseFormatting.invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""

    let private traceActionString (trace: LovenseRuleEvaluationTrace) =
        $"{trace.ExpandedFunction}:{int (System.Math.Round(trace.AfterLayerValue))}"

    let private ruleMappingSummary (traces: LovenseRuleEvaluationTrace list) (resolution: LovenseCapabilityResolution) =
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

    let private toyProfileSummary (profiles: LovenseToyCapabilityProfile list) =
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

    let private tryRefreshLocalGetToysAsync (localHttp: HttpClient) (logger: StructuredSessionLogger) (config: LovenseConfig) (session: LovenseSessionState) (nextLocalCapabilityRefreshAt: DateTimeOffset) (ct: CancellationToken) =
        task {
            let now = DateTimeOffset.UtcNow

            if not config.LocalApi.EnableGetToys || now < nextLocalCapabilityRefreshAt then
                return (session, nextLocalCapabilityRefreshAt)
            else
                let newNextRefresh = now.AddSeconds(float config.LocalApi.CapabilityRefreshIntervalSec)

                let deviceInfo =
                    session.LatestDeviceInfo
                    |> Option.defaultValue
                        {
                            ToyList = []
                            SupportedFunctions = session.SupportedFunctions
                            CapabilityProfiles = session.CapabilityProfiles
                            Domain = config.LocalApi.Domain
                            HttpsPort = config.LocalApi.HttpsPort
                            HttpPort = config.LocalApi.HttpPort
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

                    let updatedSession = ClientState.applyDeviceInfo merged session
                    logger.Debug(
                        "lovense.local_get_toys.cache_refreshed",
                        "Lovense Local API capability cache refreshed.",
                        {| nextRefreshAt = newNextRefresh; toyCount = merged.ToyList.Length |}
                    )
                    return (updatedSession, newNextRefresh)
                | Error error ->
                    logger.Debug(
                        "lovense.local_get_toys.cache_refresh_skipped",
                        "Lovense Local API capability cache refresh failed; old cache remains active.",
                        {| nextRefreshAt = newNextRefresh; error = string error |}
                    )
                    return (session, newNextRefresh)
                | _ -> return (session, newNextRefresh)
        }

    let sendCommandPlanAsync
        (http: HttpClient)
        (localHttp: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseConfig)
        (scoringConfig: ScoringConfig)
        (session: LovenseSessionState)
        (standardCallbackServer: StandardApiCallbackServer option)
        (nextLocalCapabilityRefreshAt: DateTimeOffset)
        (connectGate: Threading.SemaphoreSlim)
        (onDeviceInfo: LovenseDeviceInfo -> unit)
        (onQrCode: unit -> unit)
        (plan: LovenseCommandPlan)
        (requestedValue: int)
        (ruleTraces: LovenseRuleEvaluationTrace list)
        (ct: CancellationToken)
        =
        task {
            let safeValue = requestedValue |> Shared.clamp scoringConfig.MinIntensity scoringConfig.MaxIntensity
            let correlationId = Transport.newCorrelationId()
            let! (updatedSession1, newCallbackServer) = ClientConnection.ensureStandardApiReadyAsync http logger config session standardCallbackServer onDeviceInfo ct
            let! (updatedSession2, newNextRefresh) = tryRefreshLocalGetToysAsync localHttp logger config updatedSession1 nextLocalCapabilityRefreshAt ct
            let candidateActionString = LovenseActionCodec.planActionString plan
            let capabilityResolution = CapabilityResolver.resolve config updatedSession2.CapabilityProfiles updatedSession2.SupportedFunctions plan
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
                    (Ok
                        {
                            RequestedValue = requestedValue
                            SafeValue = safeValue
                            DryRun = config.DryRun
                            CorrelationId = correlationId
                            SocketConnected = false
                        }, updatedSession2, newCallbackServer, newNextRefresh)
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
                            payload = payload
                            actionString = finalActionForLog
                        |}
                    )

                    return
                        (Ok
                            {
                                RequestedValue = requestedValue
                                SafeValue = safeValue
                                DryRun = true
                                CorrelationId = correlationId
                                SocketConnected = false
                            }, updatedSession2, newCallbackServer, newNextRefresh)
                else
                    let transportMode = config.TransportMode.ToUpperInvariant()

                    let sendLocalCommandAsync currentSession =
                        LocalApi.sendCommandAsync localHttp logger config.LocalApi currentSession.LatestDeviceInfo filteredPlan correlationId ct

                    let sendStandardServerCommandAsync () =
                        StandardApi.sendServerCommandAsync http logger config.Developer filteredPlan correlationId ct

                    if transportMode = "STANDARDAPILOCAL" then
                        let! localResult = sendLocalCommandAsync updatedSession2

                        match localResult with
                        | Ok _ ->
                            return
                                (Ok
                                    {
                                        RequestedValue = requestedValue
                                        SafeValue = safeValue
                                        DryRun = false
                                        CorrelationId = correlationId
                                        SocketConnected = false
                                    }, updatedSession2, newCallbackServer, newNextRefresh)
                        | Error localError when config.StandardApi.UseServerCommandFallback ->
                            let! serverResult = sendStandardServerCommandAsync ()
                            match serverResult with
                            | Ok _ ->
                                return
                                    (Ok
                                        {
                                            RequestedValue = requestedValue
                                            SafeValue = safeValue
                                            DryRun = false
                                            CorrelationId = correlationId
                                            SocketConnected = false
                                        }, updatedSession2, newCallbackServer, newNextRefresh)
                            | Error serverError -> return (Error serverError, updatedSession2, newCallbackServer, newNextRefresh)
                        | Error localError -> return (Error localError, updatedSession2, newCallbackServer, newNextRefresh)
                    elif transportMode = "STANDARDAPISERVER" then
                        let! serverResult = sendStandardServerCommandAsync ()
                        match serverResult with
                        | Ok _ ->
                            return
                                (Ok
                                    {
                                        RequestedValue = requestedValue
                                        SafeValue = safeValue
                                        DryRun = false
                                        CorrelationId = correlationId
                                        SocketConnected = false
                                    }, updatedSession2, newCallbackServer, newNextRefresh)
                        | Error serverError -> return (Error serverError, updatedSession2, newCallbackServer, newNextRefresh)
                    else
                        let! (connected, connectedSession, connectedCallbackServer) =
                            ClientConnection.ensureConnectedAsync
                                http
                                logger
                                config
                                updatedSession2
                                newCallbackServer
                                connectGate
                                onDeviceInfo
                                onQrCode
                                ct

                        let tryFallbacks socketError currentSession currentCallbackServer =
                            task {
                                if transportMode = "AUTO" && config.LocalApi.EnableCommandFallback then
                                    logger.Warn(
                                        "lovense.socket.fallback_to_local",
                                        "Lovense Socket API connection failed; trying Local API command fallback.",
                                        {|
                                            correlationId = correlationId
                                            socketError = string socketError
                                            localDomain = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.LocalApi.Domain
                                            localHttpsPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.LocalApi.HttpsPort
                                            localHttpPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpPort) |> Option.orElse config.LocalApi.HttpPort
                                            action = actionString
                                        |}
                                    )

                                    let! localResult = sendLocalCommandAsync currentSession

                                    match localResult with
                                    | Ok _ ->
                                        return
                                            (Ok
                                                {
                                                    RequestedValue = requestedValue
                                                    SafeValue = safeValue
                                                    DryRun = false
                                                    CorrelationId = correlationId
                                                    SocketConnected = false
                                                }, currentSession, currentCallbackServer, newNextRefresh)
                                    | Error localError when config.StandardApi.UseServerCommandFallback ->
                                        let! serverResult = sendStandardServerCommandAsync ()

                                        match serverResult with
                                        | Ok _ ->
                                            return
                                                (Ok
                                                    {
                                                        RequestedValue = requestedValue
                                                        SafeValue = safeValue
                                                        DryRun = false
                                                        CorrelationId = correlationId
                                                        SocketConnected = false
                                                    }, currentSession, currentCallbackServer, newNextRefresh)
                                        | Error serverError ->
                                            return (Error serverError, currentSession, currentCallbackServer, newNextRefresh)
                                    | Error localError ->
                                        return (Error localError, currentSession, currentCallbackServer, newNextRefresh)
                                elif transportMode = "AUTO" && config.StandardApi.UseServerCommandFallback then
                                    let! serverResult = sendStandardServerCommandAsync ()

                                    match serverResult with
                                    | Ok _ ->
                                        return
                                            (Ok
                                                {
                                                    RequestedValue = requestedValue
                                                    SafeValue = safeValue
                                                    DryRun = false
                                                    CorrelationId = correlationId
                                                    SocketConnected = false
                                                }, currentSession, currentCallbackServer, newNextRefresh)
                                    | Error serverError ->
                                        return (Error serverError, currentSession, currentCallbackServer, newNextRefresh)
                                else
                                    return (Error(NotConnected socketError), currentSession, currentCallbackServer, newNextRefresh)
                            }

                        match connected with
                        | Error error ->
                            return! tryFallbacks error connectedSession connectedCallbackServer
                        | Ok _ ->
                            match connectedSession.Socket with
                            | None ->
                                return! tryFallbacks (SocketDisconnected "Socket was not available after successful connection.") connectedSession connectedCallbackServer
                            | Some socket when not socket.Connected ->
                                return! tryFallbacks (SocketDisconnected "Socket disconnected before command emit.") connectedSession connectedCallbackServer
                            | Some socket ->
                                let! emitResult = Transport.emitJsonAsync socket logger Constants.Lovense.SendToyCommandEmit payload config.CommandAckTimeoutMs ct

                                match emitResult with
                                | Ok ack ->
                                    logger.Info(
                                        "lovense.command.emit_success",
                                        "Lovense Socket API command emitted successfully.",
                                        {|
                                            correlationId = correlationId
                                            requestedValue = requestedValue
                                            safeValue = safeValue
                                            eventName = Constants.Lovense.SendToyCommandEmit
                                            payload = payload
                                            actionString = finalActionForLog
                                            ack = ack
                                        |}
                                    )

                                    return
                                        (Ok
                                            {
                                                RequestedValue = requestedValue
                                                SafeValue = safeValue
                                                DryRun = false
                                                CorrelationId = correlationId
                                                SocketConnected = true
                                            }, connectedSession, connectedCallbackServer, newNextRefresh)
                                | Error error ->
                                    return (Error error, connectedSession, connectedCallbackServer, newNextRefresh)
        }
