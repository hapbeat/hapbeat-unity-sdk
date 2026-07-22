# Changelog

Hapbeat Unity SDK の主要な変更点をまとめます。

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に、
バージョン付けは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

---

## [Unreleased]

### Added（追加）

- **Address Override（player/group の実行時上書き）** — 同一ビルドを複数 HMD に配布し、各端末を自分の Hapbeat に 1:1 で向けたいユースケース向け。
  - `HapbeatConfig` に `Addressing` セクション (`overridePlayer` / `overrideGroup`、-1 = 無効・1-99 = 強制適用) を追加。設定すると EventMap 側の target 文字列に関わらず、全ての送信 (Play/Stop/StopAll/StreamBegin) にこの player/group が強制適用される。
  - `HapbeatManager.SetAddressOverride(int player, int group, bool persist = false)` を追加。実行時に override を切り替え可能。`persist: true` で PlayerPrefs に保存し、次回起動時も復元される。
  - `HapbeatManager.OverridePlayer` / `OverrideGroup` / `EffectiveGroup` を公開プロパティとして追加（`EffectiveGroup` は CONNECT_STATUS の OLED 表示用グループを override 適用後の値で返す）。
  - `HapbeatClient.ResolveTarget(string target, int overridePlayer, int overrideGroup)` を追加（static、UnityEngine 非依存の純粋関数）。target 文字列の `player_<N>` / `group_<M>` セグメントを override 値で置換・挿入する。
  - Showcase サンプルに `AddressOverrideDemo` (Z4_Stream ゾーン) を追加。+/- ステッパーで player/group を選び Apply → `SetAddressOverride(..., persist: true)` を呼ぶ実演 UI。
  - `Tests/Runtime/ResolveTargetTests.cs` — `ResolveTarget` のユニットテストを追加。
  - Editor: Settings ウィンドウに Addressing フィールド、Manager Inspector に現在の override 状態表示、Editor Test Play (`HapbeatEditorTransport`) が config の override をミラーして再生プレビューに反映するよう対応。
  - `HapbeatManager.TryGetPersistedAddressOverride(out int, out int)` (static) / `ClearPersistedAddressOverride()` を追加。「この端末に保存された override」「実行時に有効な override」を常時表示する状態行と `Clear Saved Override` ボタンを追加し、PlayerPrefs 保存値が見えず戻せない問題を解消。
- `HapbeatManager.AddressOverrideDisabled` (`= -1`) を公開定数として追加。-1 は「その軸を override しない（EventMap entry の target をそのまま使う）」ことを意味し、target を -1 という値に書き換えるわけではない、という誤解を防ぐための named constant。
- `HapbeatManager.ApplyAddressPlaceholders(string, int, int)` (static) を追加。`appName` 内の `<p>` / `<g>` を現在の address-override player/group 番号（無効時は `-`）に置換する純粋関数。`HapbeatManager.AppName` が CONNECT_STATUS 送信直前に自動適用する。
- `HapbeatAddressOverridePanel` (Runtime) — Showcase サンプルの `AddressOverrideDemo` 実装を SDK 本体に昇格。`ScreenSpaceOverlay` / `WorldSpace`（VR コントローラー等への 3D パネル取り付け用）の 2 レイアウトに対応し、`PlayerUp` / `PlayerDown` / `GroupUp` / `GroupDown` / `Apply` を public メソッドとして公開（外部コントローラー / UnityEvent から配線可能）。`AddressOverrideDemo` はこのクラスの空派生に置き換え。
- `Tests/Runtime/AddressPlaceholderTests.cs` — `ApplyAddressPlaceholders` のユニットテストを追加。
- Runtime asmdef (`Hapbeat.Runtime.asmdef`) に `UnityEngine.UI` 参照、`package.json` に `com.unity.ugui` 依存を追加（`HapbeatAddressOverridePanel` の uGUI 利用を UPM 配布先プロジェクトで確実に解決するため）。
- 新サンプル `VRVerification`（`Samples~/VRVerification/`）— Quest 等 VR 実機での address-override 検証用最小 XR リグ。XRI に依存せず、code-defined InputAction（`<XRHMD>/centerEyePosition` 等）で HMD 姿勢をカメラに適用し、コントローラーボタンで world-space `HapbeatAddressOverridePanel` の player/group ステッパー + Apply + テスト再生（`sample-kit.sine_100hz`）を操作する。デスクトップ検証用にキーボードフォールバック（P/O, G/H, Space, T）も同アクションに追加バインド。
- **`Hapbeat > Open Runtime Status`** ウィンドウ (`HapbeatRuntimeStatusWindow`) を追加。1 画面で Address Override（保存値/実行時値 + Clear ボタン）、App Name（config テンプレートとプレースホルダー解決後に OLED へ送られる実文字列のプレビュー + 文字数）、Connection（Play 中のみ: 接続状態 / broadcast・unicast / ポート / AliveDeviceCount / 発見済みデバイス一覧）を常時確認できる。`HapbeatManagerEditor` の Address Override ボックスはこのウィンドウを開くボタン付きのコンパクト表示に整理し、両者の描画ロジックは新設の `HapbeatAddressOverrideStatusGUI`（internal 共有ヘルパー）に統合して重複を排除。

### Fixed（修正）

- **`HapbeatAddressOverridePanel` のネスト Canvas バグ** — 自身が別の Canvas（例: 既存のスクリーンスペース HUD）の子として配置されている場合、生成した Canvas も入れ子になり、`RenderMode` やスクリーンアンカー設定が Unity の仕様で無視され、意図しない位置（親パネル基準）に描画されていた問題を修正。`Build()` が祖先 Canvas の有無を検出し、検出時は自分の Canvas をシーンルートへ退避して独立させる。退避後も `OnEnable`/`OnDisable`/`OnDestroy` でこの Canvas の表示・生存をコンポーネント自身と同期する。
  - Showcase サンプルの `AddressOverrideDemo`（Z4_Stream ゾーン）が実際にこの構成（`StreamPanelHud` → `StreamCanvas` の子として配置）になっていたため、`Samples~/Showcase/Showcase.unity` を修正し、`Z4_Stream` 直下（`StreamCanvas` の外）の独立 GameObject に配線し直した。

### Changed（変更）

- Address Override の状態可視化を Settings ウィンドウから `HapbeatManagerEditor`（Manager インスペクタ）の "Address Override (this device)" ボックスへ移設。Edit / Play 両モードで常時表示し、レイアウトシフトを避けるため状態行は常に描画（テキストのみ切り替え）。
- Settings ウィンドウで `enableLogging` に誤って付いていた "Verbose Log" ラベルを "Enable Logging" に修正し、`verboseLogging`（PONG/keep-alive 等の詳細ログ）用の PropertyField を新規追加。

### Removed（削除）

- `HapbeatConfig.group` / `overridePlayer` / `overrideGroup` / `discoveryTimeoutMs` を削除。`group` と `discoveryTimeoutMs` はどこからも読まれない dead field、`overridePlayer` / `overrideGroup` は PlayerPrefs 経由の実行時 override（`SetAddressOverride`）に一本化したため、config 側の既定値という概念自体を廃止した。
- `HapbeatManager.DefaultGroup` / `EffectiveGroup` を削除。CONNECT_STATUS (0x20) の group バイトはデバイス側で保存後一切読まれないレガシーフィールドと判明したため、private ヘルパー経由で override group（有効時）か 0 を送るだけの実装に簡素化。

---

## [0.2.1] - 2026-05-30

v0.2.0 直後に発覚した sample アップグレード時の compile error と、EventMap の使い勝手改善をまとめた hotfix リリース。

### Fixed（修正）

- **Sample namespace を folder 名に追従** (`Hapbeat.Samples.Tutorial` → `Hapbeat.Samples.Showcase`)
  v0.2.0 で `Tutorial` → `Showcase` に folder rename したが、namespace が legacy のまま残っていた。
  古いバージョン (v0.1.x) の Tutorial sample を import 済みの状態で v0.2.0 の Showcase sample を import すると、同一 namespace 下で同名 class が二重定義になり compile error が発生していた問題を解消。
  あわせて Showcase の scene / prefab / animator 内に残っていた legacy namespace 参照 (`m_TargetAssemblyTypeName` 等) も修正し、UnityEvent 配線の解決ずれを解消。
- **`HierarchySeparator` を復活** — v0.2.0 の cleanup で巻き込み削除されていた、Hierarchy 上の区切り装飾 (`-------- ... --------`) を Showcase namespace + Unity 6 API (`EntityIdToObject`) で再追加。

### Added（追加）

- **`Hapbeat > Diagnostics > Check Sample Versions`** — `Assets/Samples/Hapbeat SDK/` 配下に複数バージョンの sample が同時 import されている場合に Console 警告を出す診断ツール。Editor 起動時に自動 scan + 手動再実行可能。
- **EventMap Window: Bulk Edit モーダル** — toolbar の `Bulk Edit` から起動。Mode / Gain / Loop / Target / Delay Offset / Manifest Override / Notes を **Override チェックで選択的に**一括編集できる。最上部の `Select all` トグル (デフォルト ON) で全 entry または Table 選択行を対象に切替。変更は 1 つの Undo group にまとまる。
- **EventMap Table view: Ctrl/Cmd+A で全行選択** — inline text field 編集中はテキスト全選択を奪わない。
- Table view の Target セルクリック / 右クリック `Set Target...` による target 一括適用 (player / position / group) も従来通り利用可能。

### Changed（変更）

- **Unity 6 (6000.0) 以上を要件化** — `package.json` の `unity` を `2021.3` → `6000.0` に更新。SDK 本体が既に Unity 6 の `EntityIdToObject` を使用しているため、実態に合わせた是正。
- **EventMap の mode 選択から `LIVE` を削除** — Table / Inspector とも `FIRE` / `CLIP` の 2 択に統一 (`LIVE` は廃止済みで UI ラベルのみ残っていた)。
- **Settings の Haptic Delay 表示を ms 単位に** — スライダーを `Haptic Delay (ms)` (0–500 ms) 表記に変更。内部ストレージは秒のまま (プロトコル / per-entry delay と一貫)。

### Migration（移行）

**v0.2.0 → v0.2.1 で Sample を再 import する場合は古いバージョンの folder を削除してください**

Unity の UPM Samples は package 更新時に古い import folder (`Assets/Samples/Hapbeat SDK/<旧バージョン>/`) を自動削除しません。残っていると同一クラスの二重定義で compile error になります。Project ウィンドウで該当 folder を削除してから新しい Sample を import してください。SDK が起動時に自動 scan して警告を出します。

---

## [0.2.0] - 2026-05-26

v0.1.0 以降に蓄積した API 整理・サンプル再編・遅延補正・Editor UX 改修をまとめたリリース。
**Pre-1.0 のため Breaking change を含みます** — 詳細は下記を参照。

### Breaking changes（破壊的変更）

- **`HapbeatAnimatorTrigger` を廃止し `HapbeatStateBehaviour` に置換**
  Animator state に直接 attach する StateMachineBehaviour 方式に変更。
  State Enter/Exit に別 entry を bind 可能、`Required Previous State` で A→B 限定発火、
  Looping StreamClip は OnStateExit で自動 Stop。**旧 trigger は移行コードなしで削除**。
- **`HapbeatEvent` + `StandardCategories` 削除**
  v0.1 期に obsolete 化していた legacy component と category 列挙を完全削除。
- **`_entryIndex` legacy fallback 全廃**
  Trigger → EventMap 参照は **stable GUID (`entry.id`) only** に統一。
  古い `_entryIndex` ベースのシリアライズデータは再 pick が必要。
- **`HapbeatBridge` subclass パターンの非推奨化**
  Trigger-first / EventMap-first 設計を標準とし、Bridge subclass は optional に。
  抽象クラス自体は API 互換のため残置。
- **サンプル再編: `Tutorial` → `Showcase` rename**
  UPM Sample import の表示名・フォルダ名が変わります。
  旧 `Tutorial` を import 済みのプロジェクトは、新規に `Showcase` を import し直してください。

### Added（追加）

**Latency compensation**
- `HapbeatConfig.hapticDelaySeconds` — グローバル遅延補正 (映像/音声に対して触覚を遅延発火)。
- `HapbeatEventEntry.delayOffsetSeconds` — エントリ単位の追加オフセット (Inspector で編集)。
- Play-mode 中の `hapticDelaySeconds` 変更で **pending coroutine を自動 flush** (即時反映)。
- `HapbeatStateBehaviour` 経由の発火も含む全 fire 経路で delay を honor。

**Trigger 機能拡張**
- `HapbeatSequenceTrigger._stopShotDelay` — On Stop one-shot を loop stop から遅延発火 (default 0.05s)。
  loop→shot の packet burst を抑制し、shot 強度を安定化。
- `HapbeatTickEmitter` dual mode —
  `AbsolutePosition` (位置基準) / `AccumulatedMotion` (累積移動量基準) を選択可能。
- Trigger の **binding pre-seed が child / parent GameObject 上の binding も検出** (旧: Self のみ)。

**EventMap Window**
- Wiring セクションに **HapbeatStateBehaviour (Animator state)** を表示。
- Wiring に **script-driven 参照** を表示 (`SerializeField` の string heuristic + `[HideInInspector]` field も拾う)。
- Script Wiring scan が `entry.id` (stable GUID) 検出にも対応。
- **Verbose Log 一括 off メニュー** 追加。

**Manifest schema 2.0.0**
- 2-bucket layout (`events` + `stream_events`) の reader を実装。
- bare filename + mode-aware lookup により、Studio 側 multi-mode 出力と整合。

**Stream**
- ステレオ pan のデフォルトを **passthrough** (equal-power → linear balance) に変更。
  中央 (pan=0) で √½ 減衰していた問題を解消。
- `HapbeatStreamPlayback.GainMultiplier` / `Pan` を再生中にリアルタイム push 可能に。
- gain modulation 計算式を `ApplyGainModulation(float)` に集約。

**Editor / Menu**
- メニューを 4 セクション構成 (**Window / Create / Tools / Developer**) に再編。flat 構成。
- Window 系メニューを `Open ...` prefix で統一 (Event Map → Batch Setup → Settings の順)。
- `Hapbeat → Create → Initial Scene Setup` を新設 (Manager + Config + EventMap の最小セットを自動生成)。
- `HapbeatManager` Inspector の Test 操作を EventMap-style に刷新 (Gain semantics を EventMap と揃える)。
- **Maintainer Sync の wipe-then-rebuild 化** — `Samples~/<sample>/` を build artifact として全消し再構築。
  dest-only orphan が次回 sync 時に必ず消えるよう保証。

**Samples**
- **`Showcase` サンプル新設** (旧 Tutorial の後継) — Z1 Bowling / Z2 Door / Z3 Pickup / Z4 Stream Console / Z5 Charge Shot の 5 ゾーンで SDK 全機能を体験。WAV を `Kit/showcase-kit/` に同梱 (Sample import 直後に EventMap が解決)。
- `BasicExample` をフラット構成に再編。`BasicExample.unity` + `BasicExampleEventMap.asset` + `Kit/basic-exam-kit/` のみ。
- 3rd-party 資産の credits を per-file 化 (`Samples~/Showcase/THIRD_PARTY_NOTICES.md`)。CC0 / CC BY 3.0 を分離記載。
- XR helpers サンプルは引き続き opt-in (`Samples~/XriHelpers/`)。

### Changed（変更）

- **UI 文字列を英語に統一** — Tooltip / Label / Dialog / HelpBox / Debug.Log の user-facing 文字列を全 23 ファイルで英語化。日本語は portal docs に集約。
- ドキュメントを **portal (devtools.hapbeat.com) に一本化** — `docs~/` を削除し各サブ repo の docs は portal が一元参照。

### Fixed（修正）

- `HapbeatUnityEventTrigger.FireWithGain` が `_entryIndex` 参照のままになっていた問題を `ResolveEntry()` 経由に修正。
- Manifest schema 2.0.0 環境で send-ahead lead が指定値を超過するケースを `WaitForSecondsRealtime` で固定。
- Sample deploy 時に destination の親フォルダが未生成だと `AssetDatabase.CopyAsset` が silent fail する問題に対し、事前に `EnsureAssetFolder` を実行。

### Removed（削除）

- `Editor/HapbeatSampleDeployment.cs` — dead code (旧 `BasicExampleSceneBuilder` と `HapbeatSampleImportDeployer` 削除により呼び出し元なし)。
- `Editor/HapbeatSampleImportDeployer.cs` — 「Deploy Imported Sample」メニュー (非標準 UX)。Sample import 後はユーザー自身が `Assets/` 配下に手動コピー。
- `docs~/` ディレクトリ — portal 集約に伴い撤去。

### Migration notes（移行メモ）

- **`HapbeatAnimatorTrigger` を使っていた場合**: Animator Controller の対象 state を選択 → Add Behaviour → `HapbeatStateBehaviour` を attach → EventMap entry を pick。
- **Tutorial サンプルを編集していた場合**: 編集内容は `Assets/` 配下にコピー済みのはず。再 import で Showcase が降ってくるが、旧 Tutorial 編集物は上書きされません (UPM の Sample import は新規パスに展開するため)。
- **古い EventMap entry の参照が外れた場合**: Trigger 側で entry を pick し直してください (`_entryIndex` 廃止のため)。

[0.2.0]: https://github.com/Hapbeat/hapbeat-unity-sdk/releases/tag/v0.2.0

---

## [0.1.0] - 2026-05-11

Initial public release.

### Added（追加）

**Core runtime**
- `HapbeatManager` — シングルトン。Wi-Fi UDP broadcast で Hapbeat デバイスと通信
- `HapbeatBridge` — `Play / PlayScaled / PlayWithCurve / Stop` を提供するサブクラスベース
- `HapbeatClient` — UDP 送受信・PING/PONG・mDNS 自動検出
- `HapbeatDiscovery` — LAN 上の Hapbeat デバイスを mDNS で自動発見
- `HapbeatConfig` — Group ID・ポート・Bridge 設定を ScriptableObject で管理

**Trigger コンポーネント**
- `HapbeatCollisionTrigger` — 物理衝突 / Trigger Enter|Exit に連動。速度スケールゲイン・AnimationCurve 対応
- `HapbeatAnimatorTrigger` — Animator パラメータ変化 (Bool / Float / Int) を検知して発火
- `HapbeatUnityEventTrigger` — UnityEvent の `Fire()` メソッドで任意タイミングに発火
- `HapbeatSequenceTrigger` — grab / hold / release を 1 コンポーネントで管理
- `HapbeatTickEmitter` — 連続値 (Slider・ScrollRect 等) の変化量に応じてスナップ触覚を生成
- `HapbeatParameterBinding` — Transform / Rigidbody → gain / pan をリアルタイムマッピング
- `HapbeatKeyDispatcher` — キー → UnityEvent のマッピング。Input System Package 完全対応

**EventMap**
- `HapbeatEventMap` ScriptableObject — Event ID・gain・mode (FIRE / CLIP) を一元管理
- `HapbeatEventEntry` — manifest.intensity を乗算した effective gain を計算
- `EventMap Window` (`Hapbeat → Event Map`) — 全エントリと配線を GUI で一覧管理、Wiring 逆引きスキャン、Play テスト

**App identity (デバイスディスプレイ表示)**
- `HapbeatConfig.appName` — Hapbeat デバイスのディスプレイに表示するクライアントアプリ名 (max 16 文字、空欄で `Application.productName` 自動使用)
- `CONNECT_STATUS` の周期送信 (Play 中) + 接続成立時 / 終了時の通知パケット送信

**ストリーミング**
- StreamClip モード — WAV を chunk 送信し、ParameterBinding で動的ゲイン・パン制御
- `streamSendAheadSeconds` で送信先行バッファを調整

**UI / Editor**
- `HapbeatStatusOverlay` — 接続状態・RTT を Canvas に表示するデバッグ UI
- `HapbeatEventLogger` — Hapbeat 系ログをフィルタしてファイル保存
- `HapbeatEventMapEditor` — Play-mode Snapshot/Restore、ポータビリティ確認
- `HapbeatSettingsWindow` — 接続設定 / アプリ名 / Bridge / Ping interval 等を一元編集
- Setup メニュー (`Hapbeat → Setup`) — HapbeatSDK フォルダ自動生成
- Build Samples メニュー (`Hapbeat → Build Samples`) — Basic / Tutorial の Scene + EventMap + Kit を自動生成 (Tutorial は With / Without 2 シーン同時生成)
- Debug メニュー — Event Logger 配線 / ログ録画 / Logs フォルダ参照などのユーザー向け診断ツール群

**サンプル**
- `BasicExample` — キーボード操作で SDK 基本機能を確認する最小構成
- `Tutorial` — 5 ゾーン (Bowling / Door / Pickup / Stream Console / Target Range) で SDK 全機能を体験。XR デバイス不要
- `XriHelpers` — `HapbeatXRGrabFilter` / `HapbeatXRSocketFilter` (XRI opt-in)

**XR 向け**
- XR Helpers sample で XRI grab / socket イベントを Hapbeat に橋渡し
- Quest 3 / Quest 3s 動作確認済み

**ドキュメント**
- [installation](docs~/installation.md) — UPM Git URL 導線
- [getting-started](docs~/getting-started.md) / [triggers](docs~/triggers.md) / [event-map](docs~/event-map.md) / [parameter-binding](docs~/parameter-binding.md) / [streaming](docs~/streaming.md) — 機能別解説
- [tutorial/](docs~/tutorial/) — Tutorial サンプルの walkthrough (Plain → With 構築手順)
- [editor-menus](docs~/editor-menus.md) — Hapbeat メニュー全項目の使い方逆引き
- [ai-assisted-workflow](docs~/ai-assisted-workflow.md) — Claude Code 等で既存シーンに触覚を後付けする 4 ステップ + コピペプロンプト集
- [multi-app](docs~/multi-app.md) — 複数アプリ共存時の運用指針 (LAN 分離 / group ID 切り分け)

[0.1.0]: https://github.com/Hapbeat/hapbeat-unity-sdk/releases/tag/v0.1.0
