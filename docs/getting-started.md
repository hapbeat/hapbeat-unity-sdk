---
title: Getting Started — Basic Example
description: 新規 Unity プロジェクトに SDK をインストールして BasicExample で Hapbeat を鳴らすまでの最短手順。
sidebar:
  order: 1
---

このガイドでは、**新規 Unity プロジェクト** に SDK をインストールし、Basic Example サンプルを通して Hapbeat デバイスから振動が出るまでを最短で体験します。

自分のプロジェクトへの組み込み方については [プロジェクトへの組み込み](/docs/unity-sdk/integration/) を参照してください。

## 前提

- **Hapbeat デバイス** が Wi-Fi に接続されてオンライン（Stream 振動はこれだけで動作します）

## 0. Unity Editor をインストール

> 対応バージョンの Editor が既にインストール済みであれば、このステップはスキップできます。

**対応バージョン**: Unity 2022.3 LTS 以上（動作確認済み: **Unity 6000.3.12f1**）

[Unity Hub](https://unity.com/download) から対応バージョンをインストールします。

### 新規プロジェクトの作成

Unity Hub → **New project** → テンプレートは **任意**（例: `3D (Core)`）。
SDK は描画パイプライン非依存なので、URP / HDRP / Built-in どれでも動作します。

### git のインストール

UPM が Git URL でパッケージを取得するために **git** が必要です。
[git-scm.com](https://git-scm.com/) からインストールし、PATH が通っていることを確認してください（`git --version` がターミナルで通れば OK）。

## 1. SDK をインストールして Basic Example をインポート

1. Unity Editor: `Window → Package Manager`
2. 左上の **`+`** → **`Install package from git URL...`**
3. 次の URL を貼り付けて **Install**:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```

4. インポートが完了したら、Package Manager で **Hapbeat SDK** が選択された状態のまま右パネル → **Samples** タブ → **Basic Example** の **Import**

インポート完了後、**`Hapbeat`** メニューがメニューバーに現れます。

バージョン固定・更新・トラブルシューティングの詳細は [インストール](/docs/unity-sdk/installation/) を参照。

## 2. Build Setup でシーン・Kit・EventMap を生成

メニューバー → **`Hapbeat → Build Samples → 1. Basic Example`** を実行します。

確認ダイアログで「生成する」を押すと以下が自動生成されます:

```
Assets/HapbeatSDK/
  Kits/basic-exam-kit/       ← Kit ファイル (WAV + manifest.json)
  EventMaps/BasicExampleEventMap.asset
  Scenes/BasicExample.unity
```

## 3. Play して振動を確認（Stream）

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

> UI に `Pong: RTT=...ms` が表示されていれば通信 OK。表示されない場合はデバイスのオンライン状態を確認してください。

Stream モード（Space / R）は PCM データをリアルタイムでデバイスに送るため、デバイス側に Kit は不要です。

**F キーを押しても反応なし** — これは正常です。Command モードはデバイスに Kit がインストールされていないと動作しません。次のステップで解決します。

## 4. EventMap を確認する（任意）

メニューバー → **`Hapbeat → Event Map`** を開きます。

EventMap は SDK が発火する触覚イベントの一覧と設定を管理するウィンドウです。BasicExample には 3 エントリが登録されています:

| Event ID | Mode | 対応キー |
|---|---|---|
| basic-exam-kit.sine_100hz_1s | StreamClip | Space |
| basic-exam-kit.sine_100hz_1s_loop | StreamClip | R |
| basic-exam-kit.sine_200hz_1s | Command | F |

各エントリ右端の **▶ ボタン（Test Play）** を押すと、Unity の Play モードに入らなくてもエディタ上から直接デバイスに発火できます。

---

以下は任意の実験です。設定を変えて Test Play で動作を確かめてみてください。

### gain を調整する

**gain** を下げると振動が弱くなります。初期値 `1.0` はやや強めなので、`0.3` 程度に下げてから Test Play で確認するのがおすすめです。

### player / group でターゲットを絞り込む

**player** と **group** フィールドで、どのデバイスに振動させるかを絞り込めます。

- **1〜99** を設定すると、Hapbeat 本体の OLED に表示されている player / group 番号と一致したデバイスだけが振動します
- **−1**（デフォルト）はワイルドカードで、デバイス側の player / group を無視してすべてに振動します

片方だけ値を変えて、一致・不一致のデバイスへの挙動の違いを Test Play で確かめてみましょう。

EventMap の詳細: [EventMap ウィンドウ](/docs/unity-sdk/event-map/)

## 5. Studio で Kit をデプロイして FIRE を有効化

Command モード（F キー）を動かすには、デバイスに `basic-exam-kit` をインストールします。Studio からのデプロイには **hapbeat-helper** が必要です（[初期セットアップ](/docs/studio/initial-setup/) 参照）。

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
