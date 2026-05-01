# Instructions: PawnController — drag/scratch 触覚の試作

**発行日:** 2026-04-22
**起票:** session-unity-sdk-eventmap-and-stream-follow-ups (2026-04-21 持ち越し)
**優先度:** 後回し（PokeButton volume 解決後）

## 背景

オブジェクトを**持ちながら動かす**ときに「擦れる感覚」の触覚フィードバックを実現したい。
`VelocityMagnitude` を Volume または BridgeGain にマッピングし、noise 系 WAV を StreamClip で鳴らしながら変調する構成を想定。

## タスク

### 1. VelocityMagnitude binding の end-to-end 確認

- `BindingSourceProperty.VelocityMagnitude` が Rigidbody または手の velocity を拾えているかテスト
- 既存の HapbeatParameterBinding にそのプロパティが定義されているか確認
- 手持ちオブジェクトに binding をアタッチして Inspector の live preview でリアルタイム値を確認

### 2. サンプルシーンのセットアップ

- 平面（テーブル or 床）の上でドラッグ移動できるオブジェクトをシーンに配置
- XRI の Grab + Translation 連動で持ち上げずに擦れる状態を作る

### 3. noise 系 WAV の用意

- ブラウンノイズまたはホワイトノイズの短いループ素材（1〜2秒）を Kit に登録
- StreamClip + loop=true で鳴らし続け、Volume で変調する

### 4. RelativeVelocity の検討（任意）

単純な velocity magnitude だと「素早く動かせば強い」だが、接触面に対する相対速度が欲しい場合は
`RelativeVelocity` ソース（別 Transform との速度差）が必要になる可能性がある。
必要であれば `HapbeatParameterBinding` にソース種別を追加する。

### 5. サンプルシーンへの統合

PlayerDemo または新しいサンプルシーンに追加し、体験者が直感的に理解できるように配置。

## 完了条件

- [ ] オブジェクトをドラッグ中に haptic が鳴り、速く動かすと強くなる
- [ ] 止まると即止まる（残響がない）
- [ ] Batch Setup でセットアップできること（手動 wire 不要）

## 依存関係

- **Required**: PokeButton volume 問題の解決（同じ binding 経路を使うため）
- **Downstream**: なし
