namespace LoLovenseRainbowBridge.App.Jobs

open System
open System.Threading
open System.Threading.Tasks
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.App
open LoLovenseRainbowBridge.Lovense
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

    let positionWeightsFromConfig (config: PositionBasedRotationConfig) quadrant normalizedX =
        match quadrant |> Option.ofObj |> Option.defaultValue "" with
        | value when String.Equals(value, "Center", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.Center.Left, config.PositionWeights.Center.Right
        | value when String.Equals(value, "TopLeft", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.TopLeft.Left, config.PositionWeights.TopLeft.Right
        | value when String.Equals(value, "TopRight", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.TopRight.Left, config.PositionWeights.TopRight.Right
        | value when String.Equals(value, "BottomLeft", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.BottomLeft.Left, config.PositionWeights.BottomLeft.Right
        | value when String.Equals(value, "BottomRight", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.BottomRight.Left, config.PositionWeights.BottomRight.Right
        | value when String.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.Left.Left, config.PositionWeights.Left.Right
        | value when String.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) -> config.PositionWeights.Right.Left, config.PositionWeights.Right.Right
        | _ ->
            CapabilityResolver.stereoWeightsFromNormalizedX 100 normalizedX
            |> fun (l, r) -> float l / 100.0, float r / 100.0

    let positionProjectionFor (config: PositionBasedRotationConfig) (position: LovensePlanningPosition) : RuntimeState.OcrPositionProjection =
        let leftWeight, rightWeight = positionWeightsFromConfig config position.Quadrant position.NormalizedX
        let quadrant = position.Quadrant
        let zone = position.Zone

        {
            PositionLeftWeight = leftWeight
            PositionRightWeight = rightWeight
            PositionIsCenter = String.Equals(quadrant, "Center", StringComparison.OrdinalIgnoreCase)
            PositionIsTopLeft = String.Equals(quadrant, "TopLeft", StringComparison.OrdinalIgnoreCase)
            PositionIsTopRight = String.Equals(quadrant, "TopRight", StringComparison.OrdinalIgnoreCase)
            PositionIsBottomLeft = String.Equals(quadrant, "BottomLeft", StringComparison.OrdinalIgnoreCase)
            PositionIsBottomRight = String.Equals(quadrant, "BottomRight", StringComparison.OrdinalIgnoreCase)
            PositionIsLeft = String.Equals(quadrant, "Left", StringComparison.OrdinalIgnoreCase)
            PositionIsRight = String.Equals(quadrant, "Right", StringComparison.OrdinalIgnoreCase)
            PositionZoneTopLane = String.Equals(zone, "TopLane", StringComparison.OrdinalIgnoreCase)
            PositionZoneMidLane = String.Equals(zone, "MidLane", StringComparison.OrdinalIgnoreCase)
            PositionZoneBottomLane = String.Equals(zone, "BottomLane", StringComparison.OrdinalIgnoreCase)
            PositionZoneJungle = String.Equals(zone, "Jungle", StringComparison.OrdinalIgnoreCase)
            PositionZoneRiver = String.Equals(zone, "River", StringComparison.OrdinalIgnoreCase)
            PositionZoneBase = String.Equals(zone, "Base", StringComparison.OrdinalIgnoreCase)
            PositionZoneUnknown = String.IsNullOrWhiteSpace zone || String.Equals(zone, "Unknown", StringComparison.OrdinalIgnoreCase)
        }

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

                                    cache.UpdateOcrSuccess(planningPosition, positionProjectionFor positionRotationConfig planningPosition)
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
