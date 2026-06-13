namespace LoLovenseRainbowBridge.MinimapDetector

open System
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
    let private bitmapToMat (bitmap: System.Drawing.Bitmap) =
        use ms = new IO.MemoryStream()
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp)
        ms.Position <- 0L
        Mat.FromImageData(ms.ToArray(), ImreadModes.Color)

    let private colorBasedDetection (mat: Mat) =
        try
            // Convert to HSV color space
            use hsv = new Mat()
            Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV)
            
            // Filter for green colors (ally player indicator: ~40-80 hue range)
            // HSV ranges: Lower (40, 50, 50), Upper (80, 255, 255)
            let lower = Scalar(40.0, 50.0, 50.0)
            let upper = Scalar(80.0, 255.0, 255.0)
            use mask = new Mat()
            // Create Scalar as Mat for the InputArray overload
            use lowerMat = new Mat(1, 3, MatType.CV_64FC1, lower)
            use upperMat = new Mat(1, 3, MatType.CV_64FC1, upper)
            Cv2.InRange(hsv, lowerMat, upperMat, mask)
            
            // Find contours in the filtered image using Mat array version
            use hierarchy = new Mat()
            let mutable contours: Mat[] = null
            Cv2.FindContours(mask, &contours, hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple)
            
            if contours <> null && contours.Length > 0 then
                // Find the largest contour by area
                let mutable maxArea = 0.0
                let mutable largestContourIdx = 0
                for i in 0 .. contours.Length - 1 do
                    let area = Cv2.ContourArea(contours.[i])
                    if area > maxArea then
                        maxArea <- area
                        largestContourIdx <- i
                
                // Check contour size (area > 50 pixels)
                if maxArea > 50.0 then
                    // Calculate centroid using moments
                    let moments = Cv2.Moments(contours.[largestContourIdx])
                    let centerX = moments.M10 / moments.M00
                    let centerY = moments.M01 / moments.M00
                    
                    // Normalize coordinates (0-1)
                    let normalizedX = centerX / float mat.Width
                    let normalizedY = centerY / float mat.Height
                    
                    // Calculate confidence based on contour area (larger = higher confidence)
                    let confidence = min 1.0 (maxArea / 500.0)
                    
                    Some {
                        X = centerX
                        Y = centerY
                        NormalizedX = normalizedX
                        NormalizedY = normalizedY
                        Confidence = confidence
                    }
                else
                    None
            else
                None
        with _ ->
            None

    let private templateMatching (mat: Mat) (template: Mat) =
        try
            // Use template matching with CCoeffNormed method
            use result = new Mat()
            Cv2.MatchTemplate(mat, template, result, TemplateMatchModes.CCoeffNormed)
            
            // Find the best match location
            let mutable minVal = 0.0
            let mutable maxVal = 0.0
            let mutable minLoc = OpenCvSharp.Point()
            let mutable maxLoc = OpenCvSharp.Point()
            Cv2.MinMaxLoc(result, &minVal, &maxVal, &minLoc, &maxLoc)
            
            // Check if confidence threshold is met (0.7 as per plan)
            let threshold = 0.7
            if maxVal >= threshold then
                // Calculate center of matched template
                let centerX = float maxLoc.X + float template.Width / 2.0
                let centerY = float maxLoc.Y + float template.Height / 2.0
                
                // Normalize coordinates (0-1)
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

    let createDefaultTemplate () =
        try
            let mat = new Mat(20, 20, MatType.CV_8UC3, new Scalar(0.0, 255.0, 0.0))
            Cv2.Circle(mat, new OpenCvSharp.Point(10, 10), 8, new Scalar(0.0, 255.0, 0.0), -1)
            Some mat
        with _ ->
            None
