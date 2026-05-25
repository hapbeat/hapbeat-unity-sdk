# Kit manifest 2.0.0 (`events` (command) + `stream_events` 2 bucket) 対応

**起点セッション:** workspace `hapbeat-studio` (2026-05-25)
**起点 commit:** `hapbeat-studio` 4dd0362 "feat(kit): KitEvent を library から独立化 + multi-mode + Deploy/Save 分離"
**関連:** [[multi-mode-suffix-spec]] (contracts 側で schema 2.0.0 を策定 / DEC-031)
**優先度:** 高 — Studio が新 schema で manifest を出力するようになると、現 reader は `events` 内に commad と stream が混在する前提の挙動になるため intensity を誤読する。

> ⚠️ **着手前提**: contracts repo で `instructions-multi-mode-suffix-spec-202605251800.md`
> (schema 2.0.0) の確定 + サンプル fixture が用意されてから着手すること。

---

## 背景

Kit manifest schema 2.0.0 (DEC-031) で構造が変わる。詳細は contracts 指示書参照。要点:

- 旧 (schema 1.x): 単一の `events: {...}` dict に `mode` フィールドで command / stream / source を区別
- 新 (schema 2.0.0): `events` を **command-mode 専用に narrow** + `stream_events` を新設
  - **`events` bucket 名は維持**: device firmware の kit_loader が `doc["events"]` を読む実装と一致させるため (Option C, DEC-031)。`command_events` への rename は将来 firmware を別件で更新するタイミングまで保留

```jsonc
// schema 2.0.0 サンプル
{
  "schema_version": "2.0.0",
  "name": "mykit",
  "events": {                              // ← command-only (旧 events を narrow 化)
    "mykit.hit": { "clip": "hit.wav", "parameters": { "intensity": 0.8 } }
  },
  "stream_events": {                       // ← 新設
    "mykit.hit":  { "clip": "hit.wav",        "parameters": { "intensity": 0.8 } },  // BOTH モード相当
    "mykit.rain": { "clip": "rain_loop.wav", "parameters": { "intensity": 0.3 } }
  }
}
```

- `events` のキーは PLAY/STOP packet の wire eventId
- `stream_events` のキーは SDK 内部の binding label (wire には乗らない)
- 同じ eventId が両 bucket に存在することは **valid** (Studio の BOTH モード相当)
- `clip` は両 bucket で **required**。bare filename で書かれ、subdir は bucket から決まる
  (`events.clip` → `install-clips/<clip>` / `stream_events.clip` → `stream-clips/<clip>`)
- 旧 `mode` フィールド / `stream_source` / `.fire` `.clip` suffix は **すべて廃止**

## 現状の問題

### `HapbeatManifestIntensity.ParseEvents` (Editor/HapbeatManifestIntensity.cs:363-435)

- `"events": { ... }` を読んでいるので bucket 名としては引き続き valid — ただし stream 系 entry は新 manifest では `events` 内には現れない。`stream_events` を別途読まないと stream モード event の intensity が引けない
- 各 entry の `mode` フィールドを読んで `KitManifestEvent.mode` にセット → 新 manifest には `mode` フィールド自体が無いため、現状コードは全 entry を `"command"` 扱いで cache する
- 旧 BOTH モード manifest (legacy v1.x) で `mykit.foo.fire` / `mykit.foo.clip` の suffix 付きキーが残っている古い kit を import する場合に対応が必要

### `TryMatchByEventId` (line 219-236)

- `eventId` 1 軸の exact match で intensity 解決 → 新 schema では同じ eventId が両 bucket に並存しうるため、(eventId, mode) tuple で区別する必要がある

### 影響

- Editor の "missing manifest intensity" 警告が stream_events の event について誤発火 (現 reader は `events` しか見ていないため stream_clip mode の entry が cache に乗らない)
- Test Play / runtime cache が stream-mode event について intensity = default 1.0 で発火 (author 設定値が無視される)
- EventMap Window の "From Kit" インポート (もしあれば) も stream 系を見落とす

## 必要な変更

### A. `KitManifestEvent` の型整理

```csharp
public class KitManifestEvent
{
    public string kitDir;
    public string kitName;
    public string eventId;        // base eventId
    public string clipRelPath;    // "install-clips/foo.wav" or "stream-clips/foo.wav" (kit root 相対の resolved path)
    public string mode;           // "command" or "stream_clip" — bucket 由来で確定
    public string description;
    public float  intensity;
}
```

- `mode` は `"command"` または `"stream_clip"` のいずれか (2 値のみ)
- `clipRelPath` は bucket に応じた subdir を **prefix 済み** で格納する (旧 code との互換のため。例: bare `"hit.wav"` を読んだら `"install-clips/hit.wav"` または `"stream-clips/hit.wav"` を格納)

### B. `ParseEvents` を 2 bucket reader に書き換え

```csharp
private static void ParseEvents(string json, string kitAssetPath, List<KitManifestEvent> output)
{
    // schema 2.0.0: `events` は command 専用、`stream_events` は新 bucket
    ParseBucket(json, "events",        "command",     "install-clips", kitAssetPath, output);
    ParseBucket(json, "stream_events", "stream_clip", "stream-clips",  kitAssetPath, output);
}

private static void ParseBucket(
    string json, string bucketName, string mode, string subdir,
    string kitAssetPath, List<KitManifestEvent> output)
{
    var bucketMatch = Regex.Match(json, $"\"{bucketName}\"\\s*:\\s*\\{{");
    if (!bucketMatch.Success) return;
    int blockStart = bucketMatch.Index + bucketMatch.Length;
    int blockEnd = FindMatchingBrace(json, blockStart);
    if (blockEnd < 0) return;
    string block = json.Substring(blockStart, blockEnd - blockStart);

    int pos = 0;
    while (pos < block.Length)
    {
        var keyMatch = Regex.Match(block.Substring(pos), "\"([^\"]+)\"\\s*:\\s*\\{");
        if (!keyMatch.Success) break;

        string eventId = keyMatch.Groups[1].Value;
        int entryStart = pos + keyMatch.Index + keyMatch.Length;
        int entryEnd = FindMatchingBrace(block, entryStart);
        if (entryEnd < 0) break;
        string body = block.Substring(entryStart, entryEnd - entryStart);

        // clip: required in 2.0.0 (両 bucket とも)
        string clipBare = "";
        var clipMatch = Regex.Match(body, "\"clip\"\\s*:\\s*\"([^\"]*)\"");
        if (clipMatch.Success) clipBare = clipMatch.Groups[1].Value;
        string clipRel = string.IsNullOrEmpty(clipBare) ? "" : $"{subdir}/{clipBare}";

        // description
        string description = "";
        var descMatch = Regex.Match(body, "\"description\"\\s*:\\s*\"([^\"]*)\"");
        if (descMatch.Success) description = descMatch.Groups[1].Value;

        // parameters.intensity (現行ロジック流用)
        float intensity = ExtractIntensity(body);

        output.Add(new KitManifestEvent {
            kitDir = kitAssetPath,
            kitName = Path.GetFileName(kitAssetPath),
            eventId = eventId,
            clipRelPath = clipRel,
            mode = mode,
            description = description,
            intensity = intensity,
        });

        pos = entryEnd + 1;
    }
}
```

旧 `mode` フィールド読み出しは不要 (bucket から決定)。

### v1.x legacy 対応 (任意)

ユーザーの Unity プロジェクトに schema 1.x の古い kit が残っている可能性が低い場合は legacy reader を省略してよい。残す場合は:

- `events` 内の entry に `mode` field がある or `.fire`/`.clip`/`.source` suffix が付いている → legacy。base eventId を抽出して `(base, mode)` tuple を出力
- `stream_events` field が存在する → v2 として読む

Studio (libraryStore) で同等の v1.x → v2 マッピングを実装済なので、参考になる場合は `hapbeat-studio/src/stores/libraryStore.ts` の `importKitsFromOutputDir` を確認。

### C. `TryResolveFromList` / マッチング関数を (eventId × mode) tuple 化

```csharp
private static bool TryMatchByEventIdAndMode(
    List<KitManifestEvent> all, string eventId, string expectedMode,
    out float intensity, out string kitPath)
{
    intensity = 1f; kitPath = "";
    if (string.IsNullOrEmpty(eventId)) return false;
    foreach (var ev in all)
    {
        if (ev.eventId == eventId && ev.mode == expectedMode)
        {
            intensity = ev.intensity;
            kitPath = ev.kitDir;
            return true;
        }
    }
    return false;
}
```

`TryResolveFromList` の改修:
- Command entry → `TryMatchByEventIdAndMode(list, entry.eventId, "command", ...)`
- StreamClip entry → まず clip path match (現行ロジック、stream-clips/ 配下の WAV と AssetPath を照合)、駄目なら `TryMatchByEventIdAndMode(list, entry.eventId, "stream_clip", ...)`

旧 `TryMatchByEventId` (mode 無視 exact match) は削除。

### D. EventMap UI / インポート系の確認

EventMap Window や "From Kit" 系インポートで `events` を仮定している箇所が無いか確認:

```bash
# 検索 hint:
grep -rn "events\b\|\"events\"" hapbeat-unity-sdk/Editor --include="*.cs"
```

`events` という bucket 名は維持されているが、stream 系も load するなら `stream_events` を別途見る必要がある。BOTH モードで author された event を Unity SDK で扱う場合、Unity 側は `HapticMode.Command` と
`HapticMode.StreamClip` 別々の `HapbeatEventEntry` を持つことになる (`HapbeatEventEntry.mode` が単一値の前提は維持)。

### E. runtime cache (`HapbeatEventEntry.CachedManifestIntensity`)

cache 書込み箇所が `entry.mode` を渡しているか確認。渡していない場合は `TryResolve` の
署名で `entry.mode` を内部参照する形に統一する (現行も entry.mode を見ているのでそのままで OK の可能性大)。

## Acceptance

- [ ] contracts repo で schema 2.0.0 と fixture が確定済み
- [ ] `HapbeatManifestIntensity.ParseEvents` が `events` (command) / `stream_events` の両 bucket を読み、`KitManifestEvent.mode` が bucket 由来で確定設定される
- [ ] 旧 `mode` フィールド / `stream_source` / suffix 関連コードを **削除** (legacy v1.x reader を残すかは判断)
- [ ] `TryResolve` が (eventId × mode) tuple で正しい intensity を返す
- [ ] BOTH モードで author された event について Command/StreamClip それぞれの EventEntry が正しい intensity を引き当てる
- [ ] Editor の "missing manifest intensity" 警告が解消
- [ ] Test Play で intensity = manifest 値 (1.0 fallback ではない)
- [ ] runtime cache が正しく populate される
- [ ] (UPM 配布物方針) ユーザー検証が完了するまで push しない

## 副次タスク (任意)

- `HapbeatManifestIntensity.KitManifestEvent.clipRelPath` を Inspector に表示
  (現状 stream-clips/foo.wav まで解決して持っているので、デバッグ時に「どのファイルを bind するか」が一目で見える)
- EventMap UI に「同じ base eventId が両 bucket に存在」を示すヒント (BOTH モードの author 状態を確認しやすく)

## 関連参照

- 起点 commit: hapbeat-studio @ 4dd0362
- contracts 側仕様策定: `hapbeat-contracts/instructions/instructions-multi-mode-suffix-spec-202605251800.md` + applied note
- decision-log: workspace `docs/decision-log.md` DEC-031
- 旧 reader 実装:
  - `hapbeat-unity-sdk/Editor/HapbeatManifestIntensity.cs:363-435` (ParseEvents)
  - `hapbeat-unity-sdk/Editor/HapbeatManifestIntensity.cs:219-236` (TryMatchByEventId)
  - `hapbeat-unity-sdk/Editor/HapbeatManifestIntensity.cs:178-217` (TryResolveFromList)
- HapticMode enum: `hapbeat-unity-sdk/Runtime/HapbeatEventEntry.cs:74-86`
- Studio reader (参考): `hapbeat-studio/src/stores/libraryStore.ts` `importKitsFromOutputDir` (v1.x / v2 自動判定 + 両 path 実装済)
