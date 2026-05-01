# Instructions: HapbeatParameterBinding と HapbeatTriggerBase の直接結合（案A）

**発行日:** 2026-04-22
**起票:** project_unity_sdk_binding_trigger_coupling.md
**優先度:** 後回し（設計改善。既存機能に影響大のため慎重に）

## 背景

現状の `HapbeatParameterBinding` は entry id スキャンによる間接結合で、同じ GameObject に
trigger と binding がいても entry id が一致しないとミスマッチになる。

ユーザーの mental model: 「1 entry = 1 trigger + bindings は一体もの」
実装の現実: 「独立した component の loose coupling」

**案A（推奨）**: binding に `_targetTrigger (HapbeatTriggerBase)` フィールドを追加し、
Inspector dropdown で直接選択させる。runtime の entry id 走査を廃止。

詳細分析: `docs/agent-memory/project_unity_sdk_binding_trigger_coupling.md` 参照。

## タスク

### 1. HapbeatParameterBinding に `_targetTrigger` フィールドを追加

```csharp
[SerializeField] private HapbeatTriggerBase _targetTrigger;
```

- Inspector に表示（ObjectField、同 GameObject / 子 / 親から選択）
- runtime の write path: `_targetTrigger?.ActivePlayback` に直接書き込み
- `_targetTrigger` が null の場合は既存の entry id 走査にフォールバック（移行期の互換）

### 2. Batch Setup の更新

`HapbeatBatchSetupWindow.ApplyBindingPresets` で binding 生成時に `_targetTrigger` を
同 GameObject の対応 trigger に自動セットする。

### 3. HapbeatMigrateLegacyReferences に追加

既存シーンの binding で `_targetTrigger` が null のものを自動解決するマイグレーション処理。
- binding の `_linkedBindingId` → entry id → 同 GameObject の trigger を解決して `_targetTrigger` にセット

### 4. 旧コードの削除

`_targetTrigger` が正しく動いたら:
- `LinkedOwnerEntryId` / scope check 関連コードを削除
- entry id 走査のフォールバック分岐を削除（旧コード不要化）

### 5. EventMap window の Bindings 表示を更新

`_targetTrigger` を使うように binding 一覧の解決ロジックを更新。

## 完了条件

- [ ] binding が trigger に直接参照されていて、Inspector で確認できる
- [ ] ミスマッチになる状況（entry id 不一致）が Inspector レベルで防がれる
- [ ] Batch Setup が `_targetTrigger` を自動セットする
- [ ] 旧シーンが migration で自動変換される
- [ ] 旧スキャンコードが削除されている
- [ ] 本ファイルを `instructions/completed/` に移動

## 依存関係

- **Required**: なし（独立して実施可能だが、大規模変更なので PokeButton 問題解決後が安全）
- **Parallel**: pawncontroller scratch 実装と並行可能
- **Downstream**: binding が trigger に直結すれば debag も簡単になり全般的に恩恵あり
