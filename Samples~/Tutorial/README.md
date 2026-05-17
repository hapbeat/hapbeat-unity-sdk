# Hapbeat Unity SDK — Tutorial Sample

キーマウス操作だけで Hapbeat SDK の全機能を体験するチュートリアル。XR デバイス不要。

## このサンプルで学べること

| ゾーン | 内容 | 学ぶ SDK 要素 |
|---|---|---|
| Z1 Bowling Lane | 左クリックで球を発射、ピンに衝突 | `HapbeatCollisionTrigger` (VelocityScaled) を BatchSetup で一括追加 |
| Z2 Swing Door | F キーで Animator state Open/Close | `HapbeatAnimatorTrigger` を Inspector 手動追加 |
| Z3 Pickup Box | 左クリックで持ち上げ → 動かす → 離す | `HapbeatSequenceTrigger` (Fire→Loop→Stop) + `HapbeatParameterBinding` (移動量で gain modulation) |
| Z4 Stream Console | Space で stream 再生、Slider で gain/pan を動的更新 | `HapbeatManager.StreamAudioClip` + `HapbeatStreamPlayback.Gain/Pan` 動的更新, `HapbeatTickEmitter` (Slider に BatchSetup) |
| Z5 Target Range | 左ボタン長押しでチャージ → 離して発射 → 命中 | スクリプト経由 `Bridge.PlayWithCurve` + `HapbeatUnityEventTrigger` (UnityEvent wiring) |

ホットキー (シーン全体で有効):
- **Q**: `Bridge.Play("manual_fire")` 単発発火
- **1〜5**: `Bridge.PlayScaled("burst", ...)` で gain スケール可変発火
- **P**: `HapbeatManager.Ping()` で接続確認 (応答時間表示)

シーン上部の **Target Picker** (Both / Neck / Arm) で Z4・Z5・ホットキーの送信先を動的切替できます。Z1〜Z3 は EventMap entry に target を固定しており、Picker 操作の影響を受けません — 「event 設計時点で target を決める」 vs 「runtime で target を上書きする」両方の設計パターンを比較できます。

## 使い方

### 1. サンプルを Import

Package Manager の Hapbeat SDK パッケージから **Tutorial** サンプルを Import します。
`Assets/Samples/Hapbeat SDK/<version>/Tutorial/` 配下に以下が展開されます (authored 版・SDK リポジトリで配布されています):

- `Scenes/Tutorial.unity` — 「触覚適用済み (With) 版」
- `Scenes/Tutorial_Plain.unity` — 「触覚なし (Without) 版」・walkthrough 起点
- `EventMaps/TutorialEventMap.asset` — 12 entry リンク済み
- `Animation/DoorAnimator.controller` — Z2 Door の IsOpen パラメータ
- `Audio/`, `Scripts/`

### 2. ユーザー領域に展開

Editor メニューから:

```
Hapbeat → Build Samples → 2. Tutorial (full scene)
```

を実行すると、以下にコピー + 参照リンクの再配線が行われます:

- **`Assets/HapbeatSDK/SDK_Samples/Tutorial/{Scenes,EventMaps,Animation}/`** ← Scene / EventMap / AnimatorController
- **`Assets/HapbeatSDK/Kits/tutorial-kit/`** ← Kit (manifest + 空の install-clips/stream-clips)

以後はそちらを編集・Play してください (Kenney モデル差し替えやレイアウト調整も HapbeatSDK 側で行うと、再 Import で上書きされません)。

`Assets/HapbeatSDK/SDK_Samples/` 配下が **SDK が追加したもの** の専有領域です。Studio が管理するユーザー Kit / Scene / EventMap は `Assets/HapbeatSDK/{Kits,Scenes,EventMaps}/` (SDK_Samples と並列) に保存され、相互に干渉しません。Kit のみは `HapbeatSDK/Kits/` 内で混在しますが、`tutorial-kit` という名前で SDK 由来と判別可能です。

### 3. Play で動作確認

`Assets/HapbeatSDK/SDK_Samples/Tutorial/Scenes/Tutorial.unity` を開き、Hapbeat デバイスを Studio または Helper でオンラインにしてから Play。
WASD で移動、マウスで視点、各ゾーンで操作を試してください。

> **AudioClip 参照について**: コピーされた EventMap は `Assets/Samples/.../Tutorial/Audio/` 配下の WAV を参照します (HapbeatSDK には複製しません)。Import 済みサンプルを削除すると参照が切れるため、サンプルフォルダは残しておいてください。

### 4. walkthrough を試す

`Tutorial_Plain.unity` を起点に手順書 ([walkthrough.md](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/tutorial/walkthrough/)) の通りに Hapbeat を組み立てると `Tutorial.unity` と同じ動作になります — 自分で実装を学ぶ用途に向いています。

## EventMap の構成 (推奨 — StreamClip ファースト)

Tutorial は **Hapbeat Studio で Kit を作らなくても、Unity 同梱 wav を StreamClip で再生** することで動くように設計しています。デバイスをオンラインにして Unity を Play すればすぐ触覚が返るのが最短導線です。

| Display Name | StreamClip (Audio) | Mode | Target | Loop | 用途 |
|---|---|---|---|---|---|
| pin_hit | `drum_hit_1.wav` | StreamClip | `*/pos_r_arm` | - | Z1 Pin 衝突 |
| door_open | `ui_click.wav` | StreamClip | `*/pos_neck` | - | Z2 ドア開 |
| door_close | `ui_click.wav` | StreamClip | `*/pos_neck` | - | Z2 ドア閉 |
| grab_start | `grab.wav` | StreamClip | `*/pos_r_arm` | - | Z3 Pickup Fire |
| grab_loop | `rain_loop.mp3` | StreamClip | `*/pos_r_arm` | ✓ | Z3 Pickup Loop (binding 対象) |
| grab_release | `release.wav` | StreamClip | `*/pos_r_arm` | - | Z3 Pickup Stop |
| stream_demo | `rain_loop.mp3` | StreamClip | (Picker) | ✓ | Z4 Stream Test |
| slider_tick | `ui_click.wav` | StreamClip | (Picker) | - | Z4 Tick |
| charge_release | `explosion.wav` | StreamClip | (Picker) | - | Z5 PlayWithCurve |
| target_hit | `target_hit.mp3` | StreamClip | (Picker) | - | Z5 命中 |
| manual_fire | `punch_impact.wav` | StreamClip | (Picker) | - | Q キー |
| burst | `gunshot.wav` | StreamClip | (Picker) | - | 1-5 キー |

`*` は全 player にマッチするワイルドカード。`pos_r_arm` 等の標準 position は contracts spec で定義されています。

### Command モードを試したい場合 (任意)

StreamClip で SDK の動作を一通り確認したら、Hapbeat Studio で同名の Kit を作って Command モードに切り替えると、

- 低遅延 (event id だけ送るので軽量)
- デバイス内蔵 clip を使うので Unity 同梱の wav が不要
- Studio で Kit のクリップを編集すれば Unity 側の変更不要

といった利点を体験できます。各 entry の `Mode` を `Command` に切替、`Event ID` (例: `tutorial-kit.pin_hit`) を Studio 側 Kit の event id と合わせるだけで Tutorial の挙動はそのまま再現します。詳細は [walkthrough](../docs/tutorial/walkthrough.md) の最後の章を参照。

## Kit (tutorial-kit) について

Tutorial は `tutorial-kit-manifest.json` で各 event の `intensity` を提供するために `tutorial-kit` を同梱しています (`Samples~/Tutorial/Kit/`)。`install-clips/` / `stream-clips/` は **初期状態では空** で、StreamClip 再生は EventMap が `Audio/` を直接参照するため不要です。Command モード化したい場合のみ、ユーザー側で `install-clips/` に対応する WAV (例: `pin_hit.wav`) を追加し、`tutorial-kit-manifest.json` の該当 event の `mode` を `"command"` に変更します。

Build メニューの Deploy mode は Kit を `Assets/HapbeatSDK/Kits/tutorial-kit/` にコピーします (Studio convention の kit root)。

manifest ファイル名の規約は **`<kit-name>-manifest.json`** (このサンプルでは `tutorial-kit-manifest.json`)。複数 Kit が並ぶときの視認性のためで、SDK の自動 scan / per-entry override picker のいずれも "manifest" 部分一致で discovery します。

各 EventMap entry には optional な **manifest override** フィールド (EventMap Window の Test Play ボタン右の "Manifest" スロット) があり、特定の `<kit-name>-manifest.json` (TextAsset) を強制参照させたい場合にセットできます。未設定なら `HapbeatSDK/Kits/` 全 manifest を自動 scan して clip 一致 → eventId 一致の順に解決します。

## 既知の制約

- 配布されるシーンは primitive (Cube/Cylinder/Sphere) ベースです。Kenney CC0 モデルへの差し替えは Unity 上で手動編集してください。
- `ChargeShooter._projectilePrefab` (Z5) は user 側で Sphere prefab を割当する必要があります (Rigidbody + tag `Projectile`)。

## ライセンス

同梱 wav・モデル等の出所は `THIRD_PARTY_NOTICES.md` を参照してください。
