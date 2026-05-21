# Streaming を background thread 化 (smoothness 改善)

**作成日**: 2026-05-21
**起点**: Tutorial Z4 で stream 再生時に glitches。Wi-Fi は安定、device firmware の non-exclusive mixer も健全だが、Unity 側の `MixerCoroutine` が main thread で動作 → frame jitter (GC / rendering spike) で chunk 送信が遅延 → device ring buffer underrun → audio drop。

## 現状の制約

- `HapbeatManager.MixerCoroutine` が `IEnumerator` / `StartCoroutine` で実装
- 毎フレーム 1 chunk (≒ 12ms @ 16kHz mono) を生成 → `_client.SendStreamData`
- ペース調整は `yield return null` (= 1 frame wait)
- `_config.streamSendAheadSeconds` (default 50ms) でバッファ
- Unity main thread の処理 (UI / physics / GC) と競合 → frame spike で chunk 遅延

50ms buffer を 150-200ms に bump すれば緩和できるが、**latency 増のトレードオフ** が発生 (stream stop で device が止まるまで 150ms 遅延)。

## 改修案

### A. Background thread + 同期キュー

- mix + PCM16 化 + send を `System.Threading.Thread` で実装
- Unity main thread から `EnqueueSample(AudioClip + gain + pan handle)` で source 追加
- 各 source の Playback handle は thread-safe (既存 Volatile.Read / Volatile.Write 維持)
- thread 内部は固定 1ms 解像度で chunk 生成 → Wi-Fi 送信
- main thread frame jitter から完全独立

### B. AudioClip.GetData の thread safety

`AudioClip.GetData` は Unity API なので main thread 限定。
StreamSource 生成時に main thread で sample[] を読んでおき、thread 内では float 配列を pure C# で処理 → OK (現状の StreamSource constructor も既にそうなっている)。

### C. Pacing 戦略

- send-ahead buffer は維持 (default 50ms で OK、frame jitter ない thread だと小さく抑えられる)
- 厳密な実時間ペース: `Stopwatch` で µs 単位の sleep
- Sleep の解像度問題: Windows Sleep(1) ≒ 15ms 粒度。要 timeBeginPeriod or busy-wait
- Linux/macOS は ~1ms 粒度

### D. Loop seam crossfade (併せて検討)

Background thread 化と独立だが、loop wrap 時の click 抑制も同 instruction で。
- src.Cursor が wrap する直前 5-20ms ぶんのサンプルを次 loop 先頭 5-20ms と equal-power crossfade
- ループ用クリップを authoring 側で zero-crossing 揃える必要が無くなる

## 影響範囲

- `Runtime/HapbeatManager.cs` の streaming セクション全体書き直し
- `Runtime/HapbeatStreamPlayback.cs` の thread-safety 再確認 (既に Volatile 使ってるはず)
- `Runtime/HapbeatClient.cs` の SendStreamData が thread-safe か確認 (UDP socket は基本 OK)
- BasicExample / HandDemo の動作確認 (regression test)

## 着手条件

- Tutorial 完成 + 配布フェーズに入る前に検討
- まず streamSendAheadSeconds bump (0.05 → 0.15) で実用化、その後本格対応

## 関連 instruction

- `later/instructions-multi-stream-support-202605180300.md` (multi-source mixing 実装済、本件で thread 化を追加すれば併せて smoother になる)
