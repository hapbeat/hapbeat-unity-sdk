# Kit Integration — Unity SDK side

> この文書は decision-log DEC-023 / DEC-024 で定めた「Kit を全モード共通の SDK-authoritative 資産にする」設計を、Unity SDK 側でどう実装するかを説明する。実装は contracts / studio / kit-tools / firmware 側の対応と平行して進める。

## 1. 用語

| 用語 | 意味 |
|---|---|
| **Kit** | Studio で作られる触覚コンテンツ一式。`manifest.json` + 音源ファイル群 + メタデータ。UI 表記は Kit、contracts schema・内部プロトコルともに Kit に統一済み (2026-04-25) |
| **Kit binary (device flash 出力)** | Kit からデバイス向けにビルドされるフラッシュ用バイナリ。Kit のうち `mode=command` の event + その clip のみを含む |
| **Working Directory** | Studio が Kit ファイル一式を編集する作業ディレクトリ。Unity 連携時は `Assets/HapbeatKits/<kit-id>/` を推奨 |
| **HapbeatKitAsset (予定)** | Unity SDK 側で Kit を ScriptableObject-wrapped な first-class asset として扱うためのラッパー（future work） |

## 2. ディレクトリ配置の推奨

```
<Unity Project>/
└── Assets/
    └── HapbeatKits/                 ← Studio の working directory
        ├── README.md                ← SDK が生成（Studio URL + 手順）
        └── <kit-id>/                ← Studio が自動生成（例: hand-demo-kit）
            ├── manifest.json
            ├── clips/               ← command mode の wav
            │   └── *.wav
            └── stream-clips/        ← stream_clip mode の wav（Unity のみ、device には行かない）
                └── *.wav
```

- `Assets/HapbeatKits/README.md` は `Hapbeat > Setup > Create HapbeatKits Folder` Editor menu で生成される（空の状態から導線を確保するため）
- Studio で working directory に `Assets/HapbeatKits/` を指定すると、`<kit-id>/` サブディレクトリが自動生成され manifest.json skeleton + clips/ が作られる
- AssetDatabase は `.wav` をそのまま認識（AudioClip として import される）

## 3. 4-layer 強度モデル

kit-format.md §5 で定める最終出力計算式:

```
最終出力 = WAV振幅 × intensity × SDK_gain × デバイス音量
```

| Layer | 範囲 | 設定場所 | 説明 |
|---|---|---|---|
| WAV 振幅 | -1.0 〜 +1.0 | 音源作成時 | クリップの波形そのもの |
| intensity | 0.0 〜 1.0 | Studio (manifest.json の event.parameters.intensity) | 制作者の基準強度 |
| SDK_gain | 0.0 〜 約 2.0 | Unity EventMap (entry.gain × ParameterBinding) | プロジェクト倍率 + runtime 変動。1.0 が intensity そのまま、2.0 で intensity の倍（clip 制限あり） |
| デバイス音量 | wiper 0–127 | デバイス側 | ユーザー調整。SDK は感知しない。Studio 側で Kit 作成時の基準 wiper を `event.parameters.device_wiper` に記録し再現用とする |

**Unity SDK での runtime 計算**:

```csharp
// HapbeatParameterBinding / HapbeatTriggerBase で:
float gainField = kit.LookupIntensity(entry.eventId)
                  * entry.gain
                  * bindingFactor; // 0-1 + ブースト
HapbeatManager.Instance.Play(entry.eventId, gainField, ...); // float32 normalized (contract 上は 0-1 想定、1 を超える値は device 側で clip される)
```

## 4. HapbeatKitAsset の設計（future work）

`Assets/HapbeatKits/<kit-id>/manifest.json` を AssetPostprocessor で監視し、以下の ScriptableObject を同期生成/更新する:

```csharp
[CreateAssetMenu(fileName = "HapbeatKit", menuName = "Hapbeat/Kit Asset", order = 1)]
public class HapbeatKitAsset : ScriptableObject
{
    public string kitId;
    public string version;
    public List<KitEvent> events;
    public string workingDirectoryPath; // Assets/HapbeatKits/<kit-id>
}

[Serializable]
public class KitEvent
{
    public string eventId;
    public HapticMode mode;
    public float intensity;   // from manifest parameters.intensity
    public int deviceWiper;   // from manifest parameters.device_wiper (reference only)
    public bool loop;         // from manifest parameters.loop
    public AudioClip streamClip; // stream_clip mode 用（Unity 側で import された参照）
    public string description;
    public string[] tags;
}
```

**HapbeatEventMap との連携**:

```csharp
public class HapbeatEventMap : ScriptableObject
{
    public HapbeatKitAsset linkedKit;   // 新規追加
    public List<HapbeatEventEntry> entries; // 既存
}

public class HapbeatEventEntry
{
    public string linkedKitEventId;     // 新規: Kit の event id を参照
    public float gain = 1.0f;           // 既存: SDK 側倍率（0-2）
    // ... 既存の binding etc.
}
```

Runtime では:

```csharp
float LookupEffectiveIntensity(HapbeatEventEntry entry)
{
    if (_eventMap.linkedKit == null) return 1.0f;
    var kitEvent = _eventMap.linkedKit.FindById(entry.linkedKitEventId);
    return kitEvent?.intensity ?? 1.0f;
}
```

## 5. 導入導線

1. ユーザーが Hapbeat SDK を UPM import
2. メニューから `Hapbeat > Setup > Create HapbeatKits Folder` を実行
3. `Assets/HapbeatKits/README.md` が生成され、Unity Console に "開いたファイルを参照、Studio URL: https://devtools.hapbeat.com/studio/" と出る
4. ユーザーが Studio を開き、working directory として `Assets/HapbeatKits/` を指定
5. Studio が `<kit-id>/manifest.json` + `clips/` を自動生成
6. ユーザーが Studio 上で event 追加 (command / stream_source / stream_clip)、intensity 等を設定
7. Unity 側は AssetPostprocessor で manifest.json 変更を検知し、HapbeatKitAsset を更新
8. ユーザーが HapbeatEventMap を作成し、linkedKit を HapbeatKitAsset に設定
9. Batch Setup で各 interactable に trigger + binding を配置

## 6. 段階実装

### Phase 1（今セッション）
- `Hapbeat > Setup > Create HapbeatKits Folder` Editor menu
- `Assets/HapbeatKits/README.md` テンプレート

### Phase 2（contracts の mode フィールド確定後）
- HapbeatKitAsset + AssetPostprocessor 実装
- HapbeatEventMap.linkedKit 追加
- HapbeatEventEntry.linkedKitEventId 追加
- Runtime で intensity × entry.gain × binding の計算反映

### Phase 3
- Kit Import Window（Studio URL への導線、Kit 選択 UI、EventMap 自動生成）
- stream_clip の AudioClip 自動 bind

## 7. 参考

- `hapbeat-contracts/specs/kit-format.md` §5 — intensity / device_wiper / loop の仕様
- `hapbeat-contracts/specs/message-format.md` §4 — PLAY / STREAM_BEGIN の wire format
- `docs/decision-log.md` DEC-023, DEC-024
