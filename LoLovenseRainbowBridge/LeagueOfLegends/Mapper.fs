namespace LoLovenseRainbowBridge.LeagueOfLegends

open LoLovenseRainbowBridge
open LoLovenseRainbowBridge.Bridge

module Mapper =

    let private toBridgePlayer (player: LolPlayerSnapshot) : BridgePlayer =
        {
            Id = player.RiotId
            Aliases = player.Aliases
            Kills = player.Kills
            Deaths = player.Deaths
            Assists = player.Assists
            CreepScore = player.CreepScore
            WardScore = player.WardScore
            Level = player.Level
            CurrentHealth = player.CurrentHealth
            MaxHealth = player.MaxHealth
        }

    let private toBridgeEventKind (config: ScoringConfig) (ev: LolEvent) =
        match ev.EventName with
        | Constants.RiotEvents.ChampionKill ->
            ChampionKill

        | Constants.RiotEvents.Multikill ->
            let streak = ev.KillStreak |> Option.defaultValue config.MinMultikillStreak
            Multikill streak

        | Constants.RiotEvents.DragonKill ->
            ObjectiveKill(Dragon ev.DragonType, ev.Stolen)

        | Constants.RiotEvents.HeraldKill ->
            ObjectiveKill(Herald, ev.Stolen)

        | Constants.RiotEvents.BaronKill ->
            ObjectiveKill(Baron, ev.Stolen)

        | Constants.RiotEvents.TurretKilled ->
            ObjectiveKill(Turret ev.TurretKilled, None)

        | Constants.RiotEvents.InhibKilled ->
            ObjectiveKill(Inhibitor ev.InhibKilled, None)

        | Constants.RiotEvents.Ace ->
            Ace ev.AcingTeam

        | other ->
            Other other

    let private toBridgeEvent config (ev: LolEvent) : BridgeEvent =
        {
            EventId = ev.EventId
            GameTime = ev.EventTime
            ActorName = ev.KillerName |> Option.orElse ev.Acer
            VictimName = ev.VictimName
            Assisters = ev.Assisters
            Kind = toBridgeEventKind config ev
        }

    let toBridgeSnapshot (config: ScoringConfig) (snapshot: LolGameSnapshot) : BridgeSnapshot =
        {
            GameTime = snapshot.GameTime
            ActiveAliases = snapshot.ActiveAliases
            ActivePlayer = toBridgePlayer snapshot.ActivePlayer
            Players = snapshot.Players |> List.map toBridgePlayer
            Events = snapshot.Events |> List.map (toBridgeEvent config)
        }
