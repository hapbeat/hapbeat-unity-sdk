# Tutorial: Unified Bowling Zone (発展案 / 棄却 → 後日検討)

**作成日**: 2026-05-18
**起点**: Tutorial 再設計議論 (5-zone curriculum)
**ステータス**: Tutorial 完成後 (Z2 / Z3 / Z4 / Z5 個別 zone 完成済み) に「**応用編 / アドバンス sample**」として実装するか判断する。短期は採用しない。

---

## 案の概要

Z1 Bowling + Z3 Pickup + Z5 Charge を 1 つのシーンに統合する。

**プレイ流れ:**
1. **ボール grab** (SequenceTrigger.Fire) — ボールを掴む
2. **ホールド中** (SequenceTrigger.Loop + ParameterBinding) — 手の motion velocity → loop gain modulation
3. **LMB hold で charge** — リリース時の発射速度がチャージ量に比例
4. **release** (SequenceTrigger.Stop + projectile launch) — ボールが forward に放物線で飛ぶ
5. **pin 衝突** (CollisionTrigger) — pin の collision velocity に応じた velocity-scaled haptic (built-in)

## 統合で覆える SDK 機能

- `HapbeatSequenceTrigger` (grab / hold loop / release)
- `HapbeatParameterBinding` (手の velocity → loop gain)
- Script-side charge curve → `GainMultiplier` (release haptic の強度)
- `HapbeatCollisionTrigger` (pin の velocity-scaled hit haptic)

→ 1 シーンで 4 patterns を流暢に体験できる **demo 性能は最高**。

## 棄却理由 (2026-05-18 議論)

- 1 zone に 4 concept 同時走行 → tutorial 学習用としては **どの操作がどの component の挙動か切り分け不能**
- 故障 (haptic 来ない) 時の原因切り分けが困難
- 「tutorial = 1 zone 1 pattern を明示」と「demo = 1 シーン全部詰め」は別物。後者は応用編に分ける

## 実装する場合のメモ

- 配置先: `Samples~/AdvancedDemo/` (Tutorial と分離した新 sample)
- 想定対象: Tutorial を一通り終えたユーザー向け
- README で「これは tutorial ではなく applied demo です」と明記
- 制御 script は Tutorial の `BallLauncher / PickupBoxController / ChargeShooter` の合成版が必要 (= `UnifiedBowlingController.cs` 1 ファイル)
- ParameterBinding source は `VelocityMagnitude` (Rigidbody) または `PositionDeltaMagnitude` (kinematic hold 中)。Pickup 中は kinematic なので後者
- charge curve の AnimationCurve は Inspector で designer 調整できるよう露出

## 着手条件

以下が全て揃ってから判断する:
- [ ] Tutorial 5 zones (Z1-Z5) が完成し動作確認済み
- [ ] without-haptic 版 (Tutorial_Plain.unity) も生成可能
- [ ] ユーザーが「応用編欲しい」と明示的に要望
- [ ] Sample サイズ増加 (UPM ZIP 容量) が許容範囲

それまで本 instruction は塩漬け。
