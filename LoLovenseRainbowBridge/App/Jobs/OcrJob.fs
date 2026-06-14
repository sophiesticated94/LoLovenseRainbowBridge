namespace LoLovenseRainbowBridge.App.Jobs

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.MinimapDetector
open LoLovenseRainbowBridge.PositionMapping
open LoLovenseRainbowBridge.ScreenCapture

type OcrCacheJob
    (
        runtimeConfig: RuntimeConfig,
        positionRotationConfig: PositionBasedRotationConfig,
        cache: RuntimeState.RuntimeStateCache,
        logger: StructuredSessionLogger
    ) =

    interface IAppJob with
        member _.Name = "OcrCacheJob"

        member _.RunAsync(ct: CancellationToken) =
            task {
                while not ct.IsCancellationRequested do
                    try
                        if not positionRotationConfig.Enable then
                            cache.UpdateOcrDisabled()
                            logger.Debug(
                                "runtime.ocr_job.disabled",
                                "OCR cache job is disabled by configuration.",
                                {| ocrPollMs = runtimeConfig.OcrPollMs |}
                            )
                            do! Task.Delay(runtimeConfig.OcrPollMs, ct)
                        else
                            let minimapRegion =
                                {
                                    X = positionRotationConfig.MinimapScreenX
                                    Y = positionRotationConfig.MinimapScreenY
                                    Width = positionRotationConfig.MinimapWidth
                                    Height = positionRotationConfig.MinimapHeight
                                }

                            let captureResult = ScreenCapture.captureLeagueMinimap minimapRegion
                            let template = positionRotationConfig.TemplateImagePath |> Option.bind MinimapDetector.loadTemplateFromFile
                            let detectionResult = MinimapDetector.detectPlayerPosition captureResult template

                            match detectionResult.Position with
                            | None ->
                                cache.UpdateOcrFailure "No player position detected in minimap."
                                logger.Debug(
                                    "runtime.ocr_job.no_detection",
                                    "OCR cache job did not detect minimap position.",
                                    {| detectionMethod = detectionResult.DetectionMethod; detectionFailures = cache.Read().Ocr.DetectionFailures |}
                                )

                            | Some playerPosition ->
                                match PositionMapping.parseMappingMode positionRotationConfig.MappingMode with
                                | None ->
                                    cache.UpdateOcrFailure $"Invalid mapping mode: {positionRotationConfig.MappingMode}"
                                | Some mode ->
                                    let rotationResult = PositionMapping.mapPositionToRotation playerPosition mode positionRotationConfig.RotationSensitivity
                                    let planningPosition : Lovense.LovensePlanningPosition =
                                        {
                                            NormalizedX = playerPosition.NormalizedX
                                            NormalizedY = playerPosition.NormalizedY
                                            Confidence = playerPosition.Confidence
                                            Quadrant = RuntimeState.planningQuadrant playerPosition.NormalizedX playerPosition.NormalizedY
                                            Zone = string rotationResult.Zone
                                            DetectionMethod = detectionResult.DetectionMethod
                                        }

                                    cache.UpdateOcrSuccess planningPosition
                                    logger.Debug(
                                        "runtime.ocr_job.success",
                                        "OCR cache updated with minimap position.",
                                        {| normalizedX = planningPosition.NormalizedX; normalizedY = planningPosition.NormalizedY; quadrant = planningPosition.Quadrant; version = cache.Read().Ocr.Version |}
                                    )

                            do! Task.Delay(runtimeConfig.OcrPollMs, ct)
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        cache.UpdateOcrFailure ex.Message
                        logger.Warn(
                            "runtime.ocr_job.error",
                            "OCR cache job hit a recoverable error.",
                            {| error = ex.Message; errorType = ex.GetType().FullName |}
                        )
                        do! Task.Delay(runtimeConfig.OcrPollMs, ct)
            } :> Task
