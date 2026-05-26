# Showcase Sample — Third Party Notices

This sample bundles third-party 3D models, textures, and audio. CC0 sources are
listed for provenance; CC BY assets are credited per-file as required by their
license.

---

## 3D Models (`Models/`)

### CC BY 3.0 — attribution required

These two assets require attribution. Both are © their respective authors,
distributed under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).

| File | Title | Author | Source |
|---|---|---|---|
| `Models/Z1_Bowling/bowling_pin.obj` | Bowling Pin | Jakob Hippe | <https://poly.pizza/m/d1ZCN1qopib> |
| `Models/Z5_ChargeShot/Missile.obj` | Missile | Poly by Google | <https://poly.pizza/m/dPVCvXP-S58> |

### CC0 — public domain

No attribution required, listed for provenance.

| File | Title | Author | Source |
|---|---|---|---|
| `Models/Z2_Door/Door.fbx` | Door | Quaternius | <https://poly.pizza/m/a948jjnuaL> |
| `Models/Z3_Fishing/FishingRod_Lvl5.obj` | Fishing Rod | Quaternius | <https://poly.pizza/m/0YAR0Lg58p> |
| `Models/Z3_Fishing/Shark.obj` | (Cute Fish pack) | Quaternius | <https://quaternius.com/packs/cutefish.html> |

### Source not yet documented

The following models are bundled but their source / license has not been
recorded yet. To be confirmed and added above:

- `Models/Z5_ChargeShot/blaster-g.fbx`
- `Models/Z5_ChargeShot/bullet-foam-tip-thick.fbx`
- `Models/Z5_ChargeShot/target-large.fbx`

---

## Textures (`Textures/`, Materials)

All textures are CC0 from [Poly Haven](https://polyhaven.com/) (every asset on
Poly Haven is CC0 by site policy).

| Use | Source |
|---|---|
| Z1 Bowling lane (laminate floor) | <https://polyhaven.com/a/laminate_floor_02> |
| Z2 Door (oak veneer) | <https://polyhaven.com/a/oak_veneer_01> |

---

## Audio (`Audio/`, `Kit/`)

`Audio/*` and `Kit/install-clips/*` / `Kit/stream-clips/*` WAVs are derived
from royalty-free sound-effect sites, then resampled / trimmed / gain-adjusted
for haptic playback.

Possible source sites (matches the credits in the SDK root `README.md`):

- 効果音ラボ — <https://soundeffect-lab.info/>
- 魔王魂 — <https://maou.audio/>
- 効果音辞典 (小森平) — <https://taira-komori.net/>
- OtoLogic — <https://otologic.jp/>
- 音人 — <https://on-jin.com/>

Files have been processed for haptic use and metadata (author / copyright tags)
removed. If you are the rights holder and need an entry corrected, removed, or
relicensed, please open a GitHub issue.

---

## License summary

- **CC BY 3.0** (attribution required): 2 model files, credited above.
- **CC0**: all remaining 3D models, all textures, all bundled audio.

The Hapbeat SDK code itself (`Editor/`, `Runtime/`, sample `Scripts/`) is
licensed under the terms in the package's main `LICENSE` file and is not
covered by the third-party notices here.
