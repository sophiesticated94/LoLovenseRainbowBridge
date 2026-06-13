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

    type PositionRotationState =
        {
            LastCaptureTime: DateTimeOffset option
            DetectionFailures: int
        }

    type LeagueCacheState =
        {
            Snapshot: BridgeSnapshot option
            DataAcquired: bool
            FailureAttemptsSinceSuccess: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type OcrCacheState =
        {
            Position: LovensePlanningPosition option
            DataAcquired: bool
            DetectionFailures: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type LovenseCacheState =
        {
            DataAcquired: bool
            Connected: bool
            FailureAttemptsSinceSuccess: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type RuntimeCacheSnapshot =
        {
            League: LeagueCacheState
            Ocr: OcrCacheState
            Lovense: LovenseCacheState
        }

    let initialPositionRotationState =
        {
            LastCaptureTime = None
            DetectionFailures = 0
        }

    let private initialLeague =
        {
            Snapshot = None
            DataAcquired = false
            FailureAttemptsSinceSuccess = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    let private initialOcr =
        {
            Position = None
            DataAcquired = false
            DetectionFailures = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    let private initialLovense =
        {
            DataAcquired = false
            Connected = false
            FailureAttemptsSinceSuccess = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    type RuntimeStateCache() =
        let gate = obj()
        let mutable snapshot =
            {
                League = initialLeague
                Ocr = initialOcr
                Lovense = initialLovense
            }

        member _.Read() =
            lock gate (fun () -> snapshot)

        member _.UpdateLeagueSuccess leagueSnapshot =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            League =
                                {
                                    snapshot.League with
                                        Snapshot = Some leagueSnapshot
                                        DataAcquired = true
                                        FailureAttemptsSinceSuccess = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.League.Version + 1L
                                }
                    })

        member _.UpdateLeagueFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            League =
                                {
                                    snapshot.League with
                                        DataAcquired = false
                                        FailureAttemptsSinceSuccess = snapshot.League.FailureAttemptsSinceSuccess + 1
                                        UnavailableSince = snapshot.League.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.League.Version + 1L
                                }
                    })

        member _.UpdateOcrSuccess position =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        Position = Some position
                                        DataAcquired = true
                                        DetectionFailures = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.Ocr.Version + 1L
                                }
                    })

        member _.UpdateOcrFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        DataAcquired = false
                                        DetectionFailures = snapshot.Ocr.DetectionFailures + 1
                                        UnavailableSince = snapshot.Ocr.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.Ocr.Version + 1L
                                }
                    })

        member _.UpdateLovenseSuccess connected =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Lovense =
                                {
                                    snapshot.Lovense with
                                        DataAcquired = true
                                        Connected = connected
                                        FailureAttemptsSinceSuccess = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.Lovense.Version + 1L
                                }
                    })

        member _.UpdateLovenseFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Lovense =
                                {
                                    snapshot.Lovense with
                                        DataAcquired = false
                                        Connected = false
                                        FailureAttemptsSinceSuccess = snapshot.Lovense.FailureAttemptsSinceSuccess + 1
                                        UnavailableSince = snapshot.Lovense.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.Lovense.Version + 1L
                                }
                    })

    type IAppJob =
        abstract Name: string
        abstract RunAsync: CancellationToken -> Task

    let private planningQuadrant normalizedX normalizedY =
        if normalizedX >= 0.42 && normalizedX <= 0.58 && normalizedY >= 0.42 && normalizedY <= 0.58 then "Center"
        elif normalizedX < 0.5 && normalizedY < 0.5 then "TopLeft"
        elif normalizedX >= 0.5 && normalizedY < 0.5 then "TopRight"
        elif normalizedX < 0.5 && normalizedY >= 0.5 then "BottomLeft"
        elif normalizedX >= 0.5 && normalizedY >= 0.5 then "BottomRight"
        elif normalizedX < 0.5 then "Left"
        else "Right"

    let private leagueErrorSummary error : obj =
        match error with
        | LeagueFetchError.ConnectionFailed(url, message) -> box {| kind = "ConnectionFailed"; url = url; message = message |}
        | LeagueFetchError.HttpFailure(url, statusCode, body) -> box {| kind = "HttpFailure"; url = url; statusCode = statusCode; bodyLength = body.Length |}
        | LeagueFetchError.InvalidJson(url, message, rawText) -> box {| kind = "InvalidJson"; url = url; message = message; rawLength = rawText.Length |}
        | LeagueFetchError.EmptyJson(url, rawText) -> box {| kind = "EmptyJson"; url = url; rawLength = rawText.Length |}
        | LeagueFetchError.UnexpectedFetchError(url, message, errorType) -> box {| kind = "UnexpectedFetchError"; url = url; message = message; errorType = errorType |}

    let private leagueErrorMessage error =
        match error with
        | LeagueFetchError.ConnectionFailed(_, message) -> message
        | LeagueFetchError.HttpFailure(_, statusCode, _) -> $"HTTP {statusCode}"
        | LeagueFetchError.InvalidJson(_, message, _) -> $"Invalid JSON: {message}"
        | LeagueFetchError.EmptyJson _ -> "Empty JSON"
        | LeagueFetchError.UnexpectedFetchError(_, message, _) -> message

    let private lovenseErrorMessage error =
        match error with
        | LovenseCommandError.NotConnected connectionError -> $"Not connected: {connectionError}"
        | LovenseCommandError.CommandEmitFailed(_, message) -> message
        | LovenseCommandError.CommandRejected(_, message) -> message
        | LovenseCommandError.CommandTimeout(_, timeoutMs) -> $"Command timed out after {timeoutMs}ms"
        | LovenseCommandError.UnexpectedCommandError(_, message, _) -> message

    let private lovenseErrorSummary error : obj =
        match error with
        | LovenseCommandError.NotConnected connectionError -> box {| kind = "NotConnected"; connectionError = string connectionError |}
        | LovenseCommandError.CommandEmitFailed(eventName, message) -> box {| kind = "CommandEmitFailed"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandRejected(eventName, message) -> box {| kind = "CommandRejected"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandTimeout(eventName, timeoutMs) -> box {| kind = "CommandTimeout"; eventName = eventName; timeoutMs = timeoutMs |}
        | LovenseCommandError.UnexpectedCommandError(eventName, message, errorType) -> box {| kind = "UnexpectedCommandError"; eventName = eventName; message = message; errorType = errorType |}

    let private neutralPlayer : BridgePlayer =
        {
            Id = "unavailable"
            Aliases = []
            Kills = 0
            Deaths = 0
            Assists = 0
            CreepScore = 0
            WardScore = 0.0
            Level = 1
            CurrentHealth = None
            MaxHealth = None
        }

    let private neutralSnapshot : BridgeSnapshot =
        {
            GameTime = 0.0
            ActiveAliases = []
            ActivePlayer = neutralPlayer
            Players = [ neutralPlayer ]
            Events = []
        }

    let private elapsedMs (since: DateTimeOffset option) (now: DateTimeOffset) =
        since
        |> Option.map (fun value -> max 0L (int64 (now - value).TotalMilliseconds))
        |> Option.defaultValue 0L

    let private runtimeRuleContext (snapshot: RuntimeCacheSnapshot) now =
        {
            LolDataAcquired = snapshot.League.DataAcquired
            OcrDataAcquired = snapshot.Ocr.DataAcquired
            LovenseDataAcquired = snapshot.Lovense.DataAcquired
            LolUnavailableElapsedMs = elapsedMs snapshot.League.UnavailableSince now
            OcrUnavailableElapsedMs = elapsedMs snapshot.Ocr.UnavailableSince now
            LovenseUnavailableElapsedMs = elapsedMs snapshot.Lovense.UnavailableSince now
            LolFailureAttemptsSinceSuccess = snapshot.League.FailureAttemptsSinceSuccess
            OcrFailureAttemptsSinceSuccess = snapshot.Ocr.DetectionFailures
            LovenseFailureAttemptsSinceSuccess = snapshot.Lovense.FailureAttemptsSinceSuccess
        }

    type LeagueCacheJob
        (
            runtimeConfig: RuntimeConfig,
            scoringConfig: ScoringConfig,
            leagueClient: LeagueLiveClient,
            cache: RuntimeStateCache,
            logger: StructuredSessionLogger
        ) =

        interface IAppJob with
            member _.Name = "LeagueCacheJob"

            member _.RunAsync(ct: CancellationToken) =
                task {
                    while not ct.IsCancellationRequested do
                        try
                            let! fetchResult = leagueClient.FetchAllGameDataAsync ct

                            match fetchResult with
                            | Error error ->
                                cache.UpdateLeagueFailure(leagueErrorMessage error)
                                let current = cache.Read().League
                                logger.Warn(
                                    "runtime.league_job.failure",
                                    "League cache job could not fetch League data.",
                                    {| error = leagueErrorSummary error; attemptSinceLastSuccess = current.FailureAttemptsSinceSuccess |}
                                )
                                do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)

                            | Ok gameData ->
                                match Parser.parseGameSnapshotResult gameData.Root with
                                | Error error ->
                                    cache.UpdateLeagueFailure(string error)
                                    let current = cache.Read().League
                                    logger.Warn(
                                        "runtime.league_job.parse_failed",
                                        "League cache job fetched data but parser returned an error.",
                                        {| parseError = error; attemptSinceLastSuccess = current.FailureAttemptsSinceSuccess; rawLength = gameData.RawText.Length |}
                                    )
                                    do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)

                                | Ok parsed ->
                                    let bridgeSnapshot = Mapper.toBridgeSnapshot scoringConfig parsed.Snapshot
                                    cache.UpdateLeagueSuccess bridgeSnapshot
                                    logger.Debug(
                                        "runtime.league_job.success",
                                        "League cache updated.",
                                        {| gameTime = bridgeSnapshot.GameTime; warnings = parsed.Warnings; version = cache.Read().League.Version |}
                                    )
                                    do! Task.Delay(runtimeConfig.LeaguePollMs, ct)
                        with
                        | :? OperationCanceledException -> ()
                        | ex ->
                            cache.UpdateLeagueFailure ex.Message
                            logger.Error(
                                "runtime.league_job.error",
                                "League cache job hit an unexpected error.",
                                {| error = ex.Message; errorType = ex.GetType().FullName |}
                            )
                            do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                } :> Task

    type OcrCacheJob
        (
            runtimeConfig: RuntimeConfig,
            positionRotationConfig: PositionBasedRotationConfig,
            cache: RuntimeStateCache,
            logger: StructuredSessionLogger
        ) =

        interface IAppJob with
            member _.Name = "OcrCacheJob"

            member _.RunAsync(ct: CancellationToken) =
                task {
                    while not ct.IsCancellationRequested do
                        try
                            if not positionRotationConfig.Enable then
                                cache.UpdateOcrFailure "Position-based rotation is disabled."
                                do! Task.Delay(runtimeConfig.OcrPollMs, ct)
                            else
                                let minimapRegion =
                                    {
                                        X = positionRotationConfig.MinimapScreenX
                                        Y = positionRotationConfig.MinimapScreenY
                                        Width = positionRotationConfig.MinimapWidth
                                        Height = positionRotationConfig.MinimapHeight
                                    }

                                let captureResult = ScreenCapture.captureLeagueMinimap minimapRegion
                                let template = positionRotationConfig.TemplateImagePath |> Option.bind MinimapDetector.loadTemplateFromFile
                                let detectionResult = MinimapDetector.detectPlayerPosition captureResult template

                                match detectionResult.Position with
                                | None ->
                                    cache.UpdateOcrFailure "No player position detected in minimap."
                                    logger.Debug(
                                        "runtime.ocr_job.no_detection",
                                        "OCR cache job did not detect minimap position.",
                                        {| detectionMethod = detectionResult.DetectionMethod; detectionFailures = cache.Read().Ocr.DetectionFailures |}
                                    )

                                | Some playerPosition ->
                                    match PositionMapping.parseMappingMode positionRotationConfig.MappingMode with
                                    | None ->
                                        cache.UpdateOcrFailure $"Invalid mapping mode: {positionRotationConfig.MappingMode}"
                                    | Some mode ->
                                        let rotationResult = PositionMapping.mapPositionToRotation playerPosition mode positionRotationConfig.RotationSensitivity
                                        let planningPosition =
                                            {
                                                NormalizedX = playerPosition.NormalizedX
                                                NormalizedY = playerPosition.NormalizedY
                                                Confidence = playerPosition.Confidence
                                                Quadrant = planningQuadrant playerPosition.NormalizedX playerPosition.NormalizedY
                                                Zone = string rotationResult.Zone
                                                DetectionMethod = detectionResult.DetectionMethod
                                            }

                                        cache.UpdateOcrSuccess planningPosition
                                        logger.Debug(
                                            "runtime.ocr_job.success",
                                            "OCR cache updated with minimap position.",
                                            {| normalizedX = planningPosition.NormalizedX; normalizedY = planningPosition.NormalizedY; quadrant = planningPosition.Quadrant; version = cache.Read().Ocr.Version |}
                                        )

                                do! Task.Delay(runtimeConfig.OcrPollMs, ct)
                        with
                        | :? OperationCanceledException -> ()
                        | ex ->
                            cache.UpdateOcrFailure ex.Message
                            logger.Warn(
                                "runtime.ocr_job.error",
                                "OCR cache job hit a recoverable error.",
                                {| error = ex.Message; errorType = ex.GetType().FullName |}
                            )
                            do! Task.Delay(runtimeConfig.OcrPollMs, ct)
                } :> Task

    type LovenseRuleJob
        (
            runtimeConfig: RuntimeConfig,
            scoringConfig: ScoringConfig,
            lovenseConfig: LovenseConfig,
            lovenseClient: LovenseClient,
            commandBuilder: ILovenseCommandValueBuilder,
            cache: RuntimeStateCache,
            logger: StructuredSessionLogger,
            recorder: GameplayRecorder option,
            recordingConfigSummary: obj
        ) =

        let mutable generatorState = initialState
        let mutable previousStateBeforeEvolve = initialState
        let mutable lastProcessedLeagueVersion = -1L
        let mutable lastSentFunctionState = LovenseActionCodec.emptyState

        let printStatus (snapshot: BridgeSnapshot) (breakdown: IntensityBreakdown) actionString =
            printfn
                "t=%6.1fs | K/D/A=%i/%i/%i | output=%i | action=%s"
                snapshot.GameTime
                snapshot.ActivePlayer.Kills
                snapshot.ActivePlayer.Deaths
                snapshot.ActivePlayer.Assists
                breakdown.Intensity
                actionString

        interface IAppJob with
            member _.Name = "LovenseRuleJob"

            member _.RunAsync(ct: CancellationToken) =
                task {
                    while not ct.IsCancellationRequested do
                        try
                            let cacheSnapshot = cache.Read()
                            let now = DateTimeOffset.UtcNow
                            let activeSnapshot = cacheSnapshot.League.Snapshot |> Option.defaultValue neutralSnapshot

                            if cacheSnapshot.League.DataAcquired && cacheSnapshot.League.Version <> lastProcessedLeagueVersion then
                                previousStateBeforeEvolve <- generatorState
                                generatorState <- evolve scoringConfig activeSnapshot generatorState
                                lastProcessedLeagueVersion <- cacheSnapshot.League.Version
                            else
                                previousStateBeforeEvolve <- generatorState

                            let commandFrame =
                                commandBuilder.Build
                                    {
                                        PreviousState = previousStateBeforeEvolve
                                        Snapshot = activeSnapshot
                                        EvolvedState = generatorState
                                        Position = if cacheSnapshot.Ocr.DataAcquired then cacheSnapshot.Ocr.Position else None
                                        Now = now
                                        LoopIteration = 0L
                                        LastSentFunctionState = lastSentFunctionState
                                        RuntimeContext = runtimeRuleContext cacheSnapshot now
                                        RuntimePollMs = runtimeConfig.LovensePollMs
                                    }

                            logger.Debug(
                                "runtime.lovense_job.calculation",
                                "Lovense rule job calculated command frame from runtime cache.",
                                {|
                                    cache =
                                        {|
                                            lolDataAcquired = cacheSnapshot.League.DataAcquired
                                            ocrDataAcquired = cacheSnapshot.Ocr.DataAcquired
                                            lovenseDataAcquired = cacheSnapshot.Lovense.DataAcquired
                                            leagueVersion = cacheSnapshot.League.Version
                                            ocrVersion = cacheSnapshot.Ocr.Version
                                        |}
                                    fullAction = commandFrame.ActionString
                                    changedAction = commandFrame.ChangedActionString
                                    changedFunctionState = commandFrame.ChangedFunctionState
                                    ruleVariables = commandFrame.RuleVariables
                                    ruleDiagnostics = commandFrame.Diagnostics
                                    ruleTraces = commandFrame.RuleTraces
                                |}
                            )

                            match commandFrame.ChangedPlan with
                            | None ->
                                logger.Debug(
                                    "runtime.lovense.no_function_changes",
                                    "Lovense command skipped because no function intensity changed.",
                                    {| fullAction = commandFrame.ActionString; fullFunctionState = commandFrame.FullFunctionState |}
                                )
                                do! Task.Delay(runtimeConfig.LovensePollMs, ct)

                            | Some changedPlan ->
                                let changedActionString = LovenseActionCodec.planActionString changedPlan
                                let shouldSend = shouldSendCommand runtimeConfig.ResendEveryMs now changedActionString generatorState

                                if not shouldSend then
                                    logger.Debug(
                                        "runtime.lovense.resend_suppressed",
                                        "Lovense diff command suppressed by resend interval.",
                                        {| changedAction = changedActionString; resendEveryMs = runtimeConfig.ResendEveryMs |}
                                    )
                                    do! Task.Delay(runtimeConfig.LovensePollMs, ct)
                                else
                                    let! result = lovenseClient.SendCommandPlanAsync(changedPlan, commandFrame.Breakdown.Intensity, commandFrame.RuleTraces, ct)

                                    match result with
                                    | Ok result ->
                                        lastSentFunctionState <- commandFrame.FullFunctionState
                                        cache.UpdateLovenseSuccess result.SocketConnected
                                        generatorState <- { generatorState with LastSent = Some(commandFrame.Breakdown.Intensity, now); LastSentCommand = Some(changedActionString, now) }

                                        cacheSnapshot.League.Snapshot
                                        |> Option.iter (fun snapshot ->
                                            recorder
                                            |> Option.iter (fun recorder ->
                                                recorder.RecordPlan(
                                                    now,
                                                    recordingConfigSummary,
                                                    snapshot,
                                                    commandFrame.Breakdown,
                                                    changedPlan,
                                                    changedActionString,
                                                    { Attempted = true; Success = Some true; Error = None }
                                                )))

                                        logger.Info(
                                            "runtime.lovense_job.send_success",
                                            "Lovense diff command sent successfully.",
                                            {| changedAction = changedActionString; changedFunctionState = commandFrame.ChangedFunctionState; fullAction = commandFrame.ActionString |}
                                        )
                                        printStatus activeSnapshot commandFrame.Breakdown changedActionString
                                        do! Task.Delay(runtimeConfig.LovensePollMs, ct)

                                    | Error error ->
                                        cache.UpdateLovenseFailure(lovenseErrorMessage error)

                                        cacheSnapshot.League.Snapshot
                                        |> Option.iter (fun snapshot ->
                                            recorder
                                            |> Option.iter (fun recorder ->
                                                recorder.RecordPlan(
                                                    now,
                                                    recordingConfigSummary,
                                                    snapshot,
                                                    commandFrame.Breakdown,
                                                    changedPlan,
                                                    changedActionString,
                                                    { Attempted = true; Success = Some false; Error = Some(lovenseErrorMessage error) }
                                                )))

                                        logger.Error(
                                            "runtime.lovense_job.send_failed",
                                            "Lovense diff command failed; last sent function state was not advanced.",
                                            {| error = lovenseErrorSummary error; changedAction = changedActionString; changedFunctionState = commandFrame.ChangedFunctionState |}
                                        )
                                        do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                        with
                        | :? OperationCanceledException -> ()
                        | ex ->
                            cache.UpdateLovenseFailure ex.Message
                            logger.Error(
                                "runtime.lovense_job.error",
                                "Lovense rule job hit an unexpected error.",
                                {| error = ex.Message; errorType = ex.GetType().FullName |}
                            )
                            do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                } :> Task
