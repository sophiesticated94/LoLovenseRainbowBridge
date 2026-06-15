namespace LoLovenseRainbowBridge.App

open System
open System.Threading
open System.Windows
open System.Windows.Controls
open System.Windows.Media
open System.Windows.Media.Imaging
open LoLovenseRainbowBridge.Lovense

type LovenseQrPresentationState =
    {
        QrCode: StandardApiQrCodeInfo option
        IsOpen: bool
        ClosedByUser: bool
    }

module LovenseQrPresentationState =
    let initial =
        {
            QrCode = None
            IsOpen = true
            ClosedByUser = false
        }

    let withQrCode qrCode state =
        {
            state with
                QrCode = Some qrCode
                IsOpen = true
        }

    let manualClose state =
        {
            state with
                IsOpen = false
                ClosedByUser = true
        }

type internal LovenseQrWindow() as this =
    inherit Window()

    let qrImage =
        Image(
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = Thickness(0.0, 16.0, 0.0, 16.0),
            Height = 420.0
        )

    let codeText =
        TextBlock(
            Text = "Waiting for Lovense QR...",
            FontSize = 28.0,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        )

    let statusText =
        TextBlock(
            Text = "This window stays open until you close it manually.",
            FontSize = 14.0,
            Opacity = 0.75,
            TextAlignment = TextAlignment.Center,
            Margin = Thickness(0.0, 12.0, 0.0, 0.0),
            TextWrapping = TextWrapping.Wrap
        )

    let panel =
        StackPanel(
            Margin = Thickness(24.0)
        )

    let updateImage (qrUrl: string) =
        try
            let bitmap = BitmapImage()
            bitmap.BeginInit()
            bitmap.UriSource <- Uri(qrUrl, UriKind.Absolute)
            bitmap.CacheOption <- BitmapCacheOption.OnLoad
            bitmap.CreateOptions <- BitmapCreateOptions.IgnoreImageCache
            bitmap.EndInit()
            bitmap.Freeze()
            qrImage.Source <- bitmap
        with _ ->
            qrImage.Source <- null

    let updateVisual (qrInfo: StandardApiQrCodeInfo option) =
        match qrInfo with
        | Some info ->
            codeText.Text <- $"Lovense pairing code: {info.Code}"
            statusText.Text <- $"Expires at {info.ExpiresAt.LocalDateTime:O}"
            updateImage info.Qr
        | None ->
            codeText.Text <- "Waiting for Lovense QR..."
            statusText.Text <- "Open Lovense Standard API pairing. The code will appear here when available."
            qrImage.Source <- null

    do
        this.Title <- "LoLovenseRainbowBridge QR"
        this.Width <- 620.0
        this.Height <- 760.0
        this.MinWidth <- 520.0
        this.MinHeight <- 620.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        this.Topmost <- true
        this.Background <- Brushes.White

        panel.Children.Add(codeText) |> ignore
        panel.Children.Add(statusText) |> ignore
        panel.Children.Add(qrImage) |> ignore
        this.Content <- ScrollViewer(Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto)

        this.Loaded.Add(fun _ -> updateVisual None)

    member _.Update(qrInfo: StandardApiQrCodeInfo option) =
        if this.Dispatcher.CheckAccess() then
            updateVisual qrInfo
        else
            this.Dispatcher.BeginInvoke(Action(fun () -> updateVisual qrInfo)) |> ignore

type LovenseQrWindowPresenter() =
    let gate = obj()
    let ready = new ManualResetEventSlim(false)
    let mutable state = LovenseQrPresentationState.initial
    let mutable window: LovenseQrWindow option = None

    do
        let thread =
            Thread(ThreadStart(fun () ->
                try
                    let app = Application()
                    let qrWindow = LovenseQrWindow()

                    qrWindow.Closed.Add(fun _ ->
                        lock gate (fun () -> state <- LovenseQrPresentationState.manualClose state))

                    lock gate (fun () -> window <- Some qrWindow)
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

    member _.CurrentState =
        lock gate (fun () -> state)

    member _.Update(qrInfo: StandardApiQrCodeInfo option) =
        lock gate (fun () ->
            state <-
                match qrInfo with
                | Some qr -> LovenseQrPresentationState.withQrCode qr state
                | None -> state)

        lock gate (fun () -> window)
        |> Option.iter (fun qrWindow -> qrWindow.Update(qrInfo))

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () -> state <- LovenseQrPresentationState.manualClose state)

            lock gate (fun () -> window)
            |> Option.iter (fun qrWindow ->
                if qrWindow.Dispatcher.CheckAccess() then
                    qrWindow.Close()
                else
                    qrWindow.Dispatcher.Invoke(Action(fun () -> qrWindow.Close())) |> ignore)
