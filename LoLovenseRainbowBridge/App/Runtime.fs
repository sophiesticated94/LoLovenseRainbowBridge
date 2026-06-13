namespace LoLovenseRainbowBridge.App

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.Recording
open LoLovenseRainbowBridge.ScreenCapture
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge.PositionMapping

module Runtime =

    type ConnectionFailureState =
        {
            LeagueFailureAttemptsSinceSuccess: int
            LovenseFailureAttemptsSinceSuccess: int
            LastLeagueError: string option
            LastLovenseError: string option
            LastSuccessfulLeagueAt: DateTimeOffset option
            LastSuccessfulLovenseAt: DateTimeOffset option
        }

    type PositionRotationState =
        {
            LastCaptureTime: DateTimeOffset option
            DetectionFailures: int
        }

    let initialFailureState =
        {
            LeagueFailureAttemptsSinceSuccess = 0
            LovenseFailureAttemptsSinceSuccess = 0
            LastLeagueError = None
            LastLovenseError = None
            LastSuccessfulLeagueAt = None
            LastSuccessfulLovenseAt = None
        }

    let initialPositionRotationState =
        {
            LastCaptureTime = None
            DetectionFailures = 0
        }

    let private planningQuadrant normalizedX normalizedY =
        if normalizedX >= 0.42 && normalizedX <= 0.58 && normalizedY >= 0.42 && normalizedY <= 0.58 then "Center"
        elif normalizedX < 0.5 && normalizedY < 0.5 then "TopLeft"
        elif normalizedX >= 0.5 && normalizedY < 0.5 then "TopRight"
        elif normalizedX < 0.5 && normalizedY >= 0.5 then "BottomLeft"
        elif normalizedX >= 0.5 && normalizedY >= 0.5 then "BottomRight"
        elif normalizedX < 0.5 then "Left"
        else "Right"

    let private recordLeagueFailure error failureState =
        {
            failureState with
                LeagueFailureAttemptsSinceSuccess = failureState.LeagueFailureAttemptsSinceSuccess + 1
                LastLeagueError = Some error
        }

    let private recordLeagueSuccess now failureState =
        {
            failureState with
                LeagueFailureAttemptsSinceSuccess = 0
                LastLeagueError = None
                LastSuccessfulLeagueAt = Some now
        }

    let private recordLovenseFailure error failureState =
        {
            failureState with
                LovenseFailureAttemptsSinceSuccess = failureState.LovenseFailureAttemptsSinceSuccess + 1
                LastLovenseError = Some error
        }

    let private recordLovenseSuccess now failureState =
        {
            failureState with
                LovenseFailureAttemptsSinceSuccess = 0
                LastLovenseError = None
                LastSuccessfulLovenseAt = Some now
        }

    let private leagueErrorSummary error : obj =
        match error with
        | LeagueFetchError.ConnectionFailed(url, message) ->
            box {| kind = "ConnectionFailed"; url = url; message = message |}
        | LeagueFetchError.HttpFailure(url, statusCode, body) ->
            box {| kind = "HttpFailure"; url = url; statusCode = statusCode; bodyLength = body.Length |}
        | LeagueFetchError.InvalidJson(url, message, rawText) ->
            box {| kind = "InvalidJson"; url = url; message = message; rawLength = rawText.Length |}
        | LeagueFetchError.EmptyJson(url, rawText) ->
            box {| kind = "EmptyJson"; url = url; rawLength = rawText.Length |}
        | LeagueFetchError.UnexpectedFetchError(url, message, errorType) ->
            box {| kind = "UnexpectedFetchError"; url = url; message = message; errorType = errorType |}

    let private leagueErrorMessage error =
        match error with
        | LeagueFetchError.ConnectionFailed(_, message) -> message
        | LeagueFetchError.HttpFailure(_, statusCode, _) -> $"HTTP {statusCode}"
        | LeagueFetchError.InvalidJson(_, message, _) -> $"Invalid JSON: {message}"
        | LeagueFetchError.EmptyJson _ -> "Empty JSON"
        | LeagueFetchError.UnexpectedFetchError(_, message, _) -> message

    let private lovenseErrorSummary error : obj =
        match error with
        | LovenseCommandError.NotConnected connectionError ->
            box {| kind = "NotConnected"; connectionError = string connectionError |}
        | LovenseCommandError.CommandEmitFailed(eventName, message) ->
            box {| kind = "CommandEmitFailed"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandRejected(eventName, message) ->
            box {| kind = "CommandRejected"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandTimeout(eventName, timeoutMs) ->
            box {| kind = "CommandTimeout"; eventName = eventName; timeoutMs = timeoutMs |}
        | LovenseCommandError.UnexpectedCommandError(eventName, message, errorType) ->
            box {| kind = "UnexpectedCommandError"; eventName = eventName; message = message; errorType = errorType |}

    let private lovenseErrorMessage error =
        match error with
        | LovenseCommandError.NotConnected connectionError -> $"Not connected: {connectionError}"
        | LovenseCommandError.CommandEmitFailed(_, message) -> message
        | LovenseCommandError.CommandRejected(_, message) -> message
        | LovenseCommandError.CommandTimeout(_, timeoutMs) -> $"Command timed out after {timeoutMs}ms"
        | LovenseCommandError.UnexpectedCommandError(_, message, _) -> message

    let private printStatus (snapshot: BridgeSnapshot) (state: GeneratorState) (breakdown: IntensityBreakdown) =
        printfn
            "t=%6.1fs | K/D/A=%i/%i/%i | norm=%.2f | multikills=%i | temp=%i | output=%i"
            snapshot.GameTime
            snapshot.ActivePlayer.Kills
            snapshot.ActivePlayer.Deaths
            snapshot.ActivePlayer.Assists
            breakdown.NormalizedScore
            state.MultikillCount
            breakdown.TemporaryBoost
            breakdown.Intensity

    let private handleUnavailable
        (runtimeConfig: RuntimeConfig)
        (lovenseConfig: LovenseConfig)
        (lovenseClient: LovenseClient)
        (logger: StructuredSessionLogger)
        (recorder: GameplayRecorder option)
        (state: GeneratorState)
        (failureState: ConnectionFailureState)
        (ct: CancellationToken)
        =
        task {
            recorder |> Option.iter (fun recorder -> recorder.CloseActiveGame(DateTimeOffset.UtcNow))

            let now = DateTimeOffset.UtcNow
            let stopPlan = Mapping.stopPlan lovenseConfig StopCommand
            let stopAction = LovenseActionCodec.planActionString stopPlan
            let shouldSendStop = shouldSendCommand runtimeConfig.ResendEveryMs now stopAction state

            logger.Info(
                "runtime.unavailable.stop_decision",
                "Decided whether to send Lovense stop command while data is unavailable.",
                {|
                    shouldSend = shouldSendStop
                    now = now
                    previousLastSent = state.LastSent
                    previousLastSentCommand = state.LastSentCommand
                    commandAction = stopAction
                |}
            )

            if shouldSendStop then
                let! result = lovenseClient.SendCommandPlanAsync(stopPlan, 0, ct)

                match result with
                | Ok _ ->
                    let nextFailureState = recordLovenseSuccess now failureState
                    return { state with LastSent = Some(0, now); LastSentCommand = Some(stopAction, now) }, nextFailureState

                | Error error ->
                    let nextFailureState = recordLovenseFailure (lovenseErrorMessage error) failureState
                    printfn "Could not send Lovense stop command: %s" (lovenseErrorMessage error)
                    logger.Error(
                        "runtime.unavailable.stop_error",
                        "Could not send Lovense stop command.",
                        {|
                            error = lovenseErrorSummary error
                            attemptSinceLastSuccess = nextFailureState.LovenseFailureAttemptsSinceSuccess
                        |}
                    )

                    return state, nextFailureState
            else
                return state, failureState
        }

    let rec loop
        (runtimeConfig: RuntimeConfig)
        (scoringConfig: ScoringConfig)
        (lovenseConfig: LovenseConfig)
        (positionRotationConfig: PositionBasedRotationConfig)
        (leagueClient: LeagueLiveClient)
        (lovenseClient: LovenseClient)
        (commandBuilder: ILovenseCommandValueBuilder)
        (logger: StructuredSessionLogger)
        (recorder: GameplayRecorder option)
        (recordingConfigSummary: obj)
        (state: GeneratorState)
        (failureState: ConnectionFailureState)
        (positionRotationState: PositionRotationState)
        (loopIteration: int64)
        (ct: CancellationToken)
        : Task<unit>
        =
        task {
            let currentLoopIteration = loopIteration + 1L

            try
                let! fetchResult = leagueClient.FetchAllGameDataAsync ct

                match fetchResult with
                | Error error ->
                    let nextFailureState = recordLeagueFailure (leagueErrorMessage error) failureState

                    printfn
                        "Waiting for active LoL game. Attempt since last success: %i. Error: %s"
                        nextFailureState.LeagueFailureAttemptsSinceSuccess
                        (leagueErrorMessage error)

                    logger.Warn(
                        "runtime.league.fetch_failed",
                        "League data fetch failed; retrying after unavailable delay.",
                        {|
                            error = leagueErrorSummary error
                            attemptSinceLastSuccess = nextFailureState.LeagueFailureAttemptsSinceSuccess
                            retryDelayMs = runtimeConfig.UnavailableRetryMs
                        |}
                    )

                    let! nextState, nextFailureState = handleUnavailable runtimeConfig lovenseConfig lovenseClient logger recorder state nextFailureState ct
                    do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                    return! loop runtimeConfig scoringConfig lovenseConfig positionRotationConfig leagueClient lovenseClient commandBuilder logger recorder recordingConfigSummary nextState nextFailureState positionRotationState currentLoopIteration ct

                | Ok gameData ->
                    match Parser.parseGameSnapshotResult gameData.Root with
                    | Error error ->
                        let nextFailureState = recordLeagueFailure (string error) failureState

                        printfn "LoL data available, but active player could not be parsed."
                        logger.Warn(
                            "runtime.league.parse_failed",
                            "LoL data available but parse failed; retrying after unavailable delay.",
                            {|
                                parseError = error
                                attemptSinceLastSuccess = nextFailureState.LeagueFailureAttemptsSinceSuccess
                                retryDelayMs = runtimeConfig.UnavailableRetryMs
                                rawLength = gameData.RawText.Length
                            |}
                        )

                        let! nextState, nextFailureState = handleUnavailable runtimeConfig lovenseConfig lovenseClient logger recorder state nextFailureState ct
                        do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                        return! loop runtimeConfig scoringConfig lovenseConfig positionRotationConfig leagueClient lovenseClient commandBuilder logger recorder recordingConfigSummary nextState nextFailureState positionRotationState currentLoopIteration ct

                    | Ok parsed ->
                        let now = DateTimeOffset.UtcNow
                        let failureAfterLeagueSuccess = recordLeagueSuccess now failureState
                        let lolSnapshot = parsed.Snapshot
                        let snapshot = Mapper.toBridgeSnapshot scoringConfig lolSnapshot
                        let evolved = evolve scoringConfig snapshot state |> updateHealthPressure scoringConfig snapshot

                        let planningPosition, positionRotationState =
                            if positionRotationConfig.Enable then
                                let shouldCapture =
                                    match positionRotationState.LastCaptureTime with
                                    | None -> true
                                    | Some lastTime ->
                                        (now - lastTime).TotalMilliseconds >= float positionRotationConfig.CaptureIntervalMs

                                if shouldCapture then
                                    try
                                        let minimapRegion =
                                            {
                                                X = positionRotationConfig.MinimapScreenX
                                                Y = positionRotationConfig.MinimapScreenY
                                                Width = positionRotationConfig.MinimapWidth
                                                Height = positionRotationConfig.MinimapHeight
                                            }
                                        
                                        let captureResult = ScreenCapture.captureLeagueMinimap minimapRegion
                                        let template =
                                            positionRotationConfig.TemplateImagePath
                                            |> Option.bind MinimapDetector.loadTemplateFromFile
                                        let detectionResult = MinimapDetector.detectPlayerPosition captureResult template
                                        
                                        let nextPositionRotationState =
                                            {
                                                positionRotationState with
                                                    LastCaptureTime = Some now
                                                    DetectionFailures = if detectionResult.Position.IsNone then positionRotationState.DetectionFailures + 1 else 0
                                            }
                                        
                                        match detectionResult.Position with
                                        | Some playerPosition ->
                                            let mappingMode = PositionMapping.parseMappingMode positionRotationConfig.MappingMode
                                            match mappingMode with
                                            | Some mode ->
                                                let rotationResult = PositionMapping.mapPositionToRotation playerPosition mode positionRotationConfig.RotationSensitivity
                                                let quadrant = planningQuadrant playerPosition.NormalizedX playerPosition.NormalizedY
                                                let planningPosition =
                                                    {
                                                        NormalizedX = playerPosition.NormalizedX
                                                        NormalizedY = playerPosition.NormalizedY
                                                        Confidence = playerPosition.Confidence
                                                        Quadrant = quadrant
                                                        Zone = string rotationResult.Zone
                                                        DetectionMethod = detectionResult.DetectionMethod
                                                    }
                                                
                                                logger.Info(
                                                    "runtime.position_context.success",
                                                    "Position context captured for Lovense rule planning.",
                                                    {|
                                                        normalizedX = playerPosition.NormalizedX
                                                        normalizedY = playerPosition.NormalizedY
                                                        confidence = playerPosition.Confidence
                                                        detectionMethod = detectionResult.DetectionMethod
                                                        templateConfigured = positionRotationConfig.TemplateImagePath.IsSome
                                                        quadrant = quadrant
                                                        mappingMethod = rotationResult.MappingMethod
                                                        zone = string rotationResult.Zone
                                                    |}
                                                )
                                                
                                                Some planningPosition, nextPositionRotationState
                                            | None ->
                                                logger.Warn(
                                                    "runtime.position_rotation.invalid_mode",
                                                    "Invalid mapping mode in configuration.",
                                                    {| mode = positionRotationConfig.MappingMode |}
                                                )
                                                None, nextPositionRotationState
                                        | None ->
                                            logger.Debug(
                                                "runtime.position_rotation.no_detection",
                                                "No player position detected in minimap.",
                                                {|
                                                    detectionMethod = detectionResult.DetectionMethod
                                                    detectionFailures = nextPositionRotationState.DetectionFailures
                                                    templateConfigured = positionRotationConfig.TemplateImagePath.IsSome
                                                |}
                                            )
                                            None, nextPositionRotationState
                                    with ex ->
                                        logger.Error(
                                            "runtime.position_rotation.error",
                                            "Error during position-based rotation detection.",
                                            {| error = ex.Message |}
                                        )
                                        None, positionRotationState
                                else
                                    None, positionRotationState
                            else
                                None, positionRotationState

                        let commandFrame =
                            commandBuilder.Build
                                {
                                    PreviousState = state
                                    Snapshot = snapshot
                                    EvolvedState = evolved
                                    Position = planningPosition
                                    Now = now
                                    LoopIteration = currentLoopIteration
                                    RuntimePollMs = runtimeConfig.PollMs
                                }

                        let commandPlan = commandFrame.Plan
                        let actionString = commandFrame.ActionString
                        let breakdown = commandFrame.Breakdown
                        let intensity = breakdown.Intensity

                        logger.Info(
                            "runtime.league.success",
                            "League data fetched and parsed successfully; resetting League retry counter.",
                            {|
                                previousAttemptsSinceLastSuccess = failureState.LeagueFailureAttemptsSinceSuccess
                                warnings = parsed.Warnings
                            |}
                        )

                        if not parsed.Warnings.IsEmpty then
                            logger.Warn(
                                "runtime.league.parse_warnings",
                                "League data parsed with optional fields defaulted.",
                                {|
                                    warnings = parsed.Warnings
                                    rawLength = gameData.RawText.Length
                                |}
                            )

                        logger.Debug(
                            "runtime.calculation",
                            "Calculated Lovense intensity from League data.",
                            {|
                                activePlayer =
                                    {|
                                        id = snapshot.ActivePlayer.Id
                                        kills = snapshot.ActivePlayer.Kills
                                        deaths = snapshot.ActivePlayer.Deaths
                                        assists = snapshot.ActivePlayer.Assists
                                        creepScore = snapshot.ActivePlayer.CreepScore
                                        wardScore = snapshot.ActivePlayer.WardScore
                                        level = snapshot.ActivePlayer.Level
                                        currentHealth = snapshot.ActivePlayer.CurrentHealth
                                        maxHealth = snapshot.ActivePlayer.MaxHealth
                                    |}
                                state =
                                    {|
                                        previousSeenEvents = state.SeenEventIds.Count
                                        evolvedSeenEvents = evolved.SeenEventIds.Count
                                        previousPulseCount = state.Pulses.Length
                                        evolvedPulseCount = evolved.Pulses.Length
                                        multikillCount = evolved.MultikillCount
                                        healthPressure = evolved.HealthPressure
                                        lastSent = evolved.LastSent
                                        lastSentCommand = evolved.LastSentCommand
                                        loopIteration = currentLoopIteration
                                    |}
                                breakdown =
                                    {|
                                        performanceScore = breakdown.PerformanceScore
                                        normalizedScore = breakdown.NormalizedScore
                                        multikillBase = breakdown.MultikillBase
                                        deathPenalty = breakdown.DeathPenalty
                                        rawBaseValue = breakdown.RawBaseValue
                                        liveHealthPercent = breakdown.LiveHealthPercent
                                        liveHealthMultiplier = breakdown.LiveHealthMultiplier
                                        healthPressureMultiplier = breakdown.HealthPressureMultiplier
                                        healthAdjustedBaseValue = breakdown.HealthAdjustedBaseValue
                                        baseIntensity = breakdown.BaseIntensity
                                        temporaryBoost = breakdown.TemporaryBoost
                                        temporaryEffects = breakdown.TemporaryEffects |> List.map temporaryEffectLog
                                        rawFinalValue = breakdown.RawFinalValue
                                        intensity = breakdown.Intensity
                                    |}
                                commandPlan =
                                    {|
                                        action = actionString
                                        reasons = commandPlan.Reasons |> List.map LovenseActionCodec.reasonToString
                                        actions = commandPlan.Actions |> List.map LovenseActionCodec.actionToString
                                        timeSec = commandPlan.TimeSec
                                        stopPrevious = commandPlan.StopPrevious
                                        builderState = commandFrame.BuilderState
                                        functionStates =
                                            commandFrame.FunctionStates
                                            |> Map.toList
                                            |> List.map (fun (fn, layers) ->
                                                {|
                                                    functionName = LovenseActionCodec.actionName fn
                                                    baseLayer = layers.Base
                                                    timedLayer = layers.Timed
                                                    effectLayer = layers.Effect
                                                    final = layers.Final
                                                    contributions = layers.Contributions
                                                |})
                                        stateDiff = commandFrame.StateDiff
                                        ruleDiagnostics = commandFrame.Diagnostics
                                        ruleVariables = commandFrame.RuleVariables
                                    |}
                            |}
                        )

                        printStatus snapshot evolved breakdown

                        let! nextState =
                            task {
                                let shouldSendValue = shouldSendCommand runtimeConfig.ResendEveryMs now actionString evolved

                                logger.Info(
                                    "runtime.send_decision",
                                    "Decided whether to send Lovense command.",
                                    {|
                                        shouldSend = shouldSendValue
                                        intensity = intensity
                                        now = now
                                        previousLastSent = evolved.LastSent
                                        previousLastSentCommand = evolved.LastSentCommand
                                        resendEveryMs = runtimeConfig.ResendEveryMs
                                        commandAction = actionString
                                        commandReasons = commandPlan.Reasons |> List.map LovenseActionCodec.reasonToString
                                    |}
                                )

                                if shouldSendValue then
                                    let! lovenseResult = lovenseClient.SendCommandPlanAsync(commandPlan, intensity, ct)

                                    match lovenseResult with
                                    | Ok _ ->
                                        let nextFailureState = recordLovenseSuccess now failureAfterLeagueSuccess

                                        recorder
                                        |> Option.iter (fun recorder ->
                                            recorder.RecordPlan(
                                                now,
                                                recordingConfigSummary,
                                                snapshot,
                                                breakdown,
                                                commandPlan,
                                                actionString,
                                                { Attempted = true; Success = Some true; Error = None }
                                            ))

                                        logger.Info(
                                            "runtime.lovense.success",
                                            "Lovense command sent successfully; resetting Lovense retry counter.",
                                            {|
                                                previousAttemptsSinceLastSuccess = failureAfterLeagueSuccess.LovenseFailureAttemptsSinceSuccess
                                            |}
                                        )

                                        return { evolved with LastSent = Some(intensity, now); LastSentCommand = Some(actionString, now) }, nextFailureState, runtimeConfig.PollMs

                                    | Error error ->
                                        let nextFailureState = recordLovenseFailure (lovenseErrorMessage error) failureAfterLeagueSuccess

                                        recorder
                                        |> Option.iter (fun recorder ->
                                            recorder.RecordPlan(
                                                now,
                                                recordingConfigSummary,
                                                snapshot,
                                                breakdown,
                                                commandPlan,
                                                actionString,
                                                { Attempted = true; Success = Some false; Error = Some(lovenseErrorMessage error) }
                                            ))

                                        logger.Error(
                                            "runtime.lovense.send_failed",
                                            "Lovense command failed; retrying after unavailable delay.",
                                            {|
                                                error = lovenseErrorSummary error
                                                attemptSinceLastSuccess = nextFailureState.LovenseFailureAttemptsSinceSuccess
                                                retryDelayMs = runtimeConfig.UnavailableRetryMs
                                            |}
                                        )

                                        return evolved, nextFailureState, runtimeConfig.UnavailableRetryMs
                                else
                                    recorder
                                    |> Option.iter (fun recorder ->
                                        recorder.RecordPlan(
                                            now,
                                            recordingConfigSummary,
                                            snapshot,
                                            breakdown,
                                            commandPlan,
                                            actionString,
                                            { Attempted = false; Success = None; Error = None }
                                        ))

                                    return evolved, failureAfterLeagueSuccess, runtimeConfig.PollMs
                            }

                        let nextGeneratorState, nextFailureState, delayMs = nextState

                        do! Task.Delay(delayMs, ct)
                        return! loop runtimeConfig scoringConfig lovenseConfig positionRotationConfig leagueClient lovenseClient commandBuilder logger recorder recordingConfigSummary nextGeneratorState nextFailureState positionRotationState currentLoopIteration ct

            with
            | :? OperationCanceledException ->
                logger.Info("runtime.cancelled", "Runtime loop cancelled.")
                return ()

            | ex ->
                printfn "Waiting for active LoL game / Lovense app. Error: %s" ex.Message
                logger.Error(
                    "runtime.loop_error",
                    "Waiting for active LoL game / Lovense app after an error.",
                    {|
                        error = ex.Message
                        errorType = ex.GetType().FullName
                    |}
                )

                let! nextState = handleUnavailable runtimeConfig lovenseConfig lovenseClient logger recorder state failureState ct
                do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                let nextGeneratorState, nextFailureState = nextState
                return! loop runtimeConfig scoringConfig lovenseConfig positionRotationConfig leagueClient lovenseClient commandBuilder logger recorder recordingConfigSummary nextGeneratorState nextFailureState positionRotationState currentLoopIteration ct
        }
