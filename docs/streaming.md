---
title: Streaming buffer (StreamClip 用)
description: StreamClip モードの送信バッファ (streamSendAheadSeconds) の意味、トレードオフ、設定指針。
---

`StreamClip` モードはホスト (Unity) からデバイスへ PCM オーディオを UDP で送り続ける送信です。`Command` モードと違い、デバイスはホストから届いたサンプルを再生するだけなので、ホスト側の **送信バッファ** がそのまま再生品質と停止遅延に影響します。

## オーディオインターフェイスとの類比

DAW でオーディオインターフェイスのバッファサイズ (samples / ms) を調整するのと同じトレードオフです:

| 項目 | DAW のバッファ | Hapbeat の `streamSendAheadSeconds` |
|---|---|---|
| 小さい | レイテンシ低い、CPU 過負荷で zipper / drop-out が起きやすい | 停止が速い、ネット遅延 / ジッタで途切れやすい |
| 大きい | レイテンシ高い、安定して途切れない | 停止が遅延する (押してから残響)、安定 |

DAW では「この曲では 64 sample で攻める / マスタリングでは 1024 sample で安定優先」のように使い分けますが、Hapbeat も用途に応じて調整します。

## 仕組み

`HapbeatManager.StreamAudioClip(clip, ...)` は coroutine を起動し、毎フレーム以下を行います:

1. AudioClip の次のチャンクを PCM16 に変換
2. UDP で送信
3. **送信済み総時間が「実時間 + sendAhead」を超えていれば次フレームまで wait**

これにより、SDK は常に「実時間より sendAhead 秒先まで」のサンプルを送信済みにキープします。デバイス側はその先送り分を内部バッファとして消費しながら再生する形です。

`StopStream()` を呼んだ時:
- SDK は coroutine を止めて即座に `STREAM_END` パケットを送る
- ただしデバイスは **既に届いた sendAhead 秒分のサンプルを再生し終えてから** 停止する
- つまり「Stop を押してから停止までの体感遅延 ≒ sendAhead の値」

## 設定方法

[HapbeatConfig](/docs/unity-sdk/installation/) の `streamSendAheadSeconds` で変更できます:

```
HapbeatConfig
  Behavior
    streamSendAheadSeconds: 0.05  ← デフォルト 50ms
```

範囲: 10ms 〜 200ms。

## 推奨値

| 用途 / 環境 | 推奨値 | 理由 |
|---|---|---|
| LAN 直結 (有線、低ジッタ) | **20–30 ms** | UDP 遅延が極小なので攻めて OK。停止が機敏 |
| 通常の Wi-Fi | **40–60 ms** (default 50ms) | 一般的なジッタ範囲を吸収しつつ停止遅延も実用範囲 |
| 混雑 Wi-Fi / 複数台同時 | **80–120 ms** | パケットロスや遅延スパイクへの耐性優先 |
| ライブパフォーマンス | 用途次第 | 「途切れたら台無し」なら大きく、「即応性重要」なら小さく |

## 影響を受けるのは StreamClip だけ

| モード | Stop 遅延 |
|---|---|
| **Command** (FIRE) | 即時 (デバイスがローカル clip を停止) |
| **StreamClip** (CLIP) | sendAhead 分の遅延あり |

`HapbeatActionHelper.StopEverything()` は両方に対して停止指示を送るので、Command の音は瞬時に止まり、Stream の音だけ ~sendAhead 秒の残響があります。

## 関連

- [Getting Started](/docs/unity-sdk/getting-started/) — `Manager.StreamAudioClip` の基本
- [Triggers](/docs/unity-sdk/triggers/) — StreamClip / Command の違い
- [Event Map](/docs/unity-sdk/event-map/) — entry の mode 設定
