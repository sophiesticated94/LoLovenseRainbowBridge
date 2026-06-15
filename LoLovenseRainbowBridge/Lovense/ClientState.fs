namespace LoLovenseRainbowBridge.Lovense

open System
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
        SocketConnected: bool
        SocketReadyAt: DateTimeOffset option
        LastConnectAttemptAt: DateTimeOffset option
        NextConnectRetryAt: DateTimeOffset option
        LocalCommandCooldownUntil: DateTimeOffset option
        ServerCommandCooldownUntil: DateTimeOffset option
        StandardQrCode: CachedSessionValue<StandardApiQrCodeInfo> option
        QrCodeLogged: bool
        SupportedFunctions: Set<string> option
        CapabilityProfiles: LovenseToyCapabilityProfile list
        GeneratedAuthToken: CachedSessionValue<string> option
        LatestDeviceInfo: LovenseDeviceInfo option
    }

module ClientState =
    let initialLovenseSessionState =
        {
            Socket = None
            SocketInfo = None
            SocketConnected = false
            SocketReadyAt = None
            LastConnectAttemptAt = None
            NextConnectRetryAt = None
            LocalCommandCooldownUntil = None
            ServerCommandCooldownUntil = None
            StandardQrCode = None
            QrCodeLogged = false
            SupportedFunctions = None
            CapabilityProfiles = []
            GeneratedAuthToken = None
            LatestDeviceInfo = None
        }

    let toSnapshot (session: LovenseSessionState) =
        {
            SocketInfo = session.SocketInfo |> Option.map (fun cached -> cached.Value)
            SocketConnected = session.SocketConnected
            SocketReadyAt = session.SocketReadyAt
            LastConnectAttemptAt = session.LastConnectAttemptAt
            NextConnectRetryAt = session.NextConnectRetryAt
            LocalCommandCooldownUntil = session.LocalCommandCooldownUntil
            ServerCommandCooldownUntil = session.ServerCommandCooldownUntil
            StandardQrCode = session.StandardQrCode |> Option.map (fun cached -> cached.Value)
            QrCodeLogged = session.QrCodeLogged
            SupportedFunctions = session.SupportedFunctions
            CapabilityProfiles = session.CapabilityProfiles
            LatestDeviceInfo = session.LatestDeviceInfo
        }

    type LovenseSessionStore(initialState: LovenseSessionState, publishSnapshot: LovenseSessionSnapshot -> unit) =
        let gate = obj()
        let mutable state = initialState

        new(publishSnapshot: LovenseSessionSnapshot -> unit) = LovenseSessionStore(initialLovenseSessionState, publishSnapshot)

        member _.Read() =
            lock gate (fun () -> state)

        member _.Update(update: LovenseSessionState -> LovenseSessionState) =
            let updated =
                lock gate (fun () ->
                    state <- update state
                    state)

            publishSnapshot (toSnapshot updated)
            updated

        member _.Set(updated: LovenseSessionState) =
            let current =
                lock gate (fun () ->
                    state <- updated
                    state)

            publishSnapshot (toSnapshot current)
            current

        member _.Publish() =
            lock gate (fun () -> state)
            |> toSnapshot
            |> publishSnapshot

    type SessionRetryPolicy =
        | DoNotRetry
        | RetrySocketUrlOnly
        | RetryAuthAndSocketUrl

    let private profileKey (profile: LovenseToyCapabilityProfile) =
        profile.ToyId
        |> Option.orElse profile.Name
        |> Option.orElse profile.ToyType
        |> Option.orElse profile.Nickname
        |> Option.defaultValue ""

    let private mergeProfiles (existing: LovenseToyCapabilityProfile list) (incoming: LovenseToyCapabilityProfile list) =
        let mergeOne (profile: LovenseToyCapabilityProfile) (state: Map<string, LovenseToyCapabilityProfile>) =
            let key = profileKey profile

            match Map.tryFind key state with
            | None -> Map.add key profile state
            | Some current ->
                Map.add
                    key
                    {
                        current with
                            ToyId = current.ToyId |> Option.orElse profile.ToyId
                            Name = current.Name |> Option.orElse profile.Name
                            ToyType = current.ToyType |> Option.orElse profile.ToyType
                            Nickname = current.Nickname |> Option.orElse profile.Nickname
                            Battery = profile.Battery |> Option.orElse current.Battery
                            Connected = profile.Connected |> Option.orElse current.Connected
                            ExplicitFunctions = Set.union current.ExplicitFunctions profile.ExplicitFunctions
                            InferredFunctions = Set.union current.InferredFunctions profile.InferredFunctions
                            SupportedFunctions = Set.union current.SupportedFunctions profile.SupportedFunctions
                            StereoVibrationSupported = current.StereoVibrationSupported || profile.StereoVibrationSupported
                            CapabilitySource =
                                match current.CapabilitySource, profile.CapabilitySource with
                                | Explicit, _ -> Explicit
                                | _, Explicit -> Explicit
                                | Inferred, _ -> Inferred
                                | _, Inferred -> Inferred
                                | Forced, _ -> Forced
                                | _, Forced -> Forced
                                | _ -> current.CapabilitySource
                            Notes =
                                current.Notes
                                |> List.append profile.Notes
                                |> List.distinct
                    }
                    state

        (existing @ incoming)
        |> List.fold (fun state profile -> mergeOne profile state) Map.empty
        |> Map.toList
        |> List.map snd

    let mergeDeviceInfo (existing: LovenseDeviceInfo option) (incoming: LovenseDeviceInfo) =
        let baseline =
            match existing with
            | Some value -> value
            | None -> incoming

        let mergedProfiles = mergeProfiles baseline.CapabilityProfiles incoming.CapabilityProfiles
        let mergedSupportedFunctions =
            match baseline.SupportedFunctions, incoming.SupportedFunctions with
            | Some left, Some right -> Some(Set.union left right)
            | Some functions, None
            | None, Some functions -> Some functions
            | None, None -> None

        {
            baseline with
                ToyList = if incoming.ToyList.IsEmpty then baseline.ToyList else incoming.ToyList
                SupportedFunctions = mergedSupportedFunctions
                CapabilityProfiles = mergedProfiles
                Domain = incoming.Domain |> Option.orElse baseline.Domain
                HttpsPort = incoming.HttpsPort |> Option.orElse baseline.HttpsPort
                HttpPort = incoming.HttpPort |> Option.orElse baseline.HttpPort
                WssPort = incoming.WssPort |> Option.orElse baseline.WssPort
        }

    let applyDeviceInfo (deviceInfo: LovenseDeviceInfo) (session: LovenseSessionState) =
        let mergedDeviceInfo = mergeDeviceInfo session.LatestDeviceInfo deviceInfo

        {
            session with
                LatestDeviceInfo = Some mergedDeviceInfo
                CapabilityProfiles = mergedDeviceInfo.CapabilityProfiles
                SupportedFunctions = mergedDeviceInfo.SupportedFunctions |> Option.orElse session.SupportedFunctions
        }

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
                SocketConnected = false
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
                SocketConnected = false
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
