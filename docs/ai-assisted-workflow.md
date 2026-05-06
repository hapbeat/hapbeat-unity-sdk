---
title: AI コーディング支援を使った Hapbeat 組み込み
description: Claude Code などの AI コーディング支援ツールを使って、既存の Unity シーンに Hapbeat 触覚フィードバックを後付けする実践フロー。コピペ用プロンプト集つき。
---

Hapbeat SDK は GameObject / Trigger / EventMap のシンプルな構成なので、**AI コーディング支援ツール (Claude Code / Cursor / Codex / GitHub Copilot Workspace 等) との相性がよい** です。

このページでは、AI に「まっさらなシーン」を読ませて触覚フィードバックを設計・実装させる、4 ステップのワークフローと、各ステップで使えるおすすめプロンプトを示します。

> Hapbeat SDK 開発時に作者自身が Claude Code を使って `Tutorial` / `XriHandDemoAugment` サンプルを実装したフローをそのまま外部ユーザー向けに整理したものです。

---

## 必要な前提

- **Unity プロジェクト** が AI から読める状態 (Claude Code / Cursor 等を該当プロジェクトのルートで起動済み)
- **Hapbeat SDK** インストール済み ([インストール手順](./installation.md))
- **触覚を載せたい既存シーン** が `Assets/` 配下に保存済み
- AI が `.unity` シーンファイル (YAML) と SDK の Trigger / EventMap 定義を読める権限を持つこと

> AI は基本的にエディタ操作を直接できません。**Editor 用 C# スクリプトを生成させて Unity 側でメニューから実行**、あるいは **手作業で配置するための具体的な手順を出力** させるのが定石です。

---

## ワークフロー全体像

```
Step 1: シーン解析 (触覚候補の洗い出し)
   ↓
Step 2: EventMap 設計 (Event ID / gain / mode 決定)
   ↓
Step 3: Wiring 提案 (どの GameObject にどの Trigger を付けるか)
   ↓
Step 4: 実装 (Editor スクリプトで一括 wiring or 手動配置)
```

各ステップを 1 プロンプトで終わらせるのではなく、**ステップごとに区切って AI と対話する** のがコツです。AI は触覚デザインの「正解」を持っていないので、各ステップで人間がレビュー・補正する前提でいきます。

---

## Step 1: シーン解析プロンプト

AI に対象シーンを読ませて、触覚を付けると効果的なイベント候補を洗い出させます。

**プロンプト例:**

````
Unity プロジェクト全体を見て、`Assets/Scenes/<対象シーン名>.unity` に対して
Hapbeat 触覚 SDK で触覚フィードバックを付けると効果的なイベントを 5〜15 個
リストアップしてください。

各候補について以下の表形式で出力してください:

| # | イベント | 対象 GameObject | 検知方法 | 推奨 Trigger | 強度 (low/mid/high) | コメント |

検知方法は以下から選択:
- 物理衝突 (OnCollisionEnter / OnTriggerEnter)
- Animator パラメータ変化 (Bool / Float)
- UnityEvent (UI Button / XR Interactable Select / Activate / Hover)
- スクリプトの公開メソッド呼び出し
- 連続値の変化量 (Slider 等)

推奨 Trigger は以下から選択:
- HapbeatCollisionTrigger
- HapbeatAnimatorTrigger
- HapbeatUnityEventTrigger
- HapbeatSequenceTrigger (grab/hold/release を 1 体化)
- HapbeatTickEmitter (連続値スナップ)

参考: `Packages/com.hapbeat.sdk/Runtime/Hapbeat*Trigger.cs` および
`Packages/com.hapbeat.sdk/docs/triggers.md` を読んでから判断してください。
````

**期待される出力例:**

```
| # | イベント | 対象 | 検知 | Trigger | 強度 |
|---|---|---|---|---|---|
| 1 | ボール着地 | Ball | OnCollisionEnter | HapbeatCollisionTrigger | mid |
| 2 | ピン倒れ | Pin × 6 | OnCollisionEnter | HapbeatCollisionTrigger | high |
| 3 | ドア開閉 | Door | Animator.IsOpen | HapbeatAnimatorTrigger | low |
| ... |
```

---

## Step 2: EventMap 設計プロンプト

Step 1 の結果をレビューしたら、EventMap (Event ID 一覧) を設計させます。

**プロンプト例:**

````
上の候補リストから EventMap (HapbeatEventMap.asset) を設計してください。

各エントリは以下のスキーマです:
- displayName: シーン内で見やすい和名 (例: "ボール着地")
- category + eventName: Event ID は "<category>.<eventName>" の形に合成される
  - category にはシーン/ゾーン名や kit 名 (例: "bowling")
  - eventName には触覚イベント名 (例: "ball_landing")
- mode: Command / StreamClip のどちらか
  - 短い ON/OFF だけで良い → Command
  - 振動の起動/停止/長さを Unity 側で制御したい・WAV を流したい → StreamClip
- gain: 0.0〜1.0 (touch design 段階では 0.5 を起点に)
- target: 触覚デバイスのターゲット (空文字 = 全 group / "neck" / "arm" 等)

参考: `Packages/com.hapbeat.sdk/docs/event-map.md` の仕様を読んでから決めて
ください。

出力は以下の C# 配列リテラル形式 (Editor スクリプト埋め込み用):

```csharp
new[] {
    new Entry { displayName = "...", category = "...", eventName = "...",
                mode = HapticMode.Command, gain = 0.5f, target = "" },
    ...
}
```
````

---

## Step 3: Wiring 提案プロンプト

EventMap を確定させたら、シーン内のどの GameObject にどの Trigger を付けるかを設計させます。

**プロンプト例:**

````
上の EventMap を前提に、各エントリに対する scene wiring を提案してください。

各 wiring は以下の表で出力:

| Event ID | scene path | Trigger | source event | 補足設定 |

scene path は GameObject の絶対パス (例: "Z1_Bowling/Pins/Pin01")
source event はその Trigger をどう発火させるかの具体的設定:
- HapbeatCollisionTrigger なら: triggerEvent (CollisionEnter etc) + tagFilter
- HapbeatAnimatorTrigger なら: targetAnimator + parameter + condition
- HapbeatUnityEventTrigger なら: 接続元の UnityEvent (例: XRGrabInteractable.selectEntered)

補足設定には、cooldown・gainMode (VelocityScaled/Fixed)・閾値などを含めて
ください。
````

レビューを終えたら、Step 4 で実装に移ります。

---

## Step 4: 実装プロンプト

ここからは AI に実行可能な Editor スクリプトを生成させて、Unity 側で 1 メニュー実行で wiring が終わる状態を狙います。

**プロンプト例 (Editor スクリプトで一括 wiring):**

````
上の EventMap と wiring 表を基に、`Assets/Editor/<シーン名>HapbeatAugmentor.cs`
を作成してください。要件:

1. メニュー: `Hapbeat/Augment <シーン名>` から実行
2. 実行内容:
   - HapbeatEventMap.asset を生成または更新 (Event ID は冪等に追加)
   - 対象シーンを開く (現シーンが dirty なら保存ダイアログ)
   - 各 wiring 表のエントリに従って:
     - 対象 GameObject に Trigger コンポーネントを `Undo.AddComponent` で追加
     - SerializedObject 経由で _eventMap / _entryId を wire
     - Trigger 固有のフィールド (tagFilter / cooldown / gainMode 等) を設定
   - 完了レポートのダイアログを出す (適用件数 / skip 件数 / warning)

3. 冪等性: 既に同じ Trigger が付いている GameObject は skip + warning
4. Undo: Ctrl+Z 1 回で全配線が巻き戻ること
5. 実行後ユーザーがレビューして手動微調整できるよう、シーンの自動保存はしない

参考: 既存サンプルの BasicExampleSceneBuilder.cs / TutorialSceneBuilder.cs を
読んで、API や namespace を合わせてください。
````

**メリット:**

- AI が直接シーンに変更を加えられない代わりに、生成された C# を Unity で実行することで「**やり直せる configuration as code**」になる
- メニュー実行 → レビュー → 微調整 → メニュー再実行のループが回せる
- 後で別シーンに展開するときも `<シーン名>` を差し替えれば再利用できる

---

## 手動配置のためのプロンプト (Editor スクリプトを書かない場合)

シンプルなシーンや、AI に Editor スクリプトを書かせるほどの規模でない場合、
**手順書を生成させて自分で Inspector を操作する** やり方も有効です。

**プロンプト例:**

````
上の EventMap と wiring を、Unity Editor 上で手動配置するための番号付き手順を
出力してください。

各手順は以下の形式:

1. Hierarchy で `<scene path>` を選択
2. Inspector で Add Component → `Hapbeat/<Trigger 名>`
3. Event Map に `<map asset name>` をドラッグ
4. Event ドロップダウンから `<displayName>` を選択
5. <Trigger 固有の設定>
6. (次の wiring へ)

最後に、全配線を確認するチェックリストも出力してください。
````

---

## コツ・注意点

### AI に渡すコンテキスト

`Packages/com.hapbeat.sdk/` 配下を読ませると SDK 仕様を正しく把握してくれます。**特に以下を渡すと精度が上がります**:

- `Runtime/Hapbeat*Trigger.cs` の各 Trigger 実装
- `Runtime/HapbeatEventMap.cs` / `HapbeatEventEntry.cs`
- `docs/triggers.md` / `docs/event-map.md`
- `Samples~/Tutorial/Editor/TutorialSceneBuilder.cs` (実装パターンの好例)

### 触覚デザインは AI に任せきらない

AI は「**衝突したら振動**」のようなパターンマッチが得意ですが、**触覚の心地よさ・没入度の判断はできません**。Step 1 / Step 2 で必ず人間がレビュー・補正してください。

特に:
- **強度 (gain)**: AI は high/mid/low を機械的に振るので、実機で都度調整
- **頻度・cooldown**: 連続発火しすぎる候補は cooldown を強めに
- **target (デバイス指定)**: シーンの文脈で `arm` / `neck` / 全体を選び分け

### 「まっさら」と言いつつ完全に空ではない

AI が完全に空のシーンから触覚を提案するのは難しいです。最低限以下があると Step 1 が機能します:

- 物理オブジェクト (Rigidbody + Collider)
- Animator がある GameObject
- UI Button / Slider 等
- XR Interactor / XRI Interactable

「ゲームの骨格 (動くもの・触れるもの) ができてから Hapbeat を載せる」くらいのフェーズで使うのが最も効率的です。

### Editor スクリプトの再実行可能性

Step 4 で生成させた Augmentor は **冪等** (同じ実行を 2 回しても重複しない) になるよう要件に含めましょう。AI に「`AddComponent` 前に既存の `<Trigger>` を `GetComponent` でチェックして既存ならスキップ」と明示すると ベターです。

### Undo を必ず要求する

`Undo.AddComponent` / `Undo.RecordObject` を AI が省略しがちです。**Ctrl+Z で 1 回戻せること**を要件として明示してください。これがあればミスっても安全に巻き戻せます。

---

## 参考: Hapbeat SDK 自体の開発で使ったプロンプト

`Tutorial` / `XriHandDemoAugment` サンプル実装時に Claude Code に渡した
詳細指示書 (テスト基準 / scene path 表 / Undo 要件 / 環境チェック) が、SDK
リポジトリの `instructions/later/` 配下にあります:

- [`instructions-xri-handdemo-augment-202605051200.md`](https://github.com/Hapbeat/hapbeat-unity-sdk/blob/master/instructions/later/instructions-xri-handdemo-augment-202605051200.md) — XRI HandDemo に Editor メニュー 1 クリックで触覚を後付けする Augmentor の設計指示書

「触覚を後付けする Editor ツールをどこまで丁寧に書けばよいか」の参考としてお使いください。
