# Unity SDK 指示書: パスベースデバイスアドレッシング対応

## 仕様

`hapbeat-contracts/specs/device-addressing.md` を参照。

## 変更概要

- `Play()` / `Stop()` / `StopAll()` に `target` パラメータを追加（`group` を置き換え）
- target はパスベースの文字列（空 = 全台、`"player_1"` = 前方一致、`"*/chest"` = ワイルドカード）
- UDP パケットフォーマット変更に対応

## API 変更

### 現在

```csharp
hapbeat.Play("impact.damage", group: 1, gain: 0.8f);
hapbeat.Stop("impact.damage", group: 1);
hapbeat.StopAll(group: 0);
```

### 変更後

```csharp
// シンプル（全台に送信）
hapbeat.Play("impact.damage");

// 特定プレイヤーの全部位
hapbeat.Play("impact.damage", target: "player_1");

// 特定プレイヤーの特定部位
hapbeat.Play("impact.damage", target: "player_1/chest");

// 全プレイヤーの胸
hapbeat.Play("impact.damage", target: "*/chest");

// チーム指定
hapbeat.Play("explosion", target: "red");

// 停止
hapbeat.Stop("impact.damage", target: "player_1");
hapbeat.StopAll(target: "red");  // 赤チーム全台停止
hapbeat.StopAll();               // 全台停止
```

### メソッドシグネチャ

```csharp
public void Play(string eventId, string target = "", float gain = 1.0f);
public void Stop(string eventId, string target = "");
public void StopAll(string target = "");
```

`group` パラメータは削除。

## パケットフォーマット変更

### PLAY (0x01)

```
event_id       (null-terminated string)
target         (null-terminated string)  ← NEW (was: target_group uint8)
target_time_us (int64)
gain           (float32)
```

### STOP (0x02)

```
event_id       (null-terminated string)
target         (null-terminated string)  ← NEW
```

### STOP_ALL (0x03)

```
target         (null-terminated string)  ← NEW (was: target_group uint8)
```

## 実装タスク

### 1. HapbeatProtocol.cs — パケット構築変更

```csharp
public static byte[] BuildPlay(ushort seq, string eventId,
                                string target = "",
                                long targetTimeUs = 0,
                                float gain = 1.0f) {
    var eventBytes = Encoding.UTF8.GetBytes(eventId + "\0");
    var targetBytes = Encoding.UTF8.GetBytes(target + "\0");
    // payload = eventBytes + targetBytes + targetTimeUs(8) + gain(4)
    ...
}
```

### 2. HapbeatClient.cs / HapbeatManager.cs — API 変更

- `Play(string eventId, int group = 0, float gain = 1.0f)`
  → `Play(string eventId, string target = "", float gain = 1.0f)`
- `Stop(string eventId, int group = 0)`
  → `Stop(string eventId, string target = "")`
- `StopAll(int group = 0)`
  → `StopAll(string target = "")`

### 3. HapbeatConfig.cs / Inspector — 設定変更

- `defaultGroup` (int) → 不要（target はコードで指定）
- Inspector に `Default Target` フィールドを追加（オプション、空 = 全台）

### 4. PONG パース変更

- `group` (uint8) → `address` (null-terminated string)
- デバイス発見時にアドレスを表示 / ログ

## 開発者向けドキュメント変更

README / サンプルコードを以下の形に更新:

```csharp
// Basic usage — all devices
hapbeat.Play("impact.damage");

// Per-player targeting
string myTarget = $"player_{PhotonNetwork.LocalPlayer.ActorNumber}";
hapbeat.Play("impact.damage", target: myTarget);

// Position-specific
hapbeat.Play("heartbeat", target: $"{myTarget}/chest");

// Team-wide
hapbeat.Play("victory", target: teamName);
```
