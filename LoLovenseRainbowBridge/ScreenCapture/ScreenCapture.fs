namespace LoLovenseRainbowBridge.ScreenCapture

open System
open System.Drawing
open System.Drawing.Imaging

type ScreenRegion =
    {
        X: int
        Y: int
        Width: int
        Height: int
    }

type CaptureResult =
    {
        Bitmap: Bitmap
        Timestamp: DateTimeOffset
    }

module ScreenCapture =
    let captureScreen () =
        let screenWidth = 1920
        let screenHeight = 1080
        let bitmap = new Bitmap(screenWidth, screenHeight, PixelFormat.Format24bppRgb)
        
        use graphics = Graphics.FromImage(bitmap)
        graphics.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy)
        
        { Bitmap = bitmap; Timestamp = DateTimeOffset.UtcNow }

    let captureRegion (region: ScreenRegion) =
        let bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb)
        let size = new Size(region.Width, region.Height)
        
        use graphics = Graphics.FromImage(bitmap)
        graphics.CopyFromScreen(region.X, region.Y, 0, 0, size, CopyPixelOperation.SourceCopy)
        
        { Bitmap = bitmap; Timestamp = DateTimeOffset.UtcNow }

    let captureLeagueMinimap (defaultRegion: ScreenRegion) =
        captureRegion defaultRegion



