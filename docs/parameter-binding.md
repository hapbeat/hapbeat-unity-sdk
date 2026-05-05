---
title: Parameter Binding
description: HapbeatParameterBinding でゲーム状態 (移動量・速度・距離) を StreamGain / StreamPan に動的にマッピングする方法。
---

`HapbeatParameterBinding` は、StreamClip 再生中の **gain** や **pan** をゲーム状態 (Transform の位置、Rigidbody の速度、Animator のパラメータなど) に応じて毎フレーム書き換えるコンポーネントです。

「掴んだ箱を動かしている間だけ手応えが強くなる」「歩く速度に応じて足元の振動が強まる」といった連続的な触覚表現を、コードを書かずに実現できます。

## どこで動いているか

ParameterBinding は `HapbeatTriggerBase` の **ActivePlayback** (StreamClip 再生中の `HapbeatStreamPlayback` ハンドル) に対して書き込みます。仕組みは:

```
[Trigger.Fire]
    → Manager.StreamAudioClip(clip) → StreamPlayback handle 取得
    → Trigger が ActivePlayback として保持
[ParameterBinding.Update] (毎フレーム)
    → source value (Transform.position 等) を読む
    → input range で 0..1 に正規化
    → curve で形を整える
    → output range にマップ
    → ActivePlayback.Gain / ActivePlayback.Pan に書く
```

つまり ParameterBinding は **Stream 中のサンプルを動的に乗算する** ものです。Command mode (eventId 送信) には作用しません — Command は単発で完結するため modulate する余地がありません。

## 設定項目

| フィールド | 役割 |
|---|---|
| Source Transform Path | source として読む Transform。空 = この GameObject 自身 |
| Source Property | `LocalPositionY` / `VelocityMagnitude` / `PositionDeltaMagnitude` / `AngularVelocityMagnitude` / `LocalRotationY` 等から選択 |
| Input Min / Max | source 値をどの範囲で 0..1 に正規化するか |
| Curve Type | `Linear` / `EaseIn` / `EaseOut` / `EaseInOut` / `Custom` |
| Output Parameter | `StreamGain` (0..2) または `StreamPan` (-1..+1) |
| Output Min / Max | 出力範囲 |
| Debug Log | true で Console に input/output を周期出力 (動作確認に便利) |

## Tutorial Z3 での実例

[Tutorial サンプル](/docs/unity-sdk/tutorial/) の Pickup Box (Z3) は、箱を持ち上げて動かしている間、loop の gain を **箱の移動速度** に追従させます。

設定:

| 項目 | 値 |
|---|---|
| Source Transform Path | (空 = PickupBox 自身) |
| Source Property | `PositionDeltaMagnitude` |
| Input Min / Max | 0 / 0.5 |
| Curve Type | `EaseInOut` |
| Output Parameter | `StreamGain` |
| Output Min / Max | 0.2 / 1.5 |

挙動:
- 箱を **静止** させていると `PositionDeltaMagnitude = 0` → 正規化 0 → curve 0 → output 0.2 (静かな loop)
- 箱を **激しく動かす** と `PositionDeltaMagnitude > 0.5` → 正規化 1 → output 1.5 (強い loop)

実装は `Samples~/Tutorial/Scripts/PickupBoxController.cs` でマウス入力に応じて Sequence.Fire / Stop を呼ぶだけ。binding は EventMap entry に preset として登録されているので、BatchSetup や Apply Binding ボタン経由で自動的に PickupBox に `HapbeatParameterBinding` が貼られます。

## EventMap preset と standalone の違い

ParameterBinding には 2 つの設定モードがあります:

1. **Linked preset** (推奨): EventMap entry の `bindings[]` に preset を登録 → BatchSetup で対象 GameObject に component が自動生成 + preset id で link。preset を編集すると runtime に反映される (live tuning 可能)
2. **Standalone**: GameObject に直接コンポーネント追加して全フィールドをローカル設定。preset との link なし

Tutorial では Linked preset を使います。理由:
- EventMap window から一元管理できる
- Play 中に preset を編集すると live で反映 (チューニングが速い)
- 同じ entry に紐づく複数 GameObject が同じ binding を共有できる

## スクリプトから動かしたい場合

ParameterBinding を使わず、スクリプトで直接 `ActivePlayback.Gain` を書き換えることもできます (Tutorial Z4 Stream Console がこの方式)。

判断基準:
- ゲーム状態の取得が単純 (Transform / Rigidbody) → ParameterBinding
- 複数の game state を組み合わせる (HP × distance × inventory など) → スクリプト

## 参考

- [Tutorial Walkthrough](/docs/unity-sdk/tutorial/walkthrough/#5-z3-pickup-box--sequence--binding-手動) — Z3 の組み立て手順
- [Method choice](/docs/unity-sdk/tutorial/method-choice/) — コンポーネント vs スクリプトの使い分け
- [Triggers](/docs/unity-sdk/triggers/) — `HapbeatSequenceTrigger` の詳細
