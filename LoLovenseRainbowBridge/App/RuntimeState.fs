namespace LoLovenseRainbowBridge.App

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge
open LoLovenseRainbowBridge.Bridge.Scoring
open LoLovenseRainbowBridge.Lovense
open LoLovenseRainbowBridge.LeagueOfLegends

module RuntimeState =

    type PositionRotationState =
        {
            LastCaptureTime: DateTimeOffset option
            DetectionFailures: int
        }

    type LeagueRuleCacheState =
        {
            [<field: CalculatorVariable(Name = "Kills")>]
            Kills: float
            [<field: CalculatorVariable(Name = "Deaths")>]
            Deaths: float
            [<field: CalculatorVariable(Name = "Assists")>]
            Assists: float
            [<field: CalculatorVariable(Name = "CreepScore")>]
            CreepScore: float
            [<field: CalculatorVariable(Name = "CS")>]
            CS: float
            [<field: CalculatorVariable(Name = "WardScore")>]
            WardScore: float
            [<field: CalculatorVariable(Name = "Level")>]
            Level: float
            [<field: CalculatorVariable(Name = "CurrentHealth")>]
            CurrentHealth: float
            [<field: CalculatorVariable(Name = "MaxHealth")>]
            MaxHealth: float
            [<field: CalculatorVariable(Name = "HealthPercent")>]
            HealthPercent: float
            [<field: CalculatorVariable(Name = "MissingHealth")>]
            MissingHealth: float
            [<field: CalculatorVariable(Name = "GameTime")>]
            GameTime: float
            [<field: CalculatorVariable(Name = "RawPerformanceScore")>]
            RawPerformanceScore: float
            [<field: CalculatorVariable(Name = "NormalizedScore")>]
            NormalizedScore: float
            [<field: CalculatorVariable(Name = "PerformanceScore")>]
            PerformanceScore: float
            [<field: CalculatorVariable(Name = "DeathPenalty")>]
            DeathPenalty: float
            [<field: CalculatorVariable(Name = "LiveHealthMultiplier")>]
            LiveHealthMultiplier: float
            [<field: CalculatorVariable(Name = "HealthPressureMultiplier")>]
            HealthPressureMultiplier: float
            [<field: CalculatorVariable(Name = "ActiveKill")>]
            ActiveKill: float
            [<field: CalculatorVariable(Name = "ActiveKillCount")>]
            ActiveKillCount: float
            [<field: CalculatorVariable(Name = "ActiveDeath")>]
            ActiveDeath: float
            [<field: CalculatorVariable(Name = "ActiveDeathCount")>]
            ActiveDeathCount: float
            [<field: CalculatorVariable(Name = "ActiveMultikill")>]
            ActiveMultikill: float
            [<field: CalculatorVariable(Name = "MultikillCount")>]
            MultikillCount: float
            [<field: CalculatorVariable(Name = "TotalMultikillCount")>]
            TotalMultikillCount: float
            [<field: CalculatorVariable(Name = "ObjectiveWaveValue")>]
            ObjectiveWaveValue: float
            [<field: CalculatorVariable(Name = "TeamfightKillCount")>]
            TeamfightKillCount: float
            [<field: CalculatorVariable(Name = "TeamfightBurstValue")>]
            TeamfightBurstValue: float
            [<field: CalculatorVariable(Name = "AceBurstValue")>]
            AceBurstValue: float
            [<field: CalculatorVariable(Name = "HeartbeatAmplitude")>]
            HeartbeatAmplitude: float
            [<field: CalculatorVariable(Name = "LowHealthHeartbeatThreshold")>]
            LowHealthHeartbeatThreshold: float
            [<field: CalculatorVariable(Name = "CriticalHealthHeartbeatThreshold")>]
            CriticalHealthHeartbeatThreshold: float
            [<field: CalculatorVariable(Name = "HeartbeatPulseMaxAmplitude")>]
            HeartbeatPulseMaxAmplitude: float
            [<field: CalculatorVariable(Name = "HeartbeatPulseCycleSec")>]
            HeartbeatPulseCycleSec: float
            [<field: CalculatorVariable(Name = "HeartbeatPulseStartPhase")>]
            HeartbeatPulseStartPhase: float
            [<field: CalculatorVariable(Name = "HeartbeatPulsePeakPhase")>]
            HeartbeatPulsePeakPhase: float
            [<field: CalculatorVariable(Name = "HeartbeatPulseEndPhase")>]
            HeartbeatPulseEndPhase: float
            [<field: CalculatorVariable(Name = "LaningTextureValue")>]
            LaningTextureValue: float
            [<field: CalculatorVariable(Name = "JungleTensionValue")>]
            JungleTensionValue: float
        }

    type LeagueCacheState =
        {
            Snapshot: BridgeSnapshot option
            Calculated: LeagueRuleCacheState
            DataAcquired: bool
            FailureAttemptsSinceSuccess: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type RuntimeContextCacheState =
        {
            [<field: CalculatorVariable(Name = "LolDataAcquired")>]
            LolDataAcquired: bool
            [<field: CalculatorVariable(Name = "OcrDataAcquired")>]
            OcrDataAcquired: bool
            [<field: CalculatorVariable(Name = "LovenseDataAcquired")>]
            LovenseDataAcquired: bool
            [<field: CalculatorVariable(Name = "ToyDataAcquired")>]
            ToyDataAcquired: bool
            [<field: CalculatorVariable(Name = "LolUnavailableElapsedMs")>]
            LolUnavailableElapsedMs: int64
            [<field: CalculatorVariable(Name = "OcrUnavailableElapsedMs")>]
            OcrUnavailableElapsedMs: int64
            [<field: CalculatorVariable(Name = "LovenseUnavailableElapsedMs")>]
            LovenseUnavailableElapsedMs: int64
            [<field: CalculatorVariable(Name = "ToyUnavailableElapsedMs")>]
            ToyUnavailableElapsedMs: int64
            [<field: CalculatorVariable(Name = "LolFailureAttemptsSinceSuccess")>]
            LolFailureAttemptsSinceSuccess: int
            [<field: CalculatorVariable(Name = "OcrFailureAttemptsSinceSuccess")>]
            OcrFailureAttemptsSinceSuccess: int
            [<field: CalculatorVariable(Name = "LovenseFailureAttemptsSinceSuccess")>]
            LovenseFailureAttemptsSinceSuccess: int
            [<field: CalculatorVariable(Name = "ToyFailureAttemptsSinceSuccess")>]
            ToyFailureAttemptsSinceSuccess: int
        }

    type OcrPositionProjection =
        {
            PositionLeftWeight: float
            PositionRightWeight: float
            PositionIsCenter: bool
            PositionIsTopLeft: bool
            PositionIsTopRight: bool
            PositionIsBottomLeft: bool
            PositionIsBottomRight: bool
            PositionIsLeft: bool
            PositionIsRight: bool
            PositionZoneTopLane: bool
            PositionZoneMidLane: bool
            PositionZoneBottomLane: bool
            PositionZoneJungle: bool
            PositionZoneRiver: bool
            PositionZoneBase: bool
            PositionZoneUnknown: bool
        }

    type OcrCacheState =
        {
            Position: Lovense.LovensePlanningPosition option
            [<field: CalculatorVariable(Name = "PositionAvailable")>]
            PositionAvailable: bool
            [<field: CalculatorVariable(Name = "PositionX")>]
            PositionX: float
            [<field: CalculatorVariable(Name = "PositionY")>]
            PositionY: float
            [<field: CalculatorVariable(Name = "PositionConfidence")>]
            PositionConfidence: float
            [<field: CalculatorVariable(Name = "PositionLeftWeight")>]
            PositionLeftWeight: float
            [<field: CalculatorVariable(Name = "PositionRightWeight")>]
            PositionRightWeight: float
            [<field: CalculatorVariable(Name = "PositionIsCenter")>]
            PositionIsCenter: bool
            [<field: CalculatorVariable(Name = "PositionIsTopLeft")>]
            PositionIsTopLeft: bool
            [<field: CalculatorVariable(Name = "PositionIsTopRight")>]
            PositionIsTopRight: bool
            [<field: CalculatorVariable(Name = "PositionIsBottomLeft")>]
            PositionIsBottomLeft: bool
            [<field: CalculatorVariable(Name = "PositionIsBottomRight")>]
            PositionIsBottomRight: bool
            [<field: CalculatorVariable(Name = "PositionIsLeft")>]
            PositionIsLeft: bool
            [<field: CalculatorVariable(Name = "PositionIsRight")>]
            PositionIsRight: bool
            [<field: CalculatorVariable(Name = "PositionZoneTopLane")>]
            PositionZoneTopLane: bool
            [<field: CalculatorVariable(Name = "PositionZoneMidLane")>]
            PositionZoneMidLane: bool
            [<field: CalculatorVariable(Name = "PositionZoneBottomLane")>]
            PositionZoneBottomLane: bool
            [<field: CalculatorVariable(Name = "PositionZoneJungle")>]
            PositionZoneJungle: bool
            [<field: CalculatorVariable(Name = "PositionZoneRiver")>]
            PositionZoneRiver: bool
            [<field: CalculatorVariable(Name = "PositionZoneBase")>]
            PositionZoneBase: bool
            [<field: CalculatorVariable(Name = "PositionZoneUnknown")>]
            PositionZoneUnknown: bool
            DataAcquired: bool
            DetectionFailures: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type LovenseCacheState =
        {
            [<field: CalculatorVariable(Name = "LoopIteration")>]
            LoopIteration: float
            [<field: CalculatorVariable(Name = "LoopIterationWithinSecond")>]
            LoopIterationWithinSecond: float
            [<field: CalculatorVariable(Name = "LoopIterationsPerSecond")>]
            LoopIterationsPerSecond: float
            [<field: CalculatorVariable(Name = "LoopTimeSec")>]
            LoopTimeSec: float
            [<field: CalculatorVariable(Name = "RuntimePollMs")>]
            RuntimePollMs: float
            DataAcquired: bool
            Connected: bool
            FailureAttemptsSinceSuccess: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type ToyCacheState =
        {
            DeviceInfo: LovenseDeviceInfo option
            DataAcquired: bool
            FailureAttemptsSinceSuccess: int
            LastSuccessfulAt: DateTimeOffset option
            UnavailableSince: DateTimeOffset option
            LastError: string option
            Version: int64
        }

    type RuntimeCacheSnapshot =
        {
            RuntimeContext: RuntimeContextCacheState
            CommandBuilder: CommandBuilderCacheState
            League: LeagueCacheState
            Ocr: OcrCacheState
            Lovense: LovenseCacheState
            Toys: ToyCacheState
        }

    let initialPositionRotationState =
        {
            LastCaptureTime = None
            DetectionFailures = 0
        }

    let private initialLeagueCalculated =
        {
            Kills = 0.0
            Deaths = 0.0
            Assists = 0.0
            CreepScore = 0.0
            CS = 0.0
            WardScore = 0.0
            Level = 0.0
            CurrentHealth = 0.0
            MaxHealth = 0.0
            HealthPercent = 1.0
            MissingHealth = 0.0
            GameTime = 0.0
            RawPerformanceScore = 0.0
            NormalizedScore = 0.0
            PerformanceScore = 0.0
            DeathPenalty = 0.0
            LiveHealthMultiplier = 1.0
            HealthPressureMultiplier = 1.0
            ActiveKill = 0.0
            ActiveKillCount = 0.0
            ActiveDeath = 0.0
            ActiveDeathCount = 0.0
            ActiveMultikill = 0.0
            MultikillCount = 0.0
            TotalMultikillCount = 0.0
            ObjectiveWaveValue = 0.0
            TeamfightKillCount = 0.0
            TeamfightBurstValue = 0.0
            AceBurstValue = 0.0
            HeartbeatAmplitude = 0.0
            LowHealthHeartbeatThreshold = 0.0
            CriticalHealthHeartbeatThreshold = 0.0
            HeartbeatPulseMaxAmplitude = 0.0
            HeartbeatPulseCycleSec = 0.0
            HeartbeatPulseStartPhase = 0.0
            HeartbeatPulsePeakPhase = 0.0
            HeartbeatPulseEndPhase = 0.0
            LaningTextureValue = 0.0
            JungleTensionValue = 0.0
        }

    let private initialLeague =
        {
            Snapshot = None
            Calculated = initialLeagueCalculated
            DataAcquired = false
            FailureAttemptsSinceSuccess = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    let private initialOcr =
        {
            Position = None
            PositionAvailable = false
            PositionX = 0.0
            PositionY = 0.0
            PositionConfidence = 0.0
            PositionLeftWeight = 0.0
            PositionRightWeight = 0.0
            PositionIsCenter = false
            PositionIsTopLeft = false
            PositionIsTopRight = false
            PositionIsBottomLeft = false
            PositionIsBottomRight = false
            PositionIsLeft = false
            PositionIsRight = false
            PositionZoneTopLane = false
            PositionZoneMidLane = false
            PositionZoneBottomLane = false
            PositionZoneJungle = false
            PositionZoneRiver = false
            PositionZoneBase = false
            PositionZoneUnknown = false
            DataAcquired = false
            DetectionFailures = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    let private initialRuntimeContext =
        {
            LolDataAcquired = false
            OcrDataAcquired = false
            LovenseDataAcquired = false
            ToyDataAcquired = false
            LolUnavailableElapsedMs = 0L
            OcrUnavailableElapsedMs = 0L
            LovenseUnavailableElapsedMs = 0L
            ToyUnavailableElapsedMs = 0L
            LolFailureAttemptsSinceSuccess = 0
            OcrFailureAttemptsSinceSuccess = 0
            LovenseFailureAttemptsSinceSuccess = 0
            ToyFailureAttemptsSinceSuccess = 0
        }

    let private initialCommandBuilder =
        {
            CurrentIncarnationId = 1
            PreviousIncarnationBase = 0.0
            CurrentBase = 0.0
            MaxBaseThisIncarnation = 0.0
            MinBaseThisIncarnation = 0.0
            LovenseIteration = 0L
            LastFunctionState = LovenseActionCodec.emptyState
            LastActionString = None
        }

    let private initialLovense =
        {
            LoopIteration = 0.0
            LoopIterationWithinSecond = 0.0
            LoopIterationsPerSecond = 0.0
            LoopTimeSec = 0.0
            RuntimePollMs = 0.0
            DataAcquired = false
            Connected = false
            FailureAttemptsSinceSuccess = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    let private initialToys =
        {
            DeviceInfo = None
            DataAcquired = false
            FailureAttemptsSinceSuccess = 0
            LastSuccessfulAt = None
            UnavailableSince = Some DateTimeOffset.UtcNow
            LastError = None
            Version = 0L
        }

    type RuntimeStateCache() =
        let gate = obj()
        let mutable snapshot =
            {
                RuntimeContext = initialRuntimeContext
                CommandBuilder = initialCommandBuilder
                League = initialLeague
                Ocr = initialOcr
                Lovense = initialLovense
                Toys = initialToys
            }

        let syncRuntimeContext now =
            let elapsedMs (since: DateTimeOffset option) =
                since
                |> Option.map (fun value -> max 0L (int64 (now - value).TotalMilliseconds))
                |> Option.defaultValue 0L

            {
                LolDataAcquired = snapshot.League.DataAcquired
                OcrDataAcquired = snapshot.Ocr.DataAcquired
                LovenseDataAcquired = snapshot.Lovense.DataAcquired
                ToyDataAcquired = snapshot.Toys.DataAcquired
                LolUnavailableElapsedMs = elapsedMs snapshot.League.UnavailableSince
                OcrUnavailableElapsedMs = elapsedMs snapshot.Ocr.UnavailableSince
                LovenseUnavailableElapsedMs = elapsedMs snapshot.Lovense.UnavailableSince
                ToyUnavailableElapsedMs = elapsedMs snapshot.Toys.UnavailableSince
                LolFailureAttemptsSinceSuccess = snapshot.League.FailureAttemptsSinceSuccess
                OcrFailureAttemptsSinceSuccess = snapshot.Ocr.DetectionFailures
                LovenseFailureAttemptsSinceSuccess = snapshot.Lovense.FailureAttemptsSinceSuccess
                ToyFailureAttemptsSinceSuccess = snapshot.Toys.FailureAttemptsSinceSuccess
            }

        member _.Read() =
            lock gate (fun () -> snapshot)

        interface IAppCache with
            member _.Read() =
                lock gate (fun () -> box snapshot)

        member _.UpdateCommandBuilder(builder: CommandBuilderCacheState) =
            lock gate (fun () ->
                snapshot <-
                    {
                        snapshot with
                            CommandBuilder =
                                {
                                    CurrentIncarnationId = builder.CurrentIncarnationId
                                    PreviousIncarnationBase = builder.PreviousIncarnationBase
                                    CurrentBase = builder.CurrentBase
                                    MaxBaseThisIncarnation = builder.MaxBaseThisIncarnation
                                    MinBaseThisIncarnation = builder.MinBaseThisIncarnation
                                    LovenseIteration = builder.LovenseIteration
                                    LastFunctionState = builder.LastFunctionState
                                    LastActionString = builder.LastActionString
                                }
                    })

        member _.UpdateLeagueSuccess(leagueSnapshot, leagueRules: LeagueRuleCacheState) =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            League =
                                {
                                    snapshot.League with
                                        Snapshot = Some leagueSnapshot
                                        Calculated = leagueRules
                                        DataAcquired = true
                                        FailureAttemptsSinceSuccess = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.League.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateLeagueFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            League =
                                {
                                    snapshot.League with
                                        DataAcquired = false
                                        FailureAttemptsSinceSuccess = snapshot.League.FailureAttemptsSinceSuccess + 1
                                        UnavailableSince = snapshot.League.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.League.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member this.UpdateOcrSuccess(position, projection: OcrPositionProjection) =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        Position = Some position
                                        PositionAvailable = true
                                        PositionX = position.NormalizedX
                                        PositionY = position.NormalizedY
                                        PositionConfidence = position.Confidence
                                        PositionLeftWeight = projection.PositionLeftWeight
                                        PositionRightWeight = projection.PositionRightWeight
                                        PositionIsCenter = projection.PositionIsCenter
                                        PositionIsTopLeft = projection.PositionIsTopLeft
                                        PositionIsTopRight = projection.PositionIsTopRight
                                        PositionIsBottomLeft = projection.PositionIsBottomLeft
                                        PositionIsBottomRight = projection.PositionIsBottomRight
                                        PositionIsLeft = projection.PositionIsLeft
                                        PositionIsRight = projection.PositionIsRight
                                        PositionZoneTopLane = projection.PositionZoneTopLane
                                        PositionZoneMidLane = projection.PositionZoneMidLane
                                        PositionZoneBottomLane = projection.PositionZoneBottomLane
                                        PositionZoneJungle = projection.PositionZoneJungle
                                        PositionZoneRiver = projection.PositionZoneRiver
                                        PositionZoneBase = projection.PositionZoneBase
                                        PositionZoneUnknown = projection.PositionZoneUnknown
                                        DataAcquired = true
                                        DetectionFailures = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.Ocr.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member this.UpdateOcrSuccess(position: Lovense.LovensePlanningPosition) =
            let projection =
                let quadrant = position.Quadrant
                let zone = position.Zone

                let leftWeight, rightWeight =
                    match quadrant |> Option.ofObj |> Option.defaultValue "" with
                    | value when String.Equals(value, "Center", StringComparison.OrdinalIgnoreCase) -> 1.0, 1.0
                    | value when String.Equals(value, "TopLeft", StringComparison.OrdinalIgnoreCase) -> 1.35, 0.35
                    | value when String.Equals(value, "TopRight", StringComparison.OrdinalIgnoreCase) -> 0.35, 1.35
                    | value when String.Equals(value, "BottomLeft", StringComparison.OrdinalIgnoreCase) -> 0.0, 0.0
                    | value when String.Equals(value, "BottomRight", StringComparison.OrdinalIgnoreCase) -> 0.55, 1.05
                    | value when String.Equals(value, "Left", StringComparison.OrdinalIgnoreCase) -> 1.15, 0.65
                    | value when String.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) -> 0.65, 1.15
                    | _ ->
                        let left, right = CapabilityResolver.stereoWeightsFromNormalizedX 100 position.NormalizedX
                        float left / 100.0, float right / 100.0

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

            this.UpdateOcrSuccess(position, projection)

        member _.UpdateOcrFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        Position = None
                                        PositionAvailable = false
                                        PositionX = 0.0
                                        PositionY = 0.0
                                        PositionConfidence = 0.0
                                        PositionLeftWeight = 0.0
                                        PositionRightWeight = 0.0
                                        PositionIsCenter = false
                                        PositionIsTopLeft = false
                                        PositionIsTopRight = false
                                        PositionIsBottomLeft = false
                                        PositionIsBottomRight = false
                                        PositionIsLeft = false
                                        PositionIsRight = false
                                        PositionZoneTopLane = false
                                        PositionZoneMidLane = false
                                        PositionZoneBottomLane = false
                                        PositionZoneJungle = false
                                        PositionZoneRiver = false
                                        PositionZoneBase = false
                                        PositionZoneUnknown = false
                                        DataAcquired = false
                                        DetectionFailures = snapshot.Ocr.DetectionFailures + 1
                                        UnavailableSince = snapshot.Ocr.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.Ocr.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateOcrDisabled() =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Ocr =
                                {
                                    snapshot.Ocr with
                                        Position = None
                                        PositionAvailable = false
                                        PositionX = 0.0
                                        PositionY = 0.0
                                        PositionConfidence = 0.0
                                        PositionLeftWeight = 0.0
                                        PositionRightWeight = 0.0
                                        PositionIsCenter = false
                                        PositionIsTopLeft = false
                                        PositionIsTopRight = false
                                        PositionIsBottomLeft = false
                                        PositionIsBottomRight = false
                                        PositionIsLeft = false
                                        PositionIsRight = false
                                        PositionZoneTopLane = false
                                        PositionZoneMidLane = false
                                        PositionZoneBottomLane = false
                                        PositionZoneJungle = false
                                        PositionZoneRiver = false
                                        PositionZoneBase = false
                                        PositionZoneUnknown = false
                                        DataAcquired = false
                                        UnavailableSince = snapshot.Ocr.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some "Position-based rotation is disabled."
                                        Version = snapshot.Ocr.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateLovenseSuccess connected =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Lovense =
                                {
                                    snapshot.Lovense with
                                        DataAcquired = true
                                        Connected = connected
                                        FailureAttemptsSinceSuccess = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.Lovense.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateLovenseFailure error =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Lovense =
                                {
                                    snapshot.Lovense with
                                        DataAcquired = false
                                        Connected = false
                                        FailureAttemptsSinceSuccess = snapshot.Lovense.FailureAttemptsSinceSuccess + 1
                                        UnavailableSince = snapshot.Lovense.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.Lovense.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateToySuccess(deviceInfo: LovenseDeviceInfo) =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Toys =
                                {
                                    snapshot.Toys with
                                        DeviceInfo = Some deviceInfo
                                        DataAcquired = true
                                        FailureAttemptsSinceSuccess = 0
                                        LastSuccessfulAt = Some now
                                        UnavailableSince = None
                                        LastError = None
                                        Version = snapshot.Toys.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateToyFailure(error: string) =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Toys =
                                {
                                    snapshot.Toys with
                                        DataAcquired = false
                                        FailureAttemptsSinceSuccess = snapshot.Toys.FailureAttemptsSinceSuccess + 1
                                        UnavailableSince = snapshot.Toys.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some error
                                        Version = snapshot.Toys.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateToyDisabled() =
            lock gate (fun () ->
                let now = DateTimeOffset.UtcNow
                snapshot <-
                    {
                        snapshot with
                            Toys =
                                {
                                    snapshot.Toys with
                                        DataAcquired = false
                                        UnavailableSince = snapshot.Toys.UnavailableSince |> Option.orElse (Some now)
                                        LastError = Some "Lovense toy cache refresh is disabled."
                                        Version = snapshot.Toys.Version + 1L
                                }
                    }
                snapshot <- { snapshot with RuntimeContext = syncRuntimeContext now })

        member _.UpdateLovenseClock(loopIteration: int64, now: DateTimeOffset, runtimePollMs: int) =
            lock gate (fun () ->
                let safePollMs = max 1 runtimePollMs
                let iterationsPerSecond = max 1.0 (Math.Round(1000.0 / float safePollMs))
                let loopIterationFloat = float loopIteration
                let loopIterationWithinSecond = float ((int64 loopIterationFloat) % int64 iterationsPerSecond)
                let loopTimeSec = loopIterationFloat * float safePollMs / 1000.0

                snapshot <-
                    {
                        snapshot with
                            Lovense =
                                {
                                    snapshot.Lovense with
                                        LoopIteration = loopIterationFloat
                                        LoopIterationWithinSecond = loopIterationWithinSecond
                                        LoopIterationsPerSecond = iterationsPerSecond
                                        LoopTimeSec = loopTimeSec
                                        RuntimePollMs = float safePollMs
                                }
                    })

    let planningQuadrant normalizedX normalizedY =
        if normalizedX >= 0.42 && normalizedX <= 0.58 && normalizedY >= 0.42 && normalizedY <= 0.58 then "Center"
        elif normalizedX < 0.5 && normalizedY < 0.5 then "TopLeft"
        elif normalizedX >= 0.5 && normalizedY < 0.5 then "TopRight"
        elif normalizedX < 0.5 && normalizedY >= 0.5 then "BottomLeft"
        elif normalizedX >= 0.5 && normalizedY >= 0.5 then "BottomRight"
        elif normalizedX < 0.5 then "Left"
        else "Right"

    let leagueErrorSummary error : obj =
        match error with
        | LeagueFetchError.ConnectionFailed(url, message) -> box {| kind = "ConnectionFailed"; url = url; message = message |}
        | LeagueFetchError.HttpFailure(url, statusCode, body) -> box {| kind = "HttpFailure"; url = url; statusCode = statusCode; bodyLength = body.Length |}
        | LeagueFetchError.InvalidJson(url, message, rawText) -> box {| kind = "InvalidJson"; url = url; message = message; rawLength = rawText.Length |}
        | LeagueFetchError.EmptyJson(url, rawText) -> box {| kind = "EmptyJson"; url = url; rawLength = rawText.Length |}
        | LeagueFetchError.UnexpectedFetchError(url, message, errorType) -> box {| kind = "UnexpectedFetchError"; url = url; message = message; errorType = errorType |}

    let leagueErrorMessage error =
        match error with
        | LeagueFetchError.ConnectionFailed(_, message) -> message
        | LeagueFetchError.HttpFailure(_, statusCode, _) -> $"HTTP {statusCode}"
        | LeagueFetchError.InvalidJson(_, message, _) -> $"Invalid JSON: {message}"
        | LeagueFetchError.EmptyJson _ -> "Empty JSON"
        | LeagueFetchError.UnexpectedFetchError(_, message, _) -> message

    let lovenseErrorMessage error =
        match error with
        | LovenseCommandError.NotConnected connectionError -> $"Not connected: {connectionError}"
        | LovenseCommandError.CommandEmitFailed(_, message) -> message
        | LovenseCommandError.CommandRejected(_, message) -> message
        | LovenseCommandError.CommandTimeout(_, timeoutMs) -> $"Command timed out after {timeoutMs}ms"
        | LovenseCommandError.UnexpectedCommandError(_, message, _) -> message

    let lovenseErrorSummary error : obj =
        match error with
        | LovenseCommandError.NotConnected connectionError -> box {| kind = "NotConnected"; connectionError = string connectionError |}
        | LovenseCommandError.CommandEmitFailed(eventName, message) -> box {| kind = "CommandEmitFailed"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandRejected(eventName, message) -> box {| kind = "CommandRejected"; eventName = eventName; message = message |}
        | LovenseCommandError.CommandTimeout(eventName, timeoutMs) -> box {| kind = "CommandTimeout"; eventName = eventName; timeoutMs = timeoutMs |}
        | LovenseCommandError.UnexpectedCommandError(eventName, message, errorType) -> box {| kind = "UnexpectedCommandError"; eventName = eventName; message = message; errorType = errorType |}

    let neutralPlayer : BridgePlayer =
        {
            Id = "unavailable"
            Aliases = []
            Kills = 0
            Deaths = 0
            Assists = 0
            CreepScore = 0
            WardScore = 0.0
            Level = 1
            CurrentHealth = None
            MaxHealth = None
        }

    let neutralSnapshot : BridgeSnapshot =
        {
            GameTime = 0.0
            ActiveAliases = []
            ActivePlayer = neutralPlayer
            Players = [ neutralPlayer ]
            Events = []
        }

    let elapsedMs (since: DateTimeOffset option) (now: DateTimeOffset) =
        since
        |> Option.map (fun value -> max 0L (int64 (now - value).TotalMilliseconds))
        |> Option.defaultValue 0L
