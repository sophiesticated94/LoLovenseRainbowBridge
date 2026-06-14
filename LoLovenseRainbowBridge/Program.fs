namespace LoLovenseRainbowBridge

open System
open System.Threading
open System.Threading.Tasks
open System.Globalization
open Microsoft.Extensions.DependencyInjection
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.App.Jobs
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.Recording

module Program =

    let private hasFlag flag (args: string array) =
        args |> Array.exists (fun arg -> String.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))

    let private valueAfter flag (args: string array) =
        args
        |> Array.tryFindIndex (fun arg -> String.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun index ->
            if index + 1 < args.Length then Some args[index + 1] else None)

    let private speedArg args =
        valueAfter "--speed" args
        |> Option.bind (fun value ->
            match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, parsed -> Some parsed
            | false, _ -> None)
        |> Option.defaultValue 1.0

    let private configSummary config =
        {|
            league = config.League
            lovense =
                {|
                    developer =
                        {| 
                            tokenConfigured = config.Lovense.Developer.Token.IsSome
                            userIdConfigured = config.Lovense.Developer.UserId.IsSome
                            userNameConfigured = config.Lovense.Developer.UserName.IsSome
                            userTokenConfigured = config.Lovense.Developer.UserToken.IsSome
                        |}
                    transportMode = config.Lovense.TransportMode
                    standardApi =
                        {|
                            enable = config.Lovense.StandardApi.Enable
                            callbackListenUrl = config.Lovense.StandardApi.CallbackListenUrl
                            publicCallbackUrl = config.Lovense.StandardApi.PublicCallbackUrl
                            generateQrOnStartup = config.Lovense.StandardApi.GenerateQrOnStartup
                            useServerCommandFallback = config.Lovense.StandardApi.UseServerCommandFallback
                            pairingQrExpiresHours = config.Lovense.StandardApi.PairingQrExpiresHours
                        |}
                    localApi = config.Lovense.LocalApi
                    toyId = config.Lovense.ToyId |> Option.map (fun _ -> "<configured>")
                    platform = config.Lovense.Platform
                    commandTimeSec = config.Lovense.CommandTimeSec
                    dryRun = config.Lovense.DryRun
                    connectTimeoutMs = config.Lovense.ConnectTimeoutMs
                    commandAckTimeoutMs = config.Lovense.CommandAckTimeoutMs
                    mapping = config.Lovense.Mapping
                |}
            runtime = config.Runtime
            scoring = config.Scoring
            logging =
                {|
                    baseDirectory = config.Logging.BaseDirectory
                    sessionDirectoryFormat = config.Logging.SessionDirectoryFormat
                    trackLogLevel = config.Logging.TrackLogLevel
                    logRawLeague = config.Logging.LogRawLeague
                    logRawLovense = config.Logging.LogRawLovense
                    rawLogPrettyPrint = config.Logging.RawLogPrettyPrint
                |}
            recording = config.Recording
            positionBasedRotation = config.PositionBasedRotation
        |}

    [<EntryPoint>]
    let main args =
        let baseConfig = Loader.load ()
        let config =
            if hasFlag "--dry-run" args then
                { baseConfig with Lovense = { baseConfig.Lovense with DryRun = true } }
            else
                baseConfig

        use cts = new CancellationTokenSource()
        use logger = new StructuredSessionLogger(config.Logging)
        let recorder =
            if config.Recording.Enabled then Some(new GameplayRecorder(config.Recording, logger)) else None

        recorder |> Option.iter (fun recorder -> recorder.EnsureSchema())

        logger.Info(
            "app.start",
            "LoLovenseRainbowBridge started.",
            {|
                config = configSummary config
                sessionDirectory = logger.SessionDirectory
                trackPath = logger.TrackPath
                recordingDatabasePath = recorder |> Option.map (fun recorder -> recorder.DatabasePath)
            |}
        )

        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            cts.Cancel()
        )

        match valueAfter "--replay" args, hasFlag "--list-recordings" args with
        | _, true ->
            match recorder with
            | None ->
                printfn "Recording is disabled."
                0
            | Some recorder ->
                let games = recorder.ListGames()
                printfn "Recorded games in %s:" recorder.DatabasePath

                for game in games do
                    printfn
                        "%s | started=%s | ended=%s | app=%s"
                        game.GameId
                        (game.StartedAt.ToString("O"))
                        (game.EndedAt |> Option.map (fun value -> value.ToString("O")) |> Option.defaultValue "<open>")
                        game.AppVersion

                0

        | Some gameId, _ ->
            use lovenseClient = new LovenseClient(config.Lovense, config.Scoring, logger)
            lovenseClient.PrepareStandardApiAsync(cts.Token) |> fun task -> task.GetAwaiter().GetResult() |> ignore

            match recorder with
            | None ->
                printfn "Recording is disabled, so replay cannot read gameplay.sqlite."
                1
            | Some recorder ->
                printfn "LoLovense replay started."
                printfn "Game ID: %s" gameId
                printfn "Dry run: %b" config.Lovense.DryRun
                printfn "Speed: %.2fx" (speedArg args)
                printfn "Track log: %s" logger.TrackPath

                try
                    Replay.play config.Lovense recorder lovenseClient logger gameId (speedArg args) cts.Token
                    |> fun task -> task.GetAwaiter().GetResult()
                    0
                finally
                    match lovenseClient.DisconnectAsync(CancellationToken.None) |> fun task -> task.GetAwaiter().GetResult() with
                    | Ok _ -> logger.Info("app.lovense.disconnect", "Lovense client disconnected after replay.")
                    | Error error -> logger.Warn("app.lovense.disconnect_failed", "Lovense replay disconnect returned an error.", {| error = string error |})

        | None, false ->
            use leagueClient = new LeagueLiveClient(config.League.BaseUrl, logger)
            use lovenseClient = new LovenseClient(config.Lovense, config.Scoring, logger)
            lovenseClient.PrepareStandardApiAsync(cts.Token) |> fun task -> task.GetAwaiter().GetResult() |> ignore
            let runtimeCache = RuntimeState.RuntimeStateCache()
            use serviceProvider =
                ServiceCollection()
                    .AddSingleton<IRuleExpressionEvaluator, RuleExpressionEvaluator>()
                    .AddSingleton<IRuleInputBuilder>(fun _ -> RuleInputBuilder(config.Scoring) :> IRuleInputBuilder)
                    .AddSingleton<ILovenseRuleInterpreter, LovenseRuleInterpreter>()
                    .AddSingleton<ILovenseCommandValueBuilder>(fun services ->
                        LovenseCommandValueBuilder(config.Lovense, services.GetRequiredService<ILovenseRuleInterpreter>()) :> ILovenseCommandValueBuilder)
                    .BuildServiceProvider()

            let commandBuilder = serviceProvider.GetRequiredService<ILovenseCommandValueBuilder>()
            let jobs : IAppJob list =
                [
                    LeagueCacheJob(config.Runtime, config.Scoring, leagueClient, runtimeCache, logger) :> IAppJob
                    OcrCacheJob(config.Runtime, config.PositionBasedRotation, runtimeCache, logger) :> IAppJob
                    LovenseRuleJob(config.Runtime, config.Scoring, config.Lovense, lovenseClient, commandBuilder, runtimeCache, logger, recorder, (configSummary config)) :> IAppJob
                ]

            printfn "LoL → Lovense job runtime started."
            printfn "LoL target: %s/liveclientdata/allgamedata" config.League.BaseUrl
            printfn "Lovense Socket API target: %s" lovenseClient.CommandUrl
            printfn "Jobs: %s" (jobs |> List.map (fun job -> job.Name) |> String.concat ", ")
            printfn "Dry run: %b" config.Lovense.DryRun
            printfn "Log directory: %s" logger.SessionDirectory
            printfn "Track log: %s" logger.TrackPath
            recorder |> Option.iter (fun recorder -> printfn "SQLite recording: %s" recorder.DatabasePath)
            if logger.IsRawLeagueEnabled then
                printfn "LoL raw log: %s" logger.LeaguePath
            if logger.IsRawLovenseEnabled then
                printfn "Lovense raw log: %s" logger.LovensePath
            printfn "Press Ctrl+C to stop."

            try
                jobs
                |> List.map (fun job -> job.RunAsync cts.Token)
                |> Task.WhenAll
                |> fun task -> task.GetAwaiter().GetResult()

                0
            finally
                recorder |> Option.iter (fun recorder -> recorder.CloseActiveGame(DateTimeOffset.UtcNow))

                try
                    logger.Info("app.stop_command.start", "Sending final Lovense stop command.")
                    let result =
                        lovenseClient.SendVibrateAsync(0, CancellationToken.None)
                        |> fun task -> task.GetAwaiter().GetResult()

                    match result with
                    | Ok _ ->
                        logger.Info("app.stop_command.sent", "Final Lovense stop command sent.")
                    | Error error ->
                        logger.Error(
                            "app.stop_command.error",
                            "Final Lovense stop command failed.",
                            {| error = "Lovense command returned Error result."; detail = string error |}
                        )

                    match lovenseClient.DisconnectAsync(CancellationToken.None) |> fun task -> task.GetAwaiter().GetResult() with
                    | Ok _ ->
                        logger.Info("app.lovense.disconnect", "Lovense client disconnected.")
                    | Error error ->
                        logger.Warn(
                            "app.lovense.disconnect_failed",
                            "Lovense client disconnect returned an error.",
                            {| error = string error |}
                        )
                with ex ->
                    logger.Error(
                        "app.stop_command.unexpected_error",
                        "Final Lovense stop command failed unexpectedly.",
                        {|
                            error = ex.Message
                            errorType = ex.GetType().FullName
                        |}
                    )
