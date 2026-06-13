# Propositions

Ideas for making the bridge more expressive over time.

## Spatial Map Feel

The dream: the player being on the right side of the map could bias the response
toward a "right-side" toy or function, while upper-right could combine a right
texture with a lighter upper/ascending texture.

Current blocker: the League Live Client data currently parsed by this app does
not expose reliable active-player map coordinates. The bridge should not pretend
to know position without a real data source.

Possible paths:

- Parse another reliable game-state source if Riot exposes coordinates.
- Use minimap OCR/computer vision as an optional companion module.
- Add a manual companion input or overlay for coarse map zones.
- Map zones to toy IDs: left/right toys, upper/lower textures, jungle/lane modes.

## Objective Waves

Dragon, Baron, Herald, turret, and inhibitor events could trigger distinctive
waves:

- dragon: rising `Vibrate,All`
- Baron: deeper slower `Thrusting`/`Suction`
- turret: short rhythmic pulses
- inhibitor: longer high-pressure pattern

This needs richer event classification than the current kill/multikill mapping.

## Teamfight Burst Mode

Detect clusters of champion kill events in a short time window and temporarily
switch from steady performance mapping to a chaotic burst plan:

- quick `All` rise
- `Rotate` or `Oscillate` texture
- stronger penta finish

## Heartbeat Near Death

When health data becomes available, low HP could create a heartbeat-like pattern:

- small repeated `Vibrate`
- short pause
- intensity rising as danger increases

The current parser does not read active-player health yet.

## Laning Phase Texture

Early game could be restrained and positional:

- creep score advantage adds subtle vibration
- ward score adds light support texture
- death resets the pattern

## Jungle Tension Ramp

If jungle/objective state becomes available, time near objective spawns could
slowly raise intensity before fights happen.

## Capability-Aware Toy Mapping

Lovense device info may expose function names for connected toys. A future
version could filter generated actions per toy:

- Nora/Max: `Rotate`/`Pump`
- Solace-style strokers: `Stroke`, `Position`, `PatternV2`
- vibration-only toys: `Vibrate`/`All`

That would let the planner be bold without sending ignored actions.
