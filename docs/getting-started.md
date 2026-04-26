---
title: Getting Started
description: SDK 導入から最初の Event 発火までの 5 分ガイド。
---

このガイドでは、まっさらな Unity シーンに Hapbeat SDK を組み込んで Event を発火するまでの最短手順を示します。

## 前提

- [SDK が UPM 経由でインストール済み](/docs/unity-sdk/installation/)
- Hapbeat Manager が起動し、デバイスがオンライン
- Studio で `gunshot` という Event ID を含む Kit がデバイスに転送済み（任意の Event ID で OK）

## 1. シーンに Bridge を追加

新規 GameObject を作成（名前: `Hapbeat`）。

`Hapbeat` GameObject に `HapbeatBridge` コンポーネントを Add Component で追加します。

設定:

- **App Name**: 任意（複数アプリ識別用）
- **Group ID**: `0`（デフォルト）。複数プレイヤーで分離する場合は別 ID
- **Auto Discover**: ON（デバイス自動検出）

## 2. Event を発火する Trigger を追加

シーン内の任意の GameObject（例: 銃モデル）に **HapbeatEventTrigger** コンポーネントを追加。

設定:

- **Event ID**: `gunshot`（Kit に登録されている ID）
- **Trigger Type**: `Manual`（後述: コードから呼ぶ）

## 3. コードから発火

スクリプトから Trigger を呼びます。

```csharp
using Hapbeat;
using UnityEngine;

public class GunController : MonoBehaviour
{
    [SerializeField] HapbeatEventTrigger _trigger;

    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            _trigger.Fire();
        }
    }
}
```

`_trigger` フィールドに先ほど追加した HapbeatEventTrigger をドラッグして紐付け、Play モードでクリックすると振動します。

## 4. もっと簡単に: Animator / Collision Trigger を使う

スクリプト不要で発火させたい場合は専用 Trigger を使います。

- **HapbeatAnimatorTrigger**: Animator のステート遷移時に Event 発火
- **HapbeatCollisionTrigger**: Collision / Trigger イベントで発火（速度連動可）
- **HapbeatSequenceTrigger**: grab / hold / release を 1 コンポーネントで（XR Interaction）

詳細: [Trigger コンポーネント](/docs/unity-sdk/triggers/)

## 5. Event ID を GUI で管理

スクリプトに Event ID 文字列を散らかすと管理が大変です。**EventMap ウィンドウ**で Event ID と Trigger の対応を可視化・一括管理できます。

メニューバー → `Hapbeat` → `EventMap` を開く。

詳細: [EventMap](/docs/unity-sdk/event-map/)

## 次のステップ

- [Trigger コンポーネントの種類](/docs/unity-sdk/triggers/)
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/)
- サンプルシーン `PlayerDemo` / `CreatorTutorial` を import して実例を確認
