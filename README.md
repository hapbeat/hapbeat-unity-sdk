# hapbeat-unity-sdk

Unity 向けの最初の正式サポート SDK。

## 概要

このリポジトリは、Hapbeat デバイスを Unity アプリケーションから制御するための公式 SDK を提供します。Unity に最適化しつつも、全体仕様を歪めない薄い SDK クライアントとして設計されています。

## 全体の中での位置づけ

hapbeat-contracts / hapbeat-bridge / hapbeat-pack の上に薄く載せる最初の SDK クライアントです。Unity 向けに最適化された API を提供しますが、プロトコルや仕様の中核を担うものではありません。重要だが中核ではない、という位置づけです。

## 設計方針

- Unity に最適化しすぎて全体仕様を歪めない
- 共通仕様（contracts）に従い、Bridge を介して通信する
- 独自プロトコルを作らない

## 依存関係

- [hapbeat-contracts](../hapbeat-contracts) — メッセージ仕様・Event ID 定義
- [hapbeat-bridge](../hapbeat-bridge) — デバイス通信の中継サーバ

## 今後の最初のタスク

1. C# 公開 API 設計（HapbeatManager, EventTrigger 等）
2. Bridge 接続クライアント設計（UDP 送信）
3. Editor tools 検討（Pack 管理、Event ID ブラウズ）

## 現状

現時点では実装コードはありません。設計・計画フェーズです。
