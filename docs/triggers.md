---
title: Trigger コンポーネント
description: Animator / Collision / Sequence など、Event 発火用 Trigger コンポーネントの種類と使い分け。
---

Hapbeat SDK は多様な Trigger コンポーネントを用意しており、コードを書かずに触覚を組み込めます。

## 一覧

| コンポーネント | 発火タイミング | 主な用途 |
|---------------|--------------|---------|
| **HapbeatEventTrigger** | コードから `Fire()` 呼び出し | カスタムロジック向けの基本 Trigger |
| **HapbeatAnimatorTrigger** | Animator state 遷移時 | キャラクターアクション、UI アニメーション |
| **HapbeatCollisionTrigger** | OnCollision / OnTrigger | 衝突、当たり判定、銃弾命中 |
| **HapbeatSequenceTrigger** | grab / hold / release の 3 段階 | XR Interaction（つかむ・持つ・離す） |

## HapbeatEventTrigger

最もシンプル。コードから `.Fire()` を呼びます。

```csharp
public class MyController : MonoBehaviour
{
    [SerializeField] HapbeatEventTrigger _trigger;
    public void OnFireButton() => _trigger.Fire();
}
```

設定:
- **Event ID**: 発火する Event ID
- **Method**: Fire / Stop（CLIP モード時に Stop で停止）

## HapbeatAnimatorTrigger

Animator のステート遷移を監視して自動発火。

設定:
- **Animator**: 監視対象の Animator
- **State Name**: 発火対象の state（`Hash` か文字列）
- **Trigger On**: Enter / Exit / Update

スクリプト不要で、キャラクターのアクション開始タイミングに同期できます。

## HapbeatCollisionTrigger

Collision / Trigger イベントで発火。**速度連動 (intensity scaling)** が可能。

設定:
- **Layer Filter**: 反応する Layer
- **Velocity Mapping**: 衝突速度 → intensity の変換 (curve)
- **Min Velocity**: これ以下の速度では発火しない

> 強い衝撃ほど強い触覚、というマッピングが組める。

## HapbeatSequenceTrigger

XR Interaction Toolkit や独自の grab system と組み合わせ、**3 段階の Event を 1 コンポーネントで管理**します。

設定:
- **On Grab**: つかんだ瞬間の Event（例: `grab-start`）
- **On Hold**: 持っている間の継続 Event（CLIP 推奨、例: `grab-hold`）
- **On Release**: 離した瞬間の Event（例: `grab-release`）

XR Helpers サンプル (`Samples~/XriHelpers/`) を Import すれば XRGrabInteractable / XRSocketInteractor との接続が自動でセットアップできます（プロジェクト側で XRI を導入していなくても scaffold 自体は壊れません）。

## ParameterBinding と組み合わせる

`HapbeatParameterBinding` を併用すると、Transform / Rigidbody の値を AudioSource や Bridge のパラメータに動的マッピングできます。

例: 距離が近いほど触覚を強くする。

詳細は [ParameterBinding](/docs/unity-sdk/parameter-binding/) （TODO: 後日追加）

## デバッグ

メニューバー → `Hapbeat` → `Enable Event Logger` を ON にすると、すべての Trigger 発火が `HapbeatEventLogger` に記録され、コンソール / log file で順序確認できます。XRI のイベント順序の可視化に有効。

## 次のステップ

- [EventMap で Event ID を一元管理](/docs/unity-sdk/event-map/)
- サンプル `PlayerDemo` / `CreatorTutorial` で実例を確認
