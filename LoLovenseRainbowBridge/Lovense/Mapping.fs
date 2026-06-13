namespace LoLovenseRainbowBridge.Lovense

open System
open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge

module Mapping =

    let private clampAction maxValue value =
        value |> Shared.clamp 0 maxValue

    let private action fn maxValue value =
        {
            Function = fn
            Value = clampAction maxValue value
            MaxValue = maxValue
            RangeStart = None
        }

    let private strokeAction maxValue startValue endValue =
        {
            Function = Stroke
            Value = clampAction maxValue endValue
            MaxValue = maxValue
            RangeStart = Some(clampAction maxValue startValue)
        }

    let private distinctActions actions =
        actions
        |> List.groupBy (fun action -> action.Function)
        |> List.map (fun (_, sameFunction) ->
            sameFunction
            |> List.maxBy (fun action -> action.Value))

    let actionName fn =
        match fn with
        | Vibrate -> Constants.Lovense.VibrateAction
        | Rotate -> Constants.Lovense.RotateAction
        | Pump -> Constants.Lovense.PumpAction
        | Thrusting -> Constants.Lovense.ThrustingAction
        | Fingering -> Constants.Lovense.FingeringAction
        | Suction -> Constants.Lovense.SuctionAction
        | Depth -> Constants.Lovense.DepthAction
        | Stroke -> Constants.Lovense.StrokeAction
        | Oscillate -> Constants.Lovense.OscillateAction
        | All -> Constants.Lovense.AllAction
        | Stop -> Constants.Lovense.StopAction

    let actionToString action =
        match action.Function, action.RangeStart with
        | Stop, _ ->
            Constants.Lovense.StopAction
        | Stroke, Some startValue ->
            $"{actionName action.Function}:{startValue}-{action.Value}"
        | _ ->
            $"{actionName action.Function}:{action.Value}"

    let reasonToString reason =
        match reason with
        | CompatibilityVibrate -> "CompatibilityVibrate"
        | BasePerformance -> "BasePerformance"
        | KillBurst eventId -> $"KillBurst:{eventId}"
        | MultikillBurst(eventId, streak) -> $"MultikillBurst:{eventId}:{streak}"
        | DeathReset -> "DeathReset"
        | AssistSupportTexture -> "AssistSupportTexture"
        | HighMomentumTexture -> "HighMomentumTexture"
        | StopCommand -> "StopCommand"

    let planActionString plan =
        match plan.Actions with
        | [] -> Constants.Lovense.StopAction
        | actions ->
            actions
            |> List.map actionToString
            |> String.concat ","

    let private activePlayerDeathEvents snapshot =
        snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind, ev.VictimName with
            | ChampionKill, Some victim ->
                snapshot.ActiveAliases
                |> List.exists (fun alias -> String.Equals(alias, victim, StringComparison.OrdinalIgnoreCase))
            | _ ->
                false)

    let private activePlayerKillEvents snapshot =
        snapshot.Events
        |> List.filter (fun ev ->
            match ev.Kind, ev.ActorName with
            | (ChampionKill | Multikill _), Some actor ->
                snapshot.ActiveAliases
                |> List.exists (fun alias -> String.Equals(alias, actor, StringComparison.OrdinalIgnoreCase))
            | _ ->
                false)

    let private recentUnseenActiveEvents previousState snapshot =
        activePlayerKillEvents snapshot
        |> List.filter (fun ev -> not (previousState.SeenEventIds.Contains ev.EventId))

    let private makePlan config actions reasons =
        {
            Actions = distinctActions actions
            Reasons = reasons
            TimeSec = config.CommandTimeSec
            StopPrevious = config.Mapping.DefaultStopPrevious
            ToyId = config.ToyId
        }

    let simpleVibratePlan (config: LovenseConfig) intensity =
        makePlan
            config
            [ action Vibrate config.Mapping.MaxActionIntensity intensity ]
            [ CompatibilityVibrate ]

    let stopPlan (config: LovenseConfig) reason =
        {
            Actions = [ action Stop config.Mapping.MaxActionIntensity 0 ]
            Reasons = [ reason ]
            TimeSec = 0.0
            StopPrevious = true
            ToyId = config.ToyId
        }

    let plan (config: LovenseConfig) (previousState: GeneratorState) (snapshot: BridgeSnapshot) (evolvedState: GeneratorState) (breakdown: IntensityBreakdown) =
        let intensity =
            breakdown.Intensity |> Shared.clamp 0 config.Mapping.MaxActionIntensity

        if String.Equals(config.Mapping.Mode, "SimpleVibrate", StringComparison.OrdinalIgnoreCase) then
            simpleVibratePlan config intensity
        else
            let newDeath =
                config.Mapping.EnableDeathStop
                && activePlayerDeathEvents snapshot
                   |> List.exists (fun ev -> not (previousState.SeenEventIds.Contains ev.EventId))

            if newDeath then
                stopPlan config DeathReset
            else
                let recentEvents = recentUnseenActiveEvents previousState snapshot

                let baseActions =
                    [ action Vibrate config.Mapping.MaxActionIntensity intensity ]

                let baseReasons = [ BasePerformance ]

                let burstActions, burstReasons =
                    if not config.Mapping.EnableEventBursts then
                        [], []
                    else
                        recentEvents
                        |> List.map (fun ev ->
                            match ev.Kind with
                            | ChampionKill ->
                                [
                                    action All config.Mapping.MaxActionIntensity (intensity + 3)
                                ],
                                [
                                    KillBurst ev.EventId
                                ]
                            | Multikill streak ->
                                let safeStreak = streak |> Shared.clamp 2 5
                                let boost = intensity + safeStreak * 3
                                let actions =
                                    [
                                        action All config.Mapping.MaxActionIntensity boost
                                        action Rotate config.Mapping.MaxActionIntensity (boost - 2)
                                        action Thrusting config.Mapping.MaxActionIntensity (boost - 4)
                                    ]

                                let actions =
                                    if safeStreak >= 5 && config.Mapping.EnableStrokeActions then
                                        strokeAction config.Mapping.StrokeMax 0 (min config.Mapping.StrokeMax 80) :: actions
                                    else
                                        actions

                                actions,
                                [
                                    MultikillBurst(ev.EventId, safeStreak)
                                ]
                            | Other _ ->
                                [], [])
                        |> List.fold (fun (actionsAcc, reasonsAcc) (actions, reasons) ->
                            actions @ actionsAcc, reasons @ reasonsAcc) ([], [])

                let pulseActions, pulseReasons =
                    if config.Mapping.EnableComboActions && breakdown.ActivePulseBoost > 0 then
                        [
                            action Rotate config.Mapping.MaxActionIntensity (intensity + breakdown.ActivePulseBoost)
                        ],
                        [
                            HighMomentumTexture
                        ]
                    else
                        [], []

                let supportActions, supportReasons =
                    if config.Mapping.EnableComboActions && (snapshot.ActivePlayer.Assists >= 5 || snapshot.ActivePlayer.WardScore >= 20.0) then
                        [
                            action Oscillate config.Mapping.MaxActionIntensity (max 1 (intensity / 2))
                            action Suction config.Mapping.MaxActionIntensity (max 1 (intensity / 3))
                        ],
                        [
                            AssistSupportTexture
                        ]
                    else
                        [], []

                let sustainedActions, sustainedReasons =
                    if config.Mapping.EnableComboActions && intensity >= 15 then
                        [
                            action All config.Mapping.MaxActionIntensity intensity
                            action Pump config.Mapping.PumpMax (min config.Mapping.PumpMax 2)
                            action Depth config.Mapping.DepthMax (min config.Mapping.DepthMax 2)
                        ],
                        [
                            HighMomentumTexture
                        ]
                    else
                        [], []

                makePlan
                    config
                    (baseActions @ burstActions @ pulseActions @ supportActions @ sustainedActions)
                    (baseReasons @ burstReasons @ pulseReasons @ supportReasons @ sustainedReasons)
