---
title: Getting Started — Basic Example
description: 新規 Unity プロジェクトに SDK をインストールして BasicExample で Hapbeat を鳴らすまでの最短手順。
sidebar:
  order: 1
---

このガイドでは、**新規 Unity プロジェクト** に SDK をインストールし、Basic Example サンプルを通して Hapbeat デバイスから振動が出るまでを最短で体験します。

自分のプロジェクトへの組み込み方については [プロジェクトへの組み込み](/docs/unity-sdk/integration/) を参照してください。

## 前提

### Unity Editor

- **Unity 2022.3 LTS 以上**（動作確認済み: **Unity 6000.3.12f1**）
- `git` が PATH に通っている（UPM の Git URL 解決に必要）

### Hapbeat 環境

- **Hapbeat デバイス** が Wi-Fi に接続されてオンライン
- **hapbeat-helper** が起動済み（[初期セットアップ](/docs/studio/initial-setup/) 参照）

## 1. SDK をインストール

1. Unity Editor: `Window → Package Manager`
2. 左上の **`+`** → **`Add package from git URL...`**
3. 次の URL を貼り付けて **Add**:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```

インポートが完了すると **`Hapbeat`** メニューがメニューバーに現れます。

動作環境・バージョン固定・トラブルシューティングの詳細は [インストール](/docs/unity-sdk/installation/) を参照。

## 2. Basic Example をインポート

1. `Window → Package Manager` で **Hapbeat SDK** を選択
2. 右パネル → **Samples** タブ → **Basic Example** の **Import**

`Assets/Samples/Hapbeat SDK/<バージョン>/BasicExample/` に展開されます。

## 3. Build Setup でシーン・Kit・EventMap を生成

メニューバー → **`Hapbeat → Build Samples → 1. Basic Example`** を実行します。

確認ダイアログで「生成する」を押すと以下が自動生成されます:

```
Assets/HapbeatSDK/
  Kits/basic-exam-kit/       ← Kit ファイル (WAV + manifest.json)
  EventMaps/BasicExampleEventMap.asset
  Scenes/BasicExample.unity
```

## 4. Play して振動を確認（Stream）

`Assets/HapbeatSDK/Scenes/BasicExample.unity` を開いて **Play** します。

画面にキー操作ガイドが表示されます:

| キー | 動作 |
|---|---|
| Space | CLIP (Stream) 1-shot — 100 Hz 正弦波 1 秒 |
| R | CLIP (Stream) loop — 100 Hz 正弦波 ループ |
| **F** | FIRE (Command) — 200 Hz 正弦波（**Kit が必要**、後述） |
| S | Stop all |
| C | Ping |

**Space** を押してデバイスが振動すれば、SDK ↔ デバイスの通信は確立しています。

> UI に `Pong: RTT=...ms` が表示されていれば通信 OK。表示されない場合は hapbeat-helper の起動状態とデバイスのオンライン状態を確認してください。

Stream モード（Space / R）は PCM データをリアルタイムでデバイスに送るため、デバイス側に Kit は不要です。

**F キーを押しても反応なし** — これは正常です。Command モードはデバイスに Kit がインストールされていないと動作しません。次のステップで解決します。

## 5. EventMap を開いて設定を確認

メニューバー → **`Hapbeat → Event Map`** を開きます。

BasicExample の 3 エントリが並んでいます:

| displayName | Event ID | Mode |
|---|---|---|
| demo_stream_sine_100hz | basic-exam-kit.sine_100hz_1s | StreamClip |
| demo_stream_loop_100hz | basic-exam-kit.sine_100hz_1s_loop | StreamClip |
| demo_command_sine_200hz | basic-exam-kit.sine_200hz_1s | Command |

**gain** スライダーを動かすと振動の強度が変わります（Play 中でも即反映）。**target** フィールドでは送信先のグループ指定ができます（`group_1` など）。

EventMap の詳細: [EventMap ウィンドウ](/docs/unity-sdk/event-map/)

## 6. Studio で Kit をデプロイして FIRE を有効化

Command モード（F キー）を動かすには、デバイスに `basic-exam-kit` をインストールします。

1. **Hapbeat Studio** を開く（`https://devtools.hapbeat.com/studio/`）
2. **Kit タブ** → フォルダ選択（「フォルダを開く」）で Unity の `Assets/HapbeatSDK/Kits/` を指定
3. `basic-exam-kit` が一覧に表示されたら選択
4. **Manage タブ** → デバイスを選択 → **Kit** サブタブ → **Deploy** を実行

デプロイ完了後、Unity の Play モードに戻って **F キー**を押すとデバイスが振動します（200 Hz 正弦波）。

## 次のステップ

- [プロジェクトへの組み込み](/docs/unity-sdk/integration/) — 自分のシーンへの追加手順と Tutorial サンプル紹介
- [Trigger コンポーネント](/docs/unity-sdk/triggers/) — Animator / Collision / Sequence 等
- [EventMap ウィンドウ](/docs/unity-sdk/event-map/) — Event ID と波形の対応を GUI 管理
- [Parameter Binding](/docs/unity-sdk/parameter-binding/) — ゲーム状態を gain / pan に動的マッピング
