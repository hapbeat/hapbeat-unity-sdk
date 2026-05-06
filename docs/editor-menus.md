---
title: Editor メニューリファレンス
description: Unity Editor 上の Hapbeat メニュー項目の使い方リファレンス。Window 系・シーン操作・サンプル生成・Debug の各カテゴリを説明。
---

Hapbeat SDK は Unity Editor のトップレベルメニュー **`Hapbeat`** にすべての操作を集約しています。このページは各項目の用途を逆引きで参照する一覧です。

```
Hapbeat/
├── Settings                 ← 接続設定 / Group / Bridge UI
├── Event Map                ← Event ID 一覧と Wiring の管理 (メイン編集画面)
├── Batch Setup              ← 複数 GameObject に Trigger を一括設定
│
├── Create Event Router      ← シーンに [Hapbeat Event Router] を配置
│
├── Setup/
│   └── Create HapbeatSDK Folder    ← Assets/HapbeatSDK/ の標準レイアウトを生成
├── Build Samples/
│   ├── 1. Basic Example            ← Basic サンプル一式 (Kit + EventMap + Scene)
│   └── 2. Tutorial (full scene)    ← Tutorial サンプル一式 (With + Without 2 シーン)
│
└── Debug/
    ├── Attach Event Logger to Selected           ← 選択 GO の UnityEvent をログに流す配線を追加
    ├── Remove Event Logger Wiring from Selected  ← 上記の解除
    │
    ├── Logs/
    │   ├── Start Recording                       ← Hapbeat 系ログのファイル記録を開始
    │   ├── Stop Recording                        ← 記録を停止して保存
    │   ├── Reveal Current File                   ← 記録中ログを Explorer/Finder で表示
    │   ├── Open Logs Folder                      ← ログ保存先フォルダを開く
    │   └── Dump Last Recording to Console        ← 直近のログを Console に流す
    │
    └── Close Edit-mode Transport                 ← Edit-mode の UDP 接続を強制クローズ
```

加えて以下のメニュー位置にも Hapbeat エントリがあります:

- **GameObject → Hapbeat → Event Router** (Hierarchy 右クリック含む) — `Create Event Router` と同じ
- **Assets → Create → Hapbeat → Config / Event Map** — ScriptableObject 生成
- **Add Component → Hapbeat/...** — 各種 Trigger / Bridge / Helper をコンポーネントとして追加

---

## Window 系

### Settings

接続設定を編集する Window。

| 設定 | 用途 |
|---|---|
| Port | UDP ポート (デフォルト 7700) |
| Group | 送信先デバイスのグループ ID (0 = 全デバイス) |
| アプリ名 | Hapbeat デバイスのディスプレイに表示するクライアントアプリ名。**Max 16 文字** (display grid 幅)。デフォルトの `app_name` 要素 (8x1) では先頭 8 文字のみ表示。空欄なら `Application.productName` が自動使用 (16 文字超過時は切り詰め) |
| Use Bridge | ESP-NOW 経由 (上位構成) を使う場合のみ ON |
| Ping Interval | キープアライブ送信間隔 (秒) |

実機との Ping テストや、シーン外からの設定編集に使います。`Assets/Create/Hapbeat/Config` で生成した `HapbeatConfig` ScriptableObject の Inspector と内容は同じ。

### Event Map

**Hapbeat 統合作業のメイン画面**。シーン内の Event ID 一覧、各 Trigger の wiring (どの GO にどの Trigger が付いているか)、ParameterBinding の設定をすべてここから操作します。

主な機能:

- 左ペイン: EventMap.asset の全エントリ一覧 (mode / target / gain などをカード表示)
- 右ペイン: 選択中エントリの詳細編集 (Event ID / streamClip / target / gain / Notes / Bindings)
- Wiring セクション: 選択中エントリを発火する Trigger を逆引きスキャン
- Play モード中の Test 再生 / Snapshot/Restore (実機調整時の値を保存・復元)

詳細: [Event Map ウィンドウ](./event-map.md)

### Batch Setup

複数の GameObject (例: 同じ Tag の Pin × 6) に Trigger コンポーネントを一括追加するための補助 Window。Drag&Drop で参照を取り込み、適用先を絞ってまとめて配線できます。

ユースケース:

- ボウリングのピン 6 個に同じ `HapbeatCollisionTrigger` を配るとき
- XR インタラクタブル多数に `HapbeatUnityEventTrigger` を配るとき

---

## シーン操作

### Create Event Router

現在のシーンに `[Hapbeat Event Router]` GameObject を配置します。中身は `HapbeatManager` (singleton) + `HapbeatActionHelper`。Hapbeat を使うシーンでは必ず 1 つだけ必要です。

すでにシーンに存在する場合はスキップして既存を選択状態にします。

> Hierarchy 右クリック → `Hapbeat → Event Router` でも同じことができます。

---

## Setup

### Create HapbeatSDK Folder

`Assets/HapbeatSDK/` 配下に標準レイアウトを生成します:

```
Assets/HapbeatSDK/
├── Kits/        ← 触覚波形 (Studio から deploy / 自前 Kit を置く場所)
├── Scenes/      ← Build Samples で生成されるシーン
└── EventMaps/   ← EventMap.asset
```

サンプルの Build メニューを使う場合は内部で自動的にこのレイアウトが作られるため、明示的に呼ぶ必要はありません。「最初に手動で枠だけ作っておきたい」時の補助。

---

## Build Samples

### 1. Basic Example

Basic Example サンプル一式 (Kit / EventMap / Scene) を `Assets/HapbeatSDK/` に生成します。

実行前に Package Manager で **Hapbeat SDK → Samples → Basic Example → Import** を済ませておく必要があります (Sample アセットの実体が `Assets/Samples/Hapbeat SDK/<version>/Basic Example/` にコピーされます)。

生成後は `Assets/HapbeatSDK/Scenes/BasicExample.unity` を開いて Play。

### 2. Tutorial (full scene)

Tutorial サンプルの 2 つのシーンを同時生成します:

- **`Tutorial.unity`** — 触覚適用済み (With) 版。すぐに動作確認できる完成形
- **`Tutorial_Plain.unity`** — 触覚なし (Without) 版。完成形と並行して開きながら、`docs/tutorial/walkthrough.md` の手順で自分で組み立て直す学習用

EventMap.asset と Audio/ 内 WAV へのリンクも自動生成されます。

詳細: [Tutorial サンプル](./tutorial/index.md)

---

## Debug

ユーザーが触ってよい範囲のデバッグ用ユーティリティ。バグ報告時に **Logs/** 配下を活用してログを添付してもらうのが推奨フローです。

### Attach Event Logger to Selected / Remove Event Logger Wiring from Selected

選択中 GameObject の UnityEvent (XR Interactable の `selectEntered` など) を Console にログ出力する補助配線を追加 / 解除します。

何が起きているか可視化したい時、Trigger を仕込む前の発火タイミング確認に便利。AI 支援で wiring を組むときも、まずこれで「どのイベントがいつ飛ぶか」を観察すると設計がブレません。詳細: [AI 支援ワークフロー](./ai-assisted-workflow.md)。

### Logs/

Hapbeat 系のログ (Console 出力 + 実行イベント) をファイルに記録する機能群。

| メニュー | 用途 |
|---|---|
| Start Recording | フィルタ済みログのファイル記録を開始 |
| Stop Recording | 記録を停止し、ファイルを Explorer/Finder で表示 |
| Reveal Current File | 記録中ファイルを Explorer/Finder で表示 (記録中のみ有効) |
| Open Logs Folder | 過去のログを集めてあるフォルダを開く |
| Dump Last Recording to Console | 直近のログを Console に書き出す (記録停止後の確認用) |

**バグ報告のおすすめフロー:**

1. `Start Recording` を実行
2. 再現手順を実行 (Play → 問題発生 → Stop)
3. `Stop Recording` で保存 → ファイルを Issue / DM に添付

### Close Edit-mode Transport

Edit-mode で開いている UDP / mDNS transport を強制クローズします。「Play モードに入る前から接続テストしたい」「ポートが掴まれっぱなしで Play できない」などのレアケース用。

通常は触る必要はありません。

---

## ScriptableObject 生成 (`Assets/Create/Hapbeat/`)

Project ビューの右クリック → `Create → Hapbeat`:

| 項目 | 用途 |
|---|---|
| Config | `HapbeatConfig.asset` (接続設定の置き場) |
| Event Map | `HapbeatEventMap.asset` (Event ID 一覧の置き場) |

生成位置: 右クリックしたフォルダの直下。**プロジェクトに 1 つあれば足りる** ため、`Assets/HapbeatSDK/EventMaps/` 配下に置くのを推奨。

---

## コンポーネント (`Add Component → Hapbeat/`)

GameObject の Inspector → Add Component → 検索欄に `Hapbeat`:

| コンポーネント | 用途 |
|---|---|
| Hapbeat Animator Trigger | Animator パラメータ変化で発火 |
| Hapbeat Collision Trigger | 物理衝突 / Trigger Enter / Exit で発火 |
| Hapbeat Sequence Trigger | grab / hold / release を 1 component で扱う |
| Hapbeat Tick Emitter | 連続値 (Slider 等) の変化量に応じてスナップ触覚 |
| Hapbeat Unity Event Trigger | UnityEvent の `Fire()` メソッドから任意発火 |
| Hapbeat Parameter Binding | Transform / Rigidbody → gain / pan のリアルタイムマッピング |
| Hapbeat Action Helper | Stop / StopAll / Ping を UnityEvent から呼ぶラッパ |
| Hapbeat Event Logger | UnityEvent 発火を Console に流す (Debug 用) |
| Hapbeat Key Dispatcher | キー押下を UnityEvent にマップ (sample / proto 用) |
| Hapbeat Status Overlay | 接続状態と Log を Canvas に表示 |

詳細: [Trigger コンポーネント](./triggers.md) / [Parameter Binding](./parameter-binding.md)
