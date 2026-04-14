# Unity SDK: アプリ名設定の追加

## 目的

Hapbeat デバイスの OLED に「接続状態」要素を表示する際、接続中のアプリ名を表示する機能がある。
SDK 側で開発者がアプリ名を設定できるようにする。

## 現状

- ファームウェアは `ConnectInfo.appName` に受信したアプリ名を保持し、OLED に `[OK]アプリ名` と表示する
- UDP の `CMD_CONNECT_STATUS` メッセージにアプリ名が含まれる仕様
- Unity SDK 側でアプリ名を設定・送信する仕組みが未実装

## 変更内容

### 1. HapbeatConfig にアプリ名フィールドを追加

SDK の設定クラス（`HapbeatManager` や `HapbeatConfig` 等）に `appName` を追加:

```csharp
[Tooltip("デバイスの OLED に表示されるアプリ名（最大8文字）")]
public string appName = "MyApp";
```

### 2. 接続状態メッセージにアプリ名を含める

UDP で `CMD_CONNECT_STATUS` を送信する際、payload にアプリ名を含める。
ファームウェアの `connect_status.cpp` が受信してパースする形式に合わせる。

### 3. Inspector で設定可能に

Unity の Inspector 上で `appName` を設定でき、実行時にデバイスに送信される。

## 注意

- アプリ名は最大 8 文字（ファーム側で `%.8s` で切り詰め）
- 空文字列の場合はアプリ名なしで接続状態のみ表示
- `CMD_CONNECT_STATUS` のメッセージ仕様は `hapbeat-contracts` を参照
