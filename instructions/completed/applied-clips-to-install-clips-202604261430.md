# Applied: clips/ → install-clips/ rename (DEC-027) — cosmetic only

**日付:** 2026-04-26
**起点セッション:** workspace (hapbeat-studio セッション内で issue 単位の横断編集)
**対象 repo:** hapbeat-unity-sdk
**関連 DEC:** [DEC-027](../../../docs/decision-log.md)
**ステータス:** ✅ 適用済み — レビューしてください
**機能影響:** なし (manifest 解析は path-agnostic)

## この repo に入った変更

- `Editor/HapbeatEventMapWindow.cs`
  - `DrawKitFolderHint()` の subfolder 識別子 `"clips"` → `"install-clips"` (現状 caller は `"stream-clips"` のみで dead branch だが整合のため)
  - hint label と error log の `clips/` 表記を `install-clips/` に
  - 関連 doc-comment 更新
- `Editor/HapbeatKitsReadme.cs`
  - FIRE mode の `DrawModeRow` location 列 `"clips/"` → `"install-clips/"`
  - Troubleshooting / Version control セクションの `clips/` 表記を `install-clips/` に
  - `templateVersion` を `"1"` → `"2"` に bump
- `Editor/HapbeatKitsFolderCreator.cs`
  - `ResetReadmeMenu()` で書き戻す `templateVersion` を `"2"` に同期
- `docs/kit-integration.md`
  - サブディレクトリ構造図の `clips/` → `install-clips/`
  - パイプライン手順説明の `clips/` → `install-clips/`

`Editor/HapbeatManifestIntensity.cs` は **変更不要**。manifest の `events.<id>.clip` を文字列としてそのまま受け取り、kit ルートと join するだけなので、`install-clips/foo.wav` も `stream-clips/foo.wav` も path-agnostic に処理される。command mode 側は `eventId` で match しているので path 影響なし。

## 変更の背景

contracts (DEC-027) で Kit の `clips/` フォルダを `install-clips/` に rename。Unity SDK が読む manifest と Studio が `Assets/HapbeatKits/<kit>/install-clips/` に配置する WAV のパスが整合するよう、UI hint / readme / docs を追従させた。

機能側は path-agnostic なので変更なし。

## 横断的に同セッションで入った関連変更

- **contracts**: schema / spec / fixture
- **kit-tools**: builder / validator / installer / tests
- **device-firmware**: TCP / kit_installer / kit_loader
- **manager**: `pack_normalize.py`
- **studio**: kit ZIP 配置 + manifest dict key

## 検証状況

- Unity プロジェクトでの Compile 検証は本セッションでは未実施 (Editor only コードのため UPM consumer 側で確認推奨)
- 開発用 Unity プロジェクト `M:\GameEngine\Unity\Projects\HapbeatSDKSamples\` で次回 SDK update 時にコンパイルエラーがないか確認すること

## この repo のエージェントへのアクション

1. 上記 4 ファイルの diff を確認
2. 開発用 Unity プロジェクトで:
   - SDK を再 import
   - `Hapbeat > Setup > Reset Readme` で readme 再生成 → install-clips/ 表記が見えること
   - EventMap で StreamClip mode 選択 → "Look in: ..." hint が `stream-clips/` を案内すること（変わらず）
   - Studio で deploy した kit が `Assets/HapbeatKits/<kit>/install-clips/` に置かれ、Reveal が機能すること
3. 問題なければ本ファイルを `instructions/completed/` に移動
