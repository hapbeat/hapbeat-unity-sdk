# Hapbeat Unity SDK — QuickStart (no Kit)

The shortest way to feel a Hapbeat from Unity. Nothing has to be installed on the
device first: the Event Map entry is a **StreamClip**, so the SDK streams the bundled
WAV (`Audio/hit_80hz.wav`, a 0.25 s, 80 Hz pulse) from your PC over Wi-Fi.
No Kit, no Hapbeat Studio deploy.

You need a Hapbeat on the same Wi-Fi/LAN as the PC. The materials use URP Lit
(the Unity 6 "Universal 3D" template).

## A. No code — `QuickStart_A_NoCode.unity`

Press Play. The orange cube falls; when it hits the floor, the Hapbeat buzzes.

How it is wired:

1. `[Hapbeat Event Router]` holds the `HapbeatManager` (one per scene).
2. `QuickStartEventMap.asset` has one entry, **Hit** (Mode: StreamClip, Clip: `hit_80hz`, Gain 0.6).
3. `Cube` has a Rigidbody and a **Hapbeat Collision Trigger** with Event Map = `QuickStartEventMap`
   and Event = `Hit`.

To do the same on your own object: Add Component → `Hapbeat/Hapbeat Collision Trigger`, then
pick the Event Map and the Event.

## B. One line of code — `QuickStart_B_OneLine.unity`

Press Play. Three projectiles drop onto the blue `Player`; every hit buzzes the Hapbeat.

`Player` has a **Hapbeat UnityEvent Trigger** (Event = `Hit`) and `Scripts/PlayerHealth.cs`,
which plays the haptic with one line:

```csharp
[SerializeField] HapbeatUnityEventTrigger _damageHaptic;

public void TakeDamage(int amount)
{
    _health -= amount;
    _damageHaptic.Fire();   // <- the one line
}
```

Strength, clip and target stay in the Event Map entry, so you tune them there
(open it with `Hapbeat > Open Event Map`) without touching the code.

## Next step

Kit events (Command mode) play waveforms installed on the device and need a Kit
deployed with Hapbeat Studio. See the BasicExample and Showcase samples.
