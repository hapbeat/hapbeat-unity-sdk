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

を実行すると `Scenes/Tutorial.unity` が生成されます。これが「触覚適用済み (With) 版」です。

### 2. Without 版を生成

`Tutorial.unity` を開いた状態で:

```
Hapbeat → Tutorial → Strip Hapbeat (Tutorial.unity → Tutorial_Plain.unity)
```

を実行すると、Hapbeat 関連コンポーネントを除去した `Tutorial_Plain.unity` が生成されます。これが「触覚なし版」で、ゲームロジックは動きますが触覚は鳴りません。手順書 (`docs/tutorial/walkthrough.md`) の通りに Plain → With へ自分で組み立てるとチュートリアルになります。

### 3. EventMap を準備

このサンプルには EventMap.asset を同梱していません — Build メニュー実行後、以下を Unity Editor で行ってください:

1. `Hapbeat → Window → Event Map` で EventMap を新規作成
2. 各 entry を README 末尾の表に従って登録 (display name / mode / target / streamClip)
3. `[Hapbeat Event Router] / TutorialBridge` の `Event Map` フィールドに作成した asset を割当

EventMap entry の詳細は walkthrough doc を参照。

### 4. Play で動作確認

Hapbeat デバイスを Studio または Helper でオンラインにし、Play ボタンを押します。
WASD で移動、マウスで視点、各ゾーンで操作を試してください。

## EventMap の構成 (推奨)

| Display Name | Event ID | Mode | Target | Loop | 用途 |
|---|---|---|---|---|---|
| pin_hit | physics.pin_hit | Command | `*/pos_r_arm` | - | Z1 Pin 衝突 |
| door_open | door.open | Command | `*/pos_neck` | - | Z2 ドア開 |
| door_close | door.close | Command | `*/pos_neck` | - | Z2 ドア閉 |
| grab_start | grab.start | Command | `*/pos_r_arm` | - | Z3 Pickup Fire |
| grab_loop | grab.loop | StreamClip (`rain_loop`) | `*/pos_r_arm` | ✓ | Z3 Pickup Loop |
| grab_release | grab.release | Command | `*/pos_r_arm` | - | Z3 Pickup Stop |
| stream_demo | stream.audio | StreamClip (`rain_loop`) | (Picker) | ✓ | Z4 Stream Test |
| slider_tick | ui.slider_tick | Command | (Picker) | - | Z4 Tick |
| charge_release | combat.shot | Command | (Picker) | - | Z5 PlayWithCurve |
| target_hit | combat.target_hit | Command | (Picker) | - | Z5 命中 |
| manual_fire | misc.beep | Command | (Picker) | - | Q キー |
| burst | combat.burst | Command | (Picker) | - | 1-5 キー |

`*` は全 player にマッチするワイルドカード。`pos_r_arm` 等の標準 position は contracts spec で定義されています。

## 既知の制約

- `Build Samples > 2. Tutorial` が生成するシーンは primitive (Cube/Cylinder/Sphere) ベースです。Kenney CC0 モデルへの差し替えは手動で行ってください。
- `ChargeShooter._projectilePrefab` (Z5) は user 側で Sphere prefab を割当する必要があります (Rigidbody + tag `Projectile`)。
- EventMap は Build スクリプトで自動生成されません。上表に従って手動作成してください (Hapbeat Studio で同名 Kit を構築すれば intensity も連動)。

## ライセンス

同梱 wav・モデル等の出所は `THIRD_PARTY_NOTICES.md` を参照してください。
