# LoLovenseRainbowBridge

Functional F# bridge:

```text
League of Legends Live Client API
→ LeagueOfLegends adapter
→ Bridge domain scoring
→ Lovense Standard Socket API adapter
→ Vibrate:0..20
```

## Project layout

```text
Shared.fs                         shared technical helpers
Configuration.fs                  environment/config loading
Bridge/Domain.fs                  pure bridge domain: scoring, state, intensity 0..20
LeagueOfLegends/Contracts.fs      LoL-specific data contracts
LeagueOfLegends/Parser.fs         Riot Live Client JSON parser
LeagueOfLegends/Mapper.fs         LoL snapshot → bridge snapshot
LeagueOfLegends/LiveClient.fs     HTTP client for https://127.0.0.1:2999
Lovense/Contracts.fs              Lovense command contracts
Lovense/Client.fs                 Lovense Standard Socket API client
App/Runtime.fs                    orchestration loop
Program.fs                        entrypoint
```

## Run safely first

```powershell
$env:DRY_RUN="true"
dotnet run
```

Then, with LoL game active and Lovense Remote running:

```powershell
$env:DRY_RUN="false"
$env:LOVENSE__AUTHTOKEN="your_lovense_user_auth_token"
dotnet run
```

Optional:

```powershell
$env:LOVENSE_TOY_ID="your_toy_id"
```

Lovense uses the Standard Socket API. The app expects a user `AuthToken` from your
server-side Lovense authorization flow; it does not store or request a Lovense
developer token locally.

## Logs

Each run creates a structured log session:

```text
log/<yyyy-MM-dd_HH-mm-ss>/
  track.log
  lol.log       optional raw LoL payloads
  lovense.log   optional raw Lovense payloads
```

`track.log` is the main application timeline with lifecycle events, retry attempts,
parse warnings, scoring breakdowns, state transitions, send decisions, and errors.

Raw LoL and Lovense payload logs are disabled by default. Enable them with:

```powershell
$env:LOGGING__LOGRAWLEAGUE="true"
$env:LOGGING__LOGRAWLOVENSE="true"
$env:LOGGING__TRACKLOGLEVEL="Debug" # Trace, Debug, Info, Warn, Error
```

## Lovense response mapping

The bridge can run in two Lovense mapping modes:

```json
"Lovense": {
  "Mapping": {
    "Mode": "SimpleVibrate",
    "EnableComboActions": true,
    "EnableEventBursts": true,
    "EnableDeathStop": true,
    "EnableStrokeActions": false,
    "DefaultStopPrevious": true,
    "MaxActionIntensity": 20,
    "PumpMax": 3,
    "DepthMax": 3,
    "StrokeMax": 100
  }
}
```

`SimpleVibrate` is the safe compatibility mode. It sends only `Vibrate:n`.

`MultiFunction` builds richer Lovense Function actions from the LoL state:

- baseline performance drives `Vibrate`
- active kill pulses can add `Rotate` or `All`
- kills and multikills can create short burst-style command plans
- high sustained intensity can add `All`, `Pump`, and `Depth`
- assists and ward score can add softer `Oscillate`/`Suction` texture
- deaths can send a stop/reset plan when `EnableDeathStop=true`

Actual toy behavior depends on the connected toy. If a toy does not support a
function, Lovense Remote may ignore that function. `EnableStrokeActions` is off
by default because Lovense documents that `Stroke` should be paired with
`Thrusting` and needs a meaningful range.

To try the richer mapping safely:

```powershell
$env:LOVENSE__MAPPING__MODE="MultiFunction"
$env:DRY_RUN="true"
dotnet run
```

Generated command plans are written to `track.log`; raw socket payloads are
written to `lovense.log` only when `LOGGING__LOGRAWLOVENSE=true`.

## Build

```powershell
dotnet build
```

The project targets `net10.0`. If your local SDK only supports preview .NET 11, change this in `LoLovenseRainbowBridge.fsproj`:

```xml
<TargetFramework>net11.0</TargetFramework>
```

## Domain rule

```text
intensity = clamp(
    5 * normalizedPerformance
    + totalMultikills
    - Σ ceil(sqrt(deathIndex))
    + activeKillPulses,
    0,
    20
)
```

Kill pulses:

```text
single kill  = +1  for 1s
double kill  = +4  for 2s
triple kill  = +9  for 3s
quadra kill  = +16 for 4s
penta kill   = +25 for 5s
```

Each multikill event permanently increases base by `+1`.
Each death subtracts `ceil(sqrt(nthDeath))` from base.
