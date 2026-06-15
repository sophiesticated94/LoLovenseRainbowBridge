namespace LoLovenseRainbowBridge

open System

module Validation =
    let validate (config: AppConfig) : AppConfig =
        if config.Runtime.PollMs <= 0 then
            invalidArg "Runtime.PollMs" "Runtime.PollMs must be greater than zero."

        if config.Runtime.LeaguePollMs <= 0 then
            invalidArg "Runtime.LeaguePollMs" "Runtime.LeaguePollMs must be greater than zero."

        if config.Runtime.OcrPollMs <= 0 then
            invalidArg "Runtime.OcrPollMs" "Runtime.OcrPollMs must be greater than zero."

        if config.Runtime.LovensePollMs <= 0 then
            invalidArg "Runtime.LovensePollMs" "Runtime.LovensePollMs must be greater than zero."

        if config.Runtime.ResendEveryMs <= 0 then
            invalidArg "Runtime.ResendEveryMs" "Runtime.ResendEveryMs must be greater than zero."

        if config.Runtime.UnavailableRetryMs <= 0 then
            invalidArg "Runtime.UnavailableRetryMs" "Runtime.UnavailableRetryMs must be greater than zero."

        if config.Lovense.ConnectTimeoutMs <= 0 then
            invalidArg "Lovense.ConnectTimeoutMs" "Lovense.ConnectTimeoutMs must be greater than zero."

        if config.Lovense.CommandAckTimeoutMs <= 0 then
            invalidArg "Lovense.CommandAckTimeoutMs" "Lovense.CommandAckTimeoutMs must be greater than zero."

        let allowedTransportModes = set [ "SOCKETAPI"; "STANDARDAPILOCAL"; "STANDARDAPISERVER"; "AUTO" ]

        if not (allowedTransportModes.Contains(config.Lovense.TransportMode.ToUpperInvariant())) then
            invalidArg "Lovense.TransportMode" "Lovense.TransportMode must be SocketApi, StandardApiLocal, StandardApiServer, or Auto."

        if config.Lovense.StandardApi.Enable then
            if String.IsNullOrWhiteSpace config.Lovense.StandardApi.CallbackListenUrl then
                invalidArg "Lovense.StandardApi.CallbackListenUrl" "Lovense.StandardApi.CallbackListenUrl cannot be empty when Standard API is enabled."

            if config.Lovense.StandardApi.PairingQrExpiresHours <= 0.0 then
                invalidArg "Lovense.StandardApi.PairingQrExpiresHours" "Lovense.StandardApi.PairingQrExpiresHours must be greater than zero."

        if config.Lovense.LocalApi.TimeoutMs <= 0 then
            invalidArg "Lovense.LocalApi.TimeoutMs" "Lovense.LocalApi.TimeoutMs must be greater than zero."

        if config.Lovense.LocalApi.CapabilityRefreshIntervalSec <= 0 then
            invalidArg "Lovense.LocalApi.CapabilityRefreshIntervalSec" "Lovense.LocalApi.CapabilityRefreshIntervalSec must be greater than zero."

        if config.Lovense.LocalApi.EnableCommandFallback || config.Lovense.LocalApi.EnableGetToys then
            match config.Lovense.LocalApi.HttpsPort with
            | Some port when port <= 0 || port > 65535 ->
                invalidArg "Lovense.LocalApi.HttpsPort" "Lovense.LocalApi.HttpsPort must be in range 1..65535."
            | _ -> ()

            match config.Lovense.LocalApi.HttpPort with
            | Some port when port <= 0 || port > 65535 ->
                invalidArg "Lovense.LocalApi.HttpPort" "Lovense.LocalApi.HttpPort must be in range 1..65535."
            | _ -> ()

        if String.IsNullOrWhiteSpace config.Lovense.LocalApi.HeaderPlatform then
            invalidArg "Lovense.LocalApi.HeaderPlatform" "Lovense.LocalApi.HeaderPlatform cannot be empty."

        let allowedMappingModes = set [ "SIMPLEVIBRATE"; "MULTIFUNCTION" ]

        if not (allowedMappingModes.Contains(config.Lovense.Mapping.Mode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.Mode" "Lovense.Mapping.Mode must be SimpleVibrate or MultiFunction."

        if config.Lovense.Mapping.MaxActionIntensity < 0 || config.Lovense.Mapping.MaxActionIntensity > 20 then
            invalidArg "Lovense.Mapping.MaxActionIntensity" "Lovense.Mapping.MaxActionIntensity must be in range 0..20."

        if config.Lovense.Mapping.PumpMax < 0 || config.Lovense.Mapping.PumpMax > 3 then
            invalidArg "Lovense.Mapping.PumpMax" "Lovense.Mapping.PumpMax must be in range 0..3."

        if config.Lovense.Mapping.DepthMax < 0 || config.Lovense.Mapping.DepthMax > 3 then
            invalidArg "Lovense.Mapping.DepthMax" "Lovense.Mapping.DepthMax must be in range 0..3."

        if config.Lovense.Mapping.StrokeMax < 0 || config.Lovense.Mapping.StrokeMax > 100 then
            invalidArg "Lovense.Mapping.StrokeMax" "Lovense.Mapping.StrokeMax must be in range 0..100."

        let allowedUnknownCapabilityModes = set [ "SAFEUNIVERSAL"; "PASSTHROUGH" ]

        if not (allowedUnknownCapabilityModes.Contains(config.Lovense.Mapping.UnknownCapabilityMode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.UnknownCapabilityMode" "Lovense.Mapping.UnknownCapabilityMode must be SafeUniversal or PassThrough."

        let allowedStereoModes = set [ "AUTO"; "DISABLED"; "FORCE" ]

        if not (allowedStereoModes.Contains(config.Lovense.Mapping.StereoMode.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.StereoMode" "Lovense.Mapping.StereoMode must be Auto, Disabled, or Force."

        let allowedStereoFallbacks = set [ "MAX"; "AVERAGE"; "LEFTONLY" ]

        if not (allowedStereoFallbacks.Contains(config.Lovense.Mapping.StereoFallback.ToUpperInvariant())) then
            invalidArg "Lovense.Mapping.StereoFallback" "Lovense.Mapping.StereoFallback must be Max, Average, or LeftOnly."

        let knownLovenseFunctions =
            set
                [
                    "VIBRATE"
                    "VIBRATE1"
                    "VIBRATE2"
                    "ROTATE"
                    "PUMP"
                    "THRUSTING"
                    "FINGERING"
                    "SUCTION"
                    "DEPTH"
                    "STROKE"
                    "OSCILLATE"
                    "ALL"
                    "STOP"
                ]

        let knownRuleKinds =
            set
                [
                    "BASEMODIFIER"
                    "THRESHOLDMODIFIER"
                    "TIMEDCONTRIBUTION"
                    "EFFECT"
                    "STATETRANSITION"
                    "CAPABILITYFALLBACK"
                    "POSITIONMODULATION"
                ]

        let knownRuleOperations =
            set
                [
                    "SET"
                    "ADD"
                    "SUBTRACT"
                    "MULTIPLY"
                    "CLAMPMIN"
                    "CLAMPMAX"
                    "TRACKMAX"
                    "TRACKMIN"
                    "CLEAR"
                    "STARTWINDOW"
                    "STARTINCARNATION"
                ]

        let knownRuleLayers =
            set [ "BASE"; "TIMED"; "EFFECT"; "OTHER"; "STATE" ]

        for profile in config.Lovense.Mapping.FunctionProfiles do
            if not (knownLovenseFunctions.Contains(profile.FunctionName.ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.FunctionProfiles.FunctionName" $"Unknown Lovense function profile: {profile.FunctionName}"

            if profile.MinOutput < 0 || profile.MaxOutput < profile.MinOutput then
                invalidArg "Lovense.Mapping.FunctionProfiles.Output" $"Invalid output range for {profile.FunctionName}."

            if profile.Smoothing < 0.0 || profile.Smoothing > 1.0 then
                invalidArg "Lovense.Mapping.FunctionProfiles.Smoothing" $"Smoothing for {profile.FunctionName} must be in range 0.0..1.0."

        for rule in config.Lovense.Mapping.Rules do
            if String.IsNullOrWhiteSpace rule.Name then
                invalidArg "Lovense.Mapping.Rules.Name" "Every Lovense rule must have a name."

            if not (knownRuleKinds.Contains(rule.Kind.ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Kind" $"Unknown Lovense rule kind: {rule.Kind}"

            if not (knownRuleOperations.Contains((rule.Operation |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Operation" $"Unknown Lovense rule operation in '{rule.Name}': {rule.Operation}"

            if not (knownRuleLayers.Contains((rule.Layer |> Option.ofObj |> Option.defaultValue "").ToUpperInvariant())) then
                invalidArg "Lovense.Mapping.Rules.Layer" $"Unknown Lovense rule layer in '{rule.Name}': {rule.Layer}"

            if String.Equals(rule.Layer, "State", StringComparison.OrdinalIgnoreCase) then
                if String.IsNullOrWhiteSpace rule.StateSlot then
                    invalidArg "Lovense.Mapping.Rules.StateSlot" $"State rule '{rule.Name}' must declare StateSlot."
            else
                let functions =
                    rule.TargetFunctions.Split('|', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                    |> Array.toList

                if functions.IsEmpty then
                    invalidArg "Lovense.Mapping.Rules.TargetFunctions" $"Rule '{rule.Name}' must declare TargetFunctions."

                for functionName in functions do
                    if not (knownLovenseFunctions.Contains(functionName.ToUpperInvariant())) then
                        invalidArg "Lovense.Mapping.Rules.TargetFunctions" $"Unknown Lovense rule target function in '{rule.Name}': {functionName}"

            if String.IsNullOrWhiteSpace rule.Expression
               && not (String.Equals(rule.Operation, "Clear", StringComparison.OrdinalIgnoreCase)
                       || String.Equals(rule.Operation, "StartIncarnation", StringComparison.OrdinalIgnoreCase)) then
                invalidArg "Lovense.Mapping.Rules.Expression" $"Rule '{rule.Name}' must declare Expression."

        if config.Scoring.MinIntensity > config.Scoring.MaxIntensity then
            invalidArg "Scoring.MinIntensity" "Scoring.MinIntensity cannot be greater than Scoring.MaxIntensity."

        if config.Scoring.BaseIntensityCap < config.Scoring.MinIntensity || config.Scoring.BaseIntensityCap > config.Scoring.MaxIntensity then
            invalidArg "Scoring.BaseIntensityCap" "Scoring.BaseIntensityCap must be between Scoring.MinIntensity and Scoring.MaxIntensity."

        if config.Scoring.HealthMinMultiplier < 0.0 || config.Scoring.HealthMinMultiplier > 1.0 then
            invalidArg "Scoring.HealthMinMultiplier" "Scoring.HealthMinMultiplier must be in range 0.0..1.0."

        if config.Scoring.HealthPressureDropThresholdPercent < 0.0 || config.Scoring.HealthPressureDropThresholdPercent > 100.0 then
            invalidArg "Scoring.HealthPressureDropThresholdPercent" "Scoring.HealthPressureDropThresholdPercent must be in range 0.0..100.0."

        if config.Scoring.FullRegainPressureFactor < 0.0 || config.Scoring.FullRegainPressureFactor > 1.0 then
            invalidArg "Scoring.FullRegainPressureFactor" "Scoring.FullRegainPressureFactor must be in range 0.0..1.0."

        if config.Scoring.MinMultikillStreak > config.Scoring.MaxMultikillStreak then
            invalidArg "Scoring.MinMultikillStreak" "Scoring.MinMultikillStreak cannot be greater than Scoring.MaxMultikillStreak."

        if config.Scoring.TeamfightWindowSec < 0.0 then
            invalidArg "Scoring.TeamfightWindowSec" "Scoring.TeamfightWindowSec must be zero or greater."

        if config.Scoring.TeamfightKillCountThreshold <= 0 then
            invalidArg "Scoring.TeamfightKillCountThreshold" "Scoring.TeamfightKillCountThreshold must be greater than zero."

        if config.Scoring.LowHealthHeartbeatThreshold < 0.0 || config.Scoring.LowHealthHeartbeatThreshold > 1.0 then
            invalidArg "Scoring.LowHealthHeartbeatThreshold" "Scoring.LowHealthHeartbeatThreshold must be in range 0.0..1.0."

        if config.Scoring.CriticalHealthHeartbeatThreshold < 0.0 || config.Scoring.CriticalHealthHeartbeatThreshold > 1.0 then
            invalidArg "Scoring.CriticalHealthHeartbeatThreshold" "Scoring.CriticalHealthHeartbeatThreshold must be in range 0.0..1.0."

        if config.Scoring.CriticalHealthHeartbeatThreshold > config.Scoring.LowHealthHeartbeatThreshold then
            invalidArg "Scoring.CriticalHealthHeartbeatThreshold" "Critical health threshold cannot be greater than low health threshold."

        if config.Scoring.HeartbeatPulseMaxAmplitude < 0.0 then
            invalidArg "Scoring.HeartbeatPulseMaxAmplitude" "Scoring.HeartbeatPulseMaxAmplitude must be zero or greater."

        if config.Scoring.HeartbeatPulseCycleSec <= 0.0 then
            invalidArg "Scoring.HeartbeatPulseCycleSec" "Scoring.HeartbeatPulseCycleSec must be greater than zero."

        for key, value in
            [
                "Scoring.HeartbeatPulseStartPhase", config.Scoring.HeartbeatPulseStartPhase
                "Scoring.HeartbeatPulsePeakPhase", config.Scoring.HeartbeatPulsePeakPhase
                "Scoring.HeartbeatPulseEndPhase", config.Scoring.HeartbeatPulseEndPhase
            ] do
            if value < 0.0 || value > 1.0 then
                invalidArg key $"{key} must be in range 0.0..1.0."

        if not (config.Scoring.HeartbeatPulseStartPhase < config.Scoring.HeartbeatPulsePeakPhase
                && config.Scoring.HeartbeatPulsePeakPhase < config.Scoring.HeartbeatPulseEndPhase) then
            invalidArg "Scoring.HeartbeatPulsePhases" "Heartbeat pulse phases must satisfy StartPhase < PeakPhase < EndPhase."

        for key, value in
            [
                "Scoring.LaningPhaseEndSec", config.Scoring.LaningPhaseEndSec
                "Scoring.DragonInitialSpawnSec", config.Scoring.DragonInitialSpawnSec
                "Scoring.DragonRespawnSec", config.Scoring.DragonRespawnSec
                "Scoring.HeraldInitialSpawnSec", config.Scoring.HeraldInitialSpawnSec
                "Scoring.HeraldDespawnSec", config.Scoring.HeraldDespawnSec
                "Scoring.BaronInitialSpawnSec", config.Scoring.BaronInitialSpawnSec
                "Scoring.BaronRespawnSec", config.Scoring.BaronRespawnSec
                "Scoring.ObjectiveTensionWindowSec", config.Scoring.ObjectiveTensionWindowSec
                "Scoring.DeathPressureWindowSec", config.Scoring.DeathPressureWindowSec
            ] do
            if value < 0.0 then
                invalidArg key $"{key} must be zero or greater."

        if config.Scoring.DeathPressureBaseLossPercent < 0.0 || config.Scoring.DeathPressureBaseLossPercent > 1.0 then
            invalidArg "Scoring.DeathPressureBaseLossPercent" "Scoring.DeathPressureBaseLossPercent must be in range 0.0..1.0."

        if config.Scoring.HpChangeThresholdPercent < 0.0 || config.Scoring.HpChangeThresholdPercent > 100.0 then
            invalidArg "Scoring.HpChangeThresholdPercent" "Scoring.HpChangeThresholdPercent must be in range 0.0..100.0."

        if config.Scoring.BaseRecoveryFloor < 0.0 || config.Scoring.BaseRecoveryFloor > 1.0 then
            invalidArg "Scoring.BaseRecoveryFloor" "Scoring.BaseRecoveryFloor must be in range 0.0..1.0."

        if config.Scoring.BaseRecoveryTarget < 0.0 || config.Scoring.BaseRecoveryTarget > 1.0 then
            invalidArg "Scoring.BaseRecoveryTarget" "Scoring.BaseRecoveryTarget must be in range 0.0..1.0."

        if config.Scoring.BaseRecoveryTarget < config.Scoring.BaseRecoveryFloor then
            invalidArg "Scoring.BaseRecoveryTarget" "Scoring.BaseRecoveryTarget cannot be less than Scoring.BaseRecoveryFloor."

        if config.PositionBasedRotation.CaptureIntervalMs <= 0 then
            invalidArg "PositionBasedRotation.CaptureIntervalMs" "PositionBasedRotation.CaptureIntervalMs must be greater than zero."

        if config.PositionBasedRotation.MinimapWidth <= 0 || config.PositionBasedRotation.MinimapHeight <= 0 then
            invalidArg "PositionBasedRotation.Minimap" "PositionBasedRotation.Minimap dimensions must be greater than zero."

        let allowedMappingModes = set [ "QUADRANT"; "CONTINUOUS"; "ZONEBASED"; "COMBINED" ]

        if not (allowedMappingModes.Contains(config.PositionBasedRotation.MappingMode.ToUpperInvariant())) then
            invalidArg "PositionBasedRotation.MappingMode" "PositionBasedRotation.MappingMode must be Quadrant, Continuous, ZoneBased, or Combined."

        if config.PositionBasedRotation.RotationSensitivity < 0.0 || config.PositionBasedRotation.RotationSensitivity > 2.0 then
            invalidArg "PositionBasedRotation.RotationSensitivity" "PositionBasedRotation.RotationSensitivity must be in range 0.0..2.0."

        let validateWeightPair key (pair: PositionWeightPairConfig) =
            if pair.Left < 0.0 || pair.Right < 0.0 then
                invalidArg key $"{key} weights must be zero or greater."

        validateWeightPair "PositionBasedRotation.PositionWeights.Center" config.PositionBasedRotation.PositionWeights.Center
        validateWeightPair "PositionBasedRotation.PositionWeights.TopLeft" config.PositionBasedRotation.PositionWeights.TopLeft
        validateWeightPair "PositionBasedRotation.PositionWeights.TopRight" config.PositionBasedRotation.PositionWeights.TopRight
        validateWeightPair "PositionBasedRotation.PositionWeights.BottomLeft" config.PositionBasedRotation.PositionWeights.BottomLeft
        validateWeightPair "PositionBasedRotation.PositionWeights.BottomRight" config.PositionBasedRotation.PositionWeights.BottomRight
        validateWeightPair "PositionBasedRotation.PositionWeights.Left" config.PositionBasedRotation.PositionWeights.Left
        validateWeightPair "PositionBasedRotation.PositionWeights.Right" config.PositionBasedRotation.PositionWeights.Right

        let allowedLogLevels = set [ "TRACE"; "DEBUG"; "INFO"; "WARN"; "ERROR" ]

        if not (allowedLogLevels.Contains(config.Logging.TrackLogLevel.ToUpperInvariant())) then
            invalidArg "Logging.TrackLogLevel" "Logging.TrackLogLevel must be one of: Trace, Debug, Info, Warn, Error."

        if config.Recording.SliceMs <= 0 then
            invalidArg "Recording.SliceMs" "Recording.SliceMs must be greater than zero."

        if String.IsNullOrWhiteSpace config.Recording.DatabasePath then
            invalidArg "Recording.DatabasePath" "Recording.DatabasePath cannot be empty."

        config
