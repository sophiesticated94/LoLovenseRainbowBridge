namespace LoLovenseRainbowBridge.LeagueOfLegends

type LolPlayerSnapshot =
    {
        RiotId: string
        Aliases: string list
        Kills: int
        Deaths: int
        Assists: int
        CreepScore: int
        WardScore: float
        Level: int
    }

type LolEvent =
    {
        EventId: int
        EventName: string
        EventTime: float
        KillerName: string option
        VictimName: string option
        KillStreak: int option
    }

type LolGameSnapshot =
    {
        GameTime: float
        ActiveAliases: string list
        ActivePlayer: LolPlayerSnapshot
        Players: LolPlayerSnapshot list
        Events: LolEvent list
    }
