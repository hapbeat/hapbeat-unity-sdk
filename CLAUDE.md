# CLAUDE.md — hapbeat-unity-sdk

## repo の目的

Unity 向け SDK。C# API、Editor tools、diagnostics UI、kit install 導線、サンプルシーンを提供する。

## 全体アーキテクチャ上の役割

contracts / bridge / kit の上に薄く載せる最初の正式 SDK。

## 責務

- C# API（HapbeatManager, EventTrigger 等）
- Wi-Fi UDP 直接通信（標準方式）
- デバイス自動検出（UDP ブロードキャスト PING）
- Bridge 接続クライアント（UDP 送信）
- Editor tools（Kit 管理、Event ID ブラウズ）
- diagnostics UI（接続状態、デバイス一覧）
- kit install 導線
- サンプルシーン

## 管理対象

- Unity C# コード
- Editor 拡張
- サンプルシーン
- Unity パッケージ設定

## 管理対象外

- Bridge サーバ実装
- ファームウェア
- Kit ビルドツール本体
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

## テスト

- C# API のユニットテスト
- Bridge 接続の統合テスト

## オフライン動作

Bridge がローカルにいれば動作可能。クラウド不要。

## 重要な概念

- **Event ID** — これで再生指示を送る
- **Bridge** — 接続先（オプション）。UDP でメッセージを送信する。Wi-Fi UDP 直接通信が標準方式であり、Bridge は中継が必要な場合に使用する
- **Discovery** — UDP ブロードキャスト PING によるデバイス自動検出。LAN 上の Hapbeat デバイスを検出し直接接続する
