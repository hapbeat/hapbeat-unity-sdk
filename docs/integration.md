---
title: プロジェクトへの組み込み
description: 既存 Unity シーンに Hapbeat SDK を追加する手順。EventMap + Trigger パターンの基本。
sidebar:
  order: 2
---

[Basic Example で動作を確認](/docs/unity-sdk/getting-started/)したら、自分のプロジェクトに組み込む準備ができています。

## Tutorial サンプル（開発中）

SDK の全機能を体験できる **Tutorial サンプル**を準備中です。

5 ゾーン構成（Bowling / Door / Pickup / Stream Console / Target Range）で、キーボード・マウスだけで XR なしに主要機能を一通り確認できます。

> **現在のステータス**: 開発中です。Package Manager の Samples タブに **Tutorial** が表示されたら `Hapbeat → Build Samples → 2. Tutorial` で試せます。

---

以下は既存シーンへの手動組み込み手順です。

## 1. シーンに Event Router を追加

メニューバー → **`Hapbeat → Create Event Router`** を実行します。

`[Hapbeat Event Router]` GameObject がシーンに追加され、内部に `HapbeatManager` (singleton) が配置されます。Hapbeat を使うシーンに 1 つあれば十分です。

## 2. EventMap を作成する

Event ID と触覚エントリの対応表を管理する `EventMap.asset` を作成します。

`Assets → Create → Hapbeat → Event Map` で作成するか、`Hapbeat → Event Map` ウィンドウを開いて **+ エントリ追加** から作業できます。

エントリの設定:

| フィールド | 内容 |
|---|---|
| Event Name | イベントの識別名（`<kit名>.<clip名>` 形式が推奨） |
| Mode | `StreamClip`（PCM 送信）/ `Command`（デバイス側 Kit 再生） |
| Gain | 振動強度（0.0 〜 1.0+） |
| Target | 送信先（空 = 全デバイス、`group_1` など） |

詳細: [EventMap ウィンドウ](/docs/unity-sdk/event-map/)

## 3. Trigger コンポーネントを追加

発火のタイミングに合ったコンポーネントを GameObject にアタッチします。

```
コードから発火
  └─ HapbeatManager.Instance.Play("event-id")

UI / XRI イベントから発火
  └─ HapbeatUnityEventTrigger  ← UnityEvent (Button.onClick 等) に接続

Animator から発火
  └─ HapbeatAnimatorTrigger    ← パラメータ変化を検知

衝突から発火
  └─ HapbeatCollisionTrigger   ← OnCollisionEnter / TriggerEnter（速度連動可）

XR グラブ / シーケンスから発火
  └─ HapbeatSequenceTrigger    ← Grab / Hold / Release を 1 コンポーネント化

連続値（スライダー等）から発火
  └─ HapbeatTickEmitter        ← 値変化スナップで断続的な触覚フィードバック
```

各コンポーネントの Inspector で **Event Map** に先ほどの `.asset` を設定し、**Event** ドロップダウンでエントリを選択します。

詳細: [Trigger コンポーネント](/docs/unity-sdk/triggers/)

## 4. コードから直接発火する場合

```csharp
using Hapbeat;
using UnityEngine;

public class GunController : MonoBehaviour
{
    void Update()
    {
        if (Input.GetMouseButtonDown(0))
            HapbeatManager.Instance?.Play("my-kit.gunshot");
    }
}
```

## 5. Kit をデバイスにデプロイ

Command モードのエントリを使う場合は、対応する Kit をデバイスにインストールします。

Hapbeat Studio → Kit タブ → フォルダ選択 → Manage タブ → Deploy

詳細: [初期セットアップ](/docs/studio/initial-setup/)

## 次のステップ

- [Trigger コンポーネント](/docs/unity-sdk/triggers/) — 各トリガーの設定例
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/) — Wiring の可視化・一括管理
- [Parameter Binding](/docs/unity-sdk/parameter-binding/) — ゲーム状態を gain / pan に動的マッピング
- [AI 支援ワークフロー](/docs/unity-sdk/ai-assisted-workflow/) — 既存シーンへの触覚後付け実践フロー
- [Editor メニューリファレンス](/docs/unity-sdk/editor-menus/) — Hapbeat メニュー全項目の使い方逆引き
- [複数アプリの共存](/docs/unity-sdk/multi-app/) — LAN 分離 / group ID 切り分け
