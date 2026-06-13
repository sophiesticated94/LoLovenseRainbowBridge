namespace LoLovenseRainbowBridge.LeagueOfLegends

open System
open System.Text.Json.Nodes
open LoLovenseRainbowBridge

module Parser =

    type ParseWarning =
        {
            Entity: string
            Identifier: string
            Field: string
            DefaultUsed: string
            Reason: string
        }

    type ParsedGameSnapshot =
        {
            Snapshot: LolGameSnapshot
            Warnings: ParseWarning list
        }

    type ParseGameSnapshotError =
        | MissingRequiredFields of string list
        | ActivePlayerNotFound of activeAliases: string list * players: LolPlayerSnapshot list

    let private aliasesFromNode (node: JsonNode) : string list =
        let riotIdGameName = Json.tryString Constants.RiotJson.RiotIdGameName node
        let riotIdTagLine = Json.tryString Constants.RiotJson.RiotIdTagLine node

        let combinedRiotId =
            match riotIdGameName, riotIdTagLine with
            | Some gameName, Some tagLine
                when not (String.IsNullOrWhiteSpace gameName)
                     && not (String.IsNullOrWhiteSpace tagLine) ->
                Some($"{gameName}#{tagLine}")
            | _ ->
                None

        [
            Json.tryString Constants.RiotJson.RiotId node
            combinedRiotId
            riotIdGameName
            Json.tryString Constants.RiotJson.SummonerName node
        ]
        |> List.choose id
        |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
        |> List.distinct

    let private warning entity identifier field defaultUsed reason =
        {
            Entity = entity
            Identifier = identifier
            Field = field
            DefaultUsed = defaultUsed
            Reason = reason
        }

    let private parsePlayer (node: JsonNode) : LolPlayerSnapshot * ParseWarning list =
        let scores = Json.tryGet Constants.RiotJson.Scores node

        let aliases = aliasesFromNode node

        let riotId =
            aliases
            |> List.tryHead
            |> Option.defaultValue Constants.RiotJson.UnknownPlayerId

        let getScoreInt (name: string) =
            match scores |> Option.bind (Json.tryInt name) with
            | Some value -> value, []
            | None ->
                0,
                [
                    warning "player" riotId name "0" "Missing or invalid optional player score field."
                ]

        let getScoreFloat (name: string) =
            match scores |> Option.bind (Json.tryFloat name) with
            | Some value -> value, []
            | None ->
                0.0,
                [
                    warning "player" riotId name "0.0" "Missing or invalid optional player score field."
                ]

        let kills, killsWarnings = getScoreInt Constants.RiotJson.Kills
        let deaths, deathsWarnings = getScoreInt Constants.RiotJson.Deaths
        let assists, assistsWarnings = getScoreInt Constants.RiotJson.Assists
        let creepScore, creepScoreWarnings = getScoreInt Constants.RiotJson.CreepScore
        let wardScore, wardScoreWarnings = getScoreFloat Constants.RiotJson.WardScore

        let level, levelWarnings =
            match Json.tryInt Constants.RiotJson.Level node with
            | Some value -> value, []
            | None ->
                Constants.RiotJson.DefaultLevel,
                [
                    warning
                        "player"
                        riotId
                        Constants.RiotJson.Level
                        (string Constants.RiotJson.DefaultLevel)
                        "Missing or invalid optional player level field."
                ]

        {
            RiotId = riotId
            Aliases = aliases
            Kills = kills
            Deaths = deaths
            Assists = assists
            CreepScore = creepScore
            WardScore = wardScore
            Level = level
        },
        [
            yield! killsWarnings
            yield! deathsWarnings
            yield! assistsWarnings
            yield! creepScoreWarnings
            yield! wardScoreWarnings
            yield! levelWarnings
        ]

    let private parseEvent (node: JsonNode) : LolEvent option =
        match Json.tryInt Constants.RiotJson.EventId node, Json.tryString Constants.RiotJson.EventName node with
        | Some eventId, Some eventName ->
            Some
                {
                    EventId = eventId
                    EventName = eventName
                    EventTime = Json.tryFloat Constants.RiotJson.EventTime node |> Option.defaultValue 0.0
                    KillerName = Json.tryString Constants.RiotJson.KillerName node
                    VictimName = Json.tryString Constants.RiotJson.VictimName node
                    KillStreak = Json.tryInt Constants.RiotJson.KillStreak node
                }

        | _ ->
            None

    let private hasAliasOverlap activeAliases (player: LolPlayerSnapshot) =
        player.Aliases
        |> List.exists (fun alias ->
            activeAliases
            |> List.exists (fun activeAlias ->
                String.Equals(alias, activeAlias, StringComparison.OrdinalIgnoreCase)))

    let parseGameSnapshotResult (root: JsonNode) : Result<ParsedGameSnapshot, ParseGameSnapshotError> =
        let activePlayerNode = Json.tryGet Constants.RiotJson.ActivePlayer root
        let allPlayersNode = Json.tryGet Constants.RiotJson.AllPlayers root
        let gameDataNode = Json.tryGet Constants.RiotJson.GameData root
        let eventsNode = Json.tryGet Constants.RiotJson.EventsContainer root

        let missing =
            [
                if activePlayerNode.IsNone then Constants.RiotJson.ActivePlayer
                if allPlayersNode.IsNone then Constants.RiotJson.AllPlayers
                if gameDataNode.IsNone then Constants.RiotJson.GameData
                if eventsNode.IsNone then Constants.RiotJson.EventsContainer
            ]

        match missing, activePlayerNode, allPlayersNode, gameDataNode, eventsNode with
        | [], Some activePlayerNode, Some allPlayersNode, Some gameDataNode, Some eventsNode ->
            let activeAliases = aliasesFromNode activePlayerNode

            let parsedPlayers =
                allPlayersNode
                |> Json.arrayItems
                |> List.map parsePlayer

            let players = parsedPlayers |> List.map fst
            let playerWarnings = parsedPlayers |> List.collect snd

            let activePlayer =
                players
                |> List.tryFind (hasAliasOverlap activeAliases)

            match activePlayer with
            | Some activePlayer ->
                let events =
                    eventsNode
                    |> Json.tryGet Constants.RiotJson.Events
                    |> Option.map Json.arrayItems
                    |> Option.defaultValue []
                    |> List.choose parseEvent

                let eventWarnings =
                    events
                    |> List.collect (fun ev ->
                        if ev.EventName = Constants.RiotEvents.Multikill && ev.KillStreak.IsNone then
                            [
                                warning
                                    "event"
                                    (string ev.EventId)
                                    Constants.RiotJson.KillStreak
                                    "configured MinMultikillStreak"
                                    "Missing optional multikill streak field."
                            ]
                        else
                            [])

                let gameTime =
                    gameDataNode
                    |> Json.tryFloat Constants.RiotJson.GameTime
                    |> Option.defaultValue 0.0

                Ok
                    {
                        Snapshot =
                            {
                                GameTime = gameTime
                                ActiveAliases = activeAliases
                                ActivePlayer = activePlayer
                                Players = players
                                Events = events
                            }
                        Warnings =
                            [
                                yield! playerWarnings
                                yield! eventWarnings
                            ]
                    }

            | None ->
                Error(ActivePlayerNotFound(activeAliases, players))

        | _ ->
            Error(MissingRequiredFields missing)

    let parseGameSnapshot (root: JsonNode) : LolGameSnapshot option =
        match parseGameSnapshotResult root with
        | Ok parsed -> Some parsed.Snapshot
        | Error _ -> None
