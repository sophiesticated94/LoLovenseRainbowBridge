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

## Pattern Presets

Add named pattern presets:

- restrained
- arcade
- cinematic
- chaos
- support-main

Presets would choose calculator weights and Lovense action preferences without
changing core code.

## Replay Calibration Mode

Feed saved or recorded Live Client JSONL into the calculators:

- replay a match timeline without LoL running
- compare generated command plans
- tune thresholds safely in dry-run mode

This would make iteration much faster than testing only in live games.

## Spatial Minimap Companion

Map-position feel is still desirable, but should only be added with a truthful
data source:

- minimap OCR/computer vision companion
- manual coarse zone overlay
- future Riot-supported coordinate source if one appears

The bridge should not fake left/right or upper/lower map sensations without real
position data.
