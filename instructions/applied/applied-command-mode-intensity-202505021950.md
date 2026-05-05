# Applied Note: Command モード manifest.intensity 乗算対応

**適用日:** 2026-05-02  
**起点:** workspace apply-instructions セッション  
**起票指示書:** `instructions/completed/instructions-verify-volume-spec-202604292200.md`  
**関連:** hapbeat-device-firmware 側変更 (device が manifest.intensity を runtime 参照しない新仕様)

## 変更内容

device 側の `kit_loader` / `kit_installer` / `tcp_server` が manifest.intensity を runtime 参照しなくなった (2026-04-29) のに合わせ、Unity SDK 側でも **送信側が gain × intensity を乗算して wire gain を決定する** 仕様に統一した。

### 変更ファイル

| ファイル | 変更概要 |
|---|---|
| `Runtime/HapbeatEventEntry.cs` | `GetEffectiveGain()` を Command でも intensity 乗算するよう統一。sentinel を `< 0` に厳密化（0 は有効な authored intensity として honor）。doc comment 更新 |
| `Runtime/HapbeatTriggerBase.cs` | Command Fire パスで `entry.gain * _gainMultiplier` → `entry.GetEffectiveGain() * _gainMultiplier`。sentinel -1 の場合に warning を 1 回出力。StreamClip の sentinel 比較も `<= 0` → `< 0` に統一 |
| `Runtime/HapbeatBridge.cs` | `ApplyManifestIntensity(entry, rawGain)` helper 追加。`Play` / `PlayByIndex` / `PlayScaled` / `PlayWithCurve` の 4 メソッドで manifest 乗算を適用 |
| `Runtime/HapbeatCollisionTrigger.cs` | `FireWithVelocity` の gain 計算に `entry.CachedManifestIntensity` を乗算 |
| `Editor/HapbeatEventMapWindow.cs` | Command Test-play パスで `entry.gain` → `entry.GetEffectiveGain()`。sentinel -1 の warning 追加 |
| `Editor/HapbeatManifestIntensity.cs` | doc comment から「Command mode devices have the intensity flashed...」の旧記述を削除 |
| `Editor/HapbeatTriggerBaseEditor.cs` | Gain FloatField に tooltip「Wire = Gain × Manifest Intensity × Trigger Multiplier」を追加 |

## 互換性注意事項

旧 firmware (manifest 乗算あり) + 新 SDK (乗算あり) の組み合わせでは **manifest 二重適用** になる。
必ず新 firmware と同時にアップグレードすること。

## 検証状況

Unity Editor でのコンパイル確認: 未実施 (Unity Editor が当セッションで利用不可)。  
実機確認: 未実施。

## 当該 repo エージェントへのアクション

- Unity Editor でコンパイルエラーがないことを確認し、問題なければ本 note を `instructions/completed/` へ移動
- `M:\GameEngine\Unity\Projects\HapbeatSDKSamples\` での実機確認 (FIRE / CLIP 音量が manifest を反映するか)
- 問題があれば新規 fix instruction を `instructions/` に作成
