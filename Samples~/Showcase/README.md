# Hapbeat Unity SDK — Showcase Sample

A keyboard-and-mouse walkthrough of every major Hapbeat SDK feature.
No XR device required.

## What you can learn

| Zone | What you do | SDK feature on display |
|---|---|---|
| **Z1 Bowling** | Left-click to launch a ball into pins | `HapbeatCollisionTrigger` (VelocityScaled), Batch Setup |
| **Z2 Door** | Press F to open / close the door | `HapbeatStateBehaviour` on Animator states (Open / Closed / Locked) |
| **Z3 Fishing** | Left-click to grab / hold / release the hook | `HapbeatSequenceTrigger` (Fire → Loop → Stop) + `HapbeatParameterBinding` (velocity → gain modulation) |
| **Z4 Stream Console** | Drag the slider, press Space to start the stream | `HapbeatTickEmitter` on a UI Slider + dynamic `HapbeatStreamPlayback.Gain` / `Pan` |
| **Z5 Target Range** | Hold left mouse to charge, release to fire | `ChargeShooter` (script directly calling `HapbeatManager.StreamAudioClip` with loop / shot delays) + `HapbeatUnityEventTrigger` on target hits |

Scene-wide hotkeys (`GlobalHotkeys.cs`):

- **Q** — fire `manual_fire`
- **1–5** — fire `burst` with scaled gain
- **P** — `HapbeatManager.Ping()` (shows round-trip)

The **Target Picker** UI at the top of the scene (Both / Neck / Arm) switches
the runtime target for Z4 / Z5 / hotkey events. Z1–Z3 keep their target fixed
on the EventMap entry — a side-by-side comparison of "design-time target" vs
"runtime-overridden target".

## Getting started

### 1. Import the sample

In Package Manager, open the Hapbeat SDK package and import **Showcase**.
Files land at `Assets/Samples/Hapbeat SDK/<version>/Showcase/`:

- `Scenes/Showcase.unity`
- `EventMaps/ShowcaseEventMap.asset` (~18 entries)
- `Animation/DoorAnimator.controller`
- `Kit/showcase-kit/` (manifest + clips, schema 2.0.0)
- `Audio/`, `Scripts/`, `Models/`, `Textures/`, `Materials/`, `Prefabs/`

### 2. Deploy into the user-owned area (optional but recommended)

The Sample folder is read-only-ish (re-importing the sample resets it).
To author your own changes safely, deploy into `Assets/HapbeatSDK/` once:

1. Open Hapbeat Studio (or the Hapbeat Helper) and connect the device.
2. Deploy the `showcase-kit` so `Assets/HapbeatSDK/Kits/showcase-kit/` is populated.
3. Copy `Scenes/`, `EventMaps/`, `Animation/` into
   `Assets/HapbeatSDK/SDK_Samples/Showcase/` so edits aren't lost on next
   sample re-import.

### 3. Play

Open `Showcase.unity`, make sure the Hapbeat device is online (via Studio or
Helper), and press Play. WASD to move, mouse to look, then visit each Zone.

> **AudioClip references** — the EventMap points at `Audio/*.wav` under the
> imported sample folder. Don't delete the sample folder while the EventMap
> still references those clips.

## EventMap layout

The Showcase uses **schema 2.0.0** Kit manifests (Studio's current format).
Each `event_id` has the form `showcase-kit.<name>`. All entries are
**StreamClip** by default so the device only needs Wi-Fi connectivity — no
firmware-side Kit install required.

Key entries:

| Zone | Entry | Loop | Notes |
|---|---|:---:|---|
| Z1 | `z1_pin_hit` | – | Fired per-pin via `HapbeatCollisionTrigger` |
| Z2 | `z2_door_{open, close, lock, unlock, rattle, slam}` | – | Per-state via `HapbeatStateBehaviour` |
| Z3 | `z3_hook_start` / `z3_hook_loop` / `z3_hook_release` | loop | `HapbeatSequenceTrigger` Fire / Loop / Stop |
| Z4 | `z4_slider_click` | – | `HapbeatTickEmitter` detent ticks |
| Z4 | `z4_stream_loop` | loop | Long-form stream demo |
| Z5 | `z5_charge_loop` | loop | Charge rumble while LMB held |
| Z5 | `z5_charge_thd` | – | Fires once at threshold cross |
| Z5 | `z5_shot_{light, heavy}` | – | Release shot (variant chosen by charge level) |
| Z5 | `z5_tar_hit_{light, heavy}` | – | Target receiver impact via `HapbeatUnityEventTrigger` |
| Hotkey | `manual_fire`, `burst` | – | Scene-wide Q / 1-5 keys |

`gain` and `intensity` semantics: **effective gain = `entry.gain × manifest.intensity × per-trigger modulators`**.
Set `entry.gain = 1.0` in the EventMap to use the authored manifest intensity
directly, or scale up / down for per-entry overrides.

### Switching to Command (Fire) mode (optional)

After you've validated StreamClip, you can move any entry to Command mode for:

- lower latency (only an event ID on the wire, no PCM stream)
- no Unity-side AudioClip dependency (device plays the Kit-installed WAV)
- Kit content authoring stays in Studio (Unity-side edits unnecessary)

In the EventMap, switch the entry `Mode` from `StreamClip` to `Command` and
make sure the device has the corresponding Kit deployed. The entry's
`category + eventName` form the wire `event_id`
(e.g. `showcase-kit.z1_pin_hit`).

## About the `showcase-kit`

`Kit/showcase-kit/showcase-kit-manifest.json` provides the per-event
`intensity` values that drive both Command and StreamClip paths. The
`install-clips/` / `stream-clips/` subfolders ship populated so the same Kit
can be used in either mode on the device.

Manifest filename convention is `<kit-name>-manifest.json`
(here: `showcase-kit-manifest.json`). The SDK auto-discovers any manifest
matching the pattern under `Assets/HapbeatSDK/Kits/`.

Each EventMap entry also has an optional **manifest override** field next to
the Test Play button. When set, the SDK looks up `intensity` from that
specific manifest first. Leave it empty for auto-discovery across all kits.

## Known constraints

- The included 3D models are CC0 / CC BY 3.0 assets — see
  [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for full credits.
- `ChargeShooter` (Z5) ships with default projectile prefabs; you can swap
  them in the inspector if you want a different shot visual.

## License

Code in this sample is covered by the package's main `LICENSE`. Bundled audio,
models, and textures are credited individually in `THIRD_PARTY_NOTICES.md`
(CC0 sources grouped, CC BY 3.0 assets attributed per file).
