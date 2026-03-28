# 指示書: Unity SDK サンプルシーン制作

## 対象 repo

`hapbeat-unity-sdk` (`Samples~/` ディレクトリ)

## 目的

SDK の各使用法を実際に動かして理解できるサンプルシーン群を制作する。ドキュメント（README）だけでは伝わらない操作感を体験できるようにする。

## 前提

- Hapbeat デバイスとの Wi-Fi UDP 通信は動作確認済み（Quest 2 スタンドアロン含む）
- SDK の全機能（EventMap, Trigger, Bridge, HapbeatEvent, コード直接呼び出し）は実装済み
- VR テンプレートベースのプロジェクトで動作確認済み

## サンプル一覧

### Sample 1: BasicExample（既存・更新）

**目的**: 最小構成での動作確認

**現状**: キーボード操作（Space=Play, S=Stop, X=StopAll, P=Ping）の `HapbeatDemo.cs`

**更新内容**:
- 既存のままで OK。コード直接呼び出し（方法1）のリファレンスとして維持

---

### Sample 2: EventMapExample（新規）

**目的**: EventMap + Trigger コンポーネントの使い方（方法2）を体験

**内容**:
- `HapbeatEventMap` アセット（5〜6 エントリ定義済み）
- `[Hapbeat Event Router]` プレハブ（AnimatorTrigger, UnityEventTrigger 設定済み）
- 3D シーン: 床 + 落下する球体 + UI ボタン
- **コード変更ゼロ** で触覚が動作することを示す

**含めるトリガー例**:

| トリガー | タイプ | 設定 |
|---|---|---|
| 球が床に衝突 | CollisionTrigger | CollisionEnter, VelocityScaled |
| UI ボタン押下 | UnityEventTrigger | Button.OnClick → Fire() |
| Animator 状態変化 | AnimatorTrigger | Bool パラメータ監視 |

**付属ファイル**:
- `EventMapExample.unity` — サンプルシーン
- `SampleEventMap.asset` — EventMap アセット
- `EventMapExample_README.md` — セットアップ手順

---

### Sample 3: VRExample（新規）

**目的**: VR (Quest) でのコントローラー入力連動

**内容**:
- XR Interaction Toolkit ベース
- 右トリガー → 振動（InputActionReference 方式）
- 左グリップ → 別の振動
- オブジェクトを掴む（Grab）→ 振動
- オブジェクトを投げる → 着地時に振動（CollisionTrigger）

**付属ファイル**:
- `VRExample.unity` — VR シーン
- `HapbeatVRTest.cs` — InputActionReference ベースのテストスクリプト
- `VRExample_README.md` — Quest ビルド手順を含む

**依存**: XR Interaction Toolkit パッケージ（VR テンプレートに含まれる）

**注意**: XR Interaction Toolkit への依存があるため、このサンプルは `Samples~/VRExample/` に配置し、Package Manager から選択的にインポートする形式とする。XR パッケージがないプロジェクトではインポートしない。

---

### Sample 4: BridgeExample（新規）

**目的**: HapbeatBridge サブクラスの使い方（方法3）を体験

**内容**:
- `SampleBridge.cs` — HapbeatBridge を継承した実装例
- 衝突速度に応じたゲイン変動（PlayScaled）
- AnimationCurve によるゲイン変換（PlayWithCurve）
- 条件分岐（速度閾値でイベント切替）
- `[SerializeField]` でゲームオブジェクトを参照する例

**付属ファイル**:
- `BridgeExample.unity` — サンプルシーン
- `SampleBridge.cs` — Bridge 実装例（コメント付き）
- `SampleEventMap.asset` — EventMap アセット
- `BridgeExample_README.md`

---

### Sample 5: AnimationEventExample（新規・オプション）

**目的**: Animation Event による特定フレーム発火（方法5）を体験

**内容**:
- 歩行アニメーション付きキャラクター
- 足の接地フレームに Animation Event を配置
- UnityEventTrigger.Fire() で振動

**付属ファイル**:
- `AnimationEventExample.unity`
- 歩行アニメーション（既存の Unity アセットを使用 or 最小のもの）
- `AnimationEventExample_README.md`

**注意**: アニメーションアセットの用意が必要。既存の Unity 標準アセットがなければ後回しでよい。

---

## ディレクトリ構成

```
Samples~/
  BasicExample/          ← 既存
    HapbeatDemo.cs
    BasicExample.unity

  EventMapExample/       ← 新規
    EventMapExample.unity
    SampleEventMap.asset
    EventMapExample_README.md

  VRExample/             ← 新規
    VRExample.unity
    HapbeatVRTest.cs
    VRExample_README.md

  BridgeExample/         ← 新規
    BridgeExample.unity
    SampleBridge.cs
    SampleEventMap.asset
    BridgeExample_README.md

  AnimationEventExample/ ← 新規（オプション）
    ...
```

## 実装方針

- 各サンプルは**独立して動作**すること（他サンプルへの依存なし）
- README は**手順ベース**で書く（概念説明は SDK の README に委譲）
- EventMap アセットの Event ID は Hapbeat デバイスの Pack に登録されているものを使用（`weapon.gunshot` 等）
- VR サンプルは XR Interaction Toolkit 依存を明記し、非 VR プロジェクトでは使わない旨を注記
- 各 README に「このサンプルで学べること」を冒頭に記載

## Event ID について

サンプルで使用する Event ID は、Hapbeat デバイスの Pack に実際に登録されている ID と一致させる必要がある。現時点で Pack に登録されている Event ID を確認し、サンプルで使用する ID を決定すること。

仮の ID 一覧（Pack 確認後に更新）:

| Event ID | 用途 |
|---|---|
| `weapon.gunshot` | 銃撃・衝撃 |
| `impact.landing` | 着地 |
| `impact.damage` | ダメージ |
| (要確認) | (要確認) |

## 優先順位

1. **Sample 2: EventMapExample** — 推奨方法の実例として最優先
2. **Sample 3: VRExample** — Quest テスト済みなのですぐ作れる
3. **Sample 4: BridgeExample** — コードベース集約の実例
4. **Sample 5: AnimationEventExample** — アセット準備次第

## 完了条件

- 各サンプルシーンを開いて Play → 触覚が動作する
- README の手順通りに操作して再現できる
- EventMap ウィンドウ（Window > Hapbeat > Event Map）で全マッピングが確認できる
- VR サンプルは Quest 2 Build And Run で動作する

## セッション記録

- 2026-03-28: Wi-Fi UDP 通信テスト（Quest 2 スタンドアロン）成功
- 2026-03-28: SoftAP 接続テストへ移行予定（ファーム側変更のみ、SDK 変更なし）
- SDK の HapbeatBridge 基底クラスは実装済み（push 済み）
- CollisionTrigger の VelocityScaled + AnimationCurve は実装済み
- AnimatorTrigger のパラメータ名ドロップダウンは実装済み
- HapbeatTriggerBaseEditor の OnEnable を protected virtual に修正済み
