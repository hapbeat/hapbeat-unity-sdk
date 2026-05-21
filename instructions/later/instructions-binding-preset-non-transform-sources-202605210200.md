# EventMap preset linker の SliderValue / External 対応

**作成日**: 2026-05-21
**起点**: Tutorial Z4 で `HapbeatParameterBinding` に SliderValue / External source 追加。Inspector 直接設定では動作するが、EventMap window から binding を追加すると preset linker が **source の auto-resolve を Transform path しか面倒見ない** ため、SliderValue で `_sourceSlider` が未設定の component が auto-attach されて runtime warning が出る。

## 現状の preset linker 仕様 (Transform 時代の設計)

`HapbeatBindingPreset` の field:
- `sourceTransformPath` (string): wired trigger 配下の Transform path
- `sourceProperty` (enum): LocalPosition* / Velocity* / SliderValue / External 等

Linker (`HapbeatEventMapWindow.SyncLinkedBindingsForEntry`) は:
1. wired trigger 配下で `sourceTransformPath` を解決 → 該当 GO に `HapbeatParameterBinding` を attach
2. trigger 所有 GO にも attach (二重)
3. attach 後 `_sourceTransform` を解決パスでセット
4. **`_sourceSlider` は触らない** (Transform 想定)
5. **External の場合 SetValue wiring を作る仕組みがない**

## 改修案

### A. Preset 側に source 種別ごとの path field を追加

```csharp
public class HapbeatBindingPreset {
    public string sourceTransformPath = "";   // 既存
    public string sourceSliderPath = "";       // 新規: SliderValue source 用
    // External は path で表現できない → preset linker はスキップ (component を attach するだけ、wiring 不要)
}
```

### B. Linker 側で source 種別に応じた auto-resolve

```csharp
switch (preset.sourceProperty) {
    case BindingSourceProperty.SliderValue: {
        var sliderGo = ResolvePath(trigger, preset.sourceSliderPath);
        var slider = sliderGo?.GetComponent<Slider>();
        if (slider != null) {
            binding._sourceSlider = slider;
        }
        break;
    }
    case BindingSourceProperty.External: {
        // path 不要、component attach のみ
        break;
    }
    default: { // Transform 系
        var t = ResolvePath(trigger, preset.sourceTransformPath);
        if (t != null) binding._sourceTransform = t;
        break;
    }
}
```

### C. EventMap UI の Source Object 入力欄を source 種別に応じて

- 現状: `Source Object` 欄 (path string) は Transform のみ想定
- 改修: Source Property = SliderValue 選択時、UI が Slider component を持つ GO のみ受付けるよう絞る
- 改修: Source Property = External 選択時、UI が Source Object 欄を非表示にする (path 不要)

### D. 重複 attach の整理

現状 linker は trigger 所有 GO + source GO 両方に binding component を attach する設計だが、SliderValue / External では trigger 所有 GO 1 個で十分。

- Transform 系: 物理計算が source 物体起点なので source GO に置きたい (現状維持)
- SliderValue / External: 単に値を読むだけなので trigger 所有 GO に 1 個で OK
- Linker の attach 数を source 種別で分岐

## 影響範囲

- `Runtime/HapbeatEventEntry.cs` (HapbeatBindingPreset に `sourceSliderPath` 追加)
- `Runtime/HapbeatParameterBinding.cs` (link 時の auto-resolve に Slider 対応追加)
- `Editor/HapbeatEventMapWindow.cs` (UI: source 種別に応じた Source Object 入力切替、linker 分岐)
- `Editor/HapbeatBatchSetupWindow.cs` (binding preset 生成時の path 設定)

## 着手前提

- Tutorial が完成して安定運用に入った後
- Slider binding 利用者が増えてきて preset 管理の需要が出てきたら
- 短期は 「EventMap UI から SliderValue binding 追加しない、Component AddComponent で手動 setup」回避策で対応

## 当面の workaround (Tutorial)

Z4 builder が `StreamPanelHud` に SliderValue source 設定済 binding を直接 attach → EventMap entry 側に preset を作らない / 作っても無視。Inspector で確認できる状態。
