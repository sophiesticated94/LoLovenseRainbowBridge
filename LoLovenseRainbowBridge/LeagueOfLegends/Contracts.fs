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
        CurrentHealth: float option
        MaxHealth: float option
    }

type LolEvent =
    {
        EventId: int
        EventName: string
        EventTime: float
        KillerName: string option
        VictimName: string option
        KillStreak: int option
        Assisters: string list
        DragonType: string option
        Stolen: bool option
        TurretKilled: string option
        InhibKilled: string option
        Acer: string option
        AcingTeam: string option
    }

type LolGameSnapshot =
    {
        GameTime: float
        ActiveAliases: string list
        ActivePlayer: LolPlayerSnapshot
        Players: LolPlayerSnapshot list
        Events: LolEvent list
    }
