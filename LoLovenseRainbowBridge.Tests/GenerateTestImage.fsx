#r "nuget: OpenCvSharp4, 4.9.0.20240103"
#r "nuget: OpenCvSharp4.runtime.win, 4.9.0.20240103"

open OpenCvSharp

// Create a synthetic minimap image (200x200 pixels)
let minimap = new Mat(200, 200, MatType.CV_8UC3, new Scalar(30.0, 30.0, 40.0)) // Dark blue-gray background

// Add some terrain features (simple circles in different colors)
Cv2.Circle(minimap, new Point(50, 50), 20, new Scalar(60.0, 80.0, 60.0), -1) // Green area
Cv2.Circle(minimap, new Point(150, 150), 25, new Scalar(80.0, 60.0, 60.0), -1) // Red area
Cv2.Circle(minimap, new Point(150, 50), 15, new Scalar(60.0, 60.0, 80.0), -1) // Blue area

// Add the green player indicator (green circle with directional arrow)
let playerX = 100
let playerY = 100
let playerRadius = 8

// Draw green circle for player position
Cv2.Circle(minimap, new Point(playerX, playerY), playerRadius, new Scalar(0.0, 255.0, 0.0), -1)

// Draw directional arrow (pointing up-right)
let arrowStart = new Point(playerX, playerY)
let arrowEnd = new Point(playerX + 6, playerY - 6)
Cv2.ArrowedLine(minimap, arrowStart, arrowEnd, new Scalar(255.0, 255.0, 255.0), 2)

// Save the image
let outputPath = "c:\Users\Zosia\source\repos\LoLovenseRainbowBridge\LoLovenseRainbowBridge.Tests\TestAssets\minimap.png"
Cv2.ImWrite(outputPath, minimap)

printfn "Test minimap image created at: %s" outputPath
