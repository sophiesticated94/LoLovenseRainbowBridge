# Propositions

Ideas for future versions after the calculator-driven bridge.

## Champion Identity Profiles

Give each champion a different response vocabulary:

- assassins: sharp short spikes after kills
- tanks: slower pressure ramps during long fights
- supports: softer assist and vision textures
- marksmen: scaling intensity tied to CS and late-game time

This would need a small champion-profile config keyed by champion name.

## Item Power Spike Motifs

Parse active-player items and react when important purchases appear:

- first completed item: short confirmation wave
- mythic-style or high-cost items: stronger ramp
- defensive item after repeated deaths: stabilizing low texture

The Live Client item payload is available, but the current parser does not map
items into bridge state yet.

## Rune-Aware Texture

Use rune identity to change the emotional grammar:

- Electrocute-style runes: short burst after trades
- Conqueror-style runes: sustained ramp during fights
- Guardian-style runes: softer assist/ally-protection texture

This needs parsing active-player runes and a curated rune mapping table.

## Adaptive Personalization

Let the bridge learn preferred intensity over time:

- remember manual max intensity preferences
- lower noisy textures that trigger too often
- strengthen rare events that should feel special

This should be opt-in and stored locally, with an easy reset.

## Per-Toy Feeling Calibration

Capability-aware mapping now knows what a toy can probably do, but not how each
function feels to a specific person. A calibration mode could ask for quick
ratings after short test pulses:

- comfortable maximum per function and per toy
- preferred stereo balance for dual-vibration toys
- which functions feel too sharp, too soft, or too distracting
- separate profiles for playful, restrained, and intense sessions

The bridge could then keep the same game logic while translating it through a
personal comfort curve.

## Multi-Toy Orchestration

Current capability resolution keeps command output viable, especially for one
target toy. A later version could intentionally orchestrate multiple connected
toys:

- route base pressure to one toy and event bursts to another
- use stereo toys for map position and single-motor toys for heartbeat
- send separate per-toy Function commands when capabilities diverge
- save per-toy timelines in SQLite replay

This should stay explicit, because accidental all-toy playback would be a very
different experience from a single selected toy.

## Pattern Presets

Add named pattern presets:

- restrained
- arcade
- cinematic
- chaos
- support-main

Presets would choose calculator weights and Lovense action preferences without
changing core code.

## Lovense Pattern Export

SQLite is now the internal gameplay recording format, but export could make
recordings useful outside the bridge:

- export simple vibration timelines to Lovense Pattern or PatternV2
- export media-sync friendly FunScript-style position curves
- keep richer multi-function/context data in SQLite as the source of truth

This should be an export step, not the primary storage format, because pattern
formats cannot represent the full bridge context cleanly.

## Replay Analysis Dashboard

Build a small local dashboard over `data/gameplay.sqlite`:

- show intensity over time
- mark kills, deaths, objectives, teamfights, and minimap rotation
- compare calculated base versus temporary boosts
- scrub the Lovense command timeline before replaying it

This would make tuning much faster than reading JSONL and SQLite rows by hand.

## Minimap Auto-Calibration

The first minimap rotation implementation uses configured screen coordinates.
Next step: make calibration easier and less brittle:

- detect the minimap frame automatically from a full screenshot
- save per-resolution and per-HUD-scale profiles
- warn when the captured region does not look like a minimap
- expose confidence and last detected marker in `track.log`

This would make the feature friendlier for different monitors, borderless/fullscreen
modes, and non-default HUD scales.

## Visual Calibration Overlay

Add a small local calibration window:

- show the live minimap crop
- draw the detected marker and confidence
- let the user drag/resize the capture region
- save the result back to config or a local profile

This would turn minimap tuning from guesswork into a one-minute setup step.

## ML Minimap Detector

OpenCV HSV/contour detection is lightweight, but it can struggle with skins,
compression, colorblind settings, and crowded fights. A future detector could use
a small ONNX/YOLO model trained on real minimap crops:

- classify own player, allies, enemies, objectives, and pings
- detect multiple champions at once
- estimate teamfight density from minimap alone
- keep the current OpenCV detector as the no-model fallback

This needs a dataset and a careful opt-in model download story.

## Path And Direction Feel

Once minimap detection is stable, map movement over time instead of only current
position:

- stronger right/left texture while rotating through river
- lane-specific patterns during long pushes
- short pulses when crossing from jungle to lane
- special ramp when moving toward Baron, dragon, or enemy base

This should be based on detected movement vectors, not guessed game state.
