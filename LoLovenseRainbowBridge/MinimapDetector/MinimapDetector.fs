namespace LoLovenseRainbowBridge.MinimapDetector

open System
open System.Drawing
open System.Drawing.Imaging
open System.IO
open OpenCvSharp
open LoLovenseRainbowBridge.ScreenCapture

type PlayerPosition =
    {
        X: float
        Y: float
        NormalizedX: float
        NormalizedY: float
        Confidence: float
    }

type DetectionResult =
    {
        Position: PlayerPosition option
        Timestamp: DateTimeOffset
        DetectionMethod: string
    }

module MinimapDetector =
    let private clamp minValue maxValue value =
        if value < minValue then minValue
        elif value > maxValue then maxValue
        else value

    let private bitmapToMat (bitmap: System.Drawing.Bitmap) =
        use ms = new IO.MemoryStream()
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp)
        ms.Position <- 0L
        Mat.FromImageData(ms.ToArray(), ImreadModes.Color)

    let private makeMask (hsv: Mat) (lower: Scalar) (upper: Scalar) =
        let mask = new Mat()
        use lowerMat = new Mat(1, 1, MatType.CV_8UC3, lower)
        use upperMat = new Mat(1, 1, MatType.CV_8UC3, upper)
        Cv2.InRange(hsv, lowerMat, upperMat, mask)
        mask

    let private candidateFromContour (mat: Mat) profileName profileWeight (contour: Mat) =
        let area = Cv2.ContourArea(contour)
        let rect = Cv2.BoundingRect(contour)
        let perimeter = Cv2.ArcLength(contour, true)

        if area < 0.5 || area > 360.0 || rect.Width < 1 || rect.Height < 1 || rect.Width > 36 || rect.Height > 36 then
            None
        else
            let aspect = float rect.Width / float rect.Height

            if aspect < 0.25 || aspect > 4.0 then
                None
            else
                let moments = Cv2.Moments(contour)
                let centerX, centerY =
                    if abs moments.M00 < 0.0001 then
                        float rect.X + float rect.Width / 2.0, float rect.Y + float rect.Height / 2.0
                    else
                        moments.M10 / moments.M00, moments.M01 / moments.M00

                let normalizedX = centerX / float mat.Width |> clamp 0.0 1.0
                let normalizedY = centerY / float mat.Height |> clamp 0.0 1.0
                let circularity =
                    if perimeter <= 0.0 then 0.0
                    else 4.0 * Math.PI * area / (perimeter * perimeter) |> clamp 0.0 1.0
                let expectedArea = 45.0
                let areaScore = 1.0 - (abs (area - expectedArea) / expectedArea) |> clamp 0.0 1.0
                let expectedSize = 14.0
                let sizeScore = 1.0 - (abs (float (max rect.Width rect.Height) - expectedSize) / expectedSize) |> clamp 0.0 1.0
                let centerDistance =
                    let dx = normalizedX - 0.5
                    let dy = normalizedY - 0.5
                    Math.Sqrt(dx * dx + dy * dy) / Math.Sqrt(0.5)
                let centerScore = 1.0 - centerDistance |> clamp 0.0 1.0
                let score = profileWeight + areaScore + sizeScore + circularity + (0.35 * centerScore)

                Some(
                    score,
                    profileName,
                    {
                        X = centerX
                        Y = centerY
                        NormalizedX = normalizedX
                        NormalizedY = normalizedY
                        Confidence = score / 4.35 |> clamp 0.0 1.0
                    }
                )

    let private contourCandidates mat profileName profileWeight mask =
        use cleaned = new Mat()
        use kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, Size(2, 2))
        Cv2.MorphologyEx(mask, cleaned, MorphTypes.Close, kernel)

        use hierarchy = new Mat()
        let mutable contours: Mat[] = null
        Cv2.FindContours(cleaned, &contours, hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple)

        if isNull contours then
            []
        else
            contours
            |> Array.choose (candidateFromContour mat profileName profileWeight)
            |> Array.toList

    let private colorBasedDetection (mat: Mat) =
        try
            use hsv = new Mat()
            Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV)

            let masks =
                [
                    "SelectedPlayerYellow", 1.3, makeMask hsv (Scalar(15.0, 45.0, 80.0)) (Scalar(45.0, 255.0, 255.0))
                    "PlayerGreen", 1.15, makeMask hsv (Scalar(38.0, 40.0, 50.0)) (Scalar(92.0, 255.0, 255.0))
                    "TeamCyanBlue", 0.85, makeMask hsv (Scalar(85.0, 45.0, 60.0)) (Scalar(125.0, 255.0, 255.0))
                ]

            try
                masks
                |> List.collect (fun (name, weight, mask) -> contourCandidates mat name weight mask)
                |> List.sortByDescending (fun (score, _, _) -> score)
                |> List.tryHead
                |> Option.map (fun (_, _, position) -> position)
            finally
                masks |> List.iter (fun (_, _, mask) -> mask.Dispose())
        with _ ->
            None

    let private templateMatching (mat: Mat) (template: Mat) =
        try
            use result = new Mat()
            Cv2.MatchTemplate(mat, template, result, TemplateMatchModes.CCoeffNormed)

            let mutable minVal = 0.0
            let mutable maxVal = 0.0
            let mutable minLoc = OpenCvSharp.Point()
            let mutable maxLoc = OpenCvSharp.Point()
            Cv2.MinMaxLoc(result, &minVal, &maxVal, &minLoc, &maxLoc)

            let threshold = 0.7
            if maxVal >= threshold then
                let centerX = float maxLoc.X + float template.Width / 2.0
                let centerY = float maxLoc.Y + float template.Height / 2.0

                let normalizedX = centerX / float mat.Width
                let normalizedY = centerY / float mat.Height

                Some {
                    X = centerX
                    Y = centerY
                    NormalizedX = normalizedX
                    NormalizedY = normalizedY
                    Confidence = maxVal
                }
            else
                None
        with _ ->
            None

    let createTemplateFromBitmap (bitmap: Bitmap) =
        try
            Some(bitmapToMat bitmap)
        with _ ->
            None

    let loadTemplateFromFile path =
        try
            if String.IsNullOrWhiteSpace path || not (File.Exists path) then
                None
            else
                use bitmap = new Bitmap(path)
                createTemplateFromBitmap bitmap
        with _ ->
            None

    let detectPlayerPosition (captureResult: CaptureResult) (template: Mat option) =
        try
            use mat = bitmapToMat captureResult.Bitmap
            
            match template with
            | Some tmpl ->
                match templateMatching mat tmpl with
                | Some pos ->
                    { Position = Some pos; Timestamp = captureResult.Timestamp; DetectionMethod = "TemplateMatching" }
                | None ->
                    match colorBasedDetection mat with
                    | Some pos ->
                        { Position = Some pos; Timestamp = captureResult.Timestamp; DetectionMethod = "ColorBased" }
                    | None ->
                        { Position = None; Timestamp = captureResult.Timestamp; DetectionMethod = "None" }
            | None ->
                match colorBasedDetection mat with
                | Some pos ->
                    { Position = Some pos; Timestamp = captureResult.Timestamp; DetectionMethod = "ColorBased" }
                | None ->
                    { Position = None; Timestamp = captureResult.Timestamp; DetectionMethod = "None" }
        with _ ->
            { Position = None; Timestamp = captureResult.Timestamp; DetectionMethod = "Error" }
