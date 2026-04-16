# Hapbeat Unity SDK ドキュメント

## 概要

Hapbeat Unity SDK は、Unity アプリケーションから Hapbeat デバイスへ触覚イベントを送信するための公式 SDK です。標準経路は Wi-Fi UDP broadcast によるデバイス直接送信です。Bridge は ESP-NOW 経路などが必要な場合に使うオプションです。

## 動作要件

- Unity 2021.3 以降
- Hapbeat デバイスが Unity 実行環境と同じネットワークに接続されていること
- VR サンプルを使う場合は XR Interaction Toolkit 3.x

## クイックスタート

### 1. パッケージのインストール

Unity Package Manager からインストールします。

ローカルからのインストール:

1. Unity エディタで `Window > Package Manager` を開く
2. `+` ボタンから `Add package from disk...` を選ぶ
3. `hapbeat-unity-sdk/package.json` を選ぶ

### 2. Event Router を作成

1. シーンで `GameObject > Hapbeat > Event Router` を選ぶ
2. `[Hapbeat Event Router]` が作成され、`HapbeatManager` が追加される
3. 再生開始時に Wi-Fi UDP broadcast で送信できる

### 3. 触覚イベントの再生

```csharp
using Hapbeat;

HapbeatManager.Instance.Play("impact.hit");
HapbeatManager.Instance.Play("impact.hit", gain: 0.8f, group: 1);
HapbeatManager.Instance.Stop("impact.hit");
HapbeatManager.Instance.StopAll();
```

## 接続方式

| 方式 | 用途 | 備考 |
|---|---|---|
| Wi-Fi UDP broadcast | 標準 | Bridge 不要。グループ ID でデバイス側フィルタ |
| Bridge | ESP-NOW 送信などの上位構成 | `HapbeatConfig.useBridge` を有効化 |

## API の使い分け

| 方法 | 用途 |
|---|---|
| `HapbeatManager.Play()` | 最小構成、通信テスト |
| `HapbeatEventMap` + Trigger | Inspector 中心のイベント管理 |
| `HapbeatUnityEventTrigger` | UI / XR Interaction Toolkit / Animation Event との接続 |
| `HapbeatCollisionTrigger` | 衝突・トリガーでの自動発火 |
| `HapbeatAnimatorTrigger` | Animator パラメータ変化との連動 |
| `HapbeatBridge` サブクラス | プロジェクト固有ロジックの集約 |
| `HapbeatAudioBridge` | AudioSource からリアルタイムストリーミング |

## サンプル

Package Manager > Hapbeat SDK > Samples からインポートできます。

| サンプル | 内容 | 状態 |
|---|---|---|
| Basic Example | キーボードで基本 API を確認 | `.unity` 同梱済み |
| Player Demo | Hub + Zone A-D の5シーン VR 体験デモ | 開発中、SceneBuilder 生成 |
| Creator Tutorial | 既存 VR ゲームへの Hapbeat 後付け手順 | 開発中、Before / After 構成 |

Player Demo は5シーン構成を正とします。

- `PlayerDemoHub.unity`
- `PlayerDemoZoneA.unity`
- `PlayerDemoZoneB.unity`
- `PlayerDemoZoneC.unity`
- `PlayerDemoZoneD.unity`

現時点で `Samples~` に Basic Example しか `.unity` がない状態は、開発中のため問題ありません。

## VR サンプル方針

- SDK 本体は XR Interaction Toolkit に直接依存しない
- XR 連携は UnityEvent 経由で行う
- WorldSpace Canvas は `TrackedDeviceGraphicRaycaster` を使う
- EventSystem は `XRUIInputModule` を使う
- Unity 6 では Build Settings に Player Demo の5シーンを登録する

## グループ ID

| ID | 説明 |
|---|---|
| 0 | ブロードキャスト、全デバイス |
| 1-254 | グループ指定 |
| 255 | 予約済み |

## トラブルシューティング

### デバイスに届かない

1. Unity 実行環境と Hapbeat デバイスが同じネットワークにいるか確認する
2. `Window > Hapbeat > Settings` で Port と Group を確認する
3. ファイアウォールが UDP 送信をブロックしていないか確認する
4. Bridge を使う場合だけ `Use Bridge` と Bridge host を確認する

### VR UI を操作できない

1. Canvas が WorldSpace になっているか確認する
2. Canvas に `TrackedDeviceGraphicRaycaster` があるか確認する
3. EventSystem が `XRUIInputModule` を使っているか確認する

### シーン遷移できない

Unity 6 では Editor Play でも `SceneManager.LoadScene()` に Build Settings 登録が必要です。`PlayerDemoHub` と Zone A-D の5シーンを Scene List に追加してください。
