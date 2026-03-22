# Hapbeat Unity SDK ドキュメント

## 概要

Hapbeat Unity SDK は、Hapbeat デバイスを Unity アプリケーションから制御するための公式 SDK です。
Hapbeat Bridge を介して UDP 通信により触覚イベントを送信します。

## 動作要件

- Unity 2021.3 以降
- Hapbeat Bridge がローカルまたはネットワーク上で起動していること

## クイックスタート

### 1. パッケージのインストール

Unity Package Manager を使用してインストールします。

**Git URL からのインストール:**

1. Unity エディタで `Window > Package Manager` を開く
2. `+` ボタンをクリックし、`Add package from git URL...` を選択
3. リポジトリの URL を入力してインストール

**ローカルからのインストール:**

1. Unity エディタで `Window > Package Manager` を開く
2. `+` ボタンをクリックし、`Add package from disk...` を選択
3. `hapbeat-unity-sdk/package.json` を選択

### 2. 設定

1. `Window > Hapbeat > Settings` を開く
2. Bridge のホスト（デフォルト: `127.0.0.1`）とポート（デフォルト: `7700`）を設定
3. 必要に応じて `HapbeatConfig` アセットが自動生成されます

### 3. シーンのセットアップ

1. 空の GameObject を作成し、`HapbeatManager` コンポーネントを追加
2. (任意) `HapbeatConfig` アセットを `Config` フィールドに割り当て
3. `Auto Connect` が有効の場合、再生開始時に自動で Bridge に接続します

### 4. 触覚イベントの再生

```csharp
using Hapbeat;

// 即時再生
HapbeatManager.Instance.Play("impact.hit");

// ゲインとグループを指定して再生
HapbeatManager.Instance.Play("impact.hit", gain: 0.8f, group: 1);

// 停止
HapbeatManager.Instance.Stop("impact.hit");

// 全停止
HapbeatManager.Instance.StopAll();
```

## API リファレンス

### HapbeatManager

シングルトンパターンで実装されたメインマネージャーです。

#### プロパティ

| プロパティ | 型 | 説明 |
|---|---|---|
| `Instance` | `HapbeatManager` | シングルトンインスタンス |
| `IsConnected` | `bool` | Bridge への接続状態 |
| `BridgeTimeOffsetUs` | `long` | Bridge との時刻オフセット (マイクロ秒) |

#### メソッド

| メソッド | 説明 |
|---|---|
| `Play(eventId, gain, group)` | 触覚イベントを即時再生 |
| `PlayScheduled(eventId, targetTimeUs, gain, group)` | 指定時刻に触覚イベントを再生 |
| `Stop(eventId, group)` | 指定イベントを停止 |
| `StopAll(group)` | 全イベントを停止 |
| `Ping()` | Bridge に Ping を送信 |
| `Connect()` | Bridge に接続 |
| `Disconnect()` | Bridge から切断 |

#### イベント

| イベント | 型 | 説明 |
|---|---|---|
| `OnConnected` | `Action` | Bridge に接続した時 |
| `OnDisconnected` | `Action` | Bridge から切断された時 |
| `OnError` | `Action<string>` | エラーが発生した時 |
| `OnPong` | `Action<long>` | Pong 応答を受信した時 (RTT マイクロ秒) |

### HapbeatEvent

GameObject にアタッチして使用するコンポーネントです。

#### プロパティ

| プロパティ | 型 | 説明 |
|---|---|---|
| `EventId` | `string` | 再生するイベント ID |
| `Gain` | `float` | ゲイン (0.0 〜 2.0) |
| `Group` | `byte` | ターゲットグループ ID |

#### メソッド

| メソッド | 説明 |
|---|---|
| `TriggerPlay()` | Play コマンドを送信 |
| `TriggerStop()` | Stop コマンドを送信 |

### HapbeatConfig

ScriptableObject による設定ファイルです。`Assets > Create > Hapbeat > Config` で作成できます。

| フィールド | 型 | デフォルト | 説明 |
|---|---|---|---|
| `bridgeHost` | `string` | `127.0.0.1` | Bridge のホスト |
| `bridgePort` | `int` | `7700` | Bridge の UDP ポート |
| `autoConnect` | `bool` | `true` | 自動接続 |
| `pingInterval` | `float` | `5.0` | Ping 間隔 (秒) |
| `enableLogging` | `bool` | `true` | ログ出力 |

### HapbeatDevice

デバイス情報を保持するデータクラスです。

| フィールド | 型 | 説明 |
|---|---|---|
| `deviceId` | `string` | デバイス ID |
| `name` | `string` | デバイス名 |
| `group` | `byte` | グループ ID |
| `firmwareVersion` | `string` | ファームウェアバージョン |
| `batteryLevel` | `float` | バッテリー残量 (0.0 〜 1.0) |
| `lastSeen` | `DateTime` | 最終通信時刻 |

## グループ ID

| ID | 説明 |
|---|---|
| 0 | ブロードキャスト（全デバイス） |
| 1-254 | グループ指定 |
| 255 | 予約済み（使用不可） |

## UDP プロトコル

SDK は Hapbeat Bridge と UDP で通信します。詳細は hapbeat-contracts を参照してください。

- デフォルトポート: 7700
- パケット最大サイズ: 512 バイト
- バイトオーダー: リトルエンディアン

## トラブルシューティング

### Bridge に接続できない

1. Hapbeat Bridge が起動しているか確認してください
2. ホストとポートの設定を確認してください
3. ファイアウォールが UDP 通信をブロックしていないか確認してください
4. `Window > Hapbeat > Settings` の Ping テストで接続を確認できます

### イベントが再生されない

1. Bridge への接続状態を確認してください（`HapbeatManager.Instance.IsConnected`）
2. Event ID が正しいか確認してください
3. コンソールにエラーメッセージが出ていないか確認してください
4. `enableLogging` を有効にして詳細ログを確認してください
