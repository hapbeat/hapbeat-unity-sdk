# HandsDemoScene 触覚追加ガイド

XR Interaction Toolkit の Hands Interaction Demo に Hapbeat 触覚を追加する手順。
シーンのロジックは一切変更せず、コンポーネント追加 + UnityEvent 接続のみで行う。

## 前提

- Unity プロジェクト: `M:\GameEngine\Unity\Projects\HapbeatSDKSamples\`
- XRI 3.3.1 の Hands Interaction Demo がインポート済み
- Hapbeat SDK がインポート済み
- HandsDemoScene を開いた状態

## Step 1: HapbeatManager の配置

1. Hierarchy ルートに空の GameObject を作成 → 名前: `[Hapbeat]`
2. Add Component → **Hapbeat > Hapbeat Manager**
3. Config が未設定なら Resources 内の HapbeatConfig を割り当て

> 完了済み

## Step 2: EventMap の作成

Project ウィンドウで右クリック → **Create > Hapbeat > Event Map** → `HandsDemoEventMap`

以下のエントリを追加（category と eventName は分離フィールド）:

| Index | displayName | category | eventName | gain | notes |
|---|---|---|---|---|---|
| 0 | Grab | `impact` | `grab` | 1.0 | オブジェクトを掴む/離す |
| 1 | Click | `ui` | `click` | 1.0 | ボタン押下 |
| 2 | Impact | `impact` | `hit` | 1.0 | 衝突（VelocityScaled の基準値） |
| 3 | Snap | `ui` | `confirm` | 1.2 | ソケット装着の確認感 |

> eventId は `category.eventName` 形式で自動計算される（contracts/specs/event-id.md 準拠）
> 新しい EventMap Editor で category はドロップダウン、eventName はテキスト入力

## Step 3: 各オブジェクトへの触覚追加

### 3-A: Cube 1 / Cube 2 / Cube 3

パス: `Table > Left > ... > Cubes > Cube 1`（2, 3 も同様）

> **Batch Setup を使う場合**: Hierarchy で Cube 1/2/3 を複数選択 →
> Window > Hapbeat > Batch Setup → EventMap + Entry 設定 → Apply で一括追加可能

**Hapbeat UnityEvent Trigger（掴む/離す）**

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`
3. Entry Index = **0** (Grab)
4. **XR Grab Interactable** の Interactable Events を展開:
   - `Select Entered` → `+` → 自分自身(Cube 1) → `HapbeatUnityEventTrigger.Fire`
   - `Select Exited` → `+` → 自分自身(Cube 1) → `HapbeatUnityEventTrigger.Fire`

**Collision Trigger（投擲衝突用）**

Cube は ThrowOnDetach=true で投げられるため、床やテーブルに当たった衝撃を検出する。

> **注意**: XRI のハンドインタラクションは Trigger Collider を使うため、手→Cube の接触は
> `TriggerEnter` で検出される。`CollisionEnter` が発火するのは Cube が物理衝突する場面
> （投げて床に当たる等）のみ。
>
> 手で触れた時の触覚は上記の UnityEvent Trigger (Select Entered) で対応済み。
> ここで追加する Collision Trigger は **投擲後の衝突フィードバック専用**。
> 不要であればスキップ可。

1. Add Component → **Hapbeat > Hapbeat Collision Trigger**
2. Event Map = `HandsDemoEventMap`
3. Entry Index = **2** (Impact)
4. Trigger Event = **CollisionEnter**（投擲後の物理衝突を検出）
5. Gain Mode = **VelocityScaled**
6. Velocity Threshold = `0.5`（軽い接触は無視）
7. Max Velocity = `5.0`
8. Cooldown = `0.1`

### 3-B: Cylinder / Arrow

パス: `Table > Top > Cylinder` / `Table > Top > Arrow`

Kinematic なので投擲衝突なし。掴む/離すのみ。

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **0** (Grab)
3. **XR Grab Interactable**:
   - `Select Entered` → `HapbeatUnityEventTrigger.Fire`
   - `Select Exited` → `HapbeatUnityEventTrigger.Fire`

### 3-C: Disc

パス: `Table > Top > Disc`

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **0** (Grab)
3. **XR Simple Interactable**:
   - `Select Entered` → `Fire`
   - `Select Exited` → `Fire`

### 3-D: PokeButton

パス: `PokeButton`（ルート直下）

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **1** (Click)
3. **XR Simple Interactable**:
   - `Select Entered` のみ → `Fire`（押した瞬間だけ）

### 3-E: DiscController / PawnController

パス: `Table > Right > DiscController` / `PawnController`

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **0** (Grab)
3. **XR Grab Interactable**:
   - `Select Entered` → `Fire`
   - `Select Exited` → `Fire`

### 3-F: SimpleSocketShape（掴める方）

パス: `Table > Top > ... > SimpleSocketShape`

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **0** (Grab)
3. **XR Grab Interactable**:
   - `Select Entered` → `Fire`
   - `Select Exited` → `Fire`

### 3-G: SimpleSocket（受け口）

パス: `Table > Top > ... > SimpleSocket`

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **3** (Snap)
3. **XR Socket Interactor**:
   - `Select Entered` → `Fire`（装着した瞬間）

### 3-H: TableHandle

パス: `TableHandle`（ルート直下）

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **0** (Grab)
3. **XR Grab Interactable**:
   - `Select Entered` → `Fire`
   - `Select Exited` → `Fire`

### 3-I: TouchPad Button 1〜9

パス: `Table > Front > Buttons > TouchPad Button 1` (〜9)

UGUI Button なので `Button.onClick` を使用。

1. Add Component → **Hapbeat > Hapbeat UnityEvent Trigger**
2. Event Map = `HandsDemoEventMap`, Entry Index = **1** (Click)
3. **Button** の `On Click ()` → `+` → 自分自身 → `HapbeatUnityEventTrigger.Fire`

## Step 4: 実機テスト

1. Quest にビルド & 実行
2. 各インタラクションで触覚が発火することを確認
3. gain 値を調整（EventMap で一括変更可）

## チェックリスト

| オブジェクト | UET | Entry | 接続先 | CT | 状態 |
|---|---|---|---|---|---|
| Cube 1 | x | 0 Grab | selectEntered/Exited | x (VelocityScaled) | |
| Cube 2 | x | 0 Grab | selectEntered/Exited | x (VelocityScaled) | |
| Cube 3 | x | 0 Grab | selectEntered/Exited | x (VelocityScaled) | |
| Cylinder | x | 0 Grab | selectEntered/Exited | - | |
| Arrow | x | 0 Grab | selectEntered/Exited | - | |
| Disc | x | 0 Grab | selectEntered/Exited | - | |
| PokeButton | x | 1 Click | selectEntered | - | |
| DiscController | x | 0 Grab | selectEntered/Exited | - | |
| PawnController | x | 0 Grab | selectEntered/Exited | - | |
| SimpleSocketShape | x | 0 Grab | selectEntered/Exited | - | |
| SimpleSocket | x | 3 Snap | selectEntered | - | |
| TableHandle | x | 0 Grab | selectEntered/Exited | - | |
| TouchPad Btn x9 | x | 1 Click | Button.onClick | - | |

UET = HapbeatUnityEventTrigger, CT = HapbeatCollisionTrigger
