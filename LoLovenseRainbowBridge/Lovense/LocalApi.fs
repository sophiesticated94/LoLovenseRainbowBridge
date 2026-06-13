namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Globalization
open System.Threading
open LoLovenseRainbowBridge

module LocalApi =

    let private getToysBody = $"""{{"command":"{Constants.Lovense.GetToysCommand}"}}"""

    let private escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let private invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let private configuredDeviceInfo (config: LovenseLocalApiConfig) =
        {
            ToyList = []
            SupportedFunctions = None
            CapabilityProfiles = []
            Domain = config.Domain
            HttpsPort = config.HttpsPort
            HttpPort = None
            WssPort = None
        }

    let private commandUrl domain httpsPort =
        $"https://{domain}:{httpsPort}{Constants.Lovense.LocalCommandPath}"

    let private commandPayload (plan: LovenseCommandPlan) actionString =
        let toyPart =
            plan.ToyId
            |> Option.map (fun toyId -> $",\"toy\":\"{escapeJsonString toyId}\"")
            |> Option.defaultValue ""

        let stopPrevious = if plan.StopPrevious then 1 else 0

        $"""{{"command":"{Constants.Lovense.CommandName}","action":"{escapeJsonString actionString}","timeSec":{invariantFloat plan.TimeSec},"stopPrevious":{stopPrevious},"apiVer":{Constants.Lovense.ApiVersion}{toyPart}}}"""

    let getToysAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseLocalApiConfig)
        (deviceInfo: LovenseDeviceInfo)
        (ct: CancellationToken)
        =
        task {
            let domain = deviceInfo.Domain |> Option.orElse config.Domain
            let httpsPort = deviceInfo.HttpsPort |> Option.orElse config.HttpsPort

            match domain, httpsPort with
            | Some domain, Some httpsPort when config.EnableGetToys ->
                let correlationId = Transport.newCorrelationId()
                let url = commandUrl domain httpsPort

                logger.Info(
                    "lovense.local_get_toys.start",
                    "Requesting Lovense toy list from Local API for capability enrichment.",
                    {|
                        correlationId = correlationId
                        domain = domain
                        httpsPort = httpsPort
                        allowSelfSignedCertificate = config.AllowSelfSignedCertificate
                    |}
                )

                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(config.TimeoutMs)

                let! response =
                    Transport.postJsonWithHeadersAsync
                        http
                        logger
                        correlationId
                        url
                        [ Constants.Lovense.PlatformHeader, config.HeaderPlatform ]
                        getToysBody
                        getToysBody
                        timeoutCts.Token

                match response with
                | Error error ->
                    logger.Warn(
                        "lovense.local_get_toys.failure",
                        "Lovense Local API GetToys failed; keeping socket capabilities.",
                        {| correlationId = correlationId; error = string error |}
                    )
                    return Error error

                | Ok response ->
                    let parsed = DeviceInfo.parseGetToys response.Body

                    logger.Info(
                        "lovense.local_get_toys.success",
                        "Lovense Local API GetToys enriched toy capabilities.",
                        {|
                            correlationId = correlationId
                            statusCode = response.StatusCode
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
            let domain = deviceInfo |> Option.bind (fun info -> info.Domain) |> Option.orElse config.Domain
            let httpsPort = deviceInfo |> Option.bind (fun info -> info.HttpsPort) |> Option.orElse config.HttpsPort

            match domain, httpsPort with
            | Some domain, Some httpsPort when config.EnableCommandFallback ->
                let actionString = LovenseActionCodec.planActionString plan
                let payload = commandPayload plan actionString
                let url = commandUrl domain httpsPort

                logger.Info(
                    "lovense.local_command.emit",
                    "Emitting Lovense command through Local API fallback.",
                    {|
                        correlationId = correlationId
                        url = url
                        action = actionString
                        commandTimeSec = plan.TimeSec
                        rawLogged = logger.IsRawLovenseEnabled
                    |}
                )

                use timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(config.TimeoutMs)

                let! response =
                    Transport.postJsonWithHeadersAsync
                        http
                        logger
                        correlationId
                        url
                        [ Constants.Lovense.PlatformHeader, config.HeaderPlatform ]
                        payload
                        payload
                        timeoutCts.Token

                match response with
                | Ok response ->
                    logger.Info(
                        "lovense.local_command.success",
                        "Lovense Local API command completed.",
                        {|
                            correlationId = correlationId
                            statusCode = response.StatusCode
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
