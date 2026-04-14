# Hapbeat Player Demo

Hapbeat の3種のフィードバックを VR で体験するデモシーンです。

## 前提条件

- Unity 2022.3 LTS 以上
- XR Interaction Toolkit 3.x（Starter Assets サンプルをインポート済み）
- Hapbeat SDK がインポート済み

## ゾーン構成

### Zone A: 能動フィードバック（Active）

プレイヤーが起こしたアクションへの反動を体験します。

- **パンチングバッグ**: 殴ると速度に応じた振動。物理復元付き
- **シューティングレンジ**: 銃を掴んで射撃 → 反動。ターゲット命中でも振動
- **ドラムステーション**: 4つのパッドを叩いて演奏。速度連動ゲイン

### Zone B: 受動フィードバック（Passive）

環境からプレイヤーへの作用を体験します。

- **レインルーム**: 雨の中を歩く。進入/退出で触覚ループ開始/停止
- **爆発フィールド**: ランダム間隔で爆発。距離に応じて振動が減衰
- **ドローン防衛**: ドローンから弾を撃たれる。被弾は回避困難な設計

### Zone C: 操作系フィードバック（UI）

ハンドトラッキング環境でコントローラー振動の代替を体験します。

- **ポークボタン**: 指で押すと触覚フィードバック
- **グラブオブジェクト**: 掴む/離す で異なる振動。投げて衝突もフィードバック

## 触覚 ON/OFF

手首付近のトグルパネルで Global / ゾーン別の ON/OFF が切り替えられます。
ON で体験 → OFF に切替 → 差を実感、という順序が最も効果的です。

## セットアップ

1. Package Manager > Hapbeat SDK > Samples > Player Demo > Import
2. `PlayerDemo.unity` を開く
3. XR Origin が含まれています（Starter Assets ベース）
4. Hapbeat デバイスを同じ Wi-Fi に接続
5. Play

## EventMap

`EventMaps/PlayerDemoEventMap.asset` に全イベント定義が含まれています。
Unity Editor で `Create > Hapbeat > Event Map` から作成してください。

| Event ID | 用途 | Gain | Zone |
|---|---|---|---|
| impact.punch | パンチ | VelocityScaled | A |
| action.shoot | 射撃反動 | 0.7 | A |
| impact.target-hit | ターゲット命中 | 0.5 | A |
| impact.drum | ドラム | VelocityScaled | A |
| ambient.rain | 雨 | 0.2 (loop) | B |
| impact.explosion | 爆発 | 距離減衰 | B |
| impact.hit-received | 被弾 | 0.8 | B |
| ui.button-press | ボタン押下 | 0.3 | C |
| ui.grab | グラブ | 0.4 | C |
| ui.release | リリース | 0.2 | C |
