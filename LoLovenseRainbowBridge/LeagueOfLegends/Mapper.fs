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
        }

    let private toBridgeEventKind (config: ScoringConfig) (ev: LolEvent) =
        match ev.EventName with
        | Constants.RiotEvents.ChampionKill ->
            ChampionKill

        | Constants.RiotEvents.Multikill ->
            let streak = ev.KillStreak |> Option.defaultValue config.MinMultikillStreak
            Multikill streak

        | other ->
            Other other

    let private toBridgeEvent config (ev: LolEvent) : BridgeEvent =
        {
            EventId = ev.EventId
            GameTime = ev.EventTime
            ActorName = ev.KillerName
            VictimName = ev.VictimName
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
