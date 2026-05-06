# Applied Note: kit-manifest から `kit_id` を削除して `name` 一本化

**適用日:** 2026-05-06
**起点:** workspace `wizardly-rhodes-acf40e` worktree (Basic Example 開発中の確認)
**起票指示書:** `hapbeat-contracts/instructions/instructions-drop-kit_id-from-manifest-202604280515.md`
**関連:** Studio 側は既に追従済み、contracts 側は近日 commit 予定

## 背景

contracts の指示書に従い manifest.json から `kit_id` フィールドを削除し、`name` フィールドを「ディレクトリ名 = wire 上の kit_id」と一本化する。Studio が既にこの形式で出力しており、SDK 同梱 Sample manifest を整合させる必要があった。

新方針:
- `manifest.name` = on-disk フォルダ名 = wire 上の `kit_id` payload (3 つ同値)
- フォルダ名は `^[a-z][a-z0-9-]*$` (kit_id pattern)
- `manifest.kit_id` フィールドは削除

## 影響範囲調査 (SDK 側コード)

| ファイル | manifest.kit_id を読む? | 対応 |
|---|---|---|
| `Editor/HapbeatEventMapWindow.cs` (KitManifestEvent パーサ) | ❌ kitId は ディレクトリ名から取得 (`Path.GetFileName(kitDir)`) | 変更不要 |
| `Editor/HapbeatManifestIntensity.cs` | ❌ events/parameters のみ参照 | 変更不要 |

SDK 側コードは元から `manifest.kit_id` を読んでいなかったため、コード修正は不要。

## 変更ファイル

- `Samples~/BasicExample/Kit/manifest.json`
  - `"kit_id": "basic-exam-kit"` 行を削除
  - `"name": "Basic Exam Kit"` (display name 形式) → `"name": "basic-exam-kit"` (kit_id 形式) に変更

## 検証状況

- 静的検証: `manifest.kit_id` を読む箇所が SDK 側に無いことを grep で確認
- 動的検証: 未実施 (Unity Editor が当セッションで利用不可)

## アクション (当該 repo エージェント向け)

- Unity Editor で BasicExample の Build Samples メニューを実行し、生成された `Assets/HapbeatSDK/Kits/basic-exam-kit/manifest.json` が `name: "basic-exam-kit"` で `kit_id` 無しになっているか確認
- HapbeatManifestIntensity が新形式 manifest を正しく parse できるか (intensity 解決が壊れていないか) 動作確認
- 問題なければ本 note を `instructions/completed/` へ移動
