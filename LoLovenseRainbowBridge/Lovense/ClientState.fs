namespace LoLovenseRainbowBridge.Lovense

open System
open System.Globalization
open SocketIOClient
open LoLovenseRainbowBridge

type CachedSessionValue<'T> =
    {
        Value: 'T
        AcquiredAt: DateTimeOffset
    }

type LovenseSessionState =
    {
        Socket: SocketIO option
        SocketInfo: CachedSessionValue<SocketUrlInfo> option
        StandardQrCode: CachedSessionValue<StandardApiQrCodeInfo> option
        QrCodeLogged: bool
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        GeneratedAuthToken: CachedSessionValue<string> option
        LatestDeviceInfo: LovenseDeviceInfo option
    }

type SessionRetryPolicy =
    | DoNotRetry
    | RetrySocketUrlOnly
    | RetryAuthAndSocketUrl

module ClientState =
    let invariantFloat (value: float) =
        value.ToString(CultureInfo.InvariantCulture)

    let escapeJsonString (value: string) =
        value.Replace("\\", "\\\\").Replace("\"", "\\\"")

    let applyDeviceInfo (deviceInfo: LovenseDeviceInfo) (session: LovenseSessionState) =
        {
            session with
                LatestDeviceInfo = Some deviceInfo
                CapabilityProfiles = deviceInfo.CapabilityProfiles
                SupportedFunctions = deviceInfo.SupportedFunctions |> Option.orElse session.SupportedFunctions
        }

    let onDeviceInfo (deviceInfo: LovenseDeviceInfo) (session: LovenseSessionState) (nextLocalCapabilityRefreshAt: DateTimeOffset byref) =
        let updatedSession = applyDeviceInfo deviceInfo session
        nextLocalCapabilityRefreshAt <- DateTimeOffset.MinValue
        updatedSession

    let onQrCode (session: LovenseSessionState) =
        if not session.QrCodeLogged then
            { session with QrCodeLogged = true }
            |> (fun updated ->
                printfn "Lovense QR code event received. See track.log or lovense.log if raw logging is enabled."
                updated)
        else
            session

    let invalidateSocketUrl reason (session: LovenseSessionState) (logger: StructuredSessionLogger) =
        logger.Warn(
            "lovense.socket_url.cache_invalidated",
            "Lovense cached Socket.IO URL invalidated before retry.",
            {| reason = reason |}
        )

        {
            session with
                SocketInfo = None
        }

    let invalidateAuthAndSocketUrl reason (session: LovenseSessionState) (logger: StructuredSessionLogger) =
        logger.Warn(
            "lovense.session.cache_invalidated",
            "Lovense cached auth token and Socket.IO URL invalidated before retry.",
            {| reason = reason |}
        )

        {
            session with
                GeneratedAuthToken = None
                SocketInfo = None
        }

    let containsAny (needles: string list) (value: string) =
        let haystack = if isNull value then "" else value.ToUpperInvariant()
        needles |> List.exists (fun needle -> haystack.Contains(needle.ToUpperInvariant(), StringComparison.Ordinal))

    let retryPolicyFor error =
        match error with
        | MissingDeveloperCredentials _ ->
            DoNotRetry
        | SocketUrlRejected(_, _, message)
            when containsAny [ "AUTH"; "TOKEN"; "EXPIRED"; "INVALID"; "UNAUTHORIZED" ] message ->
            RetryAuthAndSocketUrl
        | SocketUrlRejected _ ->
            DoNotRetry
        | SocketUrlRequestFailed _ ->
            RetrySocketUrlOnly
        | SocketConnectFailed _ ->
            RetrySocketUrlOnly
        | SocketDisconnected _ ->
            RetrySocketUrlOnly
        | UnexpectedConnectionError(_, errorType)
            when containsAny [ "AUTH"; "TOKEN"; "UNAUTHORIZED" ] errorType ->
            RetryAuthAndSocketUrl
        | UnexpectedConnectionError _ ->
            RetrySocketUrlOnly
