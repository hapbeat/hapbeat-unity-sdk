# 指示書: Volume / intensity 取り決め変更後の Unity SDK 整合性確認

**起票日:** 2026-04-29
**起点:** hapbeat-studio セッション (ファームウェア書込み + intensity 仕様整理)
**優先度:** 中 (Unity Editor 検証が必要なため別セッションで)

## 背景: ファームウェア + Studio 側の変更

device-firmware と Studio で **manifest.intensity の解釈場所** を整理した。
device 側の `kit_loader.cpp` / `kit_installer.cpp` / `tcp_server.cpp` で
manifest の `parameters.intensity` を runtime 参照していたのを撤廃し、
device は req.gain 一本のみで再生する pure executor に統一した。

新仕様 (2026-04-29 以降):

| Layer | 役割 |
|---|---|
| WAV 振幅 | 音源作成時 (Studio で baking しない) |
| **manifest.intensity** | Studio の Kit エディタで設定。**送信側 (Studio / SDK)** が PLAY コマンドの gain を決定する基準として参照する |
| **req.gain (wire)** | 送信側が `entry.gain × manifest.intensity × runtime modulation` などを乗算した結果。device はこの値だけで再生 |
| device 音量 | wiper (ハードウェア音量) |

device 側の `entry->gain` (manifest 由来) は 1.0 固定にしたため、
PLAY 時の `combinedGain = req.gain * entry->gain = req.gain * 1.0 = req.gain` となる。

関連 applied note: `hapbeat-device-firmware/instructions/applied/applied-stop-reading-manifest-intensity-202604292000.md`

## Studio 側の現状

`hapbeat-studio` の Devices > 再生テスト > FIRE では `gain: 1.0` 固定で
preview_event を送信する。Studio はまだ kit manifest を読んで gain を
決めていないので、 Command イベントは「manifest 完全無視 = 常に gain 1.0」
で再生される。Studio で manifest 値を反映する対応は別タスク予定。

## Unity SDK の現状 (要確認)

### Runtime/HapbeatEventEntry.cs `GetEffectiveGain()`

```csharp
public float GetEffectiveGain()
{
    switch (mode)
    {
        case HapticMode.StreamClip:
            return _cachedManifestIntensity > 0f
                ? gain * _cachedManifestIntensity
                : gain;
        case HapticMode.Command:
        default:
            return gain;
    }
}
```

**問題点**: `Command` 分岐が「device 側で manifest が乗る」前提で
plain `gain` を返している。device 側は今後 manifest を見ないので、
**Command も `gain × manifest_intensity` を返さないと、Unity から
Fire したイベントは manifest を完全無視する**。

### Runtime/HapbeatTriggerBase.cs Command Fire パス

```csharp
case HapticMode.Command:
    HapbeatManager.Instance.Play(entry.eventId, entry.gain, entry.group, label, target);
```

raw `entry.gain` を渡しており、manifest が wire gain に乗らない。

### Runtime/HapbeatBridge.cs

`Play / PlayByIndex / PlayScaled / PlayWithCurve` 全てが `entry.gain`
ベースで送信、manifest 乗算なし。

### Runtime/HapbeatCollisionTrigger.cs

`gain = curveValue * entry.gain` で送信、manifest 乗算なし。

### Editor/HapbeatEventMapWindow.cs (Test-play)

Command パスで `entry.gain` 直送、manifest 乗算なし。

## 整合性確認タスク

このセッション (Unity SDK 単独セッション) で以下を確認・実装してほしい:

1. **`HapbeatEventEntry.GetEffectiveGain()` を Command でも `gain × manifest_intensity`
   に揃える**
   - 現状の Command 分岐 `return gain` を削除し、StreamClip と同じ
     `_cachedManifestIntensity > 0f ? gain * _cachedManifestIntensity : gain`
     に統合
   - cache 未解決の sentinel を `< 0` に厳密化 (manifest で intensity=0 を意図的に
     書いた場合 0 を honor する。`<= 0` だと 0 がフォールバックされる)

2. **runtime fire 経路で `entry.GetEffectiveGain()` を使う**
   - HapbeatTriggerBase.cs Command 分岐を `entry.gain` → `entry.GetEffectiveGain()`
   - HapbeatBridge.cs の 4 メソッドで manifest 乗算を入れる helper
     (`ApplyManifestIntensity`) を追加
   - HapbeatCollisionTrigger.cs の gain 計算に `× intensity` を追加

3. **Editor test-play でも同様**
   - HapbeatEventMapWindow.cs の Command Test-play パスで
     `GetEffectiveGain()` を使う

4. **doc / tooltip の更新**
   - HapbeatManifestIntensity.cs の doc comment「Command mode devices have
     the intensity flashed ... and apply it internally」を撤去
   - HapbeatTriggerBaseEditor.cs の Gain FloatField に tooltip
     「Wire = Gain × Manifest Intensity」を追加 (Inspector 表示と実 wire 値の
     乖離を明示)

5. **互換性確認**
   - `HapbeatBridge.PlayScaled` 等で `gainOverride` を使っているコードへの
     影響: `gainOverride` も manifest 乗算対象にすると wire 値が変わるので
     既存 scene の動作確認
   - HapbeatSequenceTrigger は既に `entry.GetEffectiveGain()` を使うので
     #1 の修正だけで自動的に整合

6. **既存 Unity プロジェクト (`M:\GameEngine\Unity\Projects\HapbeatSDKSamples\`)
   での再生確認**
   - 新 firmware を焼いたデバイスで Sample scene の haptic が
     manifest.intensity を反映することを実機確認
   - cache 未解決 (kit deploy 前) で Console warning が 1 回だけ出る
     ことを確認

## 注意点

### 互換性マトリクス

| SDK | Firmware | 結果 |
|---|---|---|
| 旧 (manifest 乗算なし) | 旧 (device 乗算あり) | OK (`gain × manifest`) |
| 旧 (乗算なし) | **新 (乗算なし)** | **manifest 完全無視** ← 現在の状態 |
| **新 (乗算あり)** | 旧 (乗算あり) | **manifest 二重適用** (`gain × manifest²`) |
| 新 (乗算あり) | 新 (乗算なし) | OK (`gain × manifest`) |

→ Unity SDK と device firmware は **必ず同時にアップグレード** する必要がある。
   旧 firmware の device で新 SDK を使うと音量が想定より小さくなる。

### 二重適用しないようにする確認

StreamClip の `HapbeatManager.StreamAudioClip` は per-chunk PCM を
`× streamGain` (= `entry.GetEffectiveGain() × ParameterBinding`) で
事前乗算したうえで `STREAM_BEGIN.gain = 1.0` を送る。device 側は
STREAM_BEGIN.gain を 1.0 で受けるのでここの取り扱いは変更不要。

### 完了条件

- [ ] 上記 6 項目すべて対応
- [ ] tsc/コンパイル通過 (Unity Editor で実機確認)
- [ ] Sample scene で実機確認 (FIRE / CLIP の音量が manifest を反映)
- [ ] applied note を `instructions/applied/` に作成
   (`applied-command-mode-intensity-<YYYYMMDDHHmm>.md`)
- [ ] 本ファイルを `instructions/completed/` に移動

## 参考ファイル

- `Runtime/HapbeatEventEntry.cs` (GetEffectiveGain method)
- `Runtime/HapbeatTriggerBase.cs` (Command Fire path)
- `Runtime/HapbeatBridge.cs` (Play / PlayByIndex / PlayScaled / PlayWithCurve)
- `Runtime/HapbeatCollisionTrigger.cs` (FireWithVelocity gain calc)
- `Editor/HapbeatEventMapWindow.cs` (Test-play handler)
- `Editor/HapbeatManifestIntensity.cs` (doc comment)
- `Editor/HapbeatTriggerBaseEditor.cs` (Inspector tooltip)
- `dev-notes/kit-integration.md` §3 (Gain layer model — 既に新仕様で書かれており参考になる)
