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
- the stable base is capped at `Scoring.BaseIntensityCap` (`18` by default)
- temporary effects can push the final output to `19` or `20`
- kills, multikills, objectives, teamfights, low HP, laning, and objective timing all produce typed calculator effects
- unsupported Lovense functions can be filtered from the final command plan when device capabilities are known

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

## Calculators

The bridge intentionally keeps the LoL-to-Lovense logic as calculator-style
domain code. Runtime orchestration fetches data, updates state, logs breakdowns,
and sends the selected command; the math lives in pure functions.

Main calculated values:

- `rawBaseValue`: normalized performance, multikill base, and death penalty
- `liveHealthMultiplier`: interpolated from current HP, default `0.5..1.0`
- `healthPressureMultiplier`: persistent recovery scar multiplier
- `healthAdjustedBaseValue`: base value after HP modifiers
- `baseIntensity`: rounded/capped stable base, default max `18`
- `temporaryBoost`: sum of active temporary effects
- `finalIntensity`: `baseIntensity + temporaryBoost`, clamped to `0..20`

Health pressure modifies base value only. When HP is lost and then regained, the
regained part creates a permanent multiplier scar for the current session:

```text
scarFactor = 1.0 - ((1.0 - FullRegainPressureFactor) * regainedFraction)
```

With the default `FullRegainPressureFactor = 0.8`, regaining 25%, 50%, 75%, or
100% of a lost segment applies factors `0.95`, `0.90`, `0.85`, and `0.80`.
Repeated regains compound naturally.

Temporary effects include:

- objective waves for dragon, elder, herald, Baron, turret, inhibitor, and stolen objectives
- teamfight bursts from clustered champion kills and ace events
- heartbeat texture under low or critical HP
- restrained laning texture before `LaningPhaseEndSec`
- jungle/objective tension ramps before inferred spawn windows

Capability filtering is controlled by:

```json
"Lovense": {
  "Mapping": {
    "EnableCapabilityFiltering": true,
    "UnknownCapabilityMode": "SafeUniversal",
    "ForceSupportedFunctions": []
  }
}
```

When capability is unknown, `SafeUniversal` keeps only `Vibrate`, `All`, and
`Stop`. If Lovense device info exposes supported functions, unsupported actions
are dropped and the filtered plan is logged in `track.log`.

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
    clamp(
      (5 * normalizedPerformance + totalMultikills - Σ ceil(sqrt(deathIndex)))
      * liveHealthMultiplier
      * healthPressureMultiplier,
      0,
      BaseIntensityCap
    )
    + temporaryBoost,
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
