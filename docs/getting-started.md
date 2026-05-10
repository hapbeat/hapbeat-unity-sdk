---
title: Getting Started
description: SDK 導入から最初の Event 発火までの 5 分ガイド。
sidebar:
  order: 1
---

このガイドでは、まっさらな Unity シーンに Hapbeat SDK を組み込んで Event を発火するまでの最短手順を示します。

## 前提

- [SDK が UPM 経由でインストール済み](/docs/unity-sdk/installation/)
- `hapbeat-helper` が起動し、デバイスがオンライン（[Hapbeat Studio の初期セットアップ](/docs/studio/initial-setup/)参照）
- Studio で `gunshot` という Event ID を含む Kit がデバイスに転送済み（任意の Event ID で OK）

## 1. シーンに Event Router を追加

メニューバー → **`Hapbeat` → `Create Event Router`** を実行します。

`[Hapbeat Event Router]` GameObject がシーンに追加され、内部に `HapbeatManager` (singleton) が配置されます。Hapbeat を使うシーンに 1 つあれば十分です。

## 2. Event を発火する Trigger を追加

シーン内の任意の GameObject（例: 銃モデル）に **`HapbeatUnityEventTrigger`** コンポーネントを追加。

設定（Inspector）:

- **Event Map**: EventMap.asset を参照させる（まだ無ければ `Assets > Create > Hapbeat > Event Map` で作成）
- **Event**: ドロップダウンで Event エントリを選択

EventMap に `gunshot` エントリを追加: Event Map ウィンドウ（`Hapbeat > Event Map`）で + ボタン → eventId に `gunshot`、mode に `Command`。

## 3. コードから発火

UnityEvent 経由でなくコードから直接発火する場合は `HapbeatManager.Instance.Play` を使います。

```csharp
using Hapbeat;
using UnityEngine;

public class GunController : MonoBehaviour
{
    void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            HapbeatManager.Instance?.Play("gunshot");
        }
    }
}
```

## 4. Inspector だけで発火させる: Trigger コンポーネント

スクリプト不要で発火させたい場合は専用 Trigger を使います。

- **HapbeatUnityEventTrigger**: UnityEvent（Button.OnClick / XRI Activate 等）から `Fire()` を呼ぶ
- **HapbeatAnimatorTrigger**: Animator パラメータ変化で発火
- **HapbeatCollisionTrigger**: Collision / Trigger イベントで発火（速度連動可）
- **HapbeatSequenceTrigger**: grab / hold / release を 1 コンポーネントで（XR Interaction）

詳細: [Trigger コンポーネント](/docs/unity-sdk/triggers/)

## 5. Event ID を GUI で管理

スクリプトに Event ID 文字列を散らかすと管理が大変です。**EventMap ウィンドウ**で Event ID と Trigger の対応を可視化・一括管理できます。

メニューバー → `Hapbeat` → `Event Map` を開く。

詳細: [EventMap](/docs/unity-sdk/event-map/)

## 次のステップ

- [Tutorial サンプル](/docs/unity-sdk/tutorial/) — キーマウスで SDK 全機能を 30 分で体験 (XR 不要)
- [Trigger コンポーネントの種類](/docs/unity-sdk/triggers/)
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/)
- [Parameter Binding](/docs/unity-sdk/parameter-binding/) — ゲーム状態を gain/pan に動的マッピング
- [BatchSetup vs スクリプトの使い分け](/docs/unity-sdk/tutorial/method-choice/)
