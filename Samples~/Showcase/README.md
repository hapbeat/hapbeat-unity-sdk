# Hapbeat Unity SDK — Showcase Sample

A keyboard-and-mouse walkthrough of every major Hapbeat SDK feature.
No XR device required.

## What you can learn

| Zone | What you do | SDK feature on display |
|---|---|---|
| **Z1 Bowling** | Left-click to launch a ball into the pins (Space resets them) | `HapbeatCollisionTrigger` (VelocityScaled) on `BowlingPin.prefab`, shared by all 6 pins |
| **Z2 Door** | F opens / closes the door, G slams it, L locks / unlocks it | `HapbeatStateBehaviour` on the Animator states `Open` / `Closed` / `Slam` / `LockEngage` / `LockRelease` / `LockedRattle` |
| **Z3 Fishing** | Hold the left mouse button to hang an object on the line, release to drop it | `HapbeatSequenceTrigger` (Start → Loop → Stop) called from `FishingController` + `HapbeatParameterBinding` (object velocity → loop gain) |
| **Z4 Stream Console** | Space starts / stops the stream; drag the Gain / Pan sliders | `HapbeatUnityEventTrigger` (looping StreamClip) + `HapbeatParameterBinding` on each slider (slider value → stream gain / pan) + `HapbeatTickEmitter` (slider ticks). `AddressOverridePanel` sets the player / group override at runtime |
| **Z5 Charge & Shoot** | Hold the left mouse button to charge, release to fire | `ChargeShooter` calls `HapbeatManager.StreamAudioClip` directly (no Trigger component) and scales the charge loop with `ApplyGainModulation`; `TargetReceiver` → `HapbeatUnityEventTrigger` on target hits |

Scene-wide controls:

- **WASD** / mouse — move / look
- **Tab** — lock / unlock the cursor (unlock it to use UI)
- **1–5** — switch zones (`ZoneSwitcher` deactivates every zone except the current one)
- **Q** — fire `Z5_tar_hit_light` once (`HapbeatKeyDispatcher` → `HapbeatUnityEventTrigger`)
- **P** — `HapbeatManager.Ping()`; the HUD shows the round-trip time (`GlobalHotkeys`)

Q and P are wired as UnityEvents on `[Hapbeat Event Router]`; `GlobalHotkeys`
only updates the HUD when a Pong arrives.

## Getting started

### 1. Import the sample

In Package Manager, open the Hapbeat SDK package and import **Showcase**.
Files land at `Assets/Samples/Hapbeat SDK/<version>/Showcase/`:

- `Showcase.unity` (the scene)
- `EventMaps/ShowcaseEventMap.asset` (18 entries) + `ShowcaseEventMap.md` (a readable summary)
- `Animation/DoorAnimator.controller` + per-state `.anim` clips
- `Kit/showcase-kit/` (manifest + clips, schema 2.0.0)
- `Audio/`, `Scripts/`, `Models/`, `Textures/`, `Materials/`, `Prefabs/`

### 2. Play

Open `Assets/Samples/Hapbeat SDK/<version>/Showcase/Showcase.unity` and press
Play. Make sure your Hapbeat device is online (via Studio or the Helper).
Press 1–5 to switch zones; WASD to move, mouse to look.

> **Note** — if you intend to edit the sample (scene, EventMap, scripts, etc.),
> copy the Showcase folder somewhere under `Assets/` first. Re-importing the
> sample via Package Manager overwrites the original location, so in-place
> edits would be lost. (Standard Unity sample behaviour.)

If you want to use the device-side Kit (Command mode events), open Hapbeat
Studio or the Helper and deploy the `showcase-kit` to the device.

## EventMap layout

`EventMaps/ShowcaseEventMap.asset` has 18 entries, all **StreamClip**, so the
device only needs Wi-Fi connectivity — no Kit install on the device.
`EventMaps/ShowcaseEventMap.md` lists every entry (clip, gain, bindings); it is
generated from the `.asset` by **Hapbeat → Export Event Map**.

| Zone | Entry | Loop | Used by |
|---|---|:---:|---|
| Z1 | `Z1_pin_hit` | – | `HapbeatCollisionTrigger` on each pin |
| Z2 | `Z2_door_open` / `Z2_door_close` / `Z2_door_slam` | – | `HapbeatStateBehaviour` on `Open` / `Closed` / `Slam` |
| Z2 | `Z2_door_lock` / `Z2_door_unlock` / `Z2_door_rattle` | – | `HapbeatStateBehaviour` on `LockEngage` / `LockRelease` / `LockedRattle` |
| Z3 | `Z3_hook_start` / `Z3_hook_loop` / `Z3_hook_release` | loop (`Z3_hook_loop`) | `HapbeatSequenceTrigger` Start / Loop / Stop; the loop entry carries the velocity → gain binding |
| Z4 | `Z4_stream_loop` | loop | `HapbeatUnityEventTrigger` (Space); the entry carries the slider → gain / pan bindings |
| Z4 | `Z4_slider_tick` | – | `HapbeatTickEmitter` on the Gain / Pan sliders |
| Z5 | `Z5_charge_loop` | loop | `ChargeShooter`: rumble while charging |
| Z5 | `Z5_charge_thd` | – | `ChargeShooter`: once when the charge crosses the threshold |
| Z5 | `Z5_shot_light` / `Z5_shot_heavy` | – | `ChargeShooter`: on release (chosen by charge level) |
| Z5 | `Z5_tar_hit_light` / `Z5_tar_hit_heavy` | – | `HapbeatUnityEventTrigger` from `TargetReceiver.OnHitLight` / `OnHitHeavy`; Q also fires `Z5_tar_hit_light` |

`gain` and `clip_gain` semantics: **sent value = `clamp(clip_gain × entry.gain × per-trigger modulators, 0, 1)`**,
where `clip_gain` is the clip's strength from the Kit manifest and the rest is the app's gain (`app_gain`).
Set `entry.gain = 1.0` in the EventMap to use the authored `clip_gain`
directly, or scale up / down for per-entry overrides (above 1 lifts a `clip_gain` below 1, capped at 1).

### Switching to Command (Fire) mode (optional)

After you've validated StreamClip, you can move any entry to Command mode for:

- lower latency (only an event ID on the wire, no PCM stream)
- no Unity-side AudioClip dependency (device plays the Kit-installed WAV)
- Kit content authoring stays in Studio (Unity-side edits unnecessary)

In the EventMap, switch the entry `Mode` from `StreamClip` to `Command` and
make sure the device has the corresponding Kit deployed. The entry's
`category + eventName` form the wire `event_id`
(e.g. `showcase-kit.z1_pin_hit`). `Z2_door_open`, `Z2_door_close`,
`Z3_hook_loop`, `Z4_stream_loop` and `Z5_charge_loop` have no
`category` / `eventName` yet — fill them in before switching those entries.

## About the `showcase-kit`

`Kit/showcase-kit/showcase-kit-manifest.json` provides the per-event
`clip_gain` values that drive both Command and StreamClip paths. The
`install-clips/` / `stream-clips/` subfolders ship populated so the same Kit
can be used in either mode on the device.

Manifest filename convention is `<kit-name>-manifest.json`
(here: `showcase-kit-manifest.json`). The SDK auto-discovers any manifest
matching the pattern under `Assets/HapbeatSDK/Kits/`.

Each EventMap entry also has an optional **manifest override** field next to
the Test Play button. When set, the SDK looks up `clip_gain` from that
specific manifest first. Leave it empty for auto-discovery across all kits.
Every Showcase entry sets it to `showcase-kit-manifest.json`.

## Known constraints

- The included 3D models are CC0 / CC BY 3.0 assets — see
  [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for full credits.
- `ChargeShooter` (Z5) ships with default projectile prefabs; you can swap
  them in the inspector if you want a different shot visual.

## License

Code in this sample is covered by the package's main `LICENSE`. Bundled audio,
models, and textures are credited individually in `THIRD_PARTY_NOTICES.md`
(CC0 sources grouped, CC BY 3.0 assets attributed per file).
