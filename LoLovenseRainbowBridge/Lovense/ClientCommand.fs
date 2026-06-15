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

    let sendCommandPlanAsync
        (http: HttpClient)
        (localHttp: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseConfig)
        (scoringConfig: ScoringConfig)
        (sessionStore: ClientState.LovenseSessionStore)
        (kickoffConnectionWarmup: unit -> unit)
        (plan: LovenseCommandPlan)
        (requestedValue: int)
        (ruleTraces: LovenseRuleEvaluationTrace list)
        (ct: CancellationToken)
        =
        task {
            let safeValue = requestedValue |> Shared.clamp scoringConfig.MinIntensity scoringConfig.MaxIntensity
            let correlationId = Transport.newCorrelationId()
            let currentSession = sessionStore.Read()
            let candidateActionString = LovenseActionCodec.planActionString plan
            let transportMode = config.TransportMode.ToUpperInvariant()
            let now = DateTimeOffset.UtcNow
            let capabilityResolution = CapabilityResolver.resolve config currentSession.CapabilityProfiles currentSession.SupportedFunctions plan
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
                        })
            else
                let payload = createCommandPayload filteredPlan correlationId
                let isCooldownActive (until: DateTimeOffset option) =
                    until |> Option.exists (fun value -> value > now)

                let markLocalCooldown () =
                    sessionStore.Update (fun state -> { state with LocalCommandCooldownUntil = Some(now.AddMilliseconds(float config.LocalApi.TimeoutMs)) })
                    |> ignore

                let markServerCooldown () =
                    sessionStore.Update (fun state -> { state with ServerCommandCooldownUntil = Some(now.AddMilliseconds(float config.CommandAckTimeoutMs)) })
                    |> ignore

                let markSocketRetryCooldown () =
                    sessionStore.Update (fun state -> { state with NextConnectRetryAt = Some(now.AddMilliseconds(float config.ConnectTimeoutMs)); SocketConnected = false })
                    |> ignore

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
                            })
                else
                    let sendLocalCommandAsync () =
                        LocalApi.sendCommandAsync localHttp logger config.LocalApi (sessionStore.Read().LatestDeviceInfo) filteredPlan correlationId ct

                    let sendStandardServerCommandAsync () =
                        StandardApi.sendServerCommandAsync http logger config.Developer filteredPlan correlationId ct

                    let sendViaSocket (socket: SocketIO) =
                        task {
                            let! emitResult = Transport.emitJsonAsync socket logger Constants.Lovense.SendToyCommandEmit payload config.CommandAckTimeoutMs ct

                            match emitResult with
                            | Ok ack ->
                                logger.Info(
                                    "lovense.command.emit_success",
                                    "Lovense Socket API command emitted successfully.",
                                    {| correlationId = correlationId; requestedValue = requestedValue; safeValue = safeValue; eventName = Constants.Lovense.SendToyCommandEmit; payload = payload; actionString = finalActionForLog; ack = ack |}
                                )

                                sessionStore.Update (fun state -> { state with SocketConnected = true; SocketReadyAt = Some now; NextConnectRetryAt = None })
                                |> ignore

                                return Ok
                                        {
                                            RequestedValue = requestedValue
                                            SafeValue = safeValue
                                            DryRun = false
                                            CorrelationId = correlationId
                                            SocketConnected = true
                                        }
                            | Error error ->
                                sessionStore.Update (fun state -> { state with SocketConnected = false }) |> ignore
                                return Error error
                        }

                    let tryFallbacks socketError =
                        task {
                            let currentSession = sessionStore.Read()

                            if transportMode = "AUTO" && config.StandardApi.UseServerCommandFallback && not (isCooldownActive currentSession.ServerCommandCooldownUntil) then
                                logger.Warn(
                                    "lovense.socket.fallback_to_server",
                                    "Lovense Socket API connection failed; trying Standard API server fallback.",
                                    {| correlationId = correlationId; socketError = string socketError; action = actionString |}
                                )

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
                                | Error serverError ->
                                    markServerCooldown ()
                                    return Error serverError
                            elif transportMode = "STANDARDAPILOCAL" && config.LocalApi.EnableCommandFallback && not (isCooldownActive currentSession.LocalCommandCooldownUntil) then
                                logger.Warn(
                                    "lovense.socket.fallback_to_local",
                                    "Lovense Socket API connection failed; trying Local API command fallback.",
                                    {| correlationId = correlationId; socketError = string socketError; localDomain = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.LocalApi.Domain; localHttpsPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.LocalApi.HttpsPort; localHttpPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpPort) |> Option.orElse config.LocalApi.HttpPort; action = actionString |}
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
                                | Error localError when config.StandardApi.UseServerCommandFallback ->
                                    if isCooldownActive currentSession.ServerCommandCooldownUntil then
                                        return Error localError
                                    else
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
                                        | Error serverError ->
                                            markServerCooldown ()
                                            return Error serverError
                                | Error localError ->
                                    markLocalCooldown ()
                                    return Error localError
                            elif transportMode = "AUTO" && config.LocalApi.EnableCommandFallback && not (isCooldownActive currentSession.LocalCommandCooldownUntil) then
                                logger.Warn(
                                    "lovense.socket.fallback_to_local",
                                    "Lovense Socket API connection failed; trying Local API command fallback.",
                                    {| correlationId = correlationId; socketError = string socketError; localDomain = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.LocalApi.Domain; localHttpsPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.LocalApi.HttpsPort; localHttpPort = currentSession.LatestDeviceInfo |> Option.bind (fun info -> info.HttpPort) |> Option.orElse config.LocalApi.HttpPort; action = actionString |}
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
                                | Error localError when config.StandardApi.UseServerCommandFallback ->
                                    if isCooldownActive currentSession.ServerCommandCooldownUntil then
                                        return Error localError
                                    else
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
                                        | Error serverError ->
                                            markServerCooldown ()
                                            return Error serverError
                                | Error localError ->
                                    markLocalCooldown ()
                                    return Error localError
                            elif transportMode = "AUTO" && config.StandardApi.UseServerCommandFallback && not (isCooldownActive currentSession.ServerCommandCooldownUntil) then
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
                                | Error serverError ->
                                    markServerCooldown ()
                                    return Error serverError
                            else
                                return Error(NotConnected socketError)
                        }

                    let currentSession = sessionStore.Read()

                    match currentSession.Socket with
                        | Some socket when socket.Connected ->
                            return! sendViaSocket socket
                        | _ ->
                            if not (isCooldownActive currentSession.NextConnectRetryAt) then
                                kickoffConnectionWarmup()
                            if transportMode = "SOCKETAPI" then
                                return Error(NotConnected(SocketDisconnected "Socket was not available before command emit."))
                            else
                                return! tryFallbacks (SocketDisconnected "Socket was not available before command emit.")
        }

