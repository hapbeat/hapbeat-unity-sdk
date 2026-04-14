# 指示書: Hapbeat VR デモシーンの作成（Player Demo + Creator Tutorial）

- **作成日**: 2026-04-12
- **作成元**: hapbeat-sdk-workspace マイルストーン計画セッション
- **対象リポジトリ**: hapbeat-unity-sdk
- **前提指示書**: `instructions/completed/instructions-vr-sample-scene-202604071800.md`（基本方針。本指示書で上書き・拡張する）
- **目的**: (1) プレイヤーが Hapbeat の効能を体験で理解する (2) 開発者が SDK の組み込み方を学ぶ

---

## 背景

### 現状
- SDK の C# API、EventMap、Trigger コンポーネント群は実装済み
- `Samples~/BasicExample/` にキーボード操作のデモスクリプトのみ（.unity シーンなし）
- VR 環境で動くサンプルがない

### Hapbeat の効能（デモで示すべき3カテゴリ）
1. **能動的フィードバック** — プレイヤーが起こしたアクション（射撃、パンチ、ドラム等）への反動
2. **受動的フィードバック** — 環境からプレイヤーへの作用（被弾、爆発、雨等）
3. **操作系フィードバック** — ハンドトラッキング環境でコントローラー振動の代替（UI ポーク、グラブ確認等）

### 設計上の知見（競合調査より）
- **OWO 方式**: ユーザーが被弾を避けられない構造にすることで受動体験を確実に提供する
- **ON/OFF 比較**: 最初に ON で体験 → 途中で OFF に切替 → 差を実感させる順序が最も効果的
- **三位一体**: 視覚・音・触覚が同期しないと「ランダムな振動」に感じられる。オーディオとの同期を重視
- **ハンドトラッキングの盲点**: Quest のハンドトラッキングには触覚フィードバックがない → Hapbeat で補完できることが差別化ポイント

---

## 全体構成

```
Samples~/
├── BasicExample/          ← 既存（キーボードテスト用、シーンファイル追加）
├── PlayerDemo/            ← 新規: プレイヤー体験用（Phase 2-4）
└── CreatorTutorial/       ← 新規: 開発者学習用（Phase 5）
```

**実装順序**: Player Demo を先に完成 → Creator Tutorial は Player Demo の要素を流用

---

## Phase 1: ベース準備

### 1.1 プロジェクト環境

- Unity 2022.3 LTS 以上（Unity 6 も可）
- XR Interaction Toolkit 3.x を Package Manager からインストール
- **Starter Assets** サンプルをインポート（XR Origin プレハブ、テレポート、基本インタラクタブル）
- **Hands Interaction Demo** サンプルをインポート（ポーク・ピンチの参考用）
- Hapbeat SDK をインポート（Add package from disk）
- XR Device Simulator をインポート（HMD なしでのテスト用）

### 1.2 ライセンス対応

XRI アセットは **Unity Companion License v1.4** で提供されている。SDK サンプルへの同梱は Unity エコシステム内での使用に限り許可されている。

**必須**: `Samples~/THIRD_PARTY_NOTICES.md` を作成:
```markdown
# Third Party Notices

## XR Interaction Toolkit
- License: Unity Companion License v1.4
- Copyright: Unity Technologies
- URL: http://www.unity3d.com/legal/licenses/Unity_Companion_License

XR Interaction Toolkit の Starter Assets および Hands Interaction Demo の
アセット・プレハブを一部使用・改変しています。
```

### 1.3 BasicExample にシーンファイルを追加

`Samples~/BasicExample/BasicExample.unity` を作成:
```
BasicExample.unity
├── Main Camera
├── Directional Light
├── [Hapbeat Event Router]
│   ├── HapbeatManager（自動追加）
│   └── HapbeatDemo.cs（既存）
├── Canvas (Screen Space)
│   ├── Text - Title ("Hapbeat Basic Demo")
│   ├── Text - Status（接続状態表示）
│   └── Text - Instructions ("Space: Play / S: Stop / X: Stop All / P: Ping")
└── EventSystem
```

追加スクリプト `HapbeatDemoUI.cs`（30-50行）:
- HapbeatManager の接続状態を Text に反映
- OnConnected / OnPong イベントをログ表示

---

## Phase 2: Player Demo — Zone A（能動フィードバック）

### ディレクトリ構成

```
Samples~/PlayerDemo/
├── PlayerDemo.unity                 ← メインシーン
├── Scripts/
│   ├── DemoManager.cs              ← 全体管理（ON/OFF トグル、ゾーン管理）
│   ├── HapticToggleUI.cs           ← 触覚 ON/OFF パネル制御
│   ├── PunchingBag.cs              ← パンチングバッグ
│   ├── ShootingRange.cs            ← シューティングレンジ
│   ├── DrumPad.cs                  ← ドラムパッド
│   ├── RainZone.cs                 ← 雨ゾーン（Phase 3）
│   ├── ExplosionField.cs           ← 爆発フィールド（Phase 3）
│   ├── DroneDefense.cs             ← ドローン防衛（Phase 3）
│   ├── PokeButton.cs               ← ポークボタン（Phase 4）
│   └── GrabFeedback.cs             ← グラブフィードバック（Phase 4）
├── Prefabs/
│   ├── HapticTogglePanel.prefab    ← ON/OFF UI パネル
│   ├── PunchingBag.prefab
│   ├── ShootingTarget.prefab
│   ├── DrumPad.prefab
│   └── ...（各ゾーンのプレハブ）
├── Audio/                           ← 効果音（触覚と同期させる）
│   ├── punch_impact.wav
│   ├── gunshot.wav
│   ├── drum_hit.wav
│   ├── rain_loop.wav
│   ├── explosion.wav
│   └── ui_click.wav
├── EventMaps/
│   └── PlayerDemoEventMap.asset     ← 全イベント定義
└── README.md
```

### シーン構造

```
[PlayerDemo.unity]
├── XR Origin（Starter Assets ベース）
│   ├── Camera Offset
│   │   ├── Main Camera
│   │   ├── Right Controller / Right Hand
│   │   └── Left Controller / Left Hand
│   └── Locomotion System（テレポート + 連続移動）
│
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   ├── DemoManager.cs
│   └── EventMap（PlayerDemoEventMap）
│
├── Environment
│   ├── Floor（大きな Plane）
│   ├── Hub Area（中央、説明パネル + テレポートアンカー）
│   ├── Zone A Boundary（視覚的な区切り）
│   ├── Zone B Boundary
│   └── Zone C Boundary
│
├── Hub
│   ├── Welcome Panel（WorldSpace Canvas、説明テキスト）
│   ├── Haptic Toggle Panel（手首追従 or Hub 固定）
│   │   ├── Global ON/OFF トグル
│   │   ├── Zone A ON/OFF
│   │   ├── Zone B ON/OFF
│   │   └── Zone C ON/OFF
│   └── Teleport Anchors × 3
│
├── Zone A: Active Feedback
│   ├── Punching Bag
│   │   ├── Rigidbody（Mass 高め、復元力あり）
│   │   ├── Collider
│   │   ├── HapbeatCollisionTrigger（VelocityScaled, impact.punch）
│   │   ├── AudioSource（punch_impact.wav）
│   │   └── VFX（衝突パーティクル）
│   │
│   ├── Shooting Range
│   │   ├── Gun Object（Grab Interactable）
│   │   ├── ShootingRange.cs（ピンチ or Activate で射撃）
│   │   ├── HapbeatUnityEventTrigger（action.shoot）
│   │   ├── Targets × 3（当たると倒れる）
│   │   │   └── HapbeatCollisionTrigger（impact.target-hit、弾の命中時）
│   │   └── AudioSource（gunshot.wav）
│   │
│   └── Drum Station
│       ├── Drum Pads × 4（色分け）
│       ├── Collider（各パッド）
│       ├── HapbeatCollisionTrigger（VelocityScaled, impact.drum）
│       └── AudioSource（drum_hit.wav × 4音色）
│
├── Zone B: Passive Feedback   ← Phase 3 で実装
│   └── (後述)
│
├── Zone C: UI Feedback        ← Phase 4 で実装
│   └── (後述)
│
└── Lighting + Post Processing
```

### Zone A のスクリプト詳細

#### DemoManager.cs
```
役割:
- 全体の触覚 ON/OFF 状態を管理
- ゾーン別の ON/OFF 状態を管理
- HapbeatManager.Play() をラップし、OFF 時は送信しないフィルタ層を提供
- 各ゾーンの DemoManager への登録

公開メソッド:
  SetGlobalHaptics(bool on)
  SetZoneHaptics(Zone zone, bool on)
  IsHapticsEnabled(Zone zone) → bool
  PlayIfEnabled(Zone zone, string eventId, float gain)

enum Zone { A_Active, B_Passive, C_UI }
```

**実装方針**: HapbeatManager を直接ラップするのではなく、各 Trigger コンポーネントの `enabled` を切り替える方式でも良い。その場合 DemoManager は各ゾーンの Trigger 群を List で持ち、ON/OFF 時に `SetActive` や `enabled` をまとめて切り替える。

#### PunchingBag.cs
```
役割:
- パンチングバッグの物理挙動（Rigidbody の復元）
- 衝突時に AudioSource.Play() を呼ぶ（音量も速度連動）
- HapbeatCollisionTrigger が同じ GO にアタッチされているので、
  触覚は CollisionTrigger が自動処理

実装量: 20-30行（物理復元 + 音の連動のみ）
```

#### ShootingRange.cs
```
役割:
- XR Grab Interactable の Activate イベントで射撃
- Raycast で弾道を判定（物理弾ではなくヒットスキャン）
- ヒット時にターゲットを倒す + AudioSource
- 射撃時の反動は UnityEventTrigger.Fire() で Hapbeat 送信

実装量: 40-60行
```

#### DrumPad.cs
```
役割:
- 衝突検知（手 or スティック）
- 衝突位置で音色を変える（4パッド × 4音色）
- AudioSource.PlayOneShot() で音を鳴らす
- CollisionTrigger が触覚を処理

実装量: 20-30行
```

### EventMap 定義（PlayerDemoEventMap）

| displayName | eventId | gain | group | notes |
|---|---|---|---|---|
| パンチ | impact.punch | 1.0 | 0 | VelocityScaled で上書き |
| 射撃反動 | action.shoot | 0.7 | 0 | Fixed |
| ターゲット命中 | impact.target-hit | 0.5 | 0 | Fixed |
| ドラム | impact.drum | 1.0 | 0 | VelocityScaled で上書き |
| 雨 | ambient.rain | 0.2 | 0 | Loop（Phase 3） |
| 爆発 | impact.explosion | 1.0 | 0 | 距離減衰（Phase 3） |
| 被弾 | impact.hit-received | 0.8 | 0 | Fixed（Phase 3） |
| ボタン押下 | ui.button-press | 0.3 | 0 | Fixed（Phase 4） |
| グラブ | ui.grab | 0.4 | 0 | Fixed（Phase 4） |
| リリース | ui.release | 0.2 | 0 | Fixed（Phase 4） |
| スライダー | ui.slider-tick | 0.1 | 0 | 連続（Phase 4） |

---

## Phase 3: Player Demo — Zone B（受動フィードバック）

### シーン要素

```
Zone B: Passive Feedback
├── Rain Room
│   ├── Rain Zone Trigger（Box Collider, Is Trigger）
│   ├── RainZone.cs
│   ├── Particle System（雨のビジュアル）
│   ├── AudioSource（rain_loop.wav, loop）
│   └── 進入時: ambient.rain を Play（loop）、退出時: Stop
│
├── Explosion Field
│   ├── Explosion Points × 3（定位置）
│   ├── ExplosionField.cs
│   ├── タイマー（5-8秒ランダム間隔で爆発）
│   ├── Particle System（爆発 VFX）
│   ├── AudioSource（explosion.wav）
│   └── プレイヤーとの距離で gain を減衰:
│       gain = Mathf.Clamp01(1.0 - distance / maxRange)
│
└── Drone Defense（OWO 方式）
    ├── DroneDefense.cs
    ├── Drone Spawner（プレイヤーの周囲 360° からスポーン）
    ├── Drone Prefab（ゆっくり近づいてくる）
    ├── Projectile Prefab（回避困難な速度で射出）
    ├── Hit Detection（プレイヤーの Collider に当たる）
    │   └── HapbeatCollisionTrigger（impact.hit-received）
    └── ポイント: ドローンは倒せるが弾は避けにくい設計
        → 受動フィードバックを確実に体験させる
```

#### RainZone.cs
```
役割:
- OnTriggerEnter: ambient.rain を Loop 再生開始
- OnTriggerExit: ambient.rain を Stop
- 雨粒の音量と触覚を同期

実装量: 15-20行
```

#### ExplosionField.cs
```
役割:
- タイマーでランダム間隔の爆発を発生
- 爆発位置からプレイヤーまでの距離で gain を計算
- Particle + Audio + Hapbeat を同時発火
- distance > maxRange なら触覚なし

実装量: 30-40行
```

#### DroneDefense.cs
```
役割:
- プレイヤー周囲にドローンをスポーン
- ドローンはゆっくり接近し、射程内で弾を発射
- 弾はプレイヤー方向にやや追尾（回避困難）
- プレイヤーに命中 → impact.hit-received
- オプション: 手で弾を弾ける（能動フィードバックとの融合）

実装量: 60-80行（最も複雑なスクリプト）
```

---

## Phase 4: Player Demo — Zone C（操作系フィードバック）

### シーン要素

```
Zone C: UI / Interaction Feedback
├── Button Panel（WorldSpace Canvas）
│   ├── Poke Buttons × 4（色付き）
│   ├── PokeButton.cs
│   ├── XR Poke Interactor 対応（Hands Interaction Demo 参考）
│   └── ポーク（指で押す）→ ui.button-press
│
├── Object Shelf
│   ├── Grab Objects × 3（異なるサイズ・重さ）
│   ├── XR Grab Interactable
│   ├── GrabFeedback.cs
│   ├── OnSelectEntered → ui.grab
│   ├── OnSelectExited → ui.release
│   └── 投げて壁にぶつける → impact.punch（CollisionTrigger）
│
└── Control Panel
    ├── Slider（XR の Constrained Interactable 参考）
    ├── ui.slider-tick を値変化ごとに発火
    └── Hapbeat の gain をスライダーで動的に変更できるデモ
```

#### PokeButton.cs
```
役割:
- XR Poke Filter 対応（指の押し込みで発火）
- 押下時に色変化 + AudioSource + HapbeatUnityEventTrigger.Fire()
- ボタンごとに異なる gain を設定可能

実装量: 20-30行
```

#### GrabFeedback.cs
```
役割:
- XR Grab Interactable の OnSelectEntered / OnSelectExited を監視
- 掴む → ui.grab、離す → ui.release
- 投げた物体の衝突は CollisionTrigger に委譲

実装量: 20-30行
```

### 触覚 ON/OFF UI の詳細

#### HapticToggleUI.cs
```
配置: 手首追従パネル（Left Hand に追従）
  - ハンドトラッキング時: 右手で左手の甲のパネルを操作
  - コントローラー時: 左手首付近に浮遊

UI 構成:
  ├── "Haptic Feedback" タイトル
  ├── [Global ON/OFF] トグルボタン（大きめ）
  ├── Zone A [ON/OFF] ラベル: "Active"
  ├── Zone B [ON/OFF] ラベル: "Passive"
  └── Zone C [ON/OFF] ラベル: "UI"

動作:
  - Global OFF → 全ゾーン OFF（個別設定は保持）
  - Global ON → 個別設定に復帰
  - 各ゾーンのトグルで DemoManager.SetZoneHaptics() を呼ぶ
  - 現在のゾーンを DemoManager が検知し、該当ゾーンをハイライト

実装量: 40-60行
```

**推奨体験フロー**（展示時の案内用）:
1. Hub で簡単な説明を読む
2. Zone A にテレポート → パンチ・射撃・ドラムを体験（触覚 ON）
3. トグルパネルで Zone A を OFF → 同じ操作を繰り返す → 差を実感
4. Zone B にテレポート → 雨の中を歩く、爆発を受ける、ドローンに撃たれる
5. Zone C にテレポート → ハンドトラッキングでボタン押下、物を掴む
6. Global OFF → 全てが無味になることを体感

---

## Phase 5: Creator Tutorial

### 目的

開発者が「自分の VR プロジェクトに Hapbeat を5分で追加できる」ことを示す。

### ディレクトリ構成

```
Samples~/CreatorTutorial/
├── CreatorTutorial_Before.unity     ← 触覚なしの完成ゲーム
├── CreatorTutorial_After.unity      ← Hapbeat 統合済み
├── Scripts/
│   ├── SimpleShooter.cs             ← シンプルな射撃スクリプト
│   ├── Target.cs                    ← 的（当たると倒れる + スコア）
│   └── ScoreUI.cs                   ← スコア表示
├── EventMaps/
│   └── TutorialEventMap.asset
└── README.md                         ← ステップバイステップガイド
```

### Before シーン（触覚なし）

```
[CreatorTutorial_Before.unity]
├── XR Origin（Starter Assets ベース）
├── Environment
│   ├── Floor
│   └── Walls（射撃場の壁）
├── Shooting Range
│   ├── Gun（Grab Interactable、Activate で射撃）
│   ├── SimpleShooter.cs（Raycast + VFX）
│   ├── Targets × 5（当たると倒れる、3秒で復活）
│   ├── Target.cs（スコア加算）
│   └── Obstacle（動く障害物、当たるとゲームオーバー演出）
├── Canvas
│   ├── Score Text
│   └── Timer Text
├── AudioSources（射撃音、命中音、環境音）
└── EventSystem
```

→ 完全に動作するミニゲーム。ただし触覚フィードバックなし。

### After シーン（Hapbeat 統合済み）

Before シーンに以下を追加した状態:

```
追加要素:
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   ├── UnityEventTrigger（射撃反動: action.shoot）
│   └── TutorialEventMap
├── Gun
│   └── SimpleShooter.cs の OnShoot イベントに
│       UnityEventTrigger.Fire() を接続
├── Targets × 5
│   └── HapbeatCollisionTrigger（impact.target-hit）追加
├── Obstacle
│   └── HapbeatCollisionTrigger（impact.hit-received）追加
└── Haptic Toggle（ON/OFF 確認用）
```

### README.md（チュートリアルガイド）

```markdown
# Hapbeat Creator Tutorial

## 概要

このチュートリアルでは、触覚フィードバックのない VR シューティングゲームに
Hapbeat を組み込む手順を、ステップバイステップで説明します。

完成前: `CreatorTutorial_Before.unity`
完成後: `CreatorTutorial_After.unity`

## 前提条件

- Hapbeat SDK がインポート済み
- XR Interaction Toolkit 3.x がインストール済み

## Step 1: Before シーンを開く

`CreatorTutorial_Before.unity` を開いて Play してみてください。
射撃ゲームが動作しますが、触覚フィードバックはありません。

## Step 2: Hapbeat Event Router を作成

1. Hierarchy で右クリック → `Hapbeat > Event Router`
2. [Hapbeat Event Router] が作成され、HapbeatManager が自動追加されます

## Step 3: EventMap を作成

1. Project ウィンドウで右クリック → `Create > Hapbeat > Event Map`
2. 以下のエントリを追加:

   | Display Name | Event ID | Gain |
   |---|---|---|
   | 射撃反動 | action.shoot | 0.7 |
   | ターゲット命中 | impact.target-hit | 0.5 |
   | 被弾 | impact.hit-received | 0.8 |

## Step 4: 射撃に触覚を追加

1. [Hapbeat Event Router] を選択
2. Add Component → `Hapbeat/UnityEvent Trigger`
3. Event Map → Step 3 で作った EventMap を設定
4. Event → 「射撃反動」を選択
5. Hierarchy で Gun を選択 → SimpleShooter の `On Shoot ()` イベントに
   [Hapbeat Event Router] の `HapbeatUnityEventTrigger > Fire()` を設定

## Step 5: 命中フィードバックを追加

1. 各 Target を選択
2. Add Component → `Hapbeat/Collision Trigger`
3. Event Map → 同じ EventMap
4. Event → 「ターゲット命中」
5. Trigger Event → CollisionEnter

## Step 6: 被弾フィードバックを追加

1. Obstacle を選択
2. Add Component → `Hapbeat/Collision Trigger`
3. Event Map → 同じ EventMap
4. Event → 「被弾」
5. Trigger Event → TriggerEnter

## Step 7: テスト

1. Hapbeat デバイスの電源を入れ、同じ Wi-Fi に接続
2. Play モードに入る
3. HapbeatManager の Inspector で接続状態を確認
4. 射撃 → 反動を感じる
5. 的に命中 → 命中振動を感じる
6. 障害物に当たる → 被弾振動を感じる

## 応用

- CollisionTrigger の Gain Mode を VelocityScaled にすると、
  衝突速度に応じた強さになります
- 新しいイベントを EventMap に追加し、AnimatorTrigger で
  アニメーション状態と連動させることもできます
- Window > Hapbeat > Event Map で全トリガーの配置を一覧確認できます
```

### Tutorial Framework について

Unity の `com.unity.learn.iet-framework` を使えばエディタ内ガイド（ハイライト + ステップ指示）が作れるが、現段階では **README のステップバイステップ + Before/After 比較で十分**。理由:
- 依存パッケージが増える（IET Framework + Authoring Tools）
- チュートリアルコンテンツの作成・メンテナンスコストが高い
- SDK バージョンアップ時にチュートリアルも更新が必要

将来的にユーザー数が増えた段階で、IET Framework 版の検討は有効。

---

## CLAUDE.md の更新

以下を変更すること:

**変更前**:
```
## まだ作らないもの

- VR 専用機能
```

**変更後**:
```
## まだ作らないもの

- VR 専用の高度な機能（ボディトラッキング連動、ハンドトラッキング連動等）

## VR サンプル方針

- VR 専用 API は作らない。既存の API（Play, EventMap, Trigger）で VR 対応できることを示す
- XR Interaction Toolkit に直接依存しない（asmdef に参照を追加しない）。UnityEvent 経由の疎結合
- サンプルは Samples~ に配置し、UPM の Import Samples で導入可能にする
- Player Demo（体験者向け）と Creator Tutorial（開発者向け）の2系統
```

---

## package.json の更新

```json
{
  "samples": [
    {
      "displayName": "Basic Example",
      "description": "キーボードで Hapbeat を操作する最小サンプル",
      "path": "Samples~/BasicExample"
    },
    {
      "displayName": "Player Demo",
      "description": "VR で Hapbeat の3種のフィードバック（能動・受動・操作系）を体験するデモ",
      "path": "Samples~/PlayerDemo"
    },
    {
      "displayName": "Creator Tutorial",
      "description": "既存 VR ゲームに Hapbeat を組み込むステップバイステップチュートリアル",
      "path": "Samples~/CreatorTutorial"
    }
  ]
}
```

---

## 実装上の注意

1. **XR Interaction Toolkit に直接依存しない**: asmdef に XR パッケージの参照を追加しない。UnityEvent / コールバック経由の疎結合を維持
2. **オーディオとの同期を重視**: 全ての触覚イベントに対応する AudioSource.Play() を同時に呼ぶ。音なし触覚は「ランダム振動」に感じられる
3. **.unity シーンファイル**: Unity Editor で作成する必要がある。スクリプトを先に作成し、シーンの組み立てはエディタ上で行う
4. **XR Device Simulator 対応**: HMD がなくてもキーボード + マウスでテストできるようにする
5. **パフォーマンス**: Zone B のパーティクルや Drone は軽量に。Quest スタンドアロンで 72fps を維持すること
6. **Player Demo の Zone 間は独立**: 各 Zone を個別に開発・テストできる構造にする。Zone A だけでも体験として成立すること

---

## 完了条件

### Phase 1
- [ ] BasicExample にシーンファイルが存在し Play できる
- [ ] THIRD_PARTY_NOTICES.md が作成されている

### Phase 2（Zone A）
- [ ] パンチングバッグが VelocityScaled で動作
- [ ] シューティングレンジで射撃 → 反動フィードバック
- [ ] ドラムパッドが衝突速度連動で動作
- [ ] ON/OFF トグルが動作

### Phase 3（Zone B）
- [ ] レインルームで進入/退出時に ambient ループが開始/停止
- [ ] 爆発が距離減衰で触覚強度が変化
- [ ] ドローン防衛で被弾フィードバックが動作

### Phase 4（Zone C）
- [ ] ポークボタンが指押しで触覚フィードバック
- [ ] グラブ/リリースで異なるフィードバック
- [ ] スライダー操作で連続フィードバック

### Phase 5（Creator Tutorial）
- [ ] Before シーンが触覚なしで動作
- [ ] After シーンが Hapbeat 統合済みで動作
- [ ] README のステップに従って Before → After を再現できる

### 共通
- [ ] package.json に samples セクションが更新されている
- [ ] CLAUDE.md が更新されている
- [ ] 全シーンで XR Device Simulator でのテストが可能
