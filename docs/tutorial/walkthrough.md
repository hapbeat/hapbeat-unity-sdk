---
title: Tutorial Walkthrough
description: 触覚なし版 (Tutorial_Plain.unity) を起点に、ゾーンごとに Hapbeat コンポーネントを追加していく完全手順。
---

このページは [Tutorial Sample](/docs/unity-sdk/tutorial/) の **Plain → With** 構築手順です。完成形 (`Tutorial.unity`) を脇で開いて diff を取りながら進めるとさらに理解が深まります。

## 前提

- Tutorial サンプルを Package Manager から Import 済み
- `Hapbeat → Build Samples → 2. Tutorial (full scene)` で `Tutorial.unity` を 1 回生成済み
- `Hapbeat → Tutorial → Strip Hapbeat` で `Tutorial_Plain.unity` を生成済み
- Hapbeat デバイスがオンライン (Studio または Helper で確認)

## 0. EventMap の用意

すべての Trigger / Bridge は EventMap を参照するため、最初に作成します。

1. Project ウィンドウで `Samples/Hapbeat SDK/.../Tutorial/EventMap/` フォルダに移動
2. 右クリック → Create → Hapbeat → Event Map で `TutorialEventMap.asset` を作成
3. `Hapbeat → Window → Event Map` を開く
4. 作成した asset を選択して、以下の entry を追加:

| Display Name | Category | Event Name | Mode | streamClip | loop | Target | Gain |
|---|---|---|---|---|---|---|---|
| pin_hit | physics | pin_hit | Command | - | - | `*/pos_r_arm` | 1.0 |
| door_open | door | open | Command | - | - | `*/pos_neck` | 1.0 |
| door_close | door | close | Command | - | - | `*/pos_neck` | 1.0 |
| grab_start | grab | start | Command | - | - | `*/pos_r_arm` | 1.0 |
| grab_loop | grab | loop | StreamClip | rain_loop.mp3 | ✓ | `*/pos_r_arm` | 1.0 |
| grab_release | grab | release | Command | - | - | `*/pos_r_arm` | 1.0 |
| stream_demo | stream | audio | StreamClip | rain_loop.mp3 | ✓ | (空) | 1.0 |
| slider_tick | ui | slider_tick | Command | - | - | (空) | 1.0 |
| charge_release | combat | shot | Command | - | - | (空) | 1.0 |
| target_hit | combat | target_hit | Command | - | - | (空) | 1.0 |
| manual_fire | misc | beep | Command | - | - | (空) | 1.0 |
| burst | combat | burst | Command | - | - | (空) | 1.0 |

target が空のものは broadcast (= Picker UI に従う)。Z1〜Z3 は EventMap で固定 target を持つ「設計時点で決まる」例です。

`grab_loop` の `bindings` には preset を 1 つ追加してください:
- Source Transform Path: 空 (= target object)
- Source Property: `PositionDeltaMagnitude`
- Output: `StreamGain`, range 0.2〜1.5
- Curve: `EaseInOut`

## 1. シーンを開く

`Tutorial_Plain.unity` を開きます。`[Hapbeat Event Router]` GameObject は **存在しません** (Strip 済み)。

各ゾーン GameObject (Z1_Bowling 等) と Player は存在しますが、Hapbeat 系コンポーネントが一切貼られていない状態です。Play すると操作はできますが触覚は鳴りません。

## 2. Hapbeat Event Router を追加

シーンルートに新規 GameObject `[Hapbeat Event Router]` を作成し、以下のコンポーネントを追加:

1. **HapbeatManager** (Add Component → Hapbeat → Hapbeat Manager)
2. **TutorialBridge** (Add Component → Tutorial → Tutorial Bridge)
   - **Event Map** フィールドに `TutorialEventMap.asset` を割当
3. **GlobalHotkeys**
   - **Bridge** に上記 TutorialBridge をリンク
4. **TargetPickerUI**
   - **Bridge** にリンク
   - HUD 側のトグルが Plain.unity に既に存在するので、それぞれ Both / Neck / Arm Toggle にリンク

## 3. Z1 Bowling — BatchSetup で Pin に一括追加

1. **Hapbeat → Window → Batch Setup** を開く
2. Hierarchy で `Z1_Bowling/Pin_1` 〜 `Pin_6` を選択し、Batch Setup の Targets エリアにドラッグ&ドロップ
3. Trigger Type: `HapbeatCollisionTrigger`
4. EventMap: `TutorialEventMap`, Entry: `pin_hit`
5. Trigger Event: `OnCollisionEnter`
6. **Gain Mode**: `VelocityScaled`, MinVelocity 0.5, MaxVelocity 5
7. **Apply** をクリック

Pin 6 個に同じ設定が一括で貼られます。

## 4. Z2 Door — Animator + AnimatorTrigger を手動

1. `Z2_Door/Door` を選択
2. Animator にコントローラがまだなら、適当な `DoorAnimator.controller` を作成し、状態 Idle / Open / Closed と bool パラメータ `IsOpen` を作成 (Inspector で簡単に組めます)
3. Door に **HapbeatAnimatorTrigger** を 2 個追加:
   - 1 個目: EventMap `door_open`, Parameter `IsOpen`, Condition `BoolBecameTrue`
   - 2 個目: EventMap `door_close`, Parameter `IsOpen`, Condition `BoolBecameFalse`

## 5. Z3 Pickup Box — Sequence + Binding 手動

1. `Z3_Pickup/PickupBox` を選択
2. **HapbeatSequenceTrigger** を追加:
   - EventMap: `TutorialEventMap`
   - Entry (Loop): `grab_loop`
   - On Start Entry: `grab_start`
   - On Stop Entry: `grab_release`
3. PickupBoxController の `_sequence` フィールドに上記 Sequence をリンク
4. EventMap window で `grab_loop` entry を選択 → Bindings タブで Apply Binding ボタンを押すと、PickupBox に `HapbeatParameterBinding` が自動生成され preset と link されます

## 6. Z4 Stream Console — スクリプト + TickEmitter

1. `Z4_Stream/StreamPanel` の StreamDemoController に **TutorialBridge** をリンク (`_bridge` フィールド)
2. AudioClip 配列 (`_clips`) に `Audio/rain_loop.mp3` などを追加
3. **GainSlider** に **HapbeatTickEmitter** を Batch Setup で追加:
   - Trigger Type: `HapbeatTickEmitter`
   - EventMap: `TutorialEventMap`, Entry: `slider_tick`
   - Tick Threshold: 0.05
4. PanSlider にも同様に追加 (entry `slider_tick`、お好みで別 entry でも可)

## 7. Z5 Target Range — スクリプト + UnityEvent wiring

1. `Z5_Target` の ChargeShooter に **TutorialBridge** をリンク
2. **Projectile prefab** を作成: Sphere primitive + Rigidbody + Tag `Projectile` (Tag 一覧になければ作成)
3. ChargeShooter の `_projectilePrefab` にこの prefab を割当
4. `Z5_Target/TargetBoard` の TargetReceiver の **OnHit** UnityEvent に:
   - **HapbeatUnityEventTrigger** を Add Component (TargetBoard 自身でも `[Hapbeat Event Router]` でも可)
   - EventMap: `TutorialEventMap`, Entry: `target_hit`
   - OnHit のスロットに `HapbeatUnityEventTrigger.Fire()` を wire

## 8. Play

1. シーンを保存
2. Play
3. WASD で移動、各ゾーンを試す
4. Target Picker を切り替えて Z4・Z5・ホットキーが追従するか確認

## トラブルシューティング

| 症状 | 原因 / 対処 |
|---|---|
| 何も鳴らない | Hapbeat デバイスがオフライン → Studio / Helper で接続確認 |
| 接続済みなのに鳴らない | `TutorialBridge.Event Map` 未割当 |
| `[Hapbeat] Entry not found` ログが出る | EventMap の displayName と script の文字列がミスマッチ。表に従って一致させる |
| Sequence の Loop が無音 | `grab_loop` entry の `streamClip` 未割当、または `loop` 未チェック |
| Tick がスパムする | Tick Threshold が低すぎる。0.05 程度に調整 |
| Picker で切り替えても Z1 が変化しない | 仕様 (entry の固定 target が優先)。Z4・Z5・ホットキーで確認 |

完成形と比較したい場合は `Tutorial.unity` を脇で開いて、各 GameObject の Inspector を見比べてください。
