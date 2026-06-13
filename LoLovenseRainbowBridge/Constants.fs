namespace LoLovenseRainbowBridge

module Constants =

    module League =
        [<Literal>]
        let AllGameDataPath = "/liveclientdata/allgamedata"

    module Lovense =
        [<Literal>]
        let GetToken = "https://api.lovense-api.com/api/basicApi/getToken"

        [<Literal>]
        let GetSocketUrl = "https://api.lovense-api.com/api/basicApi/getSocketUrl"

        [<Literal>]
        let CommandName = "Function"

        [<Literal>]
        let GetToysCommand = "GetToys"

        [<Literal>]
        let LocalCommandPath = "/command"

        [<Literal>]
        let PlatformHeader = "X-platform"

        [<Literal>]
        let VibrateAction = "Vibrate"

        [<Literal>]
        let Vibrate1Action = "Vibrate1"

        [<Literal>]
        let Vibrate2Action = "Vibrate2"

        [<Literal>]
        let RotateAction = "Rotate"

        [<Literal>]
        let PumpAction = "Pump"

        [<Literal>]
        let ThrustingAction = "Thrusting"

        [<Literal>]
        let FingeringAction = "Fingering"

        [<Literal>]
        let SuctionAction = "Suction"

        [<Literal>]
        let DepthAction = "Depth"

        [<Literal>]
        let StrokeAction = "Stroke"

        [<Literal>]
        let OscillateAction = "Oscillate"

        [<Literal>]
        let AllAction = "All"

        [<Literal>]
        let StopAction = "Stop"

        [<Literal>]
        let GetQrCodeEmit = "basicapi_get_qrcode_ts"

        [<Literal>]
        let GetQrCodeListen = "basicapi_get_qrcode_tc"

        [<Literal>]
        let DeviceInfoListen = "basicapi_update_device_info_tc"

        [<Literal>]
        let AppStatusListen = "basicapi_update_app_status_tc"

        [<Literal>]
        let AppOnlineListen = "basicapi_update_app_online_tc"

        [<Literal>]
        let SendToyCommandEmit = "basicapi_send_toy_command_ts"

        [<Literal>]
        let AuthTokenRedacted = "<redacted>"

        [<Literal>]
        let WebSocketTransportName = "websocket"

        [<Literal>]
        let SocketIoVersion = "2.x"

        [<Literal>]
        let PlatformField = "platform"

        [<Literal>]
        let AuthTokenField = "authToken"

        [<Literal>]
        let DeveloperTokenField = "token"

        [<Literal>]
        let UserIdField = "uid"

        [<Literal>]
        let UserNameField = "uname"

        [<Literal>]
        let UserTokenField = "utoken"

        [<Literal>]
        let CodeField = "code"

        [<Literal>]
        let MessageField = "message"

        [<Literal>]
        let DataField = "data"

        [<Literal>]
        let SocketIoUrlField = "socketIoUrl"

        [<Literal>]
        let SocketIoPathField = "socketIoPath"

        [<Literal>]
        let AckIdField = "ackId"

        [<Literal>]
        let JsonMediaType = "application/json"

        [<Literal>]
        let ApiVersion = 1

        [<Literal>]
        let SuccessCode = 0

    module RiotJson =
        [<Literal>]
        let UnknownPlayerId = "unknown"

        [<Literal>]
        let DefaultLevel = 1

        [<Literal>]
        let ActivePlayer = "activePlayer"

        [<Literal>]
        let AllPlayers = "allPlayers"

        [<Literal>]
        let GameData = "gameData"

        [<Literal>]
        let EventsContainer = "events"

        [<Literal>]
        let Events = "Events"

        [<Literal>]
        let Scores = "scores"

        [<Literal>]
        let ChampionStats = "championStats"

        [<Literal>]
        let RiotId = "riotId"

        [<Literal>]
        let RiotIdGameName = "riotIdGameName"

        [<Literal>]
        let RiotIdTagLine = "riotIdTagLine"

        [<Literal>]
        let SummonerName = "summonerName"

        [<Literal>]
        let Kills = "kills"

        [<Literal>]
        let Deaths = "deaths"

        [<Literal>]
        let Assists = "assists"

        [<Literal>]
        let CreepScore = "creepScore"

        [<Literal>]
        let WardScore = "wardScore"

        [<Literal>]
        let Level = "level"

        [<Literal>]
        let CurrentHealth = "currentHealth"

        [<Literal>]
        let MaxHealth = "maxHealth"

        [<Literal>]
        let GameTime = "gameTime"

        [<Literal>]
        let EventId = "EventID"

        [<Literal>]
        let EventName = "EventName"

        [<Literal>]
        let EventTime = "EventTime"

        [<Literal>]
        let KillerName = "KillerName"

        [<Literal>]
        let VictimName = "VictimName"

        [<Literal>]
        let KillStreak = "KillStreak"

        [<Literal>]
        let Assisters = "Assisters"

        [<Literal>]
        let DragonType = "DragonType"

        [<Literal>]
        let Stolen = "Stolen"

        [<Literal>]
        let TurretKilled = "TurretKilled"

        [<Literal>]
        let InhibKilled = "InhibKilled"

        [<Literal>]
        let Acer = "Acer"

        [<Literal>]
        let AcingTeam = "AcingTeam"

    module RiotEvents =
        [<Literal>]
        let ChampionKill = "ChampionKill"

        [<Literal>]
        let Multikill = "Multikill"

        [<Literal>]
        let DragonKill = "DragonKill"

        [<Literal>]
        let HeraldKill = "HeraldKill"

        [<Literal>]
        let BaronKill = "BaronKill"

        [<Literal>]
        let TurretKilled = "TurretKilled"

        [<Literal>]
        let InhibKilled = "InhibKilled"

        [<Literal>]
        let Ace = "Ace"
