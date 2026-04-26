# docs/ — ユーザー向けドキュメント

このディレクトリは、本リポジトリの **ユーザー向け公開ドキュメント** 置き場である。

- 想定読者: Unity 開発者（SDK 利用者）
- 集約先: [hapbeat-devtools-site](https://devtools.hapbeat.com/) が build 時に自動取得し、`/docs/unity-sdk/` 等の URL で公開する

## ディレクトリ分類

| ディレクトリ | 用途 | 公開対象 |
|------|-----|--------|
| `docs/` | ユーザー向け解説（このディレクトリ） | ◯ portal site に掲載 |
| `dev-notes/` | 内部実装の知見・履歴・詳細メモ | ✗ portal には載らない |
| `Documentation~/` | Unity Package のドキュメント（`~` 接尾辞で import 除外） | Unity package 配布のローカル参照用 |

## 書くものの例

- インストール手順（UPM 登録・Sample import）
- Trigger コンポーネントの使い方
- EventMap ウィンドウのワークフロー
- BindingPreset のチュートリアル
- サンプルシーンの解説
