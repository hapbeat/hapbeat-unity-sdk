# Hapbeat Unity SDK — context for AI coding agents

Single self-contained reference so an AI coding agent can use this SDK correctly
from one file. Unity package id: `com.hapbeat.sdk`. C# namespace: `Hapbeat`.

- last-verified-against: package 0.3.0 (requires Unity `6000.0`+)
- Source of truth is the code: public runtime API in `Runtime/HapbeatManager.cs`,
  the EventMap model in `Runtime/HapbeatEventMap.cs` + `Runtime/HapbeatEventEntry.cs`,
  WifiUdp routing / addressing in `Runtime/HapbeatClient.cs`, settings in
  `Runtime/HapbeatConfig.cs`. If this file
  disagrees with the code, the code wins.
- Canonical docs: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/
- Event id and wire format are defined by **hapbeat-contracts** (e.g.
  `device-addressing.md`); follow it, do not redefine here.

## What it is

A Unity SDK to drive Hapbeat haptic devices over Wi-Fi UDP on the LAN. 2D / 3D /
XR (Quest, Pico, Vision Pro). No cloud. `PLAY` / `STOP` / `STOP_ALL` unicast to
PONG-known devices and fall back to broadcast when none are known. `StreamClip`
instead waits for matching PONG-resolved endpoints and sends each `STREAM_*` packet
only to those explicit endpoints; it never broadcasts target-less `STREAM_DATA`.
`PING` / `CONNECT_STATUS` stay broadcast for discovery. The device also self-filters
by target address; UDP has no ACK ("late is worse than dropped"). It does NOT author
or flash Kits (that's Hapbeat Studio) and is not a Bluetooth transport.

## Install & connect

Unity Package Manager → `+` → **Add package from git URL...**:
```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```
Then add the manager to your scene: `GameObject > Hapbeat > Event Router` (creates
a `[Hapbeat Event Router]` GameObject with a `HapbeatManager`). It auto-connects
(opens the UDP socket) on `Awake` — no manual `Connect()` needed in the common case.

## Make it vibrate (minimal)

```csharp
using Hapbeat;

HapbeatManager.Instance.Play("sample-kit.sine_100hz", gain: 0.3f);  // gain 0..2, default 1.0
HapbeatManager.Instance.Play("sample-kit.sine_100hz", gain: 0.3f, pan: -1f);  // pan -1 left / 0 center / +1 right
HapbeatManager.Instance.Stop("sample-kit.sine_100hz");
HapbeatManager.Instance.StopAll();
```

`pan` on FIRE is expanded to per-channel gains by the device mixer and needs
**firmware with DEC-055 PLAY pan support**; older firmware ignores it and plays centered.

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
`displayName`, `category`, `eventName` (=> `eventId` is `category.eventName`, i.e.
`<kit-name>.<clip-name>`), `streamClip`, `loop`, `gain` (0..2), `target`,
`delayOffsetSeconds`, `notes`, `bindings`, `manifestOverride`. Effective wire gain =
`gain × manifest intensity` (`GetEffectiveGain()`).

## Public runtime API (`HapbeatManager.Instance`, verbatim)

```csharp
public static HapbeatManager Instance { get; }

public void Play(string eventId, float gain = 1.0f, string displayName = null, string target = null)
public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, string target = null)
public void Stop(string eventId, string displayName = null, string target = null)
public void StopAll(string target = null)
public void Ping()
public void Connect()
public void Disconnect()
public void Discover(int timeoutMs = 3000)

public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float gain = 1.0f, string target = null, bool loop = false)
public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float baselineGain, float initialGain, string target, bool loop)
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
public IReadOnlyList<HapbeatDevice> DiscoveredDevices { get; }
public string AppName { get; }     // before <p>/<g> placeholder substitution
public float  HapticDelaySeconds { get; }
public long   TimeOffsetUs { get; }
public event Action OnConnected, OnDisconnected;
public event Action<string> OnError;
public event Action<long> OnPong;  // round-trip time (us)
```

`HapbeatStreamPlayback` (returned by `StreamAudioClip`) — write per frame to
modulate one source: `float Gain { get; set; }`, `float Pan { get; set; }`,
`float BaselineGain { get; }`, `void ApplyGainModulation(float)`,
`bool IsStopped { get; }`, `bool IsActive { get; }`, `void Stop()`.

## Address Override (0.3.0) — one build, many headsets

Ship one build to N HMDs and let each send to its own Hapbeat. When an axis is
overridden, **every** send (Play / Stop / StopAll / StreamBegin) has its target
rewritten to that player / group, regardless of the EventMap entry's `target`.

```csharp
public const int AddressOverrideDisabled = -1;   // "leave this axis as the EventMap target says"

public void SetAddressOverride(int player, int group, bool persist = false)
public int  OverridePlayer { get; }
public int  OverrideGroup  { get; }
public void ClearPersistedAddressOverride()
public static bool TryGetPersistedAddressOverride(out int player, out int group)
public static int  ResolveEffectiveOverride(int configValue, int perDeviceValue)  // pure
public static string ApplyAddressPlaceholders(string appName, int overridePlayer, int overrideGroup)

// build-wide pinning (mirrors HapbeatConfig), read-only at runtime
public int  BuildOverridePlayer { get; }
public int  BuildOverrideGroup  { get; }
public bool IsPlayerForcedByBuild { get; }
public bool IsGroupForcedByBuild  { get; }

public const string PlayerPrefsKeyOverridePlayer = "Hapbeat.OverridePlayer";
public const string PlayerPrefsKeyOverrideGroup  = "Hapbeat.OverrideGroup";
```

- Valid numbers are `1..99`; anything else normalizes to `-1` (disabled).
- `persist: true` writes to `PlayerPrefs` (per-device state — never to the shared
  `HapbeatConfig` asset) and is restored on next launch.
- Precedence, per axis: `HapbeatConfig.buildOverride*` is `1-99` → forced (the panel /
  `SetAddressOverride` / `PlayerPrefs` are ignored, and nothing is written to
  `PlayerPrefs` for that axis) → else the persisted per-device value → else disabled.
- `appName` may contain `<p>` / `<g>`; they are replaced with the current override
  numbers (`-` when disabled) before sending, so the device OLED shows the pairing.
- `HapbeatClient.ResolveTarget(string target, int overridePlayer, int overrideGroup)`
  (static, no UnityEngine dependency) performs the target-string rewrite;
  `HapbeatClient.AddressMatches(string target, string deviceAddress)` mirrors the
  firmware's matching semantics; `HapbeatClient.NormalizeOverride(int)` does the
  1..99 / -1 normalization.
- `HapbeatAddressOverridePanel` (Runtime component, `Add Component >
  Hapbeat/Hapbeat Address Override Panel`) is a drop-in settings UI —
  `ScreenSpaceOverlay` or `WorldSpace` (VR, lazy-follow). Player -/+, Group -/+,
  Play, Apply, Exit. Public: `PlayerUp` / `PlayerDown` / `GroupUp` / `GroupDown` /
  `Apply`, `RegisterFocusable(Vector2Int, Button)`, `MoveFocus(Vector2Int)`,
  `ActivateFocused()`, `ShowFocusHighlight()`, `SnapToView()`, `PanelCanvasTransform`,
  `IsFollowingView`, `FollowVerticalOffset`, events `OnPlayRequested` / `OnExitRequested`.
- Editor: `Hapbeat > Open Runtime Status` shows saved / runtime / build-forced values.

## Trigger components (Inspector-only, no code)

Add via `Add Component > Hapbeat/...`. All reference an `HapbeatEventMap` + an entry:
- **Hapbeat Collision Trigger** (`HapbeatCollisionTrigger`) — physics On/TriggerEnter,
  2D/3D auto-detected; supports velocity-scaled gain. Attach to the colliding GO.
- **Hapbeat UnityEvent Trigger** (`HapbeatUnityEventTrigger`) — exposes
  `public void Fire()`, `public void FireWithGain(float gain)`, `public void Stop()`.
  Wire `Fire()` to UI Button `OnClick`, XR Interaction Toolkit events, Animation
  Events, or any UnityEvent.
- **Hapbeat Sequence Trigger** (`HapbeatSequenceTrigger`) — grab/hold/release phases.
- **Hapbeat Tick Trigger** (`HapbeatTickEmitter`) — snap/detent haptics from a
  continuous value (Slider / ScrollRect); `AbsolutePosition` or `AccumulatedMotion`.
- **Hapbeat Parameter Binding** (`HapbeatParameterBinding`) — maps a Transform/value
  to `StreamGain` / `StreamPan` on an active StreamClip playback in real time.
- **Hapbeat Action Helper** (`HapbeatActionHelper`) — instance-method wrappers for
  `Stop` / `StopAll` / `StopStream` / `Ping`, so singleton calls can be wired from
  Inspector UnityEvents.
- **Hapbeat Key Dispatcher** (`HapbeatKeyDispatcher`) — key → UnityEvent (Input System).
- **Hapbeat Status Overlay** (`HapbeatStatusOverlay`) — connection / RTT debug HUD.
- `HapbeatStateBehaviour` — a `StateMachineBehaviour`, **not** a component: select an
  Animator state → Add Behaviour. Separate entries for state Enter / Exit, optional
  `Required Previous State`, looping StreamClip auto-stops on state exit. (The
  pre-0.2.0 `HapbeatAnimatorTrigger` no longer exists.)

## Configuration (`HapbeatConfig`, `Assets > Create > Hapbeat > Config`)

`port` (UDP, default `7700`) · `appName` (max 16 chars, shown on the device OLED;
empty = `Application.productName`; supports `<p>` / `<g>`) · `buildOverridePlayer` /
`buildOverrideGroup` (default `-1`, clamped to `-1..99`; `1-99` pins that axis for the
whole build) · `pingInterval` (5 s) · `streamSendAheadSeconds` (0.05) · `commandUnicast` (default
`true`) · `hapticDelaySeconds` (0..0.5,
audio-latency compensation) · `enableLogging` / `verboseLogging`. Settings window:
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
`HapbeatEventEntry.StandardPositions` (`pos_neck`, `pos_chest`, `pos_abd`, …); build
one with `HapbeatEventEntry.BuildTarget(int player, string position)`. Device
addresses are always the canonical `player_<N>/<position>/group_<M>` and default to
`player_1` / `group_1` — number your demo groups from 2 up so an unconfigured device
is obvious. Address Override (above) rewrites the player / group slots of whatever
target the EventMap supplies.

## Gotchas

- Nothing buzzes but a device is online => the `eventId` is not in the deployed Kit
  (#1 cause), the `target` does not match (try `""`), or an Address Override is
  routing to a player/group no device is on. `Command` events need the Kit flashed
  in Studio; `StreamClip` works with no deploy.
- StreamClip accepts differing sample rates and mono/stereo clips; the SDK normalizes
  each source to 16 kHz stereo PCM16 and mixes matching sources per device endpoint.
  A source without a matching PONG-resolved endpoint returns a non-null playback in
  `Deferred` state and sends no stream packet until that endpoint is discovered.
- `IsConnected` only means the socket is open; use `AliveDeviceCount` / `OnPong` to
  know a device actually answered. StreamClip requires a matching PONG; one-shot
  commands can still use their broadcast fallback.
- Multi-homed PC: the one-shot command broadcast fallback may exit the wrong NIC —
  ensure the Hapbeat LAN's NIC has the route.
- Edit-mode test: the `HapbeatManager` Inspector has Connect / Discover / Play / Stop
  buttons (no Play mode needed). `Hapbeat > Open Event Map` lists all entries + where
  each trigger is attached; `Hapbeat > Open Runtime Status` shows the addressing state.
- Upgrading the package: delete the old `Assets/Samples/Hapbeat SDK/<old version>/`
  folder before re-importing samples (UPM does not remove it, and duplicate classes
  break compilation). `Hapbeat > Diagnostics/Check Sample Versions` detects this.

## Editor menus (top-level `Hapbeat`)

`Open Event Map` · `Open Batch Setup` · `Open Settings` · `Open Runtime Status` ·
`Create Event Router` · `Create Event Map` · `Create HapbeatSDK Folder` ·
`Initial Scene Setup` · `Samples/Augment XRI Hand Demo` (and
`Samples/Augment XRI Hand Demo (+ diagnostic Event Logger)`) ·
`Export Event Map (Selected)` / `Export Event Map (All in Project)` ·
`Normalize Audio Folder (16kHz · 2ch · PCM16)` · `Attach Event Logger to Selected` /
`Remove Event Logger Wiring from Selected` · `Logs/Start Recording` (+ Stop / Reveal
Current File / Open Logs Folder / Dump Last Recording to Console) ·
`Disable Verbose Log on All Hapbeat Components` · `Close Edit-mode Transport` ·
`Diagnostics/Check Sample Versions`. Also `GameObject > Hapbeat > Event Router`.

## More detail

When this single file is not enough, an agent can fetch:

- **Complete reference in one text file (recommended next step):** https://devtools.hapbeat.com/_llms-txt/unity-sdk.txt
- **Samples in this package** (import via Package Manager): `BasicExample`,
  `Showcase`, `XR Helpers`, `XRI Hand Demo (haptics add-on)`, `VR Config Example`
- **Concepts** (shared by every SDK): event id <-> kit https://devtools.hapbeat.com/docs/concepts/event-id-and-kit/ - command vs clip https://devtools.hapbeat.com/docs/concepts/fire-vs-clip/ - targeting https://devtools.hapbeat.com/docs/concepts/group-player-addressing/ - communication model https://devtools.hapbeat.com/docs/concepts/communication-model/
- Human docs: https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/ - Portal: https://devtools.hapbeat.com/
