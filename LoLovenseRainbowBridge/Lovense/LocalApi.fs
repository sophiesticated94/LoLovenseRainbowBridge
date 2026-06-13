namespace LoLovenseRainbowBridge.Lovense

open System
open System.Net.Http
open System.Threading
open LoLovenseRainbowBridge

module LocalApi =

    let private getToysBody = $"""{{"command":"{Constants.Lovense.GetToysCommand}"}}"""

    let getToysAsync
        (http: HttpClient)
        (logger: StructuredSessionLogger)
        (config: LovenseLocalApiConfig)
        (deviceInfo: LovenseDeviceInfo)
        (ct: CancellationToken)
        =
        task {
            match deviceInfo.Domain, deviceInfo.HttpsPort with
            | Some domain, Some httpsPort when config.EnableGetToys ->
                let correlationId = Transport.newCorrelationId()
                let url = $"https://{domain}:{httpsPort}{Constants.Lovense.LocalCommandPath}"

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
