namespace LoLovenseRainbowBridge.LeagueOfLegends

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open LoLovenseRainbowBridge

type LeagueAllGameData =
    {
        RawText: string
        Root: JsonNode
    }

type LeagueFetchError =
    | ConnectionFailed of url: string * message: string
    | HttpFailure of url: string * statusCode: int * body: string
    | InvalidJson of url: string * message: string * rawText: string
    | EmptyJson of url: string * rawText: string
    | UnexpectedFetchError of url: string * message: string * errorType: string

type LeagueLiveClient(baseUrl: string, logger: StructuredSessionLogger) =

    let http = Shared.insecureHttpClient ()

    member _.FetchAllGameDataAsync(ct: CancellationToken) =
        task {
            let url = $"{baseUrl}{Constants.League.AllGameDataPath}"

            logger.Info(
                "league.fetch.start",
                "Fetching League Live Client all game data.",
                {| url = url |}
            )

            try
                let! response = http.GetAsync(url, ct)
                let! text = response.Content.ReadAsStringAsync(ct)

                logger.Info(
                    "league.fetch.response",
                    "Received League Live Client response.",
                    {|
                        url = url
                        statusCode = int response.StatusCode
                        isSuccessStatusCode = response.IsSuccessStatusCode
                        rawLength = text.Length
                        rawLogged = logger.IsRawLeagueEnabled
                    |}
                )

                logger.RawLeagueResponse(url, int response.StatusCode, response.IsSuccessStatusCode, text)

                if not response.IsSuccessStatusCode then
                    return Error(HttpFailure(url, int response.StatusCode, text))
                else
                    let parsed =
                        try
                            Ok(JsonNode.Parse(text))
                        with ex ->
                            Error(InvalidJson(url, ex.Message, text))

                    match parsed with
                    | Error error ->
                        return Error error

                    | Ok parsed when isNull parsed ->
                        logger.Warn(
                            "league.fetch.empty",
                            "League Live Client returned null JSON.",
                            {| url = url; rawText = text |}
                        )

                        return Error(EmptyJson(url, text))

                    | Ok parsed ->
                        return Ok { RawText = text; Root = parsed }
            with
            | :? OperationCanceledException ->
                return raise (OperationCanceledException())
            | :? HttpRequestException as ex ->
                logger.Error(
                    "league.fetch.connection_failed",
                    "Could not connect to League Live Client.",
                    {|
                        url = url
                        error = ex.Message
                    |}
                )

                return Error(ConnectionFailed(url, ex.Message))
            | ex ->
                logger.Error(
                    "league.fetch.unexpected_error",
                    "Unexpected League Live Client error.",
                    {|
                        url = url
                        error = ex.Message
                        errorType = ex.GetType().FullName
                    |}
                )

                return Error(UnexpectedFetchError(url, ex.Message, ex.GetType().FullName))
        }

    interface IDisposable with
        member _.Dispose() =
            http.Dispose()
