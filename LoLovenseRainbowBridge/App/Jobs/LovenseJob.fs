namespace LoLovenseRainbowBridge.App.Jobs

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.Recording

type LovenseRuleJob
    (
        runtimeConfig: RuntimeConfig,
        scoringConfig: ScoringConfig,
        lovenseConfig: LovenseConfig,
        lovenseClient: LovenseClient,
        commandBuilder: ILovenseCommandValueBuilder,
        cache: RuntimeState.RuntimeStateCache,
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
                        let activeSnapshot = cacheSnapshot.League.Snapshot |> Option.defaultValue RuntimeState.neutralSnapshot

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
                                    cache.UpdateLovenseFailure(RuntimeState.lovenseErrorMessage error)

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
                                                { Attempted = true; Success = Some false; Error = Some(RuntimeState.lovenseErrorMessage error) }
                                            )))

                                    logger.Error(
                                        "runtime.lovense_job.send_failed",
                                        "Lovense diff command failed; last sent function state was not advanced.",
                                        {| error = RuntimeState.lovenseErrorSummary error; changedAction = changedActionString; changedFunctionState = commandFrame.ChangedFunctionState |}
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
