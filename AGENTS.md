# Hapbeat Unity SDK — context for AI coding agents

Single self-contained reference so an AI coding agent can use this SDK correctly
from one file. Unity package id: `com.hapbeat.sdk`. C# namespace: `Hapbeat`.

- last-verified-against: package 0.2.1 (requires Unity `6000.0`+)
- Source of truth is the code: public runtime API in `Runtime/HapbeatManager.cs`,
  the EventMap model in `Runtime/HapbeatEventMap.cs` + `Runtime/HapbeatEventEntry.cs`,
  the code-first bridge in `Runtime/HapbeatBridge.cs`, settings in
  `Runtime/HapbeatConfig.cs`. If this file disagrees with the code, the code wins.
- Canonical docs: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/
- Event id and wire format are defined by **hapbeat-contracts** (e.g.
  `device-addressing.md`); follow it, do not redefine here.

## What it is

A Unity SDK to drive Hapbeat haptic devices over Wi-Fi UDP broadcast. 2D / 3D /
XR (Quest, Pico, Vision Pro). No cloud; works on the LAN. The device self-filters
by group/target; UDP has no ACK ("late is worse than dropped"). It does NOT author
or flash Kits (that's Hapbeat Studio) and is not a Bluetooth transport.

## Install & connect

Unity Package Manager → `+` → **Add package from git URL...**:
```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```
Then add the manager to your scene: `GameObject > Hapbeat > Event Router` (creates
a `[Hapbeat Event Router]` GameObject with a `HapbeatManager`). It auto-connects
(opens UDP broadcast) on `Awake` — no manual `Connect()` needed in the common case.

## Make it vibrate (minimal)

```csharp
using Hapbeat;

HapbeatManager.Instance.Play("impact.landing", gain: 0.3f);  // gain 0..2, default 1.0
HapbeatManager.Instance.Stop("impact.landing");
HapbeatManager.Instance.StopAll();
```

## Core model: trigger vs tuning, linked by event id

- **Trigger side** (when/where to fire): a Trigger component or a `Play(id)` call.
- **Tuning side** (what/how strong): the **EventMap** entry + the Kit's
  `manifest.intensity`. Keep gains/targets out of firing code; put them in the
  EventMap (and the Studio-authored Kit), referenced only by **event id**.

Recommended path = an `HapbeatEventMap` ScriptableObject
(`Assets > Create > Hapbeat > Event Map`) + a Trigger component. Triggers reference
entries by a **stable GUID** (`HapbeatEventEntry.id`), so reordering entries never
breaks wiring. Direct `Play(id, gain)` bypasses the EventMap (gain hardcoded).

`HapbeatEventEntry` fields: `mode` (`HapticMode.Command` | `HapticMode.StreamClip`),
`displayName`, `category`, `eventName` (=> `eventId` is `category.eventName`),
`streamClip`, `loop`, `gain` (0..2), `target`, `delayOffsetSeconds`, `notes`,
`bindings`. Effective wire gain = `gain × manifest intensity` (`GetEffectiveGain()`).

## Public runtime API (`HapbeatManager.Instance`, verbatim)

```csharp
public static HapbeatManager Instance { get; }

public void Play(string eventId, float gain = 1.0f, string displayName = null, string target = null)
public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, string target = null)
public void Stop(string eventId, string displayName = null, string target = null)
public void StopAll(string target = null)
public void Ping()
public void Connect()
public void ConnectToBridge()
public void Disconnect()
public void Discover(int timeoutMs = 3000)

public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float gain = 1.0f, string target = null, bool loop = false)
public void StopStream()
public void StopStreamWithFlush(string target = null)
```
Useful state / events:
```csharp
public bool IsConnected { get; }   // socket open (stays true even if device is off)
public int  AliveDeviceCount { get; }   // devices that PONGed recently; HUD: "N connected"
public bool IsAlive { get; }
public bool IsStreaming { get; }
public HapbeatStreamPlayback ActivePlayback { get; }
public event Action OnConnected, OnDisconnected;
public event Action<string> OnError;
public event Action<long> OnPong;  // round-trip time (us)
```

`HapbeatStreamPlayback` (returned by `StreamAudioClip`) — write per frame to
modulate one source: `float Gain { get; set; }`, `float Pan { get; set; }`,
`bool IsStopped { get; }`, `bool IsActive { get; }`, `void Stop()`.

## Trigger components (Inspector-only, no code)

Add via `Add Component > Hapbeat/...`. All reference an `HapbeatEventMap` + an entry:
- **Hapbeat Collision Trigger** (`HapbeatCollisionTrigger`) — physics On/TriggerEnter,
  2D/3D auto-detected; supports velocity-scaled gain. Attach to the colliding GO.
- **Hapbeat UnityEvent Trigger** (`HapbeatUnityEventTrigger`) — exposes
  `public void Fire()`, `public void FireWithGain(float gain)`, `public void Stop()`.
  Wire `Fire()` to UI Button `OnClick`, XR Interaction Toolkit events, Animation
  Events, or any UnityEvent.
- **Hapbeat Sequence Trigger** (`HapbeatSequenceTrigger`) — grab/hold/release phases.
- **Hapbeat Parameter Binding** (`HapbeatParameterBinding`) — maps a Transform/value
  to `StreamGain` / `StreamPan` on an active StreamClip playback in real time.
- `HapbeatStateBehaviour` — a `StateMachineBehaviour` you attach to an Animator state.

## Code-first option: subclass `HapbeatBridge`

For per-call gain logic centralized in one file. Place on the Router GO; assign an
EventMap. Protected helpers fire **by `displayName`**:
```csharp
protected void Play(string displayName, float gainOverride = -1f)
protected void PlayByIndex(int entryIndex, float gainOverride = -1f)
protected void PlayScaled(string displayName, float velocity, float minVelocity = 0f, float maxVelocity = 10f)
protected void PlayWithCurve(string displayName, float inputValue, AnimationCurve curve)
protected void Stop(string displayName)
protected void StopAll()
```

## Configuration (`HapbeatConfig`, `Assets > Create > Hapbeat > Config`)

`port` (UDP, default `7700`) · `group` (`-1`=no filter, `0`=broadcast, `1..254`) ·
`appName` (max 16 chars, shown on device OLED; empty = `Application.productName`) ·
`useBridge` (ESP-NOW, off) · `bridgeHost` (`127.0.0.1`) · `pingInterval` (5 s) ·
`streamSendAheadSeconds` (0.05) · `hapticDelaySeconds` (0..0.5, audio-latency
compensation) · `enableLogging` / `verboseLogging`. Settings window:
`Hapbeat > Open Settings`.

## Command vs StreamClip (same EventMap, branches on `mode`)

| `HapticMode` | what happens | pre-deploy |
|---|---|---|
| `Command` | SDK sends PLAY by `eventId`; device plays its installed clip | yes (flash Kit in Studio) |
| `StreamClip` | SDK reads the `streamClip` AudioClip and streams PCM16 over UDP | no |

Rule of thumb: prototype with StreamClip, ship with Command.

## Targeting (device-addressing)

`target` strings: `player_1/pos_chest` (one), `*/pos_neck` (all neck), `group_<N>`
suffix, `""`/`null` = broadcast. Standard positions live in
`HapbeatEventEntry.StandardPositions` (`pos_neck`, `pos_chest`, `pos_abd`, …).

## Gotchas

- Nothing buzzes but a device is online => the `eventId` is not in the deployed Kit
  (#1 cause), or `target` does not match (try `""`). `Command` events need the Kit
  flashed in Studio; `StreamClip` works with no deploy.
- StreamClip WAVs should be 16 kHz; one stream session at a time — a new source must
  match the active session's rate/channels/`target` or it's rejected (warning + null).
- `IsConnected` only means the socket is open; use `AliveDeviceCount` / `OnPong` to
  know a device actually answered.
- Multi-homed PC: UDP broadcast may exit the wrong NIC — ensure the Hapbeat LAN's NIC
  has the route.
- Edit-mode test: the `HapbeatManager` Inspector has Connect / Discover / Play / Stop
  buttons (no Play mode needed). `Hapbeat > Open Event Map` lists all entries +
  where each trigger is attached.

## More detail

When this single file is not enough, an agent can fetch:

- **Complete reference in one text file (recommended next step):** https://devtools.hapbeat.com/_llms-txt/unity-sdk.txt
- **Samples in this package:** `Samples~/` (BasicExample, Tutorial — import via Package Manager)
- **Concepts** (shared by every SDK): event id <-> kit https://devtools.hapbeat.com/docs/concepts/event-id-and-kit/ - command vs clip https://devtools.hapbeat.com/docs/concepts/fire-vs-clip/ - targeting https://devtools.hapbeat.com/docs/concepts/group-player-addressing/
- Human docs: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/ - Portal: https://devtools.hapbeat.com/
