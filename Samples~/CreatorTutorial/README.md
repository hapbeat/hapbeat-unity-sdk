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
- Window > Hapbeat > Event Map で全トリガーの配置先を一覧確認できます
