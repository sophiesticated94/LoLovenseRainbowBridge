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

    let private actionsForTemporaryEffect config intensity effect =
        match effect.Kind with
        | KillPulseEffect ->
            [ action Rotate config.Mapping.MaxActionIntensity (intensity + effect.Value) ], [ HighMomentumTexture ]
        | ObjectiveWaveEffect ->
            if effect.Value >= 5 then
                [
                    action All config.Mapping.MaxActionIntensity (intensity + effect.Value)
                    action Thrusting config.Mapping.MaxActionIntensity (intensity + effect.Value - 1)
                    action Suction config.Mapping.MaxActionIntensity (max 1 (effect.Value * 2))
                ],
                [ ObjectiveWave ]
            else
                [
                    action All config.Mapping.MaxActionIntensity (intensity + effect.Value)
                    action Rotate config.Mapping.MaxActionIntensity (intensity + effect.Value)
                    action Oscillate config.Mapping.MaxActionIntensity (max 1 effect.Value)
                ],
                [ ObjectiveWave ]
        | TeamfightBurstEffect ->
            [
                action All config.Mapping.MaxActionIntensity (intensity + effect.Value)
                action Rotate config.Mapping.MaxActionIntensity (intensity + effect.Value)
                action Oscillate config.Mapping.MaxActionIntensity (max 1 (effect.Value * 2))
            ],
            [ TeamfightBurst ]
        | AceBurstEffect ->
            [
                action All config.Mapping.MaxActionIntensity (intensity + effect.Value + 2)
                action Thrusting config.Mapping.MaxActionIntensity (intensity + effect.Value)
            ],
            [ AceBurst ]
        | HeartbeatNearDeathEffect ->
            [
                action Vibrate config.Mapping.MaxActionIntensity (max 1 intensity)
                action Oscillate config.Mapping.MaxActionIntensity (max 1 (effect.Value * 3))
            ],
            [ HeartbeatNearDeath ]
        | LaningTextureEffect ->
            [
                action Oscillate config.Mapping.MaxActionIntensity (max 1 effect.Value)
                action Suction config.Mapping.MaxActionIntensity (max 1 effect.Value)
            ],
            [ LaningTexture ]
        | JungleTensionRampEffect ->
            [
                action Rotate config.Mapping.MaxActionIntensity (max 1 (effect.Value * 2))
                action Oscillate config.Mapping.MaxActionIntensity (max 1 effect.Value)
            ],
            [ JungleTensionRamp ]

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

    let addRotationToPlan (plan: LovenseCommandPlan) rotationValue =
        let rotationAction = action Rotate 20 rotationValue
        {
            plan with
                Actions = rotationAction :: plan.Actions
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
                let baseActions =
                    [ action Vibrate config.Mapping.MaxActionIntensity intensity ]

                let baseReasons = [ BasePerformance ]

                let burstActions, burstReasons =
                    if not config.Mapping.EnableEventBursts then
                        [], []
                    else
                        recentUnseenActiveEvents previousState snapshot
                        |> List.map (fun ev ->
                            match ev.Kind with
                            | ChampionKill ->
                                [ action All config.Mapping.MaxActionIntensity (intensity + 3) ], [ KillBurst ev.EventId ]
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

                                actions, [ MultikillBurst(ev.EventId, safeStreak) ]
                            | _ ->
                                [], [])
                        |> List.fold (fun (actionsAcc, reasonsAcc) (actions, reasons) ->
                            actions @ actionsAcc, reasons @ reasonsAcc) ([], [])

                let calculatedActions, calculatedReasons =
                    if not config.Mapping.EnableComboActions then
                        [], []
                    else
                        breakdown.TemporaryEffects
                        |> List.map (actionsForTemporaryEffect config intensity)
                        |> List.fold (fun (actionsAcc, reasonsAcc) (actions, reasons) ->
                            actions @ actionsAcc, reasons @ reasonsAcc) ([], [])

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
                    (baseActions @ burstActions @ calculatedActions @ supportActions @ sustainedActions)
                    (baseReasons @ burstReasons @ calculatedReasons @ supportReasons @ sustainedReasons)

    let filterByCapabilities (config: LovenseConfig) (supportedFunctions: Set<string> option) plan =
        if not config.Mapping.EnableCapabilityFiltering then
            plan, []
        else
            let forced =
                config.Mapping.ForceSupportedFunctions
                |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Set.ofList

            let supported =
                if not forced.IsEmpty then
                    Some forced
                else
                    supportedFunctions

            let supported =
                match supported with
                | Some values ->
                    Some(values |> Set.map (fun value -> value.ToUpperInvariant()))
                | None when String.Equals(config.Mapping.UnknownCapabilityMode, "SafeUniversal", StringComparison.OrdinalIgnoreCase) ->
                    Some(set [ Constants.Lovense.VibrateAction.ToUpperInvariant(); Constants.Lovense.AllAction.ToUpperInvariant(); Constants.Lovense.StopAction.ToUpperInvariant() ])
                | None ->
                    None

            match supported with
            | None ->
                plan, []
            | Some supported ->
                let kept, dropped =
                    plan.Actions
                    |> List.partition (fun action -> supported.Contains((LovenseActionCodec.actionName action.Function).ToUpperInvariant()))

                let droppedNames = dropped |> List.map LovenseActionCodec.actionToString

                if droppedNames.IsEmpty then
                    plan, []
                else
                    let finalActions =
                        match kept with
                        | [] -> [ action Vibrate config.Mapping.MaxActionIntensity 0 ]
                        | kept -> kept

                    {
                        plan with
                            Actions = finalActions
                            Reasons = CapabilityFiltered droppedNames :: plan.Reasons
                    },
                    droppedNames
