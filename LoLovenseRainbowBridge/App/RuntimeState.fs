namespace LoLovenseRainbowBridge.App

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.LeagueOfLegends

module RuntimeState =

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
            Position: Lovense.LovensePlanningPosition option
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

        member _.UpdateOcrDisabled() =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        DataAcquired = false
                                        UnavailableSince = snapshot.Ocr.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some "Position-based rotation is disabled."
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

    let planningQuadrant normalizedX normalizedY =
        if normalizedX >= 0.42 && normalizedX <= 0.58 && normalizedY >= 0.42 && normalizedY <= 0.58 then "Center"
        elif normalizedX < 0.5 && normalizedY < 0.5 then "TopLeft"
        elif normalizedX >= 0.5 && normalizedY < 0.5 then "TopRight"
        elif normalizedX < 0.5 && normalizedY >= 0.5 then "BottomLeft"
        elif normalizedX >= 0.5 && normalizedY >= 0.5 then "BottomRight"
        elif normalizedX < 0.5 then "Left"
        else "Right"

    let leagueErrorSummary error : obj =
        match error with
        | LeagueFetchError.ConnectionFailed(url, message) -> box {| kind = "ConnectionFailed"; url = url; message = message |}
        | LeagueFetchError.HttpFailure(url, statusCode, body) -> box {| kind = "HttpFailure"; url = url; statusCode = statusCode; bodyLength = body.Length |}
        | LeagueFetchError.InvalidJson(url, message, rawText) -> box {| kind = "InvalidJson"; url = url; message = message; rawLength = rawText.Length |}
        | LeagueFetchError.EmptyJson(url, rawText) -> box {| kind = "EmptyJson"; url = url; rawLength = rawText.Length |}
        | LeagueFetchError.UnexpectedFetchError(url, message, errorType) -> box {| kind = "UnexpectedFetchError"; url = url; message = message; errorType = errorType |}

    let leagueErrorMessage error =
        match error with
        | LeagueFetchError.ConnectionFailed(_, message) -> message
        | LeagueFetchError.HttpFailure(_, statusCode, _) -> $"HTTP {statusCode}"
        | LeagueFetchError.InvalidJson(_, message, _) -> $"Invalid JSON: {message}"
        | LeagueFetchError.EmptyJson _ -> "Empty JSON"
        | LeagueFetchError.UnexpectedFetchError(_, message, _) -> message

    let lovenseErrorMessage error =
        match error with
        | LovenseCommandError.NotConnected connectionError -> $"Not connected: {connectionError}"
        | LovenseCommandError.CommandEmitFailed(_, message) -> message
        | LovenseCommandError.CommandRejected(_, message) -> message
        | LovenseCommandError.CommandTimeout(_, timeoutMs) -> $"Command timed out after {timeoutMs}ms"
        | LovenseCommandError.UnexpectedCommandError(_, message, _) -> message

    let lovenseErrorSummary error : obj =
        match error with
        | LovenseCommandError.NotConnected connectionError -> box {| kind = "NotConnected"; connectionError = string connectionError |}
        | LovenseCommandError.CommandEmitFailed(eventName, message) -> box {| kind = "CommandEmitFailed"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandRejected(eventName, message) -> box {| kind = "CommandRejected"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandTimeout(eventName, timeoutMs) -> box {| kind = "CommandTimeout"; eventName = eventName; timeoutMs = timeoutMs |}
        | LovenseCommandError.UnexpectedCommandError(eventName, message, errorType) -> box {| kind = "UnexpectedCommandError"; eventName = eventName; message = message; errorType = errorType |}

    let neutralPlayer : BridgePlayer =
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

    let neutralSnapshot : BridgeSnapshot =
        {
            GameTime = 0.0
            ActiveAliases = []
            ActivePlayer = neutralPlayer
            Players = [ neutralPlayer ]
            Events = []
        }

    let elapsedMs (since: DateTimeOffset option) (now: DateTimeOffset) =
        since
        |> Option.map (fun value -> max 0L (int64 (now - value).TotalMilliseconds))
        |> Option.defaultValue 0L

    let runtimeRuleContext (snapshot: RuntimeCacheSnapshot) now : Lovense.LovenseRuntimeRuleContext =
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
