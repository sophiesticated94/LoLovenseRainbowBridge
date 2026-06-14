namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading
open LoLovenseRainbowBridge

module LocalApi =

    let private getToysBody = $"""{{"command":"{Constants.Lovense.GetToysCommand}"}}"""

    let private configuredDeviceInfo (config: LovenseLocalApiConfig) =
        {
            ToyList = []
            SupportedFunctions = None
            CapabilityProfiles = []
            Domain = config.Domain
            HttpsPort = config.HttpsPort
            HttpPort = config.HttpPort
            WssPort = None
        }

    let private commandUrl scheme domain port =
        $"{scheme}://{domain}:{port}{Constants.Lovense.LocalCommandPath}"

    let private commandEndpoints (config: LovenseLocalApiConfig) (deviceInfo: LovenseDeviceInfo option) =
        let domain = deviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.Domain

        match domain with
        | None -> []
        | Some domain ->
            [
                match deviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.HttpsPort with
                | Some port -> "https", domain, port
                | None -> ()

                match deviceInfo |> Option.bind (fun info -> info.HttpPort) |> Option.orElse config.HttpPort with
                | Some port -> "http", domain, port
                | None -> ()
            ]

    let private commandPayload (plan: LovenseCommandPlan) actionString =
        let toyPart =
            plan.ToyId
            |> Option.map (fun toyId -> $",\"toy\":\"{LovenseFormatting.escapeJsonString toyId}\"")
            |> Option.defaultValue ""

        let stopPrevious = if plan.StopPrevious then 1 else 0

        $"""{{"command":"{Constants.Lovense.CommandName}","action":"{LovenseFormatting.escapeJsonString actionString}","timeSec":{LovenseFormatting.invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""

    let private validateLocalApiBody operation (body: string) =
        try
            let root = JsonNode.Parse(body)

            if isNull root then
                Error $"{operation} returned an empty JSON body."
            else
                let code = Json.tryInt Constants.Lovense.CodeField root

                match code with
                | Some 200 -> Ok()
                | Some other ->
                    let message = Json.tryString Constants.Lovense.MessageField root |> Option.defaultValue ""
                    let responseType = Json.tryString "type" root |> Option.defaultValue ""
                    Error $"{operation} returned Lovense Local API code {other}. type='{responseType}' message='{message}'"
                | None ->
                    Error $"{operation} response did not include a Lovense Local API code."
        with ex ->
            Error $"{operation} returned invalid JSON: {ex.Message}"

    let getToysAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseLocalApiConfig)
        (deviceInfo: LovenseDeviceInfo)
        (ct: CancellationToken)
        =
        task {
            let endpoints = commandEndpoints config (Some deviceInfo)

            match endpoints with
            | endpoints when config.EnableGetToys && not endpoints.IsEmpty ->
                let correlationId = Transport.newCorrelationId()

                logger.Info(
                    "lovense.local_get_toys.start",
                    "Requesting Lovense toy list from Local API for capability enrichment.",
                    {|
                        correlationId = correlationId
                        endpoints = endpoints |> List.map (fun (scheme, domain, port) -> commandUrl scheme domain port)
                        allowSelfSignedCertificate = config.AllowSelfSignedCertificate
                    |}
                )

                let mutable response = None
                let mutable lastError = None

                for scheme, domain, port in endpoints do
                    if response.IsNone then
                        let url = commandUrl scheme domain port
                        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        timeoutCts.CancelAfter(config.TimeoutMs)

                        let! endpointResponse =
                            Transport.postJsonWithHeadersAsync
                                http
                                logger
                                correlationId
                                url
                                [ Constants.Lovense.PlatformHeader, config.HeaderPlatform ]
                                getToysBody
                                getToysBody
                                timeoutCts.Token

                        match endpointResponse with
                        | Ok endpointResponse ->
                            response <- Some(Ok(scheme, domain, port, endpointResponse))
                        | Error error ->
                            lastError <- Some error
                            logger.Warn(
                                "lovense.local_get_toys.endpoint_failure",
                                "Lovense Local API GetToys endpoint failed; trying the next configured endpoint if available.",
                                {| correlationId = correlationId; url = url; error = string error |}
                            )

                let response =
                    response
                    |> Option.defaultValue (Error(defaultArg lastError (SocketUrlRequestFailed(Constants.Lovense.LocalCommandPath, "No Lovense Local API endpoint is configured."))))

                match response with
                | Error error ->
                    logger.Warn(
                        "lovense.local_get_toys.failure",
                        "Lovense Local API GetToys failed; keeping socket capabilities.",
                        {| correlationId = correlationId; error = string error |}
                    )
                    return Error error

                | Ok(scheme, domain, port, response) ->
                    match validateLocalApiBody Constants.Lovense.GetToysCommand response.Body with
                    | Error message ->
                        let error = SocketUrlRequestFailed(Constants.Lovense.LocalCommandPath, message)
                        logger.Warn(
                            "lovense.local_get_toys.rejected",
                            "Lovense Local API GetToys returned a non-success Lovense code.",
                            {| correlationId = correlationId; endpoint = commandUrl scheme domain port; error = message |}
                        )
                        return Error error
                    | Ok() ->
                        let parsed = DeviceInfo.parseGetToys response.Body

                        logger.Info(
                            "lovense.local_get_toys.success",
                            "Lovense Local API GetToys enriched toy capabilities.",
                            {|
                                correlationId = correlationId
                                statusCode = response.StatusCode
                                endpoint = commandUrl scheme domain port
                                toyCount = parsed.ToyList.Length
                                functionCount = parsed.SupportedFunctions |> Option.map Set.count |> Option.defaultValue 0
                                rawLogged = logger.IsRawLovenseEnabled
                            |}
                        )

                        return Ok parsed

            | _ ->
                return Error(SocketUrlRequestFailed(Constants.Lovense.LocalCommandPath, "Local API GetToys is disabled or socket device info did not include domain/httpsPort."))
        }

    let getConfiguredToysAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseLocalApiConfig)
        (ct: CancellationToken)
        =
        getToysAsync http logger config (configuredDeviceInfo config) ct

    let sendCommandAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseLocalApiConfig)
        (deviceInfo: LovenseDeviceInfo option)
        (plan: LovenseCommandPlan)
        (correlationId: string)
        (ct: CancellationToken)
        =
        task {
            let endpoints = commandEndpoints config deviceInfo

            match endpoints with
            | endpoints when config.EnableCommandFallback && not endpoints.IsEmpty ->
                let actionString = LovenseActionCodec.planActionString plan
                let payload = commandPayload plan actionString

                logger.Info(
                    "lovense.local_command.emit",
                    "Emitting Lovense command through Local API fallback.",
                    {|
                        correlationId = correlationId
                        endpoints = endpoints |> List.map (fun (scheme, domain, port) -> commandUrl scheme domain port)
                        action = actionString
                        commandTimeSec = plan.TimeSec
                        rawLogged = logger.IsRawLovenseEnabled
                    |}
                )

                let mutable response = None
                let mutable lastError = None

                for scheme, domain, port in endpoints do
                    if response.IsNone then
                        let url = commandUrl scheme domain port
                        use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        timeoutCts.CancelAfter(config.TimeoutMs)

                        let! endpointResponse =
                            Transport.postJsonWithHeadersAsync
                                http
                                logger
                                correlationId
                                url
                                [ Constants.Lovense.PlatformHeader, config.HeaderPlatform ]
                                payload
                                payload
                                timeoutCts.Token

                        match endpointResponse with
                        | Ok endpointResponse ->
                            response <- Some(Ok(scheme, domain, port, endpointResponse))
                        | Error error ->
                            lastError <- Some(CommandEmitFailed(Constants.Lovense.LocalCommandPath, string error))
                            logger.Warn(
                                "lovense.local_command.endpoint_failure",
                                "Lovense Local API command endpoint failed; trying the next configured endpoint if available.",
                                {| correlationId = correlationId; url = url; error = string error; action = actionString |}
                            )

                let response =
                    response
                    |> Option.defaultValue (Error(defaultArg lastError (CommandEmitFailed(Constants.Lovense.LocalCommandPath, "No Lovense Local API endpoint is configured."))))

                match response with
                | Ok(scheme, domain, port, response) ->
                    match validateLocalApiBody Constants.Lovense.CommandName response.Body with
                    | Error message ->
                        logger.Warn(
                            "lovense.local_command.rejected",
                            "Lovense Local API command returned a non-success Lovense code.",
                            {| correlationId = correlationId; endpoint = commandUrl scheme domain port; error = message; action = actionString |}
                        )

                        return Error(CommandEmitFailed(Constants.Lovense.LocalCommandPath, message))
                    | Ok() ->
                        logger.Info(
                            "lovense.local_command.success",
                            "Lovense Local API command completed.",
                            {|
                                correlationId = correlationId
                                statusCode = response.StatusCode
                                endpoint = commandUrl scheme domain port
                                action = actionString
                                rawLogged = logger.IsRawLovenseEnabled
                            |}
                        )

                        return Ok response

                | Error error ->
                    logger.Warn(
                        "lovense.local_command.failure",
                        "Lovense Local API command failed.",
                        {| correlationId = correlationId; error = string error; action = actionString |}
                    )

                    return Error(CommandEmitFailed(Constants.Lovense.LocalCommandPath, string error))

            | _ ->
                return Error(CommandEmitFailed(Constants.Lovense.LocalCommandPath, "Lovense Local API command fallback is disabled or missing Domain/HttpsPort."))
        }
