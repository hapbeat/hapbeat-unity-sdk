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

### 1. シーンを生成

Unity Editor のメニューから:

```
Hapbeat → Build Samples → 2. Tutorial (full scene)
```

を実行すると、`Scenes/` 配下に 2 つのシーンが同時に生成されます:

- `Tutorial.unity` — 「触覚適用済み (With) 版」。すぐ Play で動作確認できる完成形
- `Tutorial_Plain.unity` — 「触覚なし (Without) 版」。Hapbeat コンポーネントを除いてあり、ゲームロジックは動くが触覚は鳴らない

`Tutorial_Plain.unity` を起点に手順書 (`docs/tutorial/walkthrough.md`) の通りに Hapbeat を組み立てると `Tutorial.unity` と同じ動作になります — 自分で実装を学ぶ用途に向いています。

### 2. EventMap は自動生成済み

Build メニュー実行時に **`EventMap/TutorialEventMap.asset`** が同時に生成され、12 entry が StreamClip モードで `Audio/` 内の WAV と紐付け済み・`[Hapbeat Event Router] / TutorialBridge` にもリンク済みです。手動操作は不要。

`Project` ビューで `Assets/Samples/Hapbeat SDK/<version>/Tutorial/EventMap/TutorialEventMap.asset` を開けば中身を確認・編集できます。

### 3. Play で動作確認

Hapbeat デバイスを Studio または Helper でオンラインにし、Play ボタンを押します。
WASD で移動、マウスで視点、各ゾーンで操作を試してください。

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

といった利点を体験できます。各 entry の `Mode` を `Command` に切替、`Event ID` (例: `physics.pin_hit`) を Studio 側 Kit の event id と合わせるだけで Tutorial の挙動はそのまま再現します。詳細は [walkthrough](../docs/tutorial/walkthrough.md) の最後の章を参照。

## 既知の制約

- `Build Samples > 2. Tutorial` が生成するシーンは primitive (Cube/Cylinder/Sphere) ベースです。Kenney CC0 モデルへの差し替えは手動で行ってください。
- `ChargeShooter._projectilePrefab` (Z5) は user 側で Sphere prefab を割当する必要があります (Rigidbody + tag `Projectile`)。
- EventMap は Build スクリプトで自動生成されません。上表に従って手動作成してください (Hapbeat Studio で同名 Kit を構築すれば intensity も連動)。

## ライセンス

同梱 wav・モデル等の出所は `THIRD_PARTY_NOTICES.md` を参照してください。
