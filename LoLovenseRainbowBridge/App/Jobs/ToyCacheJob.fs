namespace LoLovenseRainbowBridge.App.Jobs

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.Lovense

type ToyCacheJob
    (
        runtimeConfig: RuntimeConfig,
        lovenseConfig: LovenseConfig,
        lovenseClient: LovenseClient,
        cache: RuntimeState.RuntimeStateCache,
        logger: StructuredSessionLogger
    ) =

    let seedDeviceInfo () =
        let cachedDeviceInfo =
            lovenseClient.LatestDeviceInfo
            |> Option.orElseWith (fun () -> cache.Read().Toys.DeviceInfo)

        let domain =
            cachedDeviceInfo
            |> Option.bind (fun deviceInfo -> deviceInfo.Domain)
            |> Option.orElse lovenseConfig.LocalApi.Domain

        let httpsPort =
            cachedDeviceInfo
            |> Option.bind (fun deviceInfo -> deviceInfo.HttpsPort)
            |> Option.orElse lovenseConfig.LocalApi.HttpsPort

        let httpPort =
            cachedDeviceInfo
            |> Option.bind (fun deviceInfo -> deviceInfo.HttpPort)
            |> Option.orElse lovenseConfig.LocalApi.HttpPort

        match domain, httpsPort, httpPort with
        | Some domain, httpsPort, httpPort when httpsPort.IsSome || httpPort.IsSome ->
            Some
                {
                    ToyList = cachedDeviceInfo |> Option.map (fun deviceInfo -> deviceInfo.ToyList) |> Option.defaultValue []
                    SupportedFunctions = cachedDeviceInfo |> Option.bind (fun deviceInfo -> deviceInfo.SupportedFunctions)
                    CapabilityProfiles = cachedDeviceInfo |> Option.map (fun deviceInfo -> deviceInfo.CapabilityProfiles) |> Option.defaultValue []
                    Domain = Some domain
                    HttpsPort = httpsPort
                    HttpPort = httpPort
                    WssPort = cachedDeviceInfo |> Option.bind (fun deviceInfo -> deviceInfo.WssPort)
                }
        | _ -> None

    interface IAppJob with
        member _.Name = "ToyCacheJob"

        member _.RunAsync(ct: CancellationToken) =
            task {
                use http =
                    if lovenseConfig.LocalApi.AllowSelfSignedCertificate then
                        Shared.insecureHttpClient ()
                    else
                        new System.Net.Http.HttpClient()

                while not ct.IsCancellationRequested do
                    try
                        if not lovenseConfig.LocalApi.EnableGetToys then
                            cache.UpdateToyDisabled()
                            logger.Debug(
                                "runtime.toy_job.disabled",
                                "Toy cache job is disabled by configuration.",
                                {| capabilityRefreshIntervalSec = lovenseConfig.LocalApi.CapabilityRefreshIntervalSec |}
                            )
                            do! Task.Delay(TimeSpan.FromSeconds(float lovenseConfig.LocalApi.CapabilityRefreshIntervalSec), ct)
                        else
                            match seedDeviceInfo () with
                            | None ->
                                logger.Debug(
                                    "runtime.toy_job.waiting_for_device_info",
                                    "Toy cache job is waiting for Lovense device info or configured LAN endpoints before requesting GetToys.",
                                    {| capabilityRefreshIntervalSec = lovenseConfig.LocalApi.CapabilityRefreshIntervalSec |}
                                )
                                do! Task.Delay(TimeSpan.FromSeconds(float lovenseConfig.LocalApi.CapabilityRefreshIntervalSec), ct)
                            | Some deviceInfo ->
                                let! result = LocalApi.getToysAsync http logger lovenseConfig.LocalApi deviceInfo ct

                                match result with
                                | Ok refreshed ->
                                    let merged =
                                        ClientState.mergeDeviceInfo (cache.Read().Toys.DeviceInfo) refreshed

                                    lovenseClient.ApplyDeviceInfo merged
                                    cache.UpdateToySuccess merged

                                    let toyState = cache.Read().Toys

                                    logger.Info(
                                        "runtime.toy_job.success",
                                        "Toy cache refreshed from Lovense Local API.",
                                        {| toyCount = merged.ToyList.Length; version = toyState.Version; failureAttemptsSinceSuccess = toyState.FailureAttemptsSinceSuccess |}
                                    )

                                    do! Task.Delay(TimeSpan.FromSeconds(float lovenseConfig.LocalApi.CapabilityRefreshIntervalSec), ct)

                                | Error error ->
                                    cache.UpdateToyFailure(string error)
                                    let toyState = cache.Read().Toys

                                    logger.Warn(
                                        "runtime.toy_job.failure",
                                        "Toy cache refresh failed; keeping last known good toy capabilities.",
                                        {| error = string error; attemptSinceLastSuccess = toyState.FailureAttemptsSinceSuccess |}
                                    )

                                    do! Task.Delay(TimeSpan.FromSeconds(float lovenseConfig.LocalApi.CapabilityRefreshIntervalSec), ct)
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        cache.UpdateToyFailure ex.Message
                        logger.Error(
                            "runtime.toy_job.error",
                            "Toy cache job hit an unexpected error.",
                            {| error = ex.Message; errorType = ex.GetType().FullName |}
                        )
                        do! Task.Delay(TimeSpan.FromSeconds(float lovenseConfig.LocalApi.CapabilityRefreshIntervalSec), ct)
            } :> Task
