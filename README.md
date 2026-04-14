# Hapbeat Unity SDK

Hapbeat デバイスを Unity から制御する公式 SDK。2D / 3D / XR 対応。

## インストール

Unity Package Manager → `+` → `Add package from disk...` → `package.json` を選択

## クイックスタート

1. シーンに HapbeatManager を配置: `GameObject > Hapbeat > Event Router`
2. 起動時に自動で Wi-Fi UDP ブロードキャストが開始される
3. 以下のいずれかの方法で触覚イベントを発火

## イベント割り当て方法

### 方法1: コード直接呼び出し（最小構成）

最もシンプル。通信テストや小規模プロジェクト向け。

```csharp
using Hapbeat;

// 即時再生
HapbeatManager.Instance.Play("impact.landing", gain: 0.3f);

// 停止
HapbeatManager.Instance.Stop("impact.landing");

// 全停止
HapbeatManager.Instance.StopAll();
```

**利点**: 即座に動く、学習コストゼロ
**欠点**: 触覚コードが散在する、ID やゲインがハードコード

---

### 方法2: EventMap + Trigger コンポーネント（推奨・コード不要）

イベント定義を一元管理し、Inspector だけで設定。既存コード変更不要。

#### Step 1: EventMap 作成

`Assets > Create > Hapbeat > Event Map` → エントリを追加

| displayName | eventId | gain |
|---|---|---|
| 着地 | impact.landing | 0.3 |
| ジャンプ | jump.takeoff | 0.2 |
| 敵衝突 | impact.enemy | 0.8 |

#### Step 2: Trigger コンポーネントを配置

**A. 衝突トリガー（CollisionTrigger）** — 対象 GO にアタッチ

物理衝突やトリガーイベントで発火。2D / 3D 自動判定。

```
設定項目:
  Event Map    → 作成した EventMap
  Event        → ドロップダウンで選択
  Trigger Event → CollisionEnter / TriggerEnter / Exit 系
  Tag Filter   → "Player" 等（空なら全対象）
  Layer Mask   → レイヤーフィルタ
  Gain Mode    → Fixed（固定）/ VelocityScaled（速度連動）
  Cooldown     → 連続発火防止（秒）
```

速度連動ゲイン（VelocityScaled）を使うと、衝突速度に応じて振動の強さが変わります。AnimationCurve で速度→ゲインの変換カーブを設定できます。

複数イベントを同じ GO から発火したい場合は、CollisionTrigger を複数アタッチし、Tag Filter や Layer Mask で対象を分けます。

**B. Animator トリガー（AnimatorTrigger）** — 専用 GO に配置可能

Animator パラメータの変化を検知して発火。対象 Animator はドラッグで参照指定。

```
設定項目:
  Event Map        → EventMap
  Event            → ドロップダウン
  Target Animator  → 監視対象（別 GO の Animator もOK）
  Parameter        → ドロップダウンで選択（Animator から自動取得）
  Condition        → BoolBecameTrue / BoolBecameFalse / FloatAbove 等
  Threshold        → Float/Int 条件の閾値
```

例: `grounded` が `true` になった瞬間 → 着地振動

**C. UnityEvent トリガー（UnityEventTrigger）** — 専用 GO に配置可能

`Fire()` メソッドを任意の UnityEvent から呼び出し。

- UI Button の OnClick
- XR Interaction Toolkit の OnSelectEntered / OnActivated
- Animation Event
- その他任意の UnityEvent

```
設定項目:
  Event Map → EventMap
  Event     → ドロップダウン
```

#### UnityEventTrigger の詳細: コントローラー入力で振動させる

ここでは VR コントローラーのトリガー（人差し指ボタン）を押したら振動する設定を、コード不要で行う手順を説明します。

**前提**: XR Interaction Toolkit がプロジェクトに入っていること（VR テンプレートなら最初から入っています）

**Step 1: EventMap にイベントを登録**

1. Project ウィンドウの Assets フォルダで右クリック → `Create > Hapbeat > Event Map`
2. 作成された `HapbeatEventMap` をクリックして Inspector を開く
3. `Entries` の `+` ボタンを押してエントリを追加:
   - Display Name: `トリガー振動`
   - Event Id: `input.trigger`
   - Gain: `0.5`

**Step 2: Hapbeat Event Router を作成**

1. Hierarchy ウィンドウで右クリック → `Hapbeat > Event Router`
2. `[Hapbeat Event Router]` という GO が作成される

**Step 3: UnityEventTrigger を追加**

1. Hierarchy で `[Hapbeat Event Router]` を選択
2. Inspector 下部の `Add Component` をクリック
3. 検索欄に `Hapbeat` と入力 → `Hapbeat/UnityEvent Trigger` を選択
4. 追加された HapbeatUnityEventTrigger の設定:
   - Event Map → Step 1 で作った `HapbeatEventMap` をドラッグ
   - Event → ドロップダウンから「トリガー振動」を選択

**Step 4: XR Controller の入力イベントと接続**

ここが核心です。VR テンプレートのシーンには `XR Origin` の下にコントローラーの GO があります。

1. Hierarchy で `XR Origin > Camera Offset > Right Controller` を探す
   （テンプレートによっては `Right Hand Controller` 等の名前）
2. この GO には `XR Controller` コンポーネントがあるはず
3. さらに `XR Interactor` 系のコンポーネント（`XR Ray Interactor` や `XR Direct Interactor`）があれば、そこに UnityEvent があります

**接続方法A: XR Interactor の Activate イベントを使う**

1. Right Controller の `XR Ray Interactor`（または `XR Direct Interactor`）を Inspector で開く
2. 下の方にある `Interactor Events` セクションを展開
3. `Activate` の `On Activated` イベントを見つける（トリガーを引いた時に発火）
4. `+` ボタンを押して新しいイベントを追加
5. 設定:
   - 左の欄に `[Hapbeat Event Router]` を Hierarchy からドラッグ
   - 右のドロップダウン → `HapbeatUnityEventTrigger > Fire()` を選択

```
On Activated:
  ┌─────────────────────────────────────────────────┐
  │ [Hapbeat Event Router]  │ HapbeatUnityEvent... ▼│
  │                         │ Fire()                │
  └─────────────────────────────────────────────────┘
```

これで**右コントローラーのトリガーを引く → Hapbeat が振動**します。

**接続方法B: Input Action の Events を使う（より汎用的）**

XR Interactor がない場合や、特定のボタンを直接使いたい場合:

1. `[Hapbeat Event Router]` に `Player Input` コンポーネントを追加
2. Actions に XR の Input Action Asset を設定
3. Behavior を `Invoke Unity Events` に変更
4. 表示される Events セクションで、目的のアクション（例: `XRI Right Hand > Activate`）の欄に:
   - `[Hapbeat Event Router]` の `HapbeatUnityEventTrigger.Fire()` を設定

**接続方法C: 最もシンプル — Button の OnClick**

UI ボタンの場合はさらに簡単:

1. UI Button の Inspector → `On Click ()` イベント
2. `+` → `[Hapbeat Event Router]` をドラッグ → `HapbeatUnityEventTrigger > Fire()`

**複数ボタンに異なる振動を割り当てる場合:**

Router に UnityEventTrigger を複数追加し、それぞれ違う EventMap エントリを設定:

```
[Hapbeat Event Router]
  ├─ UnityEventTrigger (Event: トリガー振動)  ← 右トリガー → Fire()
  ├─ UnityEventTrigger (Event: グリップ振動)  ← 右グリップ → Fire()
  └─ UnityEventTrigger (Event: パンチ)        ← 左トリガー → Fire()
```

各 XR Controller のイベントから、対応する UnityEventTrigger の `Fire()` に接続します。

#### 推奨配置

```
シーン Hierarchy:
  [Hapbeat Event Router]     ← GameObject > Hapbeat > Event Router で作成
    ├─ HapbeatManager        ← 自動追加
    ├─ AnimatorTrigger (着地) ← Target Animator = Player
    ├─ AnimatorTrigger (ジャンプ)
    └─ UnityEventTrigger (UI操作)

  Enemy (prefab)
    └─ CollisionTrigger (敵衝突) ← 物理コールバックのため対象 GO にアタッチ

  Token (prefab)
    └─ CollisionTrigger (コイン取得)
```

#### 一覧管理

`Window > Hapbeat > Event Map` でダッシュボードを開くと、全エントリとトリガーの配置先を一覧表示:

```
Name     │ Event ID       │ Gain │ Type      │ Attached To
着地     │ impact.landing │ 0.3  │ Animator  │ [Router]
敵衝突   │ impact.enemy   │ 0.8  │ Collision │ Enemy (3)
コイン   │ collect.token  │ 0.1  │ Collision │ Token (5)
```

---

### 方法3: HapbeatBridge サブクラス（コードベース・集約管理）

速度連動ゲインや条件分岐など、Inspector だけでは表現しきれないロジックを1ファイルに集約。

```csharp
using Hapbeat;
using UnityEngine;

public class MyHapbeatBridge : HapbeatBridge
{
    [Header("Game References")]
    [SerializeField] private PlayerController _player;

    // 衝突速度に応じてゲインを変える
    public void OnPlayerLanded(Collision2D col)
    {
        float speed = col.relativeVelocity.magnitude;
        if (speed < 1f) return;
        PlayScaled("着地", speed, minVel: 1f, maxVel: 15f);
    }

    // AnimationCurve でゲインを制御
    [SerializeField] private AnimationCurve _impactCurve;
    public void OnEnemyHit(Collision2D col)
    {
        float speed = col.relativeVelocity.magnitude;
        PlayWithCurve("敵衝突", speed, _impactCurve, maxValue: 20f);
    }

    // 固定ゲインで発火
    public void OnTokenCollected()
    {
        Play("コイン取得");
    }
}
```

`HapbeatBridge` の提供メソッド:

| メソッド | 用途 |
|---|---|
| `Play(displayName)` | EventMap の displayName で発火（固定ゲイン） |
| `Play(displayName, gainOverride)` | ゲイン上書き |
| `PlayScaled(displayName, value, min, max)` | 値を 0-1 に正規化してゲインに |
| `PlayWithCurve(displayName, value, curve, max)` | AnimationCurve でゲイン変換 |
| `Stop(displayName)` | 停止 |

**利点**: 複雑なロジックも1ファイルに集約、EventMap で ID 管理は維持
**用途**: 速度連動、条件分岐、複数イベント同時発火、カスタムロジック

---

### 方法4: HapbeatEvent コンポーネント（シンプル・単発用）

EventMap を使わず、個別にイベント ID を指定。簡易的な用途向け。

```
設定項目:
  Event ID         → "impact.landing"
  Gain             → 0.3
  Group            → -1（デフォルト）
  Trigger On Start → ON にすると有効化時に自動再生
```

公開メソッド: `TriggerPlay()` / `TriggerStop()` を UnityEvent 等から呼び出し。

---

### 方法5: Animation Event（足音など特定フレーム発火）

Animation ウィンドウでアニメーションクリップの特定フレームにイベントを追加し、`HapbeatUnityEventTrigger.Fire()` を呼ぶ。コード変更不要。

```
Run アニメーション:
  0.0 ─── 0.25 ─── 0.5 ─── 0.75 ─── 1.0
           ↑ 左足接地        ↑ 右足接地
       Fire() 呼出        Fire() 呼出
```

---

## 方法の使い分け

| 状況 | 推奨方法 |
|---|---|
| 通信テスト・プロトタイプ | 方法1（コード直接） |
| 既存ゲームへの後付け | 方法2（EventMap + Trigger） |
| 複雑なゲインロジック | 方法3（HapbeatBridge） |
| 単発の簡易トリガー | 方法4（HapbeatEvent） |
| アニメーション同期 | 方法5（Animation Event） |

方法2〜5は組み合わせ可能です。例えば大部分を Trigger コンポーネントで設定し、特殊なケースだけ Bridge サブクラスで処理する構成が実用的です。

## サンプルシーン

Package Manager > Hapbeat SDK > Samples からインポートできます。

| サンプル | 内容 | 前提 |
|---|---|---|
| Basic Example | キーボード操作（Space/S/X/P）で基本 API を確認 | なし |
| Player Demo | VR で3種のフィードバック（能動・受動・操作系）を体験 | XR Interaction Toolkit 3.x |
| Creator Tutorial | 既存 VR ゲームに Hapbeat を組み込むステップバイステップガイド | XR Interaction Toolkit 3.x |

### VR クイックスタート

1. Unity Hub で VR テンプレートからプロジェクトを作成
2. Hapbeat SDK をインポート（Package Manager > Add from disk）
3. Samples > Player Demo をインポート
4. `PlayerDemo.unity` を開いて Play
5. Hapbeat デバイスを同じネットワークに接続

### 開発者向け

1. Samples > Creator Tutorial をインポート
2. `CreatorTutorial_Before.unity`（触覚なし）で動作確認
3. README の手順に従って Hapbeat を組み込む
4. `CreatorTutorial_After.unity` で完成形を確認

## 接続設定

`Window > Hapbeat > Settings` または `Assets > Create > Hapbeat > Config`

| 設定 | デフォルト | 説明 |
|---|---|---|
| Port | 7700 | UDP ポート |
| Group | 0 | 送信先グループ（0=全デバイス、1-254=特定グループ） |
| Use Bridge | OFF | ESP-NOW 経由の場合のみ ON |
| Ping Interval | 5秒 | キープアライブ間隔 |

## Edit モード操作

プレイモードに入らなくても、Inspector の HapbeatManager から以下が可能:

- **接続 (Edit)** — ブロードキャスト開始
- **検出 (Edit)** — LAN 上の Hapbeat を検出
- **Play / Stop / Ping** — テスト送信

## 対応プラットフォーム

- PC (Windows / macOS)
- Meta Quest 2 / 3 / Pro
- Pico 4 Ultra
- Apple Vision Pro
- その他 Android / iOS デバイス
