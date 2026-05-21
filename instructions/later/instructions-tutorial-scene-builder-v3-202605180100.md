# Tutorial Scene Builder v3 (Trigger-first 構成での一括生成)

**作成日**: 2026-05-18
**ステータス**: Tutorial v3 が手動構築で完成・検証された後に着手する。
**動機**: 旧 `TutorialSceneBuilder.cs` は TutorialBridge / TargetPickerUI を前提にしていたため、v3 再設計 (Trigger-first / TutorialBridge 撤廃) で削除済み。新規ユーザーが Tutorial を import した時に Tutorial.unity を 1 クリック生成できる builder が欠落している。

## 再生成の目的

- Package Manager から Tutorial sample を import → 1 メニューで `Assets/HapbeatSDK/SDK_Samples/Tutorial/Scenes/Tutorial.unity` を作る
- ユーザーの編集を壊さない (既存 deploy あれば skip + 既存 scene を開くだけ)
- v3 アーキテクチャに準拠: HapbeatBridge subclass を作らず、各 Zone を Trigger 直接 wire

## 含めるべき要素

| Zone | 配置物 | Haptic コンポーネント |
|---|---|---|
| Z1 Bowling | Lane / Ball / Pins / SpawnPose | 各 Pin に `HapbeatCollisionTrigger` (entry: pin_hit) |
| Z2 Door | Door cube + Animator (IsOpen bool) | `HapbeatAnimatorTrigger` × 2 (door_open / door_close) |
| Z3 Fishing | Rod / RodTip / HookRest / LineRenderer / FishingObject / RestPose | `HapbeatSequenceTrigger` (grab_loop) + `HapbeatParameterBinding` |
| Z4 Stream | HUD StreamPanel (Slider × 2 + Text) | `HapbeatUnityEventTrigger` (stream_demo, StreamClip loop) + `HapbeatTickEmitter` × 2 |
| Z5 Charge | TargetBoard + Muzzle + ChargeBar UI | `HapbeatUnityEventTrigger` (charge_release) + `HapbeatUnityEventTrigger` (target_hit) |
| Global | [Hapbeat Event Router] (HapbeatManager 単独) + HUD canvas | `HapbeatKeyDispatcher` + `HapbeatActionHelper` + `HapbeatUnityEventTrigger` (manual_fire) |

## script wire 一覧

- `BallLauncher` (haptic 関与なし): _ball / _spawnPose / _aimReference (= Player Camera) / _pins
- `DoorController` (haptic 関与なし): Animator
- `FishingController`: _sequence / _object / _holdAnchor (Camera 子 HoldAnchor) / _restPose / _line / _rodTip / _hookRest
- `StreamDemoController`: _trigger (HapbeatUnityEventTrigger entry stream_demo) / _gainSlider / _panSlider / _statusText
- `ChargeShooter`: _trigger (HapbeatUnityEventTrigger entry charge_release) / _muzzle (or Camera.main) / _projectilePrefab (null OK, Sphere fallback あり) / _chargeBar
- `TargetReceiver`: _trigger (entry target_hit), _flashRenderer
- `ZoneSwitcher`: _zones (5 entry: Bowling/Door/Fishing/Stream/Charge), _player (Player Transform)
- `HudGuide`: _keysText / _descText / _connectionStatusText / _zoneSwitcher
- `GlobalHotkeys`: _pingResultText (Pong 表示のみ)
- `SimpleFPSController`: _cameraPivot (Player Camera)

## NOT 含めるもの (廃止確定)

- `TutorialBridge.cs` subclass
- `TargetPickerUI.cs` + HUD の Target Picker toggle group
- `PickupBoxController.cs` (Fishing で置き換え済)
- `_clips: AudioClip[]` 配列を持つ StreamDemoController (EventMap 経由に統一)
- Bridge.PlayByName / PlayScaledByName 系の string lookup ヘルパー
- BallLauncher.Launch() からの script Bridge.PlayScaled 呼出 (架空デモ)

## 着手前提

- [ ] Tutorial v3 が手動 wiring で完成
- [ ] 5 zone すべて Play で動作確認済み (haptic 含む)
- [ ] without-haptic 版生成スキーム (instructions-later) もある程度方針が見えている
- [ ] EventMap entry 一覧が確定 (pin_hit / door_open / door_close / grab_loop / stream_demo / slider_tick / charge_release / target_hit / manual_fire / burst (廃止候補))

## 参考

- HandDemoScene (`M:/GameEngine/Unity/Projects/HapbeatSDKSamples/Assets/Scenes/HapbeatHandsDemoScene.unity`) が "Trigger-first idiom" の良い見本
- 旧 `TutorialSceneBuilder.cs` (削除済) は git history (`Samples~/Tutorial/Editor/`) で参照可
- `TutorialAddBuilders.cs` が今は唯一の Editor 自動化 (HUD Stream Panel のみ add 可能)。これを extend する形でも良い
