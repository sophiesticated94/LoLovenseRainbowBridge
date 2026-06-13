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
Lovense/Auth.fs                   Lovense getToken flow
Lovense/SocketUrl.fs              Lovense getSocketUrl flow
Lovense/DeviceInfo.fs             toyList and capability parsing
Lovense/CapabilityResolver.fs     per-toy capability and stereo resolver
Lovense/RuleEngine.fs             configurable rule interpreter and command builder
Lovense/LocalApi.fs               Local API GetToys capability enrichment
Lovense/SocketRuntime.fs          Socket.IO connection and listeners
Lovense/Client.fs                 thin Lovense Socket API facade
Recording/Recording.fs            SQLite gameplay recording and replay
App/Runtime.fs                    orchestration loop
ScreenCapture/ScreenCapture.fs    Windows screen-region capture
MinimapDetector/MinimapDetector.fs OpenCV minimap player marker detection
PositionMapping/PositionMapping.fs minimap position to quadrant/zone context
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
dotnet run
```

Optional:

```powershell
$env:LOVENSE_TOY_ID="your_toy_id"
```

Lovense uses the Standard Socket API workflow:

1. `getToken` with local-only developer settings:
   `Lovense.Developer.Token`, `Lovense.Developer.UserId`, optional
   `Lovense.Developer.UserName`, optional `Lovense.Developer.UserToken`.
2. `getSocketUrl` with the runtime auth token returned by `getToken`.
3. Socket.IO websocket connection.
4. QR/device/app status listeners.
5. `basicapi_send_toy_command_ts` Function commands.

Do not configure `Lovense.AuthToken`; it is intentionally not a public app
setting. Put real developer values only in ignored `appsettings.Local.json`.
Tracked config and logs redact developer tokens, user tokens, and runtime auth
tokens.

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

## Gameplay Recording

SQLite gameplay recording is enabled by default:

```json
"Recording": {
  "Enabled": true,
  "DatabasePath": "data/gameplay.sqlite",
  "SliceMs": 100,
  "RecordRawContext": false
}
```

Every valid LoL session gets a unique `gameId`. The recorder starts when the
first valid League snapshot is parsed after an unavailable state, closes when
LoL becomes unavailable or the app stops, and stores Lovense output diffs in
`lovense_records`.

The database schema is EF Core code-first. The EF model and generated migrations
live in `LoLovenseRainbowBridge.Recording.Data`, and the app applies migrations
with `Database.Migrate()` at startup/list/replay time.

Schema:

```text
games(game_id, started_at, ended_at, app_version, config_summary_json)
lovense_records(id, game_id, datetime, offset_ms, duration_ms, context_diff_json)
```

`context_diff_json` is compact JSON. It records only changed Lovense function
values plus the action string, command reasons, intensity summary, recent LoL
event ids, and send result. Rows are coalesced into `Recording.SliceMs` windows;
unchanged output is not duplicated.

List saved games:

```powershell
dotnet run -- --list-recordings
```

Replay a saved Lovense output timeline without LoL running:

```powershell
dotnet run -- --replay <gameId>
dotnet run -- --replay <gameId> --dry-run
dotnet run -- --replay <gameId> --speed 2
```

Replay reconstructs the recorded Lovense Function command sequence and sends it
through the same Socket API client. SQLite is the internal source of truth
because it preserves bridge context and multi-function state; Lovense Pattern,
PatternV2, or media-sync exports can be added later for narrower use cases.

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
    "StrokeMax": 100,
    "EnableStereoVibration": true,
    "StereoMode": "Auto",
    "StereoFallback": "Max",
    "LogToyViability": true,
    "FunctionProfiles": [
      {
        "FunctionName": "Vibrate",
        "Enabled": true,
        "InheritFrom": "",
        "MinOutput": 0,
        "MaxOutput": 20,
        "BaseWeight": 1.0,
        "TimedWeight": 1.0,
        "EffectWeight": 1.0,
        "Curve": "Linear",
        "Smoothing": 0.0
      }
    ],
    "Rules": [
      {
        "Name": "vibrate-base-from-current-game-state",
        "Kind": "BaseModifier",
        "TargetFunction": "Vibrate",
        "Source": "Breakdown.BaseIntensity",
        "Operation": "Set",
        "Value": 1.0
      }
    ]
  }
}
```

`SimpleVibrate` is the safe compatibility mode. It sends only `Vibrate:n`.

`MultiFunction` builds richer Lovense Function actions from the LoL state through
the configurable rule engine:

- calculators produce stable inputs such as base intensity, health pressure, heartbeat pulse, temporary effects, and minimap context
- rules transform those inputs into per-function layers: base, timed, effect, inherited, and final
- `Vibrate1` and `Vibrate2` inherit the current resolved `Vibrate` value by default, then position rules can reshape the two channels
- command builder memory tracks incarnation thresholds, max base reached this incarnation, last function state, and diffs
- unsupported Lovense functions are filtered from the final command plan when capabilities are known

Actual toy behavior depends on the connected toy. If a toy does not support a
function, Lovense Remote may ignore that function. `EnableStrokeActions` is off
by default because Lovense documents that `Stroke` should be paired with
`Thrusting` and needs a meaningful range.

Rule kinds are finite and typed: `BaseModifier`, `ThresholdModifier`,
`TimedContribution`, `Effect`, `StateTransition`, `CapabilityFallback`,
`FunctionInheritance`, and `PositionModulation`. This is intentionally not a
general scripting language; bad function names, rule kinds, operations, and
output ranges are validated at startup.

Canonical Lovense protocol ranges are code-owned:

```text
Vibrate, Vibrate1, Vibrate2, Rotate, Thrusting, Fingering,
Suction, Oscillate, All = 0..20
Pump, Depth = 0..3
Stroke = 0..100
Stop = command-only
```

Function profiles are behavior policy over those ranges. They can enable a
function, inherit from another function, apply layer weights, clamp output, and
choose a simple curve. They do not define protocol ranges.

## Toy capabilities and stereo vibration

Lovense device updates are parsed into per-toy capability profiles. The app logs
available toys to `track.log` as `lovense.toys.available`, with toy ids redacted,
connected state, battery, explicit functions from Lovense, inferred functions,
stereo viability, and notes.

When Socket API device info includes `domain` and `httpsPort`, the bridge can
also call the Lovense Local API command `GetToys`:

```json
"Lovense": {
  "LocalApi": {
    "EnableGetToys": true,
    "TimeoutMs": 3000,
    "AllowSelfSignedCertificate": true,
    "HeaderPlatform": "LoL Lovense Bridge"
  }
}
```

`GetToys` is used only for capability enrichment. Commands still go through the
Standard Socket API. The Local API response is normalized whether `data.toys` is
returned as an object or as a JSON string, and `fullFunctionNames` /
`shortFunctionNames` become the preferred capability source.

Known behavior:

- Gemini and Edge are treated as dual-vibration candidates and can use
  `Vibrate1`/`Vibrate2`
- Ferri is treated as single-channel vibration and falls back to `Vibrate`
- Nora can use `Vibrate` and `Rotate`
- unknown toys stay conservative with `Vibrate`, `All`, and `Stop`

Stereo is controlled by:

```json
"Lovense": {
  "Mapping": {
    "EnableStereoVibration": true,
    "StereoMode": "Auto",
    "StereoFallback": "Max",
    "EnableCapabilityFiltering": true,
    "UnknownCapabilityMode": "SafeUniversal",
    "ForceSupportedFunctions": []
  }
}
```

`StereoMode=Auto` uses `Vibrate1` and `Vibrate2` only when device info or a known
toy type makes it viable. `Disabled` collapses stereo actions back to ordinary
`Vibrate`. `Force` emits dual channels even before device info is available.

When minimap position is available, the runtime passes normalized X/Y, quadrant,
zone, confidence, and detection method into the rule engine. Position is no
longer encoded through `Rotate`; `Rotate` remains a real Lovense function only.
Default `PositionModulation` rules emphasize `Vibrate1` on the left/top-left,
`Vibrate2` on the right/top-right, keep center balanced, and suppress the
bottom-left positional accent. If stereo is unavailable, `StereoFallback`
collapses dual channels by `Max`, `Average`, or `LeftOnly`.

If `Lovense.ToyId` is configured, capability resolution is narrowed to that toy
when it appears in device info. This prevents one connected toy from making an
unsupported function look valid for another.

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

Heartbeat is configurable. It uses missing health to calculate a positive pulse
amplitude, then shapes that amplitude with a short asymmetric cycle:

```json
"LowHealthHeartbeatThreshold": 0.30,
"HeartbeatPulseMaxAmplitude": 6.0,
"HeartbeatPulseCycleSec": 1.0,
"HeartbeatPulseStartPhase": 0.72,
"HeartbeatPulsePeakPhase": 0.78,
"HeartbeatPulseEndPhase": 0.98
```

Most of the cycle stays near zero; the pulse rises quickly, peaks briefly, then
falls smoothly. Lower HP means a larger final heartbeat contribution.

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

## Position-based minimap context

The bridge supports minimap position-based Lovense modulation. When enabled,
the application captures the League of Legends minimap at a configured screen
region, detects a player marker with OpenCV, normalizes the minimap position to
`0..1`, and passes that position into the Lovense rule engine. Default rules use
it for stereo `Vibrate1` / `Vibrate2`; `Rotate` is no longer used as a hidden
position carrier.

### Configuration

Position-based minimap context is enabled in `appsettings.json` by default:

```json
"PositionBasedRotation": {
  "Enable": true,
  "CaptureIntervalMs": 500,
  "MinimapScreenX": 1700,
  "MinimapScreenY": 900,
  "MinimapWidth": 200,
  "MinimapHeight": 200,
  "MappingMode": "Combined",
  "RotationSensitivity": 1.0,
  "TemplateImagePath": "",
  "DebugMode": false
}
```

Or via environment variables:

```powershell
$env:POSITIONBASEDROTATION__ENABLE="true"
$env:POSITIONBASEDROTATION__CAPTUREINTERVALMS="500"
$env:POSITIONBASEDROTATION__MINIMAPSCREENX="1700"
$env:POSITIONBASEDROTATION__MINIMAPSCREENY="900"
$env:POSITIONBASEDROTATION__MINIMAPWIDTH="200"
$env:POSITIONBASEDROTATION__MINIMAPHEIGHT="200"
$env:POSITIONBASEDROTATION__MAPPINGMODE="Combined"
$env:POSITIONBASEDROTATION__ROTATIONSENSITIVITY="1.0"
$env:POSITIONBASEDROTATION__TEMPLATEIMAGEPATH="C:\path\to\real-player-marker-template.png"
$env:POSITIONBASEDROTATION__DEBUGMODE="false"
```

The legacy `POSITION_ROTATION_ENABLE` override is still accepted for the enable flag.

### Settings

- **Enable**: Whether position-based minimap context is enabled (default: `true`)
- **CaptureIntervalMs**: How often to capture the minimap in milliseconds (default: `500`)
- **MinimapScreenX/MinimapScreenY**: Screen coordinates of the minimap's top-left corner (default: `1700, 900` for 1920x1080)
- **MinimapWidth/MinimapHeight**: Dimensions of the minimap capture region (default: `200x200`)
- **MappingMode**: Strategy for deriving position context:
  - `Quadrant`: emphasizes minimap quadrants
  - `Continuous`: uses continuous position
  - `ZoneBased`: derives broad game zones
  - `Combined`: combines quadrant, continuous, and zone-based approaches (default)
- **RotationSensitivity**: Legacy sensitivity value used by the current position mapper while deriving context (default: `1.0`)
- **TemplateImagePath**: Optional path to a real cropped player-marker template. Leave empty to use HSV/contour detection only.
- **DebugMode**: Enable debug logging for position detection (default: `false`)

### How it works

1. At the configured interval, the application captures the minimap region from the screen
2. If `TemplateImagePath` is configured, OpenCV tries template matching with that real image
3. If no template is configured or matching fails, OpenCV uses HSV color thresholding, morphology, and contour scoring
4. The detected position is converted into normalized coordinates, quadrant, zone, confidence, and detection method
5. The command builder evaluates `PositionModulation` rules, usually changing `Vibrate1` / `Vibrate2`
6. Detection failures are logged but do not interrupt the main runtime loop

### Notes

- The minimap coordinates need to be configured based on your screen resolution and League of Legends client settings
- The feature uses OpenCvSharp4 for image processing
- The detector is tested against `LoLovenseRainbowBridge.Tests/TestAssets/screenshot.jpg`, not a generated minimap
- Template matching should use a real cropped marker image; the app no longer creates a generated green-dot template
- HSV/contour detection is intentionally lightweight. It is good enough for a first local bridge, but HUD scale, minimap skin, color settings, and video compression can require tuning
- Position modulation is combined with all other Lovense rule contributions in the command plan
