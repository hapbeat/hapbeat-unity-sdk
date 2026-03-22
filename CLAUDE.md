# CLAUDE.md — hapbeat-unity-sdk

## repo の目的

Unity 向け SDK。C# API、Editor tools、diagnostics UI、pack install 導線、サンプルシーンを提供する。

## 全体アーキテクチャ上の役割

contracts / bridge / pack の上に薄く載せる最初の正式 SDK。

## 責務

- C# API（HapbeatManager, EventTrigger 等）
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
- Bridge を介さない独自接続方式を作る

## まだ作らないもの

- VR 専用機能
- Bluetooth 接続
- 高度な Editor UI

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
- **Bridge** — 接続先。UDP でメッセージを送信する
