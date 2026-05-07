---
title: 複数アプリの共存
description: 1 台の Hapbeat デバイスに対して複数の Unity アプリ (またはアプリ + Hapbeat Studio など) を同時稼働させる場合の運用指針と回避策。
---

Hapbeat SDK は **「1 デバイス = 1 アプリ専用」を基本想定** としています。1 つの Hapbeat デバイスに対して複数のクライアントアプリ (App A と App B、もしくはアプリ + Hapbeat Studio) を**同時に**接続させると、振動コマンドが衝突したり、デバイスのディスプレイ表示が頻繁に切り替わるなどの挙動になります。

このページは「どうしても複数アプリを共存させたい」場合の運用指針です。

---

## なぜ衝突するのか

Hapbeat デバイスは UDP broadcast で受け取る触覚コマンドを、`group_id` フィルタにのみ依存して処理します。送信元アプリを区別する仕組みは v0.1.0 時点では実装されていません。

そのため:

- App A が `event_id=A.foo` で発火 → デバイスが振動
- 同時に App B が `event_id=B.bar` で発火 → デバイスが**両方**振動 (重なって意図しない触覚に)
- `CONNECT_STATUS` で送られてくる `appName` は最後に届いた方で上書きされるため、ディスプレイ表示が A・B でちらつく

---

## 推奨: 物理的に LAN を分離する (一番シンプル)

最も確実で、設定もシンプルです。

### パターン 1: ルーター / SSID を分ける

- App A の PC + App A 用 Hapbeat → ルーター A (SSID `studio-a`)
- App B の PC + App B 用 Hapbeat → ルーター B (SSID `studio-b`)

それぞれの LAN 内では UDP broadcast が独立しているので、**お互いの Hapbeat に到達しない**。設定不要で完全に分離できます。

### パターン 2: Hapbeat 自体を SoftAP として使う

VR HMD や PC を Hapbeat の SoftAP に直接接続する構成。複数の Hapbeat を別々の SoftAP として動かせば、独立した小さな LAN が複数できあがるのと同じ。

- アプリ A → Hapbeat A の SoftAP `hapbeat-a-XXXX` に接続
- アプリ B → Hapbeat B の SoftAP `hapbeat-b-XXXX` に接続

> 詳細な接続シナリオは workspace の CLAUDE.md (Connection scenarios A–E) を参照してください。

---

## 次善策: group ID で切り分ける

LAN を分けられない (同じ展示ブースで複数アプリ・Hapbeat 多数台、など) 場合は、**group ID の範囲を分けて運用** することで切替自体は実現できます。

### 例: A・B でグループ範囲を分ける

| アプリ | 使う group ID |
|---|---|
| App A | 0〜10 (player 0〜10) |
| App B | 11〜20 (player 11〜20) |

- 各 Hapbeat デバイスには Hapbeat Studio または Hapbeat Helper の Settings から **個別に group ID を割り当て** ておく (例: 1, 2, 3, ..., 11, 12, ...)
- App A は `HapbeatConfig.group = 1〜10 のいずれか` を指定 (もしくは `Manager.SetTargetGroup(n)` で動的に切替)
- App B は `group = 11〜20` のいずれか

各 Hapbeat は自分の group ID に対する packet しか拾わないので、振動衝突は起きません。

### この方法の制約

- **ディスプレイ上のアプリ名表示は混乱します**: App A・App B の両方が同じネットワークに CONNECT_STATUS をブロードキャストするため、デバイス側の `app_name` 表示は最後に届いたアプリ名で上書きされる
  - 触覚自体は group filter で正しく分離されるので動作には影響しない
  - 表示で「自分のアプリと繋がっているか」を確認したい場合はこの方法では不十分
- group ID 範囲の管理は運用ルールで縛る必要がある (アプリ A が誤って 15 を送信すれば B のデバイスに伝わる)

---

## それでも source-app 単位の strict filter が欲しい場合

「同じ LAN・同じ group で、複数アプリを排他的に切り替えたい」用途 (例: 同じ Hapbeat を A・B で時間分けて使う、エンタープライズデモなど) には、**送信元アプリ ID による strict source filter** の追加が技術的には可能です:

- 各アプリに UUID / app_id を持たせる
- Hapbeat デバイスは最初に受け取った app_id を pin、それ以外を無視
- デバイス上のボタン操作で pin 解除 → 新しい app_id を受け入れ可能

bHaptics エンタープライズ版に近い設計ですが、**実装には contracts / device firmware / SDK の同時改修が必要** (DEC レベルの設計判断)。

需要があれば実装を検討するので、ユースケースを添えて [GitHub Issues](https://github.com/Hapbeat/hapbeat-unity-sdk/issues) にご連絡ください。

---

## まとめ

| 状況 | 推奨対応 |
|---|---|
| 通常運用 (1 デバイス = 1 アプリ) | デフォルト構成で OK |
| 同じ部屋で複数アプリ + 複数 Hapbeat | **LAN / SoftAP を物理分離** (最推奨) |
| LAN 分離不可・触覚切替だけしたい | **group ID 範囲で分割** (表示は混乱するが動作は OK) |
| strict source filter が必要 | Issues に連絡 (現時点では未実装) |
