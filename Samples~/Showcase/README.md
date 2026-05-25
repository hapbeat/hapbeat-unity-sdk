# Hapbeat Unity SDK — Showcase Sample

SDK の触覚配線パターンを 1 シーン × 5 ゾーンで一覧できるショーケース。「動く実装例の集合」として真似や改造の起点に使ってください。XR デバイス不要・キーマウスだけで完結します。

## 何が見られるサンプルか

| ゾーン | 触れる挙動 | 学べる SDK パターン |
|---|---|---|
| Z1 Bowling Lane | 左クリックで球を発射、ピンに衝突 | `HapbeatCollisionTrigger` (VelocityScaled) を BatchSetup で一括追加 |
| Z2 Swing Door | F キーで Animator state Open/Close | `HapbeatStateBehaviour` を Animator state に attach |
| Z3 Fishing Rod | 左ボタンで物体を釣り糸に attach → 振り回す → 離す | `HapbeatSequenceTrigger` (Fire→Loop→Stop) + `HapbeatParameterBinding` (運動量で gain modulation) |
| Z4 Stream Console | Space で stream 再生、Slider で gain/pan を動的更新 | `HapbeatManager.StreamAudioClip` + `HapbeatStreamPlayback.Gain/Pan` 動的更新, `HapbeatTickEmitter` (Slider に BatchSetup) |
| Z5 Target Range | 左ボタン長押しでチャージ → 離して発射 → 命中 | `HapbeatUnityEventTrigger` + `GainMultiplier` (charge curve) + UnityEvent wiring |

ホットキー (シーン全体で有効):
- **Q**: `manual_fire` event を単発発火 (`HapbeatKeyDispatcher` → `HapbeatUnityEventTrigger.Fire`)
- **P**: `HapbeatActionHelper.Ping()` で接続確認 (応答時間表示)
- **1〜5**: ゾーン切替

## 使い方

1. Package Manager の Hapbeat SDK パッケージから **Showcase** サンプルを Import
2. `Assets/Samples/Hapbeat SDK/<version>/Showcase/Scenes/Showcase.unity` を開く
3. Hapbeat デバイスを Studio または Helper でオンラインにして Play
4. WASD で移動、各ゾーンを試す

Import 直後のサンプルフォルダに Scene / EventMap / Kit / Audio / Animator がすべて同梱されているため、追加のビルド手順は不要です。

> **改造して残したい場合**: サンプルフォルダは再 Import で上書きされます。永続化したい改造は別フォルダ (例: `Assets/HapbeatSDK/`) にコピーしてから編集してください。

各 Zone がどう wire されているかの詳細は [Showcase docs (devtools.hapbeat.com)](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/showcase/) を参照。

## EventMap の構成 (StreamClip ファースト)

Showcase は **Hapbeat Studio で Kit を作らなくても、Unity 同梱 wav を StreamClip で再生** することで動くように設計しています。デバイスをオンラインにして Unity を Play すればすぐ触覚が返るのが最短導線です。

| Display Name | StreamClip (Audio) | Mode | Target | Loop | 用途 |
|---|---|---|---|---|---|
| pin_hit | `drum_hit_1.wav` | StreamClip | `*/pos_r_arm` | - | Z1 Pin 衝突 |
| door_open | `ui_click.wav` | StreamClip | `*/pos_neck` | - | Z2 ドア開 |
| door_close | `ui_click.wav` | StreamClip | `*/pos_neck` | - | Z2 ドア閉 |
| grab_start | `grab.wav` | StreamClip | `*/pos_r_arm` | - | Z3 Fishing Fire |
| grab_loop | `rain_loop.mp3` | StreamClip | `*/pos_r_arm` | ✓ | Z3 Fishing Loop (binding 対象) |
| grab_release | `release.wav` | StreamClip | `*/pos_r_arm` | - | Z3 Fishing Stop |
| stream_demo | `rain_loop.mp3` | StreamClip | (broadcast) | ✓ | Z4 Stream Test |
| slider_tick | `ui_click.wav` | StreamClip | (broadcast) | - | Z4 Tick |
| charge_release | `explosion.wav` | StreamClip | (broadcast) | - | Z5 Charge |
| target_hit | `target_hit.mp3` | StreamClip | (broadcast) | - | Z5 命中 |
| manual_fire | `punch_impact.wav` | StreamClip | (broadcast) | - | Q キー |
| burst | `gunshot.wav` | StreamClip | (broadcast) | - | (予備) |

`*` は全 player にマッチするワイルドカード。`pos_r_arm` 等の標準 position は contracts spec で定義されています。

### Command モードを試したい場合 (任意)

StreamClip で SDK の動作を一通り確認したら、Hapbeat Studio で同名の Kit を作って Command モードに切り替えると、

- 低遅延 (event id だけ送るので軽量)
- デバイス内蔵 clip を使うので Unity 同梱の wav が不要
- Studio で Kit のクリップを編集すれば Unity 側の変更不要

といった利点を体験できます。各 entry の `Mode` を `Command` に切替、`Event ID` (例: `showcase-kit.pin_hit`) を Studio 側 Kit の event id と合わせるだけで Showcase の挙動はそのまま再現します。詳細は [walkthrough](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/showcase/walkthrough/) の最後の章を参照。

## Kit (showcase-kit) について

Showcase は `showcase-kit-manifest.json` で各 event の `intensity` 既定値を提供するために `showcase-kit` を同梱しています (`Samples~/Showcase/Kit/`)。`install-clips/` / `stream-clips/` は **初期状態では空** で、StreamClip 再生は EventMap が `Audio/` を直接参照するため不要です。Command モード化したい場合のみ、ユーザー側で `install-clips/` に対応する WAV (例: `pin_hit.wav`) を追加し、`showcase-kit-manifest.json` の該当 event の `mode` を `"command"` に変更します。

manifest ファイル名の規約は **`<kit-name>-manifest.json`** (このサンプルでは `showcase-kit-manifest.json`)。複数 Kit が並ぶときの視認性のためで、SDK の自動 scan / per-entry override picker のいずれも "manifest" 部分一致で discovery します。

各 EventMap entry には optional な **manifest override** フィールド (EventMap Window の Test Play ボタン右の "Manifest" スロット) があり、特定の `<kit-name>-manifest.json` (TextAsset) を強制参照させたい場合にセットできます。未設定なら `HapbeatSDK/Kits/` 全 manifest を自動 scan して clip 一致 → eventId 一致の順に解決します。

## 既知の制約

- 配布されるシーンは primitive (Cube/Cylinder/Sphere) ベースです。Kenney CC0 モデルへの差し替えは Unity 上で手動編集してください。
- `ChargeShooter._projectilePrefab` (Z5) は user 側で Sphere prefab を割当する必要があります (Rigidbody + tag `Projectile`)。

## ライセンス

同梱 wav・モデル等の出所は `THIRD_PARTY_NOTICES.md` を参照してください。
