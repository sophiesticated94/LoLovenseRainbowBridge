namespace LoLovenseRainbowBridge

open System
open System.Threading
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.LeagueOfLegends
open LoLovenseRainbowBridge.Lovense

module Program =

    [<EntryPoint>]
    let main _ =
        let config = Configuration.load ()

        use cts = new CancellationTokenSource()
        use logger = new StructuredSessionLogger(config.Logging)
        use leagueClient = new LeagueLiveClient(config.League.BaseUrl, logger)
        use lovenseClient = new LovenseClient(config.Lovense, config.Scoring, logger)

        logger.Info(
            "app.start",
            "LoLovenseRainbowBridge started.",
            {|
                league = config.League
                lovense =
                    {|
                        authToken = config.Lovense.AuthToken |> Option.map (fun _ -> "<configured>")
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
                        sessionDirectory = logger.SessionDirectory
                        trackPath = logger.TrackPath
                    |}
            |}
        )

        Console.CancelKeyPress.Add(fun args ->
            args.Cancel <- true
            cts.Cancel()
        )

        printfn "LoL → Lovense intensity generator started."
        printfn "LoL target: %s/liveclientdata/allgamedata" config.League.BaseUrl
        printfn "Lovense Socket API target: %s" lovenseClient.CommandUrl
        printfn "Dry run: %b" config.Lovense.DryRun
        printfn "Log directory: %s" logger.SessionDirectory
        printfn "Track log: %s" logger.TrackPath
        if logger.IsRawLeagueEnabled then
            printfn "LoL raw log: %s" logger.LeaguePath
        if logger.IsRawLovenseEnabled then
            printfn "Lovense raw log: %s" logger.LovensePath
        printfn "Press Ctrl+C to stop."

        try
            Runtime.loop config.Runtime config.Scoring config.Lovense config.PositionBasedRotation leagueClient lovenseClient logger initialState Runtime.initialFailureState Runtime.initialPositionRotationState cts.Token
            |> fun task -> task.GetAwaiter().GetResult()

            0
        finally
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
