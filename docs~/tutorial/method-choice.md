---
title: BatchSetup vs スクリプトの使い分け
description: Hapbeat Unity SDK の触覚配線をどちらの方式で書くべきか — Tutorial サンプルでの使い分け基準。
---

Hapbeat の触覚を Unity に組み込む方法は大きく 2 通りあります:

1. **BatchSetup / Inspector でコンポーネントを貼る** — UnityEvent や Animator state、Collision callback と直結
2. **スクリプトから `HapbeatBridge` / `HapbeatManager` を呼ぶ** — 自前のロジック内で能動的に発火

どちらも正解で、状況によって使い分けます。

## 早見表

| 状況 | 推奨方式 |
|---|---|
| 同じ Trigger を 3 個以上のオブジェクトに貼る | BatchSetup |
| 発火条件が「衝突した・スライダ動いた・state 変わった」だけで決まる | コンポーネント |
| gain は EventMap entry そのまま、または velocity scaling だけで足りる | コンポーネント |
| 発火条件に複数の game state (charge レベル、アイテム所持、HP 等) が絡む | スクリプト |
| gain / pan / target を runtime で計算したい | スクリプト |
| 1 つの発火点から複数の Event を分岐させたい | スクリプト |
| 同じイベントを多数の場所から発火する (例: 自作 Bridge にロジック集約) | スクリプト |

## Tutorial サンプルでの実例

### コンポーネント方式

| ゾーン | コンポーネント | なぜこちらか |
|---|---|---|
| Z1 Bowling | `HapbeatCollisionTrigger` (BatchSetup で Pin 6 個に一括) | 同じ設定を多数オブジェクトに貼る典型例。BatchSetup の真骨頂 |
| Z2 Door | `HapbeatAnimatorTrigger` (Inspector 手動) | Animator state の Enter は UnityEvent 不要、コンポーネントで宣言的 |
| Z3 Pickup Box | `HapbeatSequenceTrigger` + `HapbeatParameterBinding` | Fire→Loop→Stop の 3 段とパラメータ連動を Inspector で完結 |
| Z4 Slider Tick | `HapbeatTickEmitter` (BatchSetup) | Slider.onValueChanged を自動 wire できる |
| Z5 Hit | `HapbeatUnityEventTrigger` (TargetReceiver の OnHit から wiring) | 発火点が 1 つだが UnityEvent wiring の典型例として |

### スクリプト方式

| ゾーン / ホットキー | 呼び出す API | なぜこちらか |
|---|---|---|
| Z4 Stream Console | `Manager.StreamAudioClip` + `StreamPlayback.Gain/Pan` 直接書き換え | runtime で gain/pan を毎フレーム計算する |
| Z5 Charge | `Bridge.PlayWithCurve(charge_value, curve)` | charge state は時間ベース、AnimationCurve で動的 mapping |
| Q キー | `Bridge.Play("manual_fire")` | 単発発火を任意のタイミングから呼ぶ |
| 1〜5 キー | `Bridge.PlayScaled("burst", gain)` | キー番号で gain を変えるロジックがある |
| P キー | `Manager.Ping()` | 接続診断は scripted action |

## 設計のコツ

### Bridge 派生クラスを作る

スクリプト方式を選んだ場合でも、`HapbeatManager.Instance.Play()` を直接呼ぶより、**プロジェクト固有の `HapbeatBridge` 派生クラス** を 1 つ作って、そこに発火ロジックを集約するのが推奨です。

理由:
- gain / target / curve のチューニングが 1 箇所に集まる
- ロジックが game logic から分離して保守しやすい
- Tutorial の `TutorialBridge` がこの形 ([Scripts/TutorialBridge.cs](https://github.com/yus988/hapbeat-unity-sdk/blob/master/Samples~/Tutorial/Scripts/TutorialBridge.cs))

### 両方を組み合わせる

Z3 Pickup Box は典型的な組み合わせ例です:
- `HapbeatSequenceTrigger` (コンポーネント) で Fire→Loop→Stop の構造を宣言
- `PickupBoxController` (スクリプト) でマウス入力に応じて `Sequence.Fire()` / `Stop()` を呼ぶ
- `HapbeatParameterBinding` (コンポーネント) で loop 中の gain を box の移動量に連動

「コンポーネントで宣言的に書ける部分はコンポーネント、ロジックが必要な部分だけスクリプト」が読みやすいコードになります。

## まとめ

- まず [BatchSetup](/docs/unity-sdk/event-map/#batch-setup) で書けないか考える
- 書けないなら自前 Bridge 派生 + スクリプト
- 同じシーンで両方を混在させて OK (Tutorial サンプルがそうしている)
