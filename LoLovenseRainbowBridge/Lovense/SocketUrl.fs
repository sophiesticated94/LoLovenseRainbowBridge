namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open LoLovenseRainbowBridge

module SocketUrl =

    let private escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let buildSocketUrlRequestBody platform authToken =
        $"""{{"{Constants.Lovense.PlatformField}":"{escapeJsonString platform}","{Constants.Lovense.AuthTokenField}":"{escapeJsonString authToken}"}}"""

    let buildRedactedSocketUrlRequestBody platform =
        $"""{{"{Constants.Lovense.PlatformField}":"{escapeJsonString platform}","{Constants.Lovense.AuthTokenField}":"{Constants.Lovense.AuthTokenRedacted}"}}"""

    let parseSocketUrlResponse (body: string) =
        try
            let root = JsonNode.Parse(body)

            if isNull root then
                Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, None, "Lovense returned empty socket URL response."))
            else
                let code = Json.tryInt Constants.Lovense.CodeField root
                let message = Json.tryString Constants.Lovense.MessageField root |> Option.defaultValue ""

                match code with
                | Some Constants.Lovense.SuccessCode ->
                    let data = Json.tryGet Constants.Lovense.DataField root

                    match
                        data |> Option.bind (Json.tryString Constants.Lovense.SocketIoUrlField),
                        data |> Option.bind (Json.tryString Constants.Lovense.SocketIoPathField)
                    with
                    | Some socketIoUrl, Some socketIoPath
                        when not (String.IsNullOrWhiteSpace socketIoUrl)
                             && not (String.IsNullOrWhiteSpace socketIoPath) ->
                        Ok
                            {
                                SocketIoUrl = socketIoUrl
                                SocketIoPath = socketIoPath
                            }

                    | _ ->
                        Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, code, "Socket URL response did not include socketIoUrl/socketIoPath."))

                | _ ->
                    Error(SocketUrlRejected(Constants.Lovense.GetSocketUrl, code, message))
        with ex ->
            Error(SocketUrlRequestFailed(Constants.Lovense.GetSocketUrl, $"Could not parse socket URL response: {ex.Message}"))

    let requestSocketUrlAsync (http: HttpClient) (logger: StructuredSessionLogger) platform authToken (ct: CancellationToken) =
        task {
            let correlationId = Transport.newCorrelationId()

            logger.Info(
                "lovense.socket_url.request",
                "Requesting Lovense Socket.IO URL.",
                {|
                    correlationId = correlationId
                    url = Constants.Lovense.GetSocketUrl
                    platform = platform
                    authToken = Constants.Lovense.AuthTokenRedacted
                    rawLogged = logger.IsRawLovenseEnabled
                |}
            )

            let! transportResult =
                Transport.postJsonAsync
                    http
                    logger
                    correlationId
                    Constants.Lovense.GetSocketUrl
                    (buildRedactedSocketUrlRequestBody platform)
                    (buildSocketUrlRequestBody platform authToken)
                    ct

            match transportResult with
            | Error error ->
                return Error error
            | Ok response ->
                match parseSocketUrlResponse response.Body with
                | Ok info ->
                    logger.Info(
                            "lovense.socket_url.success",
                            "Received Lovense Socket.IO connection details.",
                            {|
                                correlationId = response.CorrelationId
                                socketIoUrl = Shared.redactUrlSecrets info.SocketIoUrl
                                socketIoPath = info.SocketIoPath
                            |}
                        )

                    return Ok info

                | Error error ->
                    logger.Warn(
                        "lovense.socket_url.rejected",
                        "Lovense rejected or returned unusable Socket.IO connection details.",
                        {|
                            correlationId = response.CorrelationId
                            error = string error
                        |}
                    )

                    return Error error
        }
