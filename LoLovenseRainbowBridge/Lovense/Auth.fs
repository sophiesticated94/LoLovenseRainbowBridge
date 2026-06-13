namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open LoLovenseRainbowBridge

module Auth =

    let private isMissing value =
        value |> Option.exists (String.IsNullOrWhiteSpace >> not) |> not

    let missingDeveloperCredentialFields (developer: LovenseDeveloperConfig) =
        [
            if isMissing developer.Token then
                "Lovense.Developer.Token"

            if isMissing developer.UserId then
                "Lovense.Developer.UserId"
        ]

    let private addOptional (body: JsonObject) (key: string) value =
        value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.iter (fun value -> body[key] <- JsonValue.Create(value))

    let buildTokenRequestBody (developer: LovenseDeveloperConfig) =
        let body = JsonObject()
        body[Constants.Lovense.DeveloperTokenField] <- JsonValue.Create(developer.Token |> Option.defaultValue "")
        body[Constants.Lovense.UserIdField] <- JsonValue.Create(developer.UserId |> Option.defaultValue "")
        addOptional body Constants.Lovense.UserNameField developer.UserName
        addOptional body Constants.Lovense.UserTokenField developer.UserToken
        body.ToJsonString()

    let buildRedactedTokenRequestBody (developer: LovenseDeveloperConfig) =
        let body = JsonObject()
        body[Constants.Lovense.DeveloperTokenField] <- JsonValue.Create(Constants.Lovense.AuthTokenRedacted)
        body[Constants.Lovense.UserIdField] <- JsonValue.Create(developer.UserId |> Option.defaultValue "")
        addOptional body Constants.Lovense.UserNameField developer.UserName

        developer.UserToken
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.iter (fun _ -> body[Constants.Lovense.UserTokenField] <- JsonValue.Create(Constants.Lovense.AuthTokenRedacted))

        body.ToJsonString()

    let parseTokenResponse (body: string) =
        try
            let root = JsonNode.Parse(body)

            if isNull root then
                Error(SocketUrlRejected(Constants.Lovense.GetToken, None, "Lovense returned empty auth token response."))
            else
                let code = Json.tryInt Constants.Lovense.CodeField root
                let message = Json.tryString Constants.Lovense.MessageField root |> Option.defaultValue ""

                match code with
                | Some Constants.Lovense.SuccessCode ->
                    match root |> Json.tryGet Constants.Lovense.DataField |> Option.bind (Json.tryString Constants.Lovense.AuthTokenField) with
                    | Some authToken when not (String.IsNullOrWhiteSpace authToken) -> Ok authToken
                    | _ -> Error(SocketUrlRejected(Constants.Lovense.GetToken, code, "Token response did not include authToken."))
                | _ ->
                    Error(SocketUrlRejected(Constants.Lovense.GetToken, code, message))
        with ex ->
            Error(SocketUrlRequestFailed(Constants.Lovense.GetToken, $"Could not parse auth token response: {ex.Message}"))

    let requestAuthTokenAsync (http: HttpClient) (logger: StructuredSessionLogger) (developer: LovenseDeveloperConfig) (ct: CancellationToken) =
        task {
            match missingDeveloperCredentialFields developer with
            | missingFields when not missingFields.IsEmpty ->
                logger.Warn(
                    "lovense.auth_token.missing_credentials",
                    "Lovense developer credentials are not configured.",
                    {| missingFields = missingFields |}
                )

                return Error(MissingDeveloperCredentials missingFields)

            | _ ->
                let correlationId = Transport.newCorrelationId()

                logger.Info(
                    "lovense.auth_token.request",
                    "Requesting Lovense user auth token from local developer settings.",
                    {|
                        correlationId = correlationId
                        url = Constants.Lovense.GetToken
                        userIdConfigured = developer.UserId.IsSome
                        userNameConfigured = developer.UserName.IsSome
                        userTokenConfigured = developer.UserToken.IsSome
                        developerToken = Constants.Lovense.AuthTokenRedacted
                        rawLogged = logger.IsRawLovenseEnabled
                    |}
                )

                let! transportResult =
                    Transport.postJsonAsync
                        http
                        logger
                        correlationId
                        Constants.Lovense.GetToken
                        (buildRedactedTokenRequestBody developer)
                        (buildTokenRequestBody developer)
                        ct

                match transportResult with
                | Error error ->
                    return Error error
                | Ok response ->
                    match parseTokenResponse response.Body with
                    | Ok authToken ->
                        logger.Info(
                            "lovense.auth_token.success",
                            "Received Lovense user auth token.",
                            {| correlationId = response.CorrelationId; authToken = Constants.Lovense.AuthTokenRedacted |}
                        )

                        return Ok authToken
                    | Error error ->
                        logger.Warn(
                            "lovense.auth_token.rejected",
                            "Lovense rejected or returned unusable auth token response.",
                            {| correlationId = response.CorrelationId; error = string error |}
                        )

                        return Error error
        }
