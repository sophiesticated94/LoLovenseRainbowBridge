namespace LoLovenseRainbowBridge.Lovense

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge

type StandardApiQrCodeInfo =
    {
        Qr: string
        Code: string
        ExpiresAt: DateTimeOffset
    }

type StandardApiCallbackServer =
    {
        Stop: unit -> unit
        ListeningUrl: string
    }

module StandardApi =

    let private missingDeveloperFields (developer: LovenseDeveloperConfig) =
        [
            if developer.Token |> Option.forall String.IsNullOrWhiteSpace then
                "Lovense.Developer.Token"
            if developer.UserId |> Option.forall String.IsNullOrWhiteSpace then
                "Lovense.Developer.UserId"
        ]

    let private optionalStringField name value =
        value
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (fun text ->
            let escaped = LovenseFormatting.escapeJsonString text
            sprintf ",\"%s\":\"%s\"" name escaped)
        |> Option.defaultValue ""

    let buildQrCodeRequestBody (developer: LovenseDeveloperConfig) =
        match developer.Token, developer.UserId with
        | Some token, Some uid when not (String.IsNullOrWhiteSpace token) && not (String.IsNullOrWhiteSpace uid) ->
            Ok
                $"""{{"token":"{LovenseFormatting.escapeJsonString token}","uid":"{LovenseFormatting.escapeJsonString uid}"{optionalStringField Constants.Lovense.UserNameField developer.UserName}{optionalStringField Constants.Lovense.UserTokenField developer.UserToken},"v":2}}"""
        | _ ->
            Error(MissingDeveloperCredentials(missingDeveloperFields developer))

    let buildRedactedQrCodeRequestBody (developer: LovenseDeveloperConfig) =
        match developer.UserId with
        | Some uid when not (String.IsNullOrWhiteSpace uid) ->
            Ok
                $"""{{"token":"{Constants.Lovense.AuthTokenRedacted}","uid":"{LovenseFormatting.escapeJsonString uid}"{optionalStringField Constants.Lovense.UserNameField developer.UserName}{optionalStringField Constants.Lovense.UserTokenField (developer.UserToken |> Option.map (fun _ -> Constants.Lovense.AuthTokenRedacted))},"v":2}}"""
        | _ ->
            buildQrCodeRequestBody developer |> Result.map (fun _ -> "")

    let parseQrCodeResponse (expiresAt: DateTimeOffset) (body: string) =
        try
            let root = JsonNode.Parse(body)

            if isNull root then
                Error(SocketUrlRejected(Constants.Lovense.GetQrCode, None, "Lovense returned empty QR code response."))
            else
                let code = Json.tryInt Constants.Lovense.CodeField root
                let message = Json.tryString Constants.Lovense.MessageField root |> Option.defaultValue ""

                if code = Some Constants.Lovense.SuccessCode then
                    match Json.tryGet Constants.Lovense.DataField root with
                    | Some data ->
                        match Json.tryString Constants.Lovense.QrField data, Json.tryString Constants.Lovense.CodeField data with
                        | Some qr, Some pairingCode when not (String.IsNullOrWhiteSpace qr) && not (String.IsNullOrWhiteSpace pairingCode) ->
                            Ok { Qr = qr; Code = pairingCode; ExpiresAt = expiresAt }
                        | _ ->
                            Error(SocketUrlRejected(Constants.Lovense.GetQrCode, code, "QR response did not include data.qr/data.code."))
                    | None ->
                        Error(SocketUrlRejected(Constants.Lovense.GetQrCode, code, "QR response did not include data."))
                else
                    Error(SocketUrlRejected(Constants.Lovense.GetQrCode, code, message))
        with ex ->
            Error(SocketUrlRequestFailed(Constants.Lovense.GetQrCode, $"Could not parse QR code response: {ex.Message}"))

    let requestQrCodeAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseStandardApiConfig)
        (developer: LovenseDeveloperConfig)
        (ct: CancellationToken)
        =
        task {
            match buildQrCodeRequestBody developer, buildRedactedQrCodeRequestBody developer with
            | Error error, _ -> return Error error
            | _, Error error -> return Error error
            | Ok body, Ok redactedBody ->
                let correlationId = Transport.newCorrelationId()
                let expiresAt = DateTimeOffset.UtcNow.AddHours(config.PairingQrExpiresHours)

                logger.Info(
                    "lovense.standard.qr.request",
                    "Requesting Lovense Standard API pairing QR/code.",
                    {| correlationId = correlationId; expiresAt = expiresAt |}
                )

                let! response =
                    Transport.postJsonAsync
                        http
                        logger
                        correlationId
                        Constants.Lovense.GetQrCode
                        redactedBody
                        body
                        ct

                match response with
                | Error error ->
                    logger.Warn(
                        "lovense.standard.qr.failure",
                        "Lovense Standard API pairing QR/code request failed.",
                        {| correlationId = correlationId; error = string error |}
                    )
                    return Error error
                | Ok response ->
                    match parseQrCodeResponse expiresAt response.Body with
                    | Error error ->
                        logger.Warn(
                            "lovense.standard.qr.rejected",
                            "Lovense Standard API pairing QR/code response was rejected.",
                            {| correlationId = correlationId; error = string error |}
                        )
                        return Error error
                    | Ok qrInfo ->
                        logger.Info(
                            "lovense.standard.qr.available",
                            "Lovense Standard API pairing QR/code is available.",
                            {| correlationId = correlationId; code = qrInfo.Code; qr = qrInfo.Qr; expiresAt = qrInfo.ExpiresAt |}
                        )
                        return Ok qrInfo
        }

    let validateCallback (developer: LovenseDeveloperConfig) rawBody =
        match developer.UserId, DeviceInfo.callbackUid rawBody with
        | Some expectedUid, Some actualUid when not (String.Equals(expectedUid, actualUid, StringComparison.Ordinal)) ->
            Error $"Unexpected uid '{actualUid}'."
        | Some _, None ->
            Error "Callback did not include uid."
        | _ ->
            match developer.UserToken, DeviceInfo.callbackUserToken rawBody with
            | Some expected, Some actual when not (String.Equals(expected, actual, StringComparison.Ordinal)) ->
                Error "Callback included an unexpected utoken."
            | Some _, None ->
                Error "Callback did not include configured utoken."
            | _ ->
                Ok(DeviceInfo.parseStandardCallback rawBody)

    let normalizeListenPrefix (url: string) =
        let text = if url.EndsWith("/", StringComparison.Ordinal) then url else url + "/"

        match Uri.TryCreate(text, UriKind.Absolute) with
        | true, uri ->
            let host =
                if String.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal) then
                    "+"
                else
                    uri.Host

            $"{uri.Scheme}://{host}:{uri.Port}{uri.AbsolutePath}"
        | _ ->
            invalidArg (nameof url) $"Invalid callback listen URL '{url}'."

    let private localhostFallbackPrefix (prefix: string) =
        match Uri.TryCreate(prefix, UriKind.Absolute) with
        | true, uri when String.Equals(uri.Host, "+", StringComparison.Ordinal) || String.Equals(uri.Host, "0.0.0.0", StringComparison.Ordinal) ->
            Some $"{uri.Scheme}://localhost:{uri.Port}{uri.AbsolutePath}"
        | _ -> None

    let private tryStartListener (logger: StructuredSessionLogger) (config: LovenseStandardApiConfig) (prefix: string) : Result<HttpListener, exn> =
        let listener = new HttpListener()
        listener.Prefixes.Add(prefix)

        try
            listener.Start()
            Ok listener
        with
        | :? HttpListenerException as ex when ex.ErrorCode = 5 ->
            listener.Close()
            logger.Warn(
                "lovense.standard.callback.urlacl_required",
                "Lovense Standard API callback listener needs a URL ACL or a loopback-only prefix on Windows.",
                {|
                    listenUrl = config.CallbackListenUrl
                    normalizedPrefix = prefix
                    error = ex.Message
                    hint = $"If you want to bind to this URL, run an elevated shell and reserve it, e.g. netsh http add urlacl url={prefix} user={Environment.UserDomainName}\\{Environment.UserName}"
                |}
            )
            Error ex
        | ex ->
            listener.Close()
            Error ex

    let startCallbackListener
        (logger: StructuredSessionLogger)
        (config: LovenseStandardApiConfig)
        (developer: LovenseDeveloperConfig)
        (onDeviceInfo: LovenseDeviceInfo -> unit)
        =
        if not config.Enable then
            None
        else
            try
                let prefix = normalizeListenPrefix config.CallbackListenUrl
                let listener =
                    match tryStartListener logger config prefix with
                    | Ok listener -> listener
                    | Error _ ->
                        match localhostFallbackPrefix prefix with
                        | Some fallbackPrefix ->
                            logger.Info(
                                "lovense.standard.callback.localhost_fallback",
                                "Retrying Lovense Standard API callback listener on localhost because the configured prefix requires an ACL.",
                                {| listenUrl = config.CallbackListenUrl; fallbackPrefix = fallbackPrefix; publicCallbackUrl = config.PublicCallbackUrl |}
                            )

                            match tryStartListener logger config fallbackPrefix with
                            | Ok listener -> listener
                            | Error ex -> raise ex
                        | None ->
                            raise (HttpListenerException(5, $"Could not start callback listener at '{prefix}'."))

                logger.Info(
                    "lovense.standard.callback.started",
                    "Lovense Standard API callback listener started.",
                    {| listenUrl = config.CallbackListenUrl; normalizedPrefix = prefix; publicCallbackUrl = config.PublicCallbackUrl |}
                )

                let cts = new CancellationTokenSource()

                let rec loop () =
                    task {
                        if not cts.IsCancellationRequested && listener.IsListening then
                            try
                                let! context = listener.GetContextAsync()
                                use reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding)
                                let! body = reader.ReadToEndAsync()

                                let statusCode, responseBody =
                                    match validateCallback developer body with
                                    | Error message ->
                                        logger.Warn(
                                            "lovense.standard.callback.rejected",
                                            "Lovense Standard API callback rejected.",
                                            {| error = message; bodyLength = body.Length |}
                                        )
                                        403, $"""{{"ok":false,"message":"{LovenseFormatting.escapeJsonString message}"}}"""
                                    | Ok deviceInfo ->
                                        logger.Info(
                                            "lovense.standard.callback.accepted",
                                            "Lovense Standard API callback accepted and device capabilities updated.",
                                            {|
                                                toyCount = deviceInfo.ToyList.Length
                                                domain = deviceInfo.Domain
                                                httpsPort = deviceInfo.HttpsPort
                                                functionCount = deviceInfo.SupportedFunctions |> Option.map Set.count |> Option.defaultValue 0
                                            |}
                                        )
                                        onDeviceInfo deviceInfo
                                        200, """{"ok":true}"""

                                let bytes = Encoding.UTF8.GetBytes(responseBody)
                                context.Response.StatusCode <- statusCode
                                context.Response.ContentType <- Constants.Lovense.JsonMediaType
                                context.Response.ContentLength64 <- int64 bytes.Length
                                do! context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, CancellationToken.None)
                                context.Response.Close()
                            with
                            | :? ObjectDisposedException -> ()
                            | :? HttpListenerException -> ()
                            | ex ->
                                logger.Warn(
                                    "lovense.standard.callback.error",
                                    "Lovense Standard API callback listener hit a recoverable error.",
                                    {| error = ex.Message; errorType = ex.GetType().FullName |}
                                )

                            return! loop ()
                    }

                Task.Run(fun () -> loop () :> Task) |> ignore

                Some
                    {
                        ListeningUrl = config.CallbackListenUrl
                        Stop =
                            fun () ->
                                cts.Cancel()
                                try listener.Stop() with _ -> ()
                                listener.Close()
                                cts.Dispose()
                                logger.Info("lovense.standard.callback.stopped", "Lovense Standard API callback listener stopped.")
                    }
            with ex ->
                logger.Warn(
                    "lovense.standard.callback.start_failed",
                    "Lovense Standard API callback listener could not start.",
                    {| listenUrl = config.CallbackListenUrl; error = ex.Message; errorType = ex.GetType().FullName |}
                )
                None

    let serverCommandPayload (developer: LovenseDeveloperConfig) (plan: LovenseCommandPlan) (actionString: string) =
        match developer.Token, developer.UserId with
        | Some token, Some uid when not (String.IsNullOrWhiteSpace token) && not (String.IsNullOrWhiteSpace uid) ->
            let toyPart =
                plan.ToyId
                |> Option.map (fun toyId ->
                    let escaped = LovenseFormatting.escapeJsonString toyId
                    sprintf ",\"toy\":\"%s\"" escaped)
                |> Option.defaultValue ""

            let stopPrevious = if plan.StopPrevious then 1 else 0

            Ok
                $"""{{"token":"{LovenseFormatting.escapeJsonString token}","uid":"{LovenseFormatting.escapeJsonString uid}","command":"{Constants.Lovense.CommandName}","action":"{LovenseFormatting.escapeJsonString actionString}","timeSec":{LovenseFormatting.invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""
        | _ ->
            Error(MissingDeveloperCredentials(missingDeveloperFields developer))

    let redactedServerCommandPayload (developer: LovenseDeveloperConfig) (plan: LovenseCommandPlan) actionString =
        match serverCommandPayload developer plan actionString with
        | Error error -> Error error
        | Ok body ->
            developer.Token
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map (fun token -> body.Replace(token, Constants.Lovense.AuthTokenRedacted))
            |> Option.defaultValue body
            |> Ok

    let sendServerCommandAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (developer: LovenseDeveloperConfig)
        (plan: LovenseCommandPlan)
        (correlationId: string)
        (ct: CancellationToken)
        =
        task {
            let actionString = LovenseActionCodec.planActionString plan

            match serverCommandPayload developer plan actionString, redactedServerCommandPayload developer plan actionString with
            | Error error, _ -> return Error(CommandEmitFailed(Constants.Lovense.StandardServerCommand, string error))
            | _, Error error -> return Error(CommandEmitFailed(Constants.Lovense.StandardServerCommand, string error))
            | Ok payload, Ok redactedPayload ->
                logger.Info(
                    "lovense.standard.server_command.emit",
                    "Emitting Lovense command through Standard API server fallback.",
                    {| correlationId = correlationId; action = actionString; rawLogged = logger.IsRawLovenseEnabled |}
                )

                let! response =
                    Transport.postJsonAsync
                        http
                        logger
                        correlationId
                        Constants.Lovense.StandardServerCommand
                        redactedPayload
                        payload
                        ct

                match response with
                | Ok response ->
                    logger.Info(
                        "lovense.standard.server_command.success",
                        "Lovense Standard API server command completed.",
                        {| correlationId = correlationId; statusCode = response.StatusCode; action = actionString |}
                    )
                    return Ok response
                | Error error ->
                    logger.Warn(
                        "lovense.standard.server_command.failure",
                        "Lovense Standard API server command failed.",
                        {| correlationId = correlationId; error = string error; action = actionString |}
                    )
                    return Error(CommandEmitFailed(Constants.Lovense.StandardServerCommand, string error))
        }
