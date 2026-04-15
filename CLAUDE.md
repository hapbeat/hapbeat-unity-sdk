# CLAUDE.md — hapbeat-unity-sdk

## repo の目的

Unity 向け SDK。C# API、Editor tools、diagnostics UI、pack install 導線、サンプルシーンを提供する。

## 全体アーキテクチャ上の役割

contracts / bridge / pack の上に薄く載せる最初の正式 SDK。

## 責務

- C# API（HapbeatManager, EventTrigger 等）
- Wi-Fi UDP 直接通信（標準方式）
- デバイス自動検出（UDP ブロードキャスト PING）
- Bridge 接続クライアント（UDP 送信）
- Editor tools（Pack 管理、Event ID ブラウズ）
- diagnostics UI（接続状態、デバイス一覧）
- pack install 導線
- サンプルシーン

## 管理対象

- Unity C# コード
- Editor 拡張
- サンプルシーン
- Unity パッケージ設定

## 管理対象外

- Bridge サーバ実装
- ファームウェア
- Pack ビルドツール本体
- 送信機ファーム

## 依存関係

### 依存してよい repo

- hapbeat-contracts
- hapbeat-bridge

### 依存される repo

- なし

## 壊してはいけない公開インターフェース

- C# 公開 API（HapbeatManager.Play(eventId) 等）

## やってはいけないこと

- Unity 固有の仕様で全体仕様を歪める
- 独自プロトコルを作る
- 送信機ファームと直接通信する前提を作る

## まだ作らないもの

- VR 専用の高度な機能（ボディトラッキング連動、ハンドトラッキング連動等）
- Bluetooth 接続
- 高度な Editor UI

## VR サンプル方針

- VR 専用 API は作らない。既存の API（Play, EventMap, Trigger）で VR 対応できることを示す
- XR Interaction Toolkit に直接依存しない（asmdef に参照を追加しない）。UnityEvent 経由の疎結合
- サンプルは Samples~ に配置し、UPM の Import Samples で導入可能にする
- Player Demo（体験者向け）と Creator Tutorial（開発者向け）の2系統

## 最初の着手タスク

1. C# 公開 API 設計
2. Bridge UDP クライアント実装
3. 最小サンプルシーン
4. Editor tools 基本設計

## 実装優先順位

C# API → Bridge 接続 → サンプルシーン → Editor tools → diagnostics

## テスト

- C# API のユニットテスト
- Bridge 接続の統合テスト

## オフライン動作

Bridge がローカルにいれば動作可能。クラウド不要。

## 重要な概念

- **Event ID** — これで再生指示を送る
- **Bridge** — 接続先（オプション）。UDP でメッセージを送信する。Wi-Fi UDP 直接通信が標準方式であり、Bridge は中継が必要な場合に使用する
- **Discovery** — UDP ブロードキャスト PING によるデバイス自動検出。LAN 上の Hapbeat デバイスを検出し直接接続する

## 開発用 Unity プロジェクト

サンプルシーンの開発・動作確認用の Unity プロジェクト。SDK リポジトリとは別の場所に配置。

- **パス**: `M:\GameEngine\Unity\Projects\HapbeatSDKSamples\`
- **サンプル Import 先**: `Assets\Samples\Hapbeat SDK\0.1.0\`
- **編集許可**: このプロジェクト内のファイルは自由に編集・削除してよい
- **方針**: プロジェクト側で直接編集・動作確認し、完了後にシーンビルダー（Samples~/Editor/）にまとめて反映する

## 指示書

- `instructions/` — 他セッションからの未実行の指示書
- `instructions/completed/` — 完了済みの指示書
- セッション開始時に `instructions/` を確認し、該当する指示書があれば適用する

## エージェント共通メモリ（Claude / OpenAI 系共通）

- セッション間で引き継ぐ知見・ログ・ルールはワークスペースルートの `docs/agent-memory/` に保存する
- インデックスは `docs/agent-memory/INDEX.md`
- この repo から参照する場合の相対パスは `../docs/agent-memory/`
- メモリを新規作成・更新した場合は、必ず `INDEX.md` も更新する
