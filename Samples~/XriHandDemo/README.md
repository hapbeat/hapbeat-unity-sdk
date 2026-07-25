# XRI Hand Demo (haptics add-on)

Adds Hapbeat haptics to the **XR Interaction Toolkit "Hands Interaction Demo"** scene.

The XRI sample scene is licensed under the Unity Companion License, so a modified copy
of it cannot be redistributed. This sample therefore ships only the Hapbeat side —
the EventMap, the haptic Kit, and an Editor command that applies the wiring to the
scene **you** import from the XRI package.

## Contents

| File | What it is |
|---|---|
| `HandsDemoEventMap.asset` | 10 haptic entries (grab / hold / UI click / scratch / snap / poke) |
| `Kit/hand-demo-kit/` | The 9 stream clips those entries play, plus the kit manifest |

## Setup

1. **Package Manager → XR Interaction Toolkit → Samples**: import
   *Starter Assets*, *Hands Interaction Demo* (and the XRI dependencies it asks for).
   Open `HandsDemoScene.unity`.
2. **Package Manager → Hapbeat SDK → Samples**: import *XR Helpers* and
   *XRI Hand Demo (haptics add-on)*.
3. Menu **Hapbeat → Samples → Augment XRI Hand Demo**.
4. Deploy `Kit/hand-demo-kit` to the device with Hapbeat Studio, then press Play.

Use **Augment XRI Hand Demo (+ diagnostic Event Logger)** instead of step 3 if you want
the poke button to also log every XRI interactable event to the Console — useful when
deciding which event to wire haptics to, noisy otherwise.

## Notes

- The command is idempotent: running it again adds nothing and reports what it skipped.
  Everything it does is a single Undo step.
- It never edits XRI's own select events. The two `XR Helpers` filter components sit in
  front of the socket interactions and expose hand-vs-socket specific events, which is
  what the haptics are wired to.
- If your XRI version renames or moves GameObjects, the command applies what it can and
  logs a warning naming every path it could not find — check the Console after running it.
