---
title: Tutorial Walkthrough
description: 触覚なし版 (Tutorial_Plain.unity) を起点に、ゾーンごとに Hapbeat コンポーネントを追加していく完全手順。
---

このページは [Tutorial Sample](/docs/unity-sdk/tutorial/) の **Plain → With** 構築手順です。完成形 (`Tutorial.unity`) を脇で開いて diff を取りながら進めるとさらに理解が深まります。

## 前提

- Tutorial サンプルを Package Manager から Import 済み
- `Hapbeat → Build Samples → 2. Tutorial (full scene)` を 1 回実行 (`Tutorial.unity` と `Tutorial_Plain.unity` が同時生成される)
- Hapbeat デバイスがオンライン (Studio または Helper で確認)

## 0. EventMap は Build メニューで自動生成済み

このチュートリアルは **Hapbeat Studio で Kit を作らずに**、Unity 同梱の WAV を StreamClip 経由で送信して動くように設計しています。Studio 連動は本ページ末尾の任意ステップで扱います。

`Hapbeat → Build Samples → 2. Tutorial (full scene)` を実行すると、シーンと一緒に **`EventMap/TutorialEventMap.asset`** が自動生成され、12 entry が StreamClip モードで `Audio/` 内の WAV と紐付け済みです。`[Hapbeat Event Router]` の `TutorialBridge.Event Map` フィールドにも自動でリンクされます。

### 生成される entry 一覧 (参考)

| Display Name | Mode | streamClip | loop | Target |
|---|---|---|---|---|
| pin_hit | StreamClip | `Audio/drum_hit_1.wav` | - | `*/pos_r_arm` |
| door_open / door_close | StreamClip | `Audio/ui_click.wav` | - | `*/pos_neck` |
| grab_start | StreamClip | `Audio/grab.wav` | - | `*/pos_r_arm` |
| grab_loop | StreamClip | `Audio/rain_loop.mp3` | ✓ | `*/pos_r_arm` |
| grab_release | StreamClip | `Audio/release.wav` | - | `*/pos_r_arm` |
| stream_demo | StreamClip | `Audio/rain_loop.mp3` | ✓ | (空) |
| slider_tick | StreamClip | `Audio/ui_click.wav` | - | (空) |
| charge_release | StreamClip | `Audio/explosion.wav` | - | (空) |
| target_hit | StreamClip | `Audio/target_hit.mp3` | - | (空) |
| manual_fire | StreamClip | `Audio/punch_impact.wav` | - | (空) |
| burst | StreamClip | `Audio/gunshot.wav` | - | (空) |

target が空のものは broadcast (= Picker UI に従う)。Z1〜Z3 は EventMap で固定 target を持つ「設計時点で決まる」例です。

> **ヒント**: Mode を StreamClip にしておけば、デバイスは Unity から送られてくる PCM サンプルをそのまま再生するだけなので、Studio で Kit を作る・配布する手間ゼロで動作確認できます。Command モードへの切替は最後の任意ステップ ([Step 9](#step-9-command-モードに切替-任意-studio-連動)) を参照してください。

> 生成済み asset を再生成したい場合は同じ Build メニューを再実行してください (entries はクリアされて再生成されます — 個別カスタマイズ済みの場合は別 asset として複製してから編集を)。

`grab_loop` の `bindings` には preset を 1 つ追加してください:
- Source Transform Path: 空 (= target object)
- Source Property: `PositionDeltaMagnitude`
- Output: `StreamGain`, range 0.2〜1.5
- Curve: `EaseInOut`

## 1. シーンを開く

`Tutorial_Plain.unity` を開きます。`[Hapbeat Event Router]` GameObject は **存在しません** (Hapbeat コンポーネントが除かれた Without 版なので)。

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

## Step 9: Command モードに切替 (任意, Studio 連動)

ここまで StreamClip だけで Tutorial の挙動は完成しています。次に「Hapbeat Studio で Kit を整備すると何が変わるか」を Command モードで体験できます。任意ステップなのでスキップして OK。

### Command モードのメリット

- **低遅延**: event id (短い文字列) だけ送るので、PCM ストリームより到達が速い
- **デバイス内蔵 clip 再生**: Unity 側に WAV を持たなくてよい (アプリの容量削減)
- **Studio で clip を編集**: Kit を Studio で更新して deploy すれば、Unity 側のビルドし直しは不要

### 手順

1. **Studio で Kit を作る**
   - Hapbeat Studio を開いて新規 Kit を作成
   - 各 entry 名に対応する clip を登録 (例: `physics.pin_hit` → drum 系の clip)
   - 同じ event id を使うのが楽: Studio の Kit にも `physics.pin_hit` で登録
2. **Kit をデバイスに deploy** (Studio の Save → Deploy)
3. **EventMap で mode を Command に切替**
   - `pin_hit` entry を開いて Mode を `Command` に
   - Category = `physics`, Event Name = `pin_hit` を入力 (Event ID = `physics.pin_hit` が自動算出)
   - streamClip フィールドはそのまま (Command モードでは無視される)
4. **試したい entry を順次 Command 化**
   - 全部 Command にする必要はなく、低遅延を効かせたい衝撃系だけ Command、ambient/drag 系は StreamClip という混在運用が実用的
5. Play で動作確認

### Command + StreamClip 混在の指針

- **Command 向き**: 衝撃 (pin_hit, target_hit, charge_release, manual_fire など)。低遅延が効く
- **StreamClip 向き**: ループ系 (grab_loop, stream_demo)。ParameterBinding で動的 modulation できる

実機で両方触ってみると、Command の応答の速さと StreamClip の表現の自由度の違いが体感できます。

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
