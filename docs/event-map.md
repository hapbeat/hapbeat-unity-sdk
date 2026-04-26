---
title: EventMap ウィンドウ
description: Event ID と Trigger / Kit の対応を GUI で一元管理する EventMap の使い方。
---

EventMap は **Event ID と Trigger / Kit の対応関係を一括管理する Editor ウィンドウ**です。コードに Event ID 文字列を散らかすことなく、Inspector で Trigger を紐付け・整理できます。

メニューバー: `Hapbeat` → `EventMap`

## ウィンドウの構成

2 列レイアウト:

- **左**: シーン中の Trigger / Wiring を逆引きスキャンした一覧
- **右**: 選択中 Trigger の編集パネル + Kit との対応

## 主な機能

### Event エントリ管理

- **+ Add Event**: 新規 Event エントリを追加
- **Copy / Paste / Duplicate**: 行操作
- **Category + Event Name** に分割表示（`combat/gunshot` のような階層整理）

### Wiring 逆引き

- シーン中のどの Trigger がこの Event ID を使っているか自動列挙
- クリックで該当 Trigger に Hierarchy ジャンプ

### Mode 別アイコン

各 Event 行に mode を示すアイコン:

- **▶** FIRE (command)
- **♪** CLIP (stream_clip)
- **~** LIVE (stream_source)

### BatchSetup

選択した GameObject に対して、対応 Trigger を自動追加・設定する機能。

- **Drag & Drop**: GameObject を Drop ターゲットに、Trigger 自動追加
- **Reference Import**: 参照 BatchSetup から設定をクローン
- **Replace toggle**: 既存設定を上書きするか保持するか

## EventMap ↔ Kit の連動

EventMap は Studio で作成した Kit と整合しています。

- Studio で Kit に Event ID を追加 → Unity 側で EventMap ウィンドウを開くと反映（Kit JSON を fetch）
- Auto-sync チェックボックスで Kit 側の変更を継続追従

> Kit 編集と Trigger 設定を別アプリで行う構成のため、Auto-sync で連携を簡素化できます。

## BindingPreset との連携

`HapbeatParameterBinding` のプリセット (`HapbeatBindingPreset`) は EventMap と link する仕組みがあります。runtime lookup で live tuning も可能です。

- BindingPreset に **stable GUID** を割当
- EventMap で preset を参照
- 実行中に preset 変更が即時反映

## Play-mode Snapshot / Restore

EventMap の状態は Play mode に入る前にスナップショットされ、終了時に復元されます。

- Play 中の試行錯誤で EventMap が変わっても Edit mode に戻ると元の状態に
- 意図的な変更は明示的に Apply

## Portability

EventMap データは Project Asset として保存されます。

- Git で履歴管理可能
- 他プロジェクトに asset コピーで移植可能

## 次のステップ

- [Trigger コンポーネント](/docs/unity-sdk/triggers/)
- [Hapbeat Studio で Kit を作る](/docs/studio/getting-started/)
