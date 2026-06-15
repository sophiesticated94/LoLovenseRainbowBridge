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
                    let startedAt = DateTimeOffset.UtcNow
                    let iteration = cache.UpdateLovenseCycleStarted()

                    try
                        let cacheSnapshot = cache.Read()
                        let now = DateTimeOffset.UtcNow
                        cache.UpdateLovenseClock(iteration, now, runtimeConfig.LovensePollMs)

                        let activeSnapshot = cacheSnapshot.League.Snapshot |> Option.defaultValue RuntimeState.neutralSnapshot

                        let commandFrame =
                            commandBuilder.Build
                                {
                                    PreviousState = initialState
                                    Snapshot = activeSnapshot
                                    EvolvedState = initialState
                                    Position = if cacheSnapshot.Ocr.DataAcquired then cacheSnapshot.Ocr.Position else None
                                    Now = now
                                    LoopIteration = iteration
                                    LastSentFunctionState = lastSentFunctionState
                                    RuntimePollMs = runtimeConfig.LovensePollMs
                                }

                        logger.Debug(
                            "runtime.lovense_job.calculation",
                            "Lovense rule job calculated command frame from runtime cache.",
                            {| 
                                cache =
                                    {| 
                                        lolDataAcquired = cacheSnapshot.RuntimeContext.LolDataAcquired
                                        ocrDataAcquired = cacheSnapshot.RuntimeContext.OcrDataAcquired
                                        lovenseDataAcquired = cacheSnapshot.RuntimeContext.LovenseDataAcquired
                                        toyDataAcquired = cacheSnapshot.RuntimeContext.ToyDataAcquired
                                        leagueVersion = cacheSnapshot.League.Version
                                        ocrVersion = cacheSnapshot.Ocr.Version
                                        toyVersion = cacheSnapshot.Toys.Version
                                    |}
                                fullAction = commandFrame.ActionString
                                changedAction = commandFrame.ChangedActionString
                                changedFunctionState = commandFrame.ChangedFunctionState
                                ruleVariables = commandFrame.RuleVariables
                                ruleDiagnostics = commandFrame.Diagnostics
                                ruleTraces = commandFrame.RuleTraces
                            |}
                        )

                        let outgoingPlan = commandFrame.ChangedPlan
                        let outgoingActionString = commandFrame.ChangedActionString

                        match outgoingPlan, outgoingActionString with
                        | Some outgoingPlan, Some outgoingActionString ->
                            let! result = lovenseClient.SendCommandPlanAsync(outgoingPlan, commandFrame.Breakdown.Intensity, commandFrame.RuleTraces, ct)

                            match result with
                            | Ok result ->
                                lastSentFunctionState <- commandFrame.FullFunctionState
                                cache.UpdateLovenseSuccess result.SocketConnected
                                cache.UpdateLovenseCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Success")

                                cacheSnapshot.League.Snapshot
                                |> Option.iter (fun snapshot ->
                                    recorder
                                    |> Option.iter (fun recorder ->
                                        recorder.RecordPlan(
                                            now,
                                            recordingConfigSummary,
                                            snapshot,
                                            commandFrame.Breakdown,
                                            outgoingPlan,
                                            outgoingActionString,
                                            { Attempted = true; Success = Some true; Error = None }
                                        )))

                                logger.Info(
                                    "runtime.lovense_job.send_success",
                                    "Lovense command sent successfully.",
                                    {| changedAction = outgoingActionString; changedFunctionState = commandFrame.ChangedFunctionState; fullAction = commandFrame.ActionString |}
                                )
                                printStatus activeSnapshot commandFrame.Breakdown outgoingActionString
                                do! Task.Delay(runtimeConfig.LovensePollMs, ct)

                            | Error error ->
                                cache.UpdateLovenseFailure(RuntimeState.lovenseErrorMessage error)
                                cache.UpdateLovenseCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Retrying")

                                cacheSnapshot.League.Snapshot
                                |> Option.iter (fun snapshot ->
                                    recorder
                                    |> Option.iter (fun recorder ->
                                        recorder.RecordPlan(
                                            now,
                                            recordingConfigSummary,
                                            snapshot,
                                            commandFrame.Breakdown,
                                            outgoingPlan,
                                            outgoingActionString,
                                            { Attempted = true; Success = Some false; Error = Some(RuntimeState.lovenseErrorMessage error) }
                                        )))

                                logger.Error(
                                    "runtime.lovense_job.send_failed",
                                    "Lovense command failed.",
                                    {| 
                                        error = RuntimeState.lovenseErrorSummary error
                                        changedAction = outgoingActionString
                                        changedFunctionState = commandFrame.ChangedFunctionState
                                    |}
                                )
                                do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
                        | _ ->
                            logger.Debug(
                                "runtime.lovense_job.no_function_changes",
                                "Lovense command frame did not change, so nothing was emitted.",
                                {| fullAction = commandFrame.ActionString; fullFunctionState = commandFrame.FullFunctionState |}
                            )
                            cache.UpdateLovenseCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "NoChange")
                            do! Task.Delay(runtimeConfig.LovensePollMs, ct)

                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        cache.UpdateLovenseFailure ex.Message
                        cache.UpdateLovenseCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Error")
                        logger.Error(
                            "runtime.lovense_job.error",
                            "Lovense rule job hit an unexpected error.",
                            {| error = ex.Message; errorType = ex.GetType().FullName |}
                        )
                        do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
            } :> Task
