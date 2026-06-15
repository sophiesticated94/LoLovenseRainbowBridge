namespace LoLovenseRainbowBridge.App.Jobs

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends

type LeagueCacheJob
    (
        runtimeConfig: RuntimeConfig,
        scoringConfig: ScoringConfig,
        leagueClient: LeagueLiveClient,
        cache: RuntimeState.RuntimeStateCache,
        logger: StructuredSessionLogger
    ) =

    let mutable generatorState = initialState

    interface IAppJob with
        member _.Name = "LeagueCacheJob"

        member _.RunAsync(ct: CancellationToken) =
            task {
                while not ct.IsCancellationRequested do
                    let startedAt = DateTimeOffset.UtcNow
                    let iteration = cache.UpdateLeagueCycleStarted()

                    try
                        let! fetchResult = leagueClient.FetchAllGameDataAsync ct

                        match fetchResult with
                        | Error error ->
                            cache.UpdateLeagueFailure(RuntimeState.leagueErrorMessage error)
                            let current = cache.Read().League
                            logger.Warn(
                                "runtime.league_job.failure",
                                "League cache job could not fetch League data.",
                                {| error = RuntimeState.leagueErrorSummary error; attemptSinceLastSuccess = current.FailureAttemptsSinceSuccess |}
                            )
                            cache.UpdateLeagueCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Retrying")
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
                                cache.UpdateLeagueCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "ParseError")
                                do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)

                            | Ok parsed ->
                                let bridgeSnapshot = Mapper.toBridgeSnapshot scoringConfig parsed.Snapshot
                                let leagueRules =
                                    LeagueRuleVariableCalculator.calculate scoringConfig bridgeSnapshot generatorState

                                generatorState <- evolve scoringConfig bridgeSnapshot generatorState
                                cache.UpdateLeagueSuccess(bridgeSnapshot, leagueRules)
                                cache.UpdateLeagueCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Success")
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
                        cache.UpdateLeagueCycleCompleted(iteration, int64 (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds, "Error")
                        logger.Error(
                            "runtime.league_job.error",
                            "League cache job hit an unexpected error.",
                            {| error = ex.Message; errorType = ex.GetType().FullName |}
                        )
                        do! Task.Delay(runtimeConfig.UnavailableRetryMs, ct)
            } :> Task
