---
title: Tutorial Sample
description: キーマウスだけで Hapbeat Unity SDK の全機能を体験する 1 シーン構成のチュートリアル。
---

このチュートリアルは「Hapbeat デバイスは買ったけど Unity で何ができるか分からない」という人を 30 分で SDK 全体に慣れさせるサンプルです。

XR デバイス不要・キーマウスだけで完結します (XRI HandDemo 連携は [別サンプル](/docs/unity-sdk/xri-handdemo-quickstart/) を参照)。

> **ポイント**: Tutorial は **Hapbeat Studio で Kit を作らずに、Unity 同梱の WAV を StreamClip で送信して動かす** ことから始められます。デバイスをオンラインにして Unity を Play すればすぐ触覚が返ります。Studio 連動 (Command モード) は walkthrough の最後で任意ステップとして扱います。

## 構成

5 ゾーンを含む 1 シーン構成。各ゾーンが SDK の異なる側面を担当します:

| ゾーン | 学べる SDK 要素 | 使う実装方式 |
|---|---|---|
| Z1 Bowling Lane | `HapbeatCollisionTrigger` (VelocityScaled) | **BatchSetup** で Pin × 6 に一括追加 |
| Z2 Swing Door | `HapbeatAnimatorTrigger` | 手動 (Inspector 1 個追加) |
| Z3 Pickup Box | `HapbeatSequenceTrigger` + `HapbeatParameterBinding` | 手動 + Binding preset |
| Z4 Stream Console | `Manager.StreamAudioClip` + `StreamPlayback.Gain/Pan` 動的更新, `HapbeatTickEmitter` | スクリプト + BatchSetup |
| Z5 Target Range | `Bridge.PlayWithCurve` + `HapbeatUnityEventTrigger` (UnityEvent wiring) | スクリプト + wiring |

シーン全体で有効なホットキー:
- **Q**: `Bridge.Play()` 単発発火
- **1〜5**: `Bridge.PlayScaled()` で gain スケール可変
- **P**: `Manager.Ping()` で接続/応答時間確認

## Target Picker

シーン上部に **Target Picker UI** (Both / Neck / Arm) があり、Z4・Z5・ホットキーの送信先 device を実機で動的切替できます。
Z1〜Z3 は EventMap entry に target を固定しているため Picker 操作の影響を受けません — 「event 設計時点で target を決める」設計と「runtime で target を上書きする」設計を同じシーン内で比較できます。

## ふたつの版

このサンプルには 2 つのシーンが入っています:

- **`Tutorial.unity`** — 触覚適用済み (With) 版。すぐに動作確認できる完成形
- **`Tutorial_Plain.unity`** — 触覚なし (Without) 版。ゲームロジックは動くが触覚は鳴らない

`Tutorial_Plain.unity` を起点に [walkthrough](/docs/unity-sdk/tutorial/walkthrough/) の手順通りに Hapbeat コンポーネントを追加していくと `Tutorial.unity` と同じ動作になります。完成形を参照しながら実装を学べます。

## 始め方

1. Unity Editor で **Window → Package Manager → Hapbeat SDK → Samples → Tutorial → Import**
2. **Hapbeat → Build Samples → 2. Tutorial (full scene)** を実行 → `Tutorial.unity` (With 版) と `Tutorial_Plain.unity` (Without 版) が同時に生成される
3. EventMap を作成して `[Hapbeat Event Router]` の TutorialBridge にリンク (詳細は [walkthrough](/docs/unity-sdk/tutorial/walkthrough/))
4. Hapbeat Studio または Helper でデバイスをオンラインにして Play

## 関連ドキュメント

- [Walkthrough](/docs/unity-sdk/tutorial/walkthrough/) — Plain → With の完全手順
- [Method choice](/docs/unity-sdk/tutorial/method-choice/) — BatchSetup vs スクリプトの使い分け
- [Triggers](/docs/unity-sdk/triggers/) — 各 Trigger の詳細
- [Event Map](/docs/unity-sdk/event-map/) — EventMap window の使い方
- [Parameter Binding](/docs/unity-sdk/parameter-binding/) — Z3 Pickup での実例
