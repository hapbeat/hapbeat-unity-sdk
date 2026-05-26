# ShowcaseEventMap

_Auto-generated from `Assets/HapbeatSDK/SDK_Samples/Showcase/EventMaps/ShowcaseEventMap.asset` (Hapbeat → Export Event Map). 編集は Unity の EventMap window 経由を推奨。手動編集はこの md ではなく .asset 側を変更してください。_

**Entry count**: 18

## Z1_pin_hit

- **id**: `1260162361b5486bb433bb2c1779cb59`
- **mode**: Command
- **eventId**: `showcase-kit.z1_pin_hit`
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.25
- **effective gain**: 0.25
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_open

- **id**: `097c5c77c643422290a4cc15e5da789e`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z2_door_open.wav` (16000 Hz, 2ch, 2.64s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_close

- **id**: `3ec09f14e87b40b0a2120de59f283c7d`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z2_door_close.wav` (16000 Hz, 2ch, 2.78s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.20
- **effective gain**: 0.20
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_slam

- **id**: `13c5585a0719421ba8b5b6e8bcf681bb`
- **mode**: Command
- **eventId**: `showcase-kit.z2_door_slam`
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.25
- **effective gain**: 0.25
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_lock

- **id**: `548dbbe23170484082274dcf288398ed`
- **mode**: Command
- **eventId**: `showcase-kit.z2_door_lock`
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_unlock

- **id**: `17f736b6c7664d74a9478c06bdbf8fd9`
- **mode**: Command
- **eventId**: `showcase-kit.z2_door_unlock`
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z2_door_rattle

- **id**: `96508c38734348408e681396f0f6dd89`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z2_door_rattle.wav` (16000 Hz, 2ch, 0.54s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.25
- **effective gain**: 0.25
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z3_hook_start

- **id**: `a81f3af1b37e40bba0a9e7f2a421e259`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z3_hook_start.wav` (16000 Hz, 2ch, 0.30s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.60
- **effective gain**: 0.60
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z3_hook_loop

- **id**: `a7a19cd41ce042f7977118c65f0ff078`
- **mode**: StreamClip (loop)
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z3_hook_loop.wav` (16000 Hz, 2ch, 10.00s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.50
- **effective gain**: 0.50
- **target**: broadcast (any device)
- **Parameter Bindings (1)**:
  - `StreamGain` ← VelocityMagnitude (input 0..3, Linear) → output 0..1.5

## Z3_hook_release

- **id**: `9c20ef5e159e44028435aef426257938`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z3_hook_release.wav` (16000 Hz, 2ch, 0.30s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.55
- **effective gain**: 0.55
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z4_stream_loop

- **id**: `9a75447994fe4046a1f4ce20a03069e3`
- **mode**: StreamClip (loop)
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z4_stream_loop.wav` (16000 Hz, 2ch, 10.00s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.50
- **effective gain**: 0.50
- **target**: broadcast (any device)
- **Parameter Bindings (2)**:
  - `StreamGain` ← SliderValue (input 0..1, Linear) → output 0..1
  - `StreamPan` ← SliderValue (input -1..1, Linear) → output -1..1

## Z4_slider_tick

- **id**: `699d0b5d6fa84f3c9ec316feb65be7d3`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z4_slider_click.wav` (16000 Hz, 2ch, 0.01s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_tar_hit_light

- **id**: `c840e0dda57242409e8783ef15fc7eda`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_tar_hit_light.wav` (16000 Hz, 2ch, 0.05s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.45
- **effective gain**: 0.45
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_tar_hit_heavy

- **id**: `74313ca4328146ad8bbfe9b9a4459d95`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_tar_hit_heavy.wav` (16000 Hz, 2ch, 0.84s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.40
- **effective gain**: 0.40
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_charge_loop

- **id**: `93ea5314391546d5b9264fb3b6c1928a`
- **mode**: StreamClip (loop)
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_charge_loop.wav` (16000 Hz, 2ch, 10.00s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.14
- **effective gain**: 0.14
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_shot_light

- **id**: `17df415ff7784c9baf207ee84ba64b13`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_shot_light.wav` (16000 Hz, 2ch, 0.09s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_shot_heavy

- **id**: `36b35d42d61847558df415c1766187ac`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_shot_heavy.wav` (16000 Hz, 2ch, 0.47s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.40
- **effective gain**: 0.40
- **target**: broadcast (any device)
- **Parameter Bindings**: none

## Z5_charge_thd

- **id**: `41db498a8b3942bea009e11cae943771`
- **mode**: StreamClip
- **streamClip**: `Assets/HapbeatSDK/Kits/showcase-kit/stream-clips/z5_charge_thd.wav` (16000 Hz, 2ch, 0.20s)
- **gain (authored)**: 1.00
- **manifest intensity (cached)**: 0.30
- **effective gain**: 0.30
- **target**: broadcast (any device)
- **Parameter Bindings**: none

