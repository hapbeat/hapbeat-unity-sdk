# WIP: サンプルシーンの修正記録

プロジェクト（`M:\GameEngine\Unity\Projects\HapbeatSDKSamples`）側で直接修正し、
動作確認後にシーンビルダー（`Samples~/Editor/`）にまとめて反映する。

## 修正済み

### 1. Canvas の GraphicRaycaster → TrackedDeviceGraphicRaycaster
- **問題**: WorldSpace Canvas に通常の GraphicRaycaster が付いており、VR コントローラーのレイで操作できない
- **修正**: `Assets/Editor/FixCanvasRaycasters.cs` を作成。メニュー `Hapbeat > Fix > Replace GraphicRaycaster with TrackedDevice` で一括修正
- **対象**: 全 PlayerDemo シーンの WorldSpace Canvas
- **ビルダー反映**: PlayerDemoSceneBuilder の `CreateMenuPanel()` で `TrackedDeviceGraphicRaycaster` を使うよう修正済み（未テスト）

### 2. EventSystem に XRUIInputModule が必要
- **問題**: デフォルトの InputSystemUIInputModule では VR コントローラーの UI 操作が動かない
- **修正**: `Assets/Editor/FixXRUIInteraction.cs` で XRUIInputModule に差し替え
- **ビルダー反映**: NewScene() の EventSystem 生成部分で XRUIInputModule を使うよう修正が必要

### 3. Build Settings にシーン登録が必要（Unity 6）
- **問題**: Unity 6 では Editor Play でも SceneManager.LoadScene() に Build Settings 登録が必要
- **修正**: `Assets/Editor/RegisterPlayerDemoScenes.cs` で一括登録
- **ビルダー反映**: BuildAll() の完了ダイアログに手順を明記。または自動登録を追加

### 4. Zone A 全面再構築（動かないデモ方式）
- 体験者は一歩も動かない。メニューで選択→道具がコントローラーにアタッチ + 対象が目の前に出現
- PunchingBag: 吊り下げ式に改修（振り子物理 + ロープ可視化）
- ShootingRange: コントローラー入力で射撃 + 弾道ライン表示
- DrumStick: コントローラーから前方に伸びる向き（Euler(90,0,0) → Euler(60,0,0) で前傾）
- 全スクリプトに AudioClip 自動接続 + コリジョンデバッグログ
- メニュー: 左手追従（FollowController）+ TrackedDeviceGraphicRaycaster + XRUIInputModule
- Clear ボタン: メニュー末尾に配置（誤爆しにくい位置）

### 5. パススルー: Meta XR SDK が必要
- Quest Link でのパススルーはカメラ背景透明だけでは不可
- Meta XR SDK (com.meta.xr.sdk.all) の OVRManager + OVRPassthroughLayer が必要
- XRI と共存可能
- SetupPassthrough.cs でインストールガイド + 自動設定を提供

## 未確認・要修正

- SceneMenu のボタンでシーン切り替えが動作するか
- Zone A-D の各インタラクションが VR で動作するか
- Audio 自動接続が正しく動いているか
- Zone D の Activation Zone が動作するか

_最終更新: 2026-04-14_
