---
title: Trigger コンポーネント
description: Animator / Collision / Sequence など、Event 発火用 Trigger コンポーネントの種類と使い分け。
---

Hapbeat SDK は多様な Trigger コンポーネントを用意しており、コードを書かずに触覚を組み込めます。

## 一覧

| コンポーネント | 発火タイミング | 主な用途 |
|---------------|--------------|---------|
| **HapbeatUnityEventTrigger** | UnityEvent から `Fire()` を呼ぶ | UI Button / XR Interactable / Animation Event 等 |
| **HapbeatAnimatorTrigger** | Animator パラメータ変化時 | キャラクターアクション、UI アニメーション |
| **HapbeatCollisionTrigger** | OnCollision / OnTrigger | 衝突、当たり判定、銃弾命中 |
| **HapbeatSequenceTrigger** | grab / hold / release の 3 段階 | XR Interaction（つかむ・持つ・離す） |
| **HapbeatTickEmitter** | 連続値の変化量に応じてスナップ発火 | Slider / ScrollRect 等のスクロール触覚 |

## HapbeatUnityEventTrigger

UnityEvent（Button.OnClick / XRI Activate / Animation Event 等）の `Fire()` メソッドを紐付けて発火します。コードなしで任意の UnityEvent から触覚を呼べます。

設定:
- **Event Map**: EventMap.asset を参照
- **Event**: EventMap 内のエントリをドロップダウンで選択

```
On Activated:
  [Hapbeat Event Router] → HapbeatUnityEventTrigger.Fire()
```

## HapbeatAnimatorTrigger

Animator のパラメータ変化を監視して自動発火。

設定:
- **Target Animator**: 監視対象の Animator（別 GO でも指定可）
- **Parameter**: ドロップダウンで選択（Animator から自動取得）
- **Condition**: `BoolBecameTrue` / `BoolBecameFalse` / `FloatAbove` 等
- **Threshold**: Float / Int 条件の閾値

スクリプト不要で、キャラクターのアクション開始タイミングに同期できます。

## HapbeatCollisionTrigger

Collision / Trigger イベントで発火。**速度連動 (Gain Mode: VelocityScaled)** が可能。

設定:
- **Tag Filter**: 反応するオブジェクトの Tag（空 = 全対象）
- **Layer Mask**: レイヤーフィルタ
- **Gain Mode**: Fixed（固定）/ VelocityScaled（速度連動）
- **Min Velocity**: これ以下の速度では発火しない
- **Cooldown**: 連続発火防止（秒）

> 強い衝撃ほど強い触覚、というマッピングが組めます。

## HapbeatSequenceTrigger

XR Interaction Toolkit や独自の grab system と組み合わせ、**3 段階の Event を 1 コンポーネントで管理**します。

設定:
- **On Grab**: つかんだ瞬間の Event（例: `bowling.grab`）
- **On Hold**: 持っている間の継続 Event（CLIP 推奨、例: `bowling.hold`）
- **On Release**: 離した瞬間の Event（例: `bowling.release`）

XR Helpers サンプル (`Samples~/XriHelpers/`) を Import すれば XRGrabInteractable / XRSocketInteractor との接続が自動でセットアップできます（プロジェクト側で XRI を導入していなくても scaffold 自体は壊れません）。

## HapbeatTickEmitter

Slider / ScrollRect などの連続値の変化量に応じて、スナップアルゴリズムでトリガーします。

- Cooldown 不要（アルゴリズムが連続発火を自制）
- `snap interval` で感度を調整

## ParameterBinding と組み合わせる

`HapbeatParameterBinding` を併用すると、Transform / Rigidbody の値を StreamClip の gain / pan に毎フレームマッピングできます。

例: 距離が近いほど触覚を強くする、移動速度が上がるほど振動が強まる。

詳細は [ParameterBinding](/docs/unity-sdk/parameter-binding/)

## デバッグ

メニューバー → `Hapbeat` → `Debug` → `Attach Event Logger to Selected` を実行すると、選択中 GameObject の UnityEvent 発火をコンソールにログ出力できます。XRI のイベント発火順序の可視化に有効です。

詳細ログを記録する場合: `Hapbeat → Debug → Logs → Start Recording`

## 次のステップ

- [EventMap で Event ID を一元管理](/docs/unity-sdk/event-map/)
- [Streaming buffer の調整](/docs/unity-sdk/streaming/) — StreamClip モードの停止遅延・途切れ耐性のトレードオフ
- [Tutorial サンプル](/docs/unity-sdk/tutorial/) — 各 Trigger の実例 (Z1 Collision / Z2 Animator / Z3 Sequence + Binding / Z4 TickEmitter / Z5 UnityEvent + スクリプト)
- [BatchSetup vs スクリプトの使い分け](/docs/unity-sdk/tutorial/method-choice/)
