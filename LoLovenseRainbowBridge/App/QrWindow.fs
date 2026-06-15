namespace LoLovenseRainbowBridge.App

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.Windows
open System.Windows.Controls
open System.Windows.Media
open System.Windows.Media.Imaging
open System.Windows.Threading
open System.Windows.Markup
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Lovense

type internal LovenseQrWindow() as this =
    inherit Window()

    let mutable qrImage: Image = null
    let mutable codeText: TextBlock = null
    let mutable statusText: TextBlock = null
    let mutable jobStatusText: TextBlock = null
    let imageGate = obj()
    let mutable imageLoadVersion = 0L
    let mutable imageLoadCancellation: CancellationTokenSource option = None

    let loadView () =
        let xamlPath = Path.Combine(AppContext.BaseDirectory, "App", "LovenseQrWindow.xaml")
        let xaml = File.ReadAllText(xamlPath)
        let root = XamlReader.Parse(xaml) :?> FrameworkElement
        this.Content <- root
        qrImage <- root.FindName("QrImage") :?> Image
        codeText <- root.FindName("CodeText") :?> TextBlock
        statusText <- root.FindName("StatusText") :?> TextBlock
        jobStatusText <- root.FindName("JobStatusText") :?> TextBlock

    let cancelInFlightImageLoad () =
        lock imageGate (fun () ->
            imageLoadCancellation
            |> Option.iter (fun cts ->
                try
                    cts.Cancel()
                finally
                    cts.Dispose())

            imageLoadVersion <- imageLoadVersion + 1L

            let cts = new CancellationTokenSource()
            imageLoadCancellation <- Some cts
            imageLoadVersion, cts)

    let loadBitmapFromBytes (bytes: byte array) =
        use stream = new MemoryStream(bytes)
        let frame =
            BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat ||| BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad
            )

        frame.Freeze()
        frame :> ImageSource

    let setImageSource (source: ImageSource option) =
        if this.Dispatcher.CheckAccess() then
            qrImage.Source <- defaultArg source null
        else
            this.Dispatcher.BeginInvoke(Action(fun () -> qrImage.Source <- defaultArg source null)) |> ignore

    let updateImage (qrUrl: string) =
        let version, cts = cancelInFlightImageLoad ()

        try
            if String.IsNullOrWhiteSpace qrUrl then
                setImageSource None
            elif qrUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) then
                let commaIndex = qrUrl.IndexOf(',')

                if commaIndex > 0 then
                    let base64 = qrUrl.Substring(commaIndex + 1)
                    let bytes = Convert.FromBase64String(base64)
                    setImageSource (Some(loadBitmapFromBytes bytes))
                else
                    setImageSource None
            elif Uri.IsWellFormedUriString(qrUrl, UriKind.Absolute) then
                setImageSource None

                Task.Run(
                    Func<Task>(fun () ->
                        task {
                            try
                                use http = Shared.insecureHttpClient ()
                                use! response = http.GetAsync(qrUrl, cts.Token)
                                response.EnsureSuccessStatusCode() |> ignore
                                let! bytes = response.Content.ReadAsByteArrayAsync(cts.Token)

                                if not cts.IsCancellationRequested then
                                    let image = loadBitmapFromBytes bytes

                                    if lock imageGate (fun () -> imageLoadVersion = version) then
                                        setImageSource (Some image)
                            with
                            | :? OperationCanceledException -> ()
                            | _ ->
                                if lock imageGate (fun () -> imageLoadVersion = version) then
                                    setImageSource None
                        }
                    )
                )
                |> ignore
            elif File.Exists(qrUrl) then
                use stream = File.OpenRead(qrUrl)
                let frame =
                    BitmapFrame.Create(
                        stream,
                        BitmapCreateOptions.PreservePixelFormat ||| BitmapCreateOptions.IgnoreImageCache,
                        BitmapCacheOption.OnLoad
                    )

                frame.Freeze()
                setImageSource (Some(frame :> ImageSource))
            else
                setImageSource None
        with _ ->
            setImageSource None

    let formatDuration = function
        | None -> "N/A"
        | Some durationMs -> $"{durationMs}ms"

    let renderJobStatus name iteration duration status =
        $"{name}: run {iteration} | last {formatDuration duration} | {status}"

    let updateVisual (snapshot: RuntimeState.RuntimeCacheSnapshot) =
        match snapshot.LovenseSession.StandardQrCode with
        | Some info ->
            codeText.Text <- $"Lovense pairing code: {info.Code}"
            statusText.Text <- $"Expires at {info.ExpiresAt.LocalDateTime:O}"
            updateImage info.Qr
        | None ->
            codeText.Text <- "Waiting for Lovense QR..."
            statusText.Text <- "Open Lovense Standard API pairing. The code will appear here when available."
            qrImage.Source <- null

        let jobs =
            [
                renderJobStatus "League" snapshot.League.CurrentIteration snapshot.League.LastCompletedCycleDurationMs snapshot.League.Status
                renderJobStatus "OCR" snapshot.Ocr.CurrentIteration snapshot.Ocr.LastCompletedCycleDurationMs snapshot.Ocr.Status
                renderJobStatus "Toy" snapshot.Toys.CurrentIteration snapshot.Toys.LastCompletedCycleDurationMs snapshot.Toys.Status
                renderJobStatus "Lovense" snapshot.Lovense.CurrentIteration snapshot.Lovense.LastCompletedCycleDurationMs snapshot.Lovense.Status
            ]
            |> String.concat Environment.NewLine

        jobStatusText.Text <- $"Jobs:{Environment.NewLine}{jobs}"

    do
        loadView()

        this.Title <- "LoLovenseRainbowBridge QR"
        this.Width <- 620.0
        this.Height <- 840.0
        this.MinWidth <- 520.0
        this.MinHeight <- 680.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        this.Topmost <- true
        this.Background <- Brushes.White

    member _.Refresh(snapshot: RuntimeState.RuntimeCacheSnapshot) =
        if this.Dispatcher.CheckAccess() then
            updateVisual snapshot
        else
            this.Dispatcher.BeginInvoke(Action(fun () -> updateVisual snapshot)) |> ignore

type LovenseQrWindowPresenter(cache: RuntimeState.RuntimeStateCache) =
    let gate = obj()
    let ready = new ManualResetEventSlim(false)
    let mutable window: LovenseQrWindow option = None
    let mutable timer: DispatcherTimer option = None

    do
        let thread =
            Thread(ThreadStart(fun () ->
                try
                    let app = Application()
                    let qrWindow = LovenseQrWindow()
                    let refreshTimer = DispatcherTimer()
                    refreshTimer.Interval <- TimeSpan.FromMilliseconds(250.0)

                    refreshTimer.Tick.Add(fun _ -> qrWindow.Refresh(cache.Read()))
                    qrWindow.Loaded.Add(fun _ ->
                        qrWindow.Refresh(cache.Read())
                        refreshTimer.Start())
                    qrWindow.Closed.Add(fun _ ->
                        refreshTimer.Stop()
                        lock gate (fun () -> timer <- None))

                    lock gate (fun () ->
                        window <- Some qrWindow
                        timer <- Some refreshTimer)
                    ready.Set() |> ignore

                    app.Run(qrWindow) |> ignore
                finally
                    lock gate (fun () -> window <- None)
                    if not ready.IsSet then
                        ready.Set() |> ignore
            ))

        thread.IsBackground <- true
        thread.SetApartmentState(ApartmentState.STA)
        thread.Start()
        ready.Wait()

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                timer |> Option.iter (fun t -> t.Stop())
                timer <- None)

            lock gate (fun () -> window)
            |> Option.iter (fun qrWindow ->
                if qrWindow.Dispatcher.CheckAccess() then
                    qrWindow.Close()
                else
                    qrWindow.Dispatcher.Invoke(Action(fun () -> qrWindow.Close())) |> ignore)
