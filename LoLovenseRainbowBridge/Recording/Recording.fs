namespace LoLovenseRainbowBridge.Recording

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Linq
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.EntityFrameworkCore
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.Recording.Data

type LovenseSendStatus =
    {
        Attempted: bool
        Success: bool option
        Error: string option
    }

type RecordedGame =
    {
        GameId: string
        StartedAt: DateTimeOffset
        EndedAt: DateTimeOffset option
        AppVersion: string
    }

type RecordedLovenseRow =
    {
        Id: int64
        GameId: string
        DateTime: DateTimeOffset
        OffsetMs: int64
        DurationMs: int
        ContextDiffJson: string
    }

type GameplayRecorder(config: RecordingConfig, logger: StructuredSessionLogger) =

    let serializerOptions = JsonSerializerOptions(WriteIndented = false)
    let databasePath = Path.GetFullPath config.DatabasePath
    let mutable activeGameId: string option = None
    let mutable activeStartedAt: DateTimeOffset option = None
    let mutable previousState: Map<string, int> option = None
    let mutable previousContextKey: string option = None
    let gate = obj ()

    let ensureDirectory () =
        let directory = Path.GetDirectoryName databasePath

        if not (String.IsNullOrWhiteSpace directory) then
            Directory.CreateDirectory(directory) |> ignore

    let dbOptions () =
        ensureDirectory ()
        DbContextOptionsBuilder<GameplayRecordingDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options

    let openDb () =
        new GameplayRecordingDbContext(dbOptions ())

    let ensureSchema () =
        use db = openDb ()
        db.Database.Migrate()

    let insertGame (gameId: string) (startedAt: DateTimeOffset) (configSummaryJson: string) =
        use db = openDb ()
        db.Games.Add(
            GameEntity(
                GameId = gameId,
                StartedAt = startedAt.ToString("O"),
                EndedAt = null,
                AppVersion = typeof<GameplayRecorder>.Assembly.GetName().Version.ToString(),
                ConfigSummaryJson = configSummaryJson
            )
        )
        |> ignore

        db.SaveChanges() |> ignore

    let closeGame (gameId: string) (endedAt: DateTimeOffset) =
        use db = openDb ()
        let game = db.Games.SingleOrDefault(fun game -> game.GameId = gameId && game.EndedAt = null)

        if not (isNull game) then
            game.EndedAt <- endedAt.ToString("O")
            db.SaveChanges() |> ignore

    let insertRecord (gameId: string) (timestamp: DateTimeOffset) (offsetMs: int64) (durationMs: int) (contextDiffJson: string) =
        use db = openDb ()
        db.LovenseRecords.Add(
            LovenseRecordEntity(
                GameId = gameId,
                DateTime = timestamp.ToString("O"),
                OffsetMs = offsetMs,
                DurationMs = durationMs,
                ContextDiffJson = contextDiffJson
            )
        )
        |> ignore

        db.SaveChanges() |> ignore

    let jsonObject (pairs: (string * JsonNode) list) =
        let obj = JsonObject()

        for name, node in pairs do
            obj[name] <- node

        obj

    let jsonArrayString (values: string list) =
        let arr = JsonArray()

        for value in values do
            arr.Add(JsonValue.Create(value))

        arr

    let jsonArrayInt (values: int list) =
        let arr = JsonArray()

        for value in values do
            arr.Add(JsonValue.Create(value))

        arr

    let functionsJson (changes: (string * int) list) =
        let obj = JsonObject()

        for name, value in changes do
            obj[name] <- JsonValue.Create(value)

        obj

    let eventIds (snapshot: BridgeSnapshot) (breakdown: IntensityBreakdown) =
        let effectIds =
            breakdown.TemporaryEffects
            |> List.collect (fun effect -> effect.SourceEventIds)

        let recentIds =
            snapshot.Events
            |> List.filter (fun ev -> snapshot.GameTime - ev.GameTime <= 12.0)
            |> List.map (fun ev -> ev.EventId)

        (effectIds @ recentIds)
        |> List.distinct
        |> List.sort

    let contextJson (changes: (string * int) list) (actionString: string) (reasons: string list) (snapshot: BridgeSnapshot) (breakdown: IntensityBreakdown) (sendStatus: LovenseSendStatus) =
        let context =
            jsonObject
                [
                    "functions", functionsJson changes :> JsonNode
                    "action", JsonValue.Create actionString :> JsonNode
                    "reasons", jsonArrayString reasons :> JsonNode
                    "gameTime", JsonValue.Create snapshot.GameTime :> JsonNode
                    "eventIds", jsonArrayInt (eventIds snapshot breakdown) :> JsonNode
                    "intensity",
                        jsonObject
                            [
                                "performanceScore", JsonValue.Create breakdown.PerformanceScore :> JsonNode
                                "normalizedScore", JsonValue.Create breakdown.NormalizedScore :> JsonNode
                                "rawBaseValue", JsonValue.Create breakdown.RawBaseValue :> JsonNode
                                "liveHealthPercent", JsonSerializer.SerializeToNode(breakdown.LiveHealthPercent, serializerOptions)
                                "liveHealthMultiplier", JsonValue.Create breakdown.LiveHealthMultiplier :> JsonNode
                                "healthPressureMultiplier", JsonValue.Create breakdown.HealthPressureMultiplier :> JsonNode
                                "healthAdjustedBaseValue", JsonValue.Create breakdown.HealthAdjustedBaseValue :> JsonNode
                                "baseIntensity", JsonValue.Create breakdown.BaseIntensity :> JsonNode
                                "temporaryBoost", JsonValue.Create breakdown.TemporaryBoost :> JsonNode
                                "rawFinalValue", JsonValue.Create breakdown.RawFinalValue :> JsonNode
                                "finalIntensity", JsonValue.Create breakdown.Intensity :> JsonNode
                            ]
                        :> JsonNode
                    "send",
                        jsonObject
                            [
                                "attempted", JsonValue.Create sendStatus.Attempted :> JsonNode
                                "success", JsonSerializer.SerializeToNode(sendStatus.Success, serializerOptions)
                                "error", JsonSerializer.SerializeToNode(sendStatus.Error, serializerOptions)
                            ]
                        :> JsonNode
                ]

        if config.RecordRawContext then
            context["snapshot"] <-
                JsonSerializer.SerializeToNode(
                    {|
                        activePlayer =
                            {|
                                id = snapshot.ActivePlayer.Id
                                aliases = snapshot.ActivePlayer.Aliases
                                kills = snapshot.ActivePlayer.Kills
                                deaths = snapshot.ActivePlayer.Deaths
                                assists = snapshot.ActivePlayer.Assists
                                creepScore = snapshot.ActivePlayer.CreepScore
                                wardScore = snapshot.ActivePlayer.WardScore
                                level = snapshot.ActivePlayer.Level
                                currentHealth = snapshot.ActivePlayer.CurrentHealth
                                maxHealth = snapshot.ActivePlayer.MaxHealth
                            |}
                        players = snapshot.Players |> List.map (fun player -> {| id = player.Id; aliases = player.Aliases; kills = player.Kills; deaths = player.Deaths; assists = player.Assists |})
                        events = snapshot.Events |> List.map (fun ev -> {| eventId = ev.EventId; gameTime = ev.GameTime; actorName = ev.ActorName; victimName = ev.VictimName; assisters = ev.Assisters; kind = string ev.Kind |})
                    |},
                    serializerOptions
                )

        context

    member _.DatabasePath = databasePath

    member _.EnsureSchema() =
        if config.Enabled then
            ensureSchema ()

    member this.StartGame(now: DateTimeOffset, configSummary: obj) =
        if config.Enabled then
            lock gate (fun () ->
                match activeGameId with
                | Some gameId -> gameId
                | None ->
                    ensureSchema ()
                    let gameId = Guid.NewGuid().ToString("N")
                    let configSummaryJson = JsonSerializer.Serialize(configSummary, serializerOptions)
                    insertGame gameId now configSummaryJson
                    activeGameId <- Some gameId
                    activeStartedAt <- Some now
                    previousState <- None
                    previousContextKey <- None

                    logger.Info(
                        "recording.game.started",
                        "Started SQLite gameplay recording.",
                        {| gameId = gameId; databasePath = databasePath; startedAt = now |}
                    )

                    gameId)
        else
            ""

    member _.CloseActiveGame(now: DateTimeOffset) =
        if config.Enabled then
            lock gate (fun () ->
                match activeGameId with
                | None -> ()
                | Some gameId ->
                    try
                        closeGame gameId now
                        logger.Info("recording.game.closed", "Closed SQLite gameplay recording.", {| gameId = gameId; endedAt = now |})
                    with ex ->
                        logger.Warn("recording.game.close_failed", "Could not close SQLite gameplay recording.", {| gameId = gameId; error = ex.Message |})

                    activeGameId <- None
                    activeStartedAt <- None
                    previousState <- None
                    previousContextKey <- None)

    member this.RecordPlan(now: DateTimeOffset, configSummary: obj, snapshot: BridgeSnapshot, breakdown: IntensityBreakdown, plan: LovenseCommandPlan, actionString: string, sendStatus: LovenseSendStatus) =
        if config.Enabled then
            lock gate (fun () ->
                try
                    let gameId = this.StartGame(now, configSummary)
                    let startedAt = activeStartedAt |> Option.defaultValue now
                    let offsetMs = int64 ((now - startedAt).TotalMilliseconds)
                    let sliceOffsetMs = (offsetMs / int64 config.SliceMs) * int64 config.SliceMs
                    let currentState = LovenseActionCodec.stateFromActions plan.Actions
                    let previous = previousState |> Option.defaultValue LovenseActionCodec.emptyState
                    let changes =
                        match previousState with
                        | None -> LovenseActionCodec.canonicalFunctions |> List.map (fun name -> name, currentState |> Map.tryFind name |> Option.defaultValue 0)
                        | Some _ -> LovenseActionCodec.diff previous currentState

                    let reasons = plan.Reasons |> List.map LovenseActionCodec.reasonToString
                    let context = contextJson changes actionString reasons snapshot breakdown sendStatus
                    let contextText = context.ToJsonString(serializerOptions)
                    let contextKey =
                        JsonSerializer.Serialize(
                            {|
                                action = actionString
                                reasons = reasons
                                send = sendStatus
                            |},
                            serializerOptions
                        )

                    if not changes.IsEmpty || previousContextKey <> Some contextKey then
                        insertRecord gameId now sliceOffsetMs config.SliceMs contextText
                        previousState <- Some currentState
                        previousContextKey <- Some contextKey

                        logger.Debug(
                            "recording.lovense.recorded",
                            "Recorded Lovense state diff.",
                            {|
                                gameId = gameId
                                offsetMs = sliceOffsetMs
                                durationMs = config.SliceMs
                                changedFunctions = changes |> List.map fst
                                action = actionString
                            |}
                        )
                with ex ->
                    logger.Warn("recording.lovense.record_failed", "Could not record Lovense state diff.", {| error = ex.Message; errorType = ex.GetType().FullName |}))

    member _.ListGames() =
        ensureSchema ()
        use db = openDb ()

        db.Games.AsNoTracking()
        |> Seq.toList
        |> List.sortByDescending (fun game -> game.StartedAt)
        |> List.map (fun game ->
                {
                    GameId = game.GameId
                    StartedAt = DateTimeOffset.Parse(game.StartedAt, CultureInfo.InvariantCulture)
                    EndedAt =
                        if String.IsNullOrWhiteSpace game.EndedAt then None
                        else Some(DateTimeOffset.Parse(game.EndedAt, CultureInfo.InvariantCulture))
                    AppVersion = game.AppVersion
                })

    member _.ReadRecords(gameId: string) =
        ensureSchema ()
        use db = openDb ()

        db.LovenseRecords.AsNoTracking()
        |> Seq.filter (fun record -> record.GameId = gameId)
        |> Seq.sortBy (fun record -> record.OffsetMs, record.Id)
        |> Seq.toList
        |> List.map (fun record ->
                {
                    Id = record.Id
                    GameId = record.GameId
                    DateTime = DateTimeOffset.Parse(record.DateTime, CultureInfo.InvariantCulture)
                    OffsetMs = record.OffsetMs
                    DurationMs = record.DurationMs
                    ContextDiffJson = record.ContextDiffJson
                })

module Replay =

    let private actionFromContext (contextText: string) =
        let node = JsonNode.Parse(contextText)

        match node with
        | null -> Constants.Lovense.StopAction
        | node ->
            node["action"]
            |> Option.ofObj
            |> Option.map (fun value -> value.GetValue<string>())
            |> Option.defaultValue Constants.Lovense.StopAction

    let play
        (config: LovenseConfig)
        (repository: GameplayRecorder)
        (lovenseClient: LovenseClient)
        (logger: StructuredSessionLogger)
        (gameId: string)
        (speed: float)
        (ct: CancellationToken)
        : Task<unit>
        =
        task {
            let safeSpeed = if speed <= 0.0 then 1.0 else speed
            let records = repository.ReadRecords gameId

            logger.Info(
                "replay.start",
                "Started Lovense replay.",
                {| gameId = gameId; recordCount = records.Length; speed = safeSpeed; dryRun = config.DryRun |}
            )

            let mutable previousOffset = 0L

            for record in records do
                let waitMs = max 0L (record.OffsetMs - previousOffset)
                let adjustedWaitMs = int (float waitMs / safeSpeed)

                if adjustedWaitMs > 0 then
                    do! Task.Delay(adjustedWaitMs, ct)

                let actionString = actionFromContext record.ContextDiffJson
                let plan = LovenseActionCodec.planFromActionString config actionString
                let intensity =
                    plan.Actions
                    |> List.tryFind (fun action -> action.Function = Vibrate || action.Function = All)
                    |> Option.map (fun action -> action.Value)
                    |> Option.defaultValue 0

                logger.Info(
                    "replay.command",
                    "Sending replayed Lovense command.",
                    {| gameId = gameId; offsetMs = record.OffsetMs; action = actionString; dryRun = config.DryRun |}
                )

                let! result = lovenseClient.SendCommandPlanAsync(plan, intensity, [], ct)

                match result with
                | Ok _ -> ()
                | Error error ->
                    logger.Warn(
                        "replay.command_failed",
                        "Replay Lovense command failed; continuing timeline.",
                        {| gameId = gameId; offsetMs = record.OffsetMs; error = string error |}
                    )

                previousOffset <- record.OffsetMs

            logger.Info("replay.finished", "Finished Lovense replay.", {| gameId = gameId |})
        }
