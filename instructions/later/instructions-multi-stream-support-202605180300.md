# Multi-stream support (HapbeatManager.StreamAudioClip)

**作成日**: 2026-05-18
**起点**: Tutorial Z4 で `HapbeatTickEmitter` (StreamClip slider_tick) を slider に attach すると `HapbeatUnityEventTrigger` (StreamClip stream_demo loop) が中断される現象を調査。

## 現状

`HapbeatManager.StreamAudioClip` (Runtime/HapbeatManager.cs ≒line 349) は **single-stream 設計**:

```csharp
public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, ...)
{
    ...
    if (_streamCoroutine != null)
        StopStream();           // ← 既存 stream を強制停止
    ...
    _streamCoroutine = StartCoroutine(...);
}
```

`_streamCoroutine` / `_activePlayback` は単一フィールド。同時に走れる stream は最大 1 本。

デバイス側ファームは `audio_stream.cpp` で **「非排他ミキサー」** (2026-04-11 commit) として複数 stream の重畳再生を実装済 (CLAUDE.md 参照)。つまり受信側はマルチ対応しているが、Unity SDK が単一スロットでボトルネックになっている。

## 影響

- Z4 stream_demo loop 再生中に slider_tick が発火すると stream_demo が止まる
- HapbeatSequenceTrigger の loop と並行 tick / 他 sequence が共存できない
- ambient loop (環境系の継続触覚) + spot trigger (衝突等の単発) を同時に出したいゲームで支障

## 2026-05-18 調査結果: SDK 単体修正は不可

- **Wire format (contracts)**: `STREAM_BEGIN` payload に `stream_id` フィールドなし (sample_rate uint16 / channels uint8 / format uint8 / total_samples uint32 / gain float32 / target string)。同様に `STREAM_DATA` / `STREAM_END` も stream id を持たない。プロトコルレベルで単一 slot 前提。
- **Device firmware** (`audio_stream.cpp`): `static StereoFrame s_ring[RING_FRAMES]` 単一 ring buffer + decoder。複数 stream 並行受信不可。
- **Unity SDK** (`HapbeatManager.cs:357-358`): `if (_streamCoroutine != null) StopStream();` で新規 stream 開始時に旧 stream 強制停止。

3 リポ協調変更が必要。SDK 単体改修では device 側が拾えない。

## 代替案: Unity 側 local mixing

SDK 内で複数 AudioClip を float サンプル合成し、1 stream として STREAM_BEGIN 経由で送出する方式。protocol / firmware を変えずに済む。ただし:
- N stream を 1 channel に mix する scheduler が必要
- gain/pan の独立制御を SDK 側で管理 (mix 前に各 source に適用)
- loop / one-shot 混在の lifecycle 管理
- 開始 / 停止時の crossfade

実装規模: 中〜大。Tutorial 範疇を大きく超える。

## 検討すべき API 設計

1. `_activePlayback` を List<HapbeatStreamPlayback> に拡張
2. `_streamCoroutine` も List<Coroutine> に
3. 新 stream 開始時に既存を停止しない (ただし合計帯域 / device 側の slot 数を考慮)
4. `StopStream()` / `StopAllStreams()` / `playback.Stop()` を分離
5. デバイス側 audio_stream はどこまで slot を許容しているか確認 (現状 cap が無いなら Unity 側でレート制限要)
6. STREAM_BEGIN / STREAM_END / STREAM_DATA の wire format 上 stream id をどこに入れるか (現状 single-slot 前提のフォーマットだと device 側の dispatch も拡張要)

## 影響リポ

- `hapbeat-unity-sdk` (Unity 側 API)
- `hapbeat-contracts` (wire format に stream_id 追加検討)
- `hapbeat-device-firmware` (audio_stream slot 数 / dispatch 確認)
- `hapbeat-bridge` / `hapbeat-helper` (中継経路の対応)

## 暫定 Tutorial 対応 (このセッションで実施済)

- `TutorialAddBuilders.AddHudStreamPanel` で TickEmitter の attach をコメントアウト
- Z4 commands に "Tick demo は SDK multi-stream 対応待ち" と明記する案あり

## 着手前提

- contracts 側で stream_id wire format 整理
- device firmware の non-exclusive mixer slot 仕様確認
- Unity 側 API のマイグレーション計画 (StopStream → StopAllStreams etc.)

優先度は中。Tutorial 完成・配布フェーズに入る前に必要なら fix。
