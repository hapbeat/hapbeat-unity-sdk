# Changelog

All notable changes to Hapbeat Unity SDK are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Version numbers follow [Semantic Versioning](https://semver.org/).

---

## [0.1.0] - 2026-05-06

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
- `EventMap Window` (`Window → Hapbeat → Event Map`) — 全エントリと配線を GUI で一覧管理、Wiring 逆引きスキャン、Play テスト

**ストリーミング**
- StreamClip モード — WAV を chunk 送信し、ParameterBinding で動的ゲイン・パン制御
- `streamSendAheadSeconds` で送信先行バッファを調整

**UI / Editor**
- `HapbeatStatusOverlay` — 接続状態・RTT を Canvas に表示するデバッグ UI
- `HapbeatEventLogger` — Hapbeat 系ログをフィルタしてファイル保存
- `HapbeatEventMapEditor` — Play-mode Snapshot/Restore、ポータビリティ確認
- Setup メニュー (`Hapbeat → Setup`) — HapbeatSDK フォルダ自動生成
- Build Samples メニュー (`Hapbeat → Build Samples`) — シーン・EventMap・Kit フォルダを自動生成

**サンプル**
- `BasicExample` — キーボード操作で SDK 基本機能を確認する最小構成
- `Tutorial` — 5 ゾーン (Bowling / Door / Pickup / Stream Console / Target Range) で SDK 全機能を体験。XR デバイス不要
- `XriHelpers` — `HapbeatXRGrabFilter` / `HapbeatXRSocketFilter` (XRI opt-in)

**XR 向け**
- XR Helpers sample で XRI grab / socket イベントを Hapbeat に橋渡し
- Quest 3 / Quest 3s 動作確認済み

[0.1.0]: https://github.com/Hapbeat/hapbeat-unity-sdk/releases/tag/v0.1.0
