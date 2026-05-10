# Changelog

All notable changes to Hapbeat Unity SDK are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [0.1.2] - 2026-05-11

### Fixed
- BasicExample: EventMap が trigger に null のまま残る問題の根本修正
  `SerializedObject` で基底クラスの `protected` フィールドを辿れない
  Unity バージョン依存の問題が根本原因。`HapbeatTriggerBase` に
  `EditorSetupEntry()` を追加して直接代入に変更。

---

## [0.1.1] - 2026-05-11

### Fixed
- BasicExample: fresh project で EventMap がトリガーに自動設定されない問題を修正
  (`BuildOrLoadEventMap` で `SaveAssets` 後に `Refresh + LoadAssetAtPath` してリロード)
- `EnsureFolder`: フォルダ作成時のみ `AssetDatabase.Refresh()` を呼ぶよう修正（不要な多重 Refresh の排除）

### Changed
- `docs/` → `docs~/` にリネーム（Unity Package のインポート対象外に統一）
- `Documentation~/` を削除（内容が `docs~/` に統合済み）

---

## [0.1.0] - 2026-05-07

Initial public release.

### Added

**Core runtime**
- `HapbeatManager` — シングルトン。Wi-Fi UDP broadcast で Hapbeat デバイスと通信
- `HapbeatBridge` — `Play / PlayScaled / PlayWithCurve / Stop` を提供するサブクラスベース
- `HapbeatClient` — UDP 送受信・PING/PONG・mDNS 自動検出
- `HapbeatDiscovery` — LAN 上の Hapbeat デバイスを mDNS で自動発見
- `HapbeatConfig` — Group ID・ポート・Bridge 設定を ScriptableObject で管理

**Trigger コンポーネント**
- `HapbeatCollisionTrigger` — 物理衝突 / Trigger Enter|Exit に連動。速度スケールゲイン・AnimationCurve 対応
- `HapbeatAnimatorTrigger` — Animator パラメータ変化 (Bool / Float / Int) を検知して発火
- `HapbeatUnityEventTrigger` — UnityEvent の `Fire()` メソッドで任意タイミングに発火
- `HapbeatSequenceTrigger` — grab / hold / release を 1 コンポーネントで管理
- `HapbeatTickEmitter` — 連続値 (Slider・ScrollRect 等) の変化量に応じてスナップ触覚を生成
- `HapbeatParameterBinding` — Transform / Rigidbody → gain / pan をリアルタイムマッピング
- `HapbeatKeyDispatcher` — キー → UnityEvent のマッピング。Input System Package 完全対応

**EventMap**
- `HapbeatEventMap` ScriptableObject — Event ID・gain・mode (FIRE / CLIP) を一元管理
- `HapbeatEventEntry` — manifest.intensity を乗算した effective gain を計算
- `EventMap Window` (`Hapbeat → Event Map`) — 全エントリと配線を GUI で一覧管理、Wiring 逆引きスキャン、Play テスト

**App identity (デバイスディスプレイ表示)**
- `HapbeatConfig.appName` — Hapbeat デバイスのディスプレイに表示するクライアントアプリ名 (max 16 文字、空欄で `Application.productName` 自動使用)
- `CONNECT_STATUS` の周期送信 (Play 中) + 接続成立時 / 終了時の通知パケット送信

**ストリーミング**
- StreamClip モード — WAV を chunk 送信し、ParameterBinding で動的ゲイン・パン制御
- `streamSendAheadSeconds` で送信先行バッファを調整

**UI / Editor**
- `HapbeatStatusOverlay` — 接続状態・RTT を Canvas に表示するデバッグ UI
- `HapbeatEventLogger` — Hapbeat 系ログをフィルタしてファイル保存
- `HapbeatEventMapEditor` — Play-mode Snapshot/Restore、ポータビリティ確認
- `HapbeatSettingsWindow` — 接続設定 / アプリ名 / Bridge / Ping interval 等を一元編集
- Setup メニュー (`Hapbeat → Setup`) — HapbeatSDK フォルダ自動生成
- Build Samples メニュー (`Hapbeat → Build Samples`) — Basic / Tutorial の Scene + EventMap + Kit を自動生成 (Tutorial は With / Without 2 シーン同時生成)
- Debug メニュー — Event Logger 配線 / ログ録画 / Logs フォルダ参照などのユーザー向け診断ツール群

**サンプル**
- `BasicExample` — キーボード操作で SDK 基本機能を確認する最小構成
- `Tutorial` — 5 ゾーン (Bowling / Door / Pickup / Stream Console / Target Range) で SDK 全機能を体験。XR デバイス不要
- `XriHelpers` — `HapbeatXRGrabFilter` / `HapbeatXRSocketFilter` (XRI opt-in)

**XR 向け**
- XR Helpers sample で XRI grab / socket イベントを Hapbeat に橋渡し
- Quest 3 / Quest 3s 動作確認済み

**ドキュメント**
- [installation](docs/installation.md) — UPM Git URL 導線
- [getting-started](docs/getting-started.md) / [triggers](docs/triggers.md) / [event-map](docs/event-map.md) / [parameter-binding](docs/parameter-binding.md) / [streaming](docs/streaming.md) — 機能別解説
- [tutorial/](docs/tutorial/) — Tutorial サンプルの walkthrough (Plain → With 構築手順)
- [editor-menus](docs/editor-menus.md) — Hapbeat メニュー全項目の使い方逆引き
- [ai-assisted-workflow](docs/ai-assisted-workflow.md) — Claude Code 等で既存シーンに触覚を後付けする 4 ステップ + コピペプロンプト集
- [multi-app](docs/multi-app.md) — 複数アプリ共存時の運用指針 (LAN 分離 / group ID 切り分け)

[0.1.0]: https://github.com/Hapbeat/hapbeat-unity-sdk/releases/tag/v0.1.0
