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
- 新サンプル `VRConfigExample`（`Samples~/VRConfigExample/`）— Quest 等 VR 実機での address-override 検証用最小 XR リグ。XRI に依存せず、`TrackedPoseDriver`（`UnityEngine.InputSystem.XR`、実行時に AddComponent + コードで構成）で HMD 姿勢をカメラに適用する。全操作は world-space `HapbeatAddressOverridePanel` 内の全 GUI ボタン（Player -/+、Group -/+、Play、Apply、Exit）に統合され、コントローラーはスティック傾き（デッドゾーン + エッジ検出 + 0.4 秒リピート）で 2D フォーカスグリッドを移動、trigger/A(X)/B(Y)（いずれの手・いずれのボタンでも同一）でフォーカス中のボタンを押下する 2 アクションのみ（Keyboard: 矢印キー / Enter・Space）。スティック押込 = recenter（起動時も実行、視界中央・カメラ高さ・正面 1.5m・yaw 追従）、左手 Menu / Keyboard Esc = Exit 直結、は維持。テスト再生（`VRConfigExampleEventMap` の CLIP エントリ、100Hz sine、Kit 不要）は Play ボタン経由。
- **`Hapbeat > Open Runtime Status`** ウィンドウ (`HapbeatRuntimeStatusWindow`) を追加。1 画面で Address Override（保存値/実行時値 + Clear ボタン）、App Name（config テンプレートとプレースホルダー解決後に OLED へ送られる実文字列のプレビュー + 文字数）、Connection（Play 中のみ: 接続状態 / broadcast・unicast / ポート / AliveDeviceCount / 発見済みデバイス一覧）を常時確認できる。`HapbeatManagerEditor` の Address Override ボックスはこのウィンドウを開くボタン付きのコンパクト表示に整理し、両者の描画ロジックは新設の `HapbeatAddressOverrideStatusGUI`（internal 共有ヘルパー）に統合して重複を排除。
- `HapbeatRuntimeStatusWindow` の Address Override 節に、Player/Group を直接編集して "Save to this device" で保存できる行を追加。Play Mode に入らなくても（`PlayerPrefs` に直接書き込む）、Play Mode 中は `HapbeatManager.SetAddressOverride` 経由で保存できる。
- `HapbeatAddressOverridePanel` に 2D フォーカスナビゲーショングリッドを追加（`RegisterFocusable(Vector2Int, Button)` / `MoveFocus(Vector2Int)` / `ActivateFocused()` を public 化）。パネルが internally 構築する全ボタン（Player -/+、Group -/+、Play、Apply、Exit）をグリッド座標に登録し、外部コントローラーが登録した任意のボタンも同じグリッドに参加できる。最初に登録されたボタンが起動時から即座にフォーカスされ、以後 `MoveFocus` で 1 マスずつ移動、`ActivateFocused` でフォーカス中のボタンを押下する。旧 `SetFocusedField(HapbeatAddressOverrideFocusField)`（Player/Group の行単位ハイライト）は廃止し、ボタン単位のフォーカス表示に置き換え。
- `HapbeatAddressOverridePanel` に Play（テスト再生）/ Exit（シーン遷移）ボタンを追加し、Apply と合わせて 1 パネルに統合。パネル自体はテストトリガーやシーンの概念を持たず、`OnPlayRequested` / `OnExitRequested`（`event Action`）を外部コントローラーが購読して実際の動作を注入する。
- ボタン活性化時の視覚フィードバックを、Apply 専用の ~0.3 秒暗色フラッシュから、登録済みの全ボタン共通の ~0.2 秒明色（白寄り）フラッシュに変更 — パネルの暗い背景に埋もれず視認できる。クリック経由・`ActivateFocused` 経由のどちらで押下されても同じフィードバックが出る。
- `Samples~/VRConfigExample/sample-kit-manifest.json` を追加（`BasicExample` の `basic-exam-kit-manifest.json` と同じ運用: `manifestOverride` 経由の Editor 上 intensity プレビュー専用、実機への Kit インストールは不要）。`VRConfigExampleEventMap` の StreamClip エントリ（`sample-kit.sine_100hz`）に配線し、intensity は contracts 標準の `sample-kit`（`fixtures/sample-kit-manifest.json`）と同じ `1.0` を踏襲。
- **`streamUnicast`（StreamClip のユニキャスト送信）** — Wi-Fi broadcast の DTIM 省電力バッチングによる周期的な触覚途切れ（~170ms 前後で観測、送信側計測ではジッタなしを確認済み）の検証兼対処。`HapbeatConfig.streamUnicast`（既定 `true`）を追加。broadcast モードで PONG 済みの既知デバイスが 1 台以上いる場合、`STREAM_BEGIN`/`STREAM_DATA`/`STREAM_END` をそれらの IP へ直接ユニキャスト送信する（各デバイスへの複製送信。target 文字列によるデバイス側フィルタリングは変わらない）。既知デバイスが 0 台、または Bridge モードでは自動的に broadcast へフォールバックする。`Play`/`Stop`/`StopAll` は対象外で常に broadcast のまま。
  - `HapbeatClient.SetStreamUnicastTargets(IReadOnlyCollection<IPAddress>)` を追加。ストリームセッション開始時（`HapbeatManager.StreamAudioClip` の新規セッション時）に一度だけ既知デバイス IP をスナップショットし、以後は背景ミキサースレッドからロックフリーで参照する（セッション途中で新規に PONG が返ってきたデバイスは次セッションから対象）。
  - Settings ウィンドウ（`Hapbeat > Open Settings`）に `Stream Unicast` トグルを追加。

### Fixed（修正）

- **`HapbeatClient.ResolveTarget` の group override 挿入位置バグ** — firmware/spec の target 照合は位置ベース（i 番目セグメント同士のみ比較、`*` は 1 セグメント消費）。既存の `group_` セグメントが無い target に対し、単純に末尾へ `group_<M>` を追加していたため、player/position スロットを省略した target（`""` / `"*"` / `"player_3"` 等）では group が本来の position スロットにずれ込み、firmware 側で絶対に一致しなくなっていた（触覚が一切発火しない）。position セグメントが無い場合は player スロット直後に `"*"`（position プレースホルダー）を補ってから group を挿入するよう修正（例: `"" → "*/*/group_2"`、`"player_3" → "player_3/*/group_2"`）。`pos_` セグメントが既にある target は従来どおりその直後に挿入（挙動不変）。
- **`HapbeatAddressOverridePanel` のネスト Canvas バグ** — 自身が別の Canvas（例: 既存のスクリーンスペース HUD）の子として配置されている場合、生成した Canvas も入れ子になり、`RenderMode` やスクリーンアンカー設定が Unity の仕様で無視され、意図しない位置（親パネル基準）に描画されていた問題を修正。`Build()` が祖先 Canvas の有無を検出し、検出時は自分の Canvas をシーンルートへ退避して独立させる。退避後も `OnEnable`/`OnDisable`/`OnDestroy` でこの Canvas の表示・生存をコンポーネント自身と同期する。
  - Showcase サンプルの `AddressOverrideDemo`（Z4_Stream ゾーン）が実際にこの構成（`StreamPanelHud` → `StreamCanvas` の子として配置）になっていたため、`Samples~/Showcase/Showcase.unity` を修正し、`Z4_Stream` 直下（`StreamCanvas` の外）の独立 GameObject に配線し直した。
- `HapbeatAddressOverridePanel` のパネルサイズを大幅圧縮 (340×190 → 300×180px 相当)。ScreenSpaceOverlay で左上のガイドテキストと視覚的に被っていたレイアウトを、Player/Group ステッパーを左 2 行・Apply ボタンをその右に 2 行分の高さの正方形に近いボタンとして配置し直すことで解消。ステータス行も「編集中/適用済み」の 2 行常時表示から、編集中の解決プレビュー 1 行（例: `player_1/pos_chest → player_3/pos_chest`）に縮小し、行の出し入れなしで解決結果のみ差し替える（workspace レイアウトシフト禁止ルール準拠）。
- **`HapbeatAddressOverridePanel` の Showcase Z4 マウス入力ロック調査 + 対策** — override 値を変更した以後、同ゾーンの他 UI（Gain/Pan スライダー等）がマウス操作を受け付けなくなる実機不具合の調査。`BuildDiffHighlightedRichText` / `HapbeatClient.ResolveTarget` の全分岐を境界値まで机上実行し、到達可能な入力では index 例外が発生しないことを確認（該当なし）。パネルは top-center 300×180px・Z4 の Gain/Pan スライダーは bottom-center 460×180px で画面上重ならないことも確認（単純な raycast 重なりではない）。断定的な再現には Unity Editor 実行が必要なため、以下 2 点を確定的な改善として適用: (1) 背景 Image・タイトル/ラベル/値/ステータスの各 Text を `raycastTarget = false` に変更（`CreateText` のデフォルトを false 化）— 操作対象である +/-/Apply の Button 自身の Image 以外は raycast を一切奪わないようにするハードニング。(2) +/-/Apply クリック後に `EventSystem.SetSelectedGameObject(null)` で選択状態を明示的に解除する `DeselectEventSystem()` を追加 — 同ゾーンの GainSlider/PanSlider は `UiDeselectOnPointerUp` で同様の deselect を既に行っており、このパネルの Button だけがクリック後も選択状態を持ち越す非対称な状態だった。既存の deselect パターンに揃えることで、選択残留を要因の一つとして完全に除去。
- 新サンプル `VRConfigExample`（`VRConfigExampleController`）に Exit 機能を追加。`_returnSceneName`（Editor では `SceneAsset` フィールドからも自動同期）を設定すると、左手メニューボタン/Keyboard Esc/画面上の Exit ボタンのいずれでも指定シーンへ `SceneManager.LoadScene` で戻れる。未設定時は警告ログのみで無効化（既定 = 空文字）。ガイドテキストに Exit 用の固定 1 行を追加（6 行構成に、パネルサイズも追従）。想定運用: このサンプルを自プロジェクトへ import → 両シーンを Build Settings に追加 → 自シーンから `SceneManager.LoadScene("VRConfigExample")` で入場 → Address Override を確認・設定 → Exit で復帰。
- **`VRConfigExample` の world-fixed UI 微振動を根治** — HMD 姿勢をコード内の手書き `LateUpdate` で毎フレーム 1 回だけ Transform に適用していたため、XR コンポジタ側の遅延ラッチ/reprojection パスと基準タイミングがずれ、パネル/ガイドテキストが目に見えて微振動していた。手書き適用を廃止し、`_cameraTransform` に実行時 `AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>()` してコードで構成（`positionInput`/`rotationInput` を `<XRHMD>/centerEyePosition` / `centerEyeRotation` にバインド、`updateType = UpdateAndBeforeRender`）。Input System 自身のレンダー直前サンプリングに揃うため、コンポジタと同じタイミングで姿勢を取得するようになる。
- **`VRConfigExample` の Exit バインドが右手メニューボタンで不発だった原因を特定** — Quest では右 Touch コントローラーの「システム」ボタンは OS 側で予約されており（Quest システムメニューを開く）、アプリの Input System アクションには一切配信されない。左 Touch コントローラーの Menu ボタンのみアプリから利用可能。旧 Exit バインド（右手 `menuButton` + 右スティック押し込み）はこれが原因で実機発火しなかった。Exit を左手 `menuButton` のみに変更（右手は削除）。
- **`VRConfigExample` の実機不発 3 件（スティック押し込み / Menu / トリガー長押し Apply）を修正** — Quest 3s + Meta Quest Touch Plus/Pro 実機検証で、A/B/X/Y・トリガー・スティック傾きは効くのに上記 3 つだけ不発と判明。`com.unity.xr.openxr` の `OculusTouchControllerProfile` / `MetaQuestTouchPlusControllerProfile` / `MetaQuestTouchProControllerProfile` 実ソースを確認して原因を特定・修正した。
  - スティック押し込み: 旧バインド `<XRController>{Hand}/primary2DAxisClick` は、`primary2DAxisClick` がそのコントロールの OpenXR *usage* タグに過ぎず control 名/エイリアスではないため一致しない、実質デッドバインドだった（Input System が usage をパスセグメントとして照合するのは `{primary2DAxisClick}` の brace 表記のみ — Unity 公式サンプル `com.unity.xr.openxr/Samples~/Controller/ControllerSampleActions.inputactions` で確認）。control の実名は `thumbstickClicked`（エイリアス: `JoystickOrPadPressed` / `thumbstickClick` / `joystickClicked`）。両手 × 名前バインド + braced usage バインドの計 4 本を冗長登録。
  - Menu: 左手 `menuButton`（エイリアス）のみのバインドに加え、control の実名 `menu`（Unity 公式サンプルが使う表記）も冗長登録。
  - トリガー長押し Apply: `started`/`canceled` コールバックのシーケンスが実機の長押しジェスチャーに対して不安定だった（A/B/X/Y・スティック傾きのような単発 `performed` は影響を受けなかった）。`Update()` で `InputAction.IsPressed()` を毎フレーム読む `PollTrigger()` に一元化し、`started`/`canceled` 購読を廃止。長押し閾値到達（Apply）と解放時の未到達（テスト再生）を 1 回のホールドにつき排他的に発火するようラッチ管理。
  - 診断行末尾に最後に検知したコントロール名を `last: RightHand/thumbstickClicked` 形式で常時表示（`RecordLastControl`）。全 shared action の `performed` に配線し、on-device でどの物理コントロールが実際に解決したかを即座に確認できる。
- **`HapbeatAddressOverridePanel.MoveFocus` の縦方向フォーカス移動バグ** — 実機検証でスティック入力が `(0, ±1)` と正しく読めているのに下方向（`dir.y > 0`）へフォーカスが移動しない不具合を特定。原因は `Unity` の `Mathf.Sign(0f)` が `0` ではなく `+1` を返す仕様で、垂直判定の同一行エントリ（`dy == 0`）が `Sign(dy) == Sign(dir.y)` を満たしてしまい、実際に下の行にあるボタンより距離 0 の同一行ボタンが誤って最短候補として選ばれ、行が一切進まなくなっていた（上方向は `Sign(dir.y)` が負のため偶然影響を受けず「上は動くのに下だけ動かない」という非対称な症状になっていた）。`Mathf.Sign` の代わりに 0 を正しく 0 として返す整数版 `SignOfInt` を新設し、水平・垂直両方の符号比較をこれに置き換えて修正。
- **`HapbeatAddressOverridePanel` の Apply/Play/Exit フォーカスハイライト消失バグ（Player+ からの右移動でのみ発生）** — Apply/Play/Exit は行 0/行 1 の両座標に同一ボタンをエイリアス登録しているため、`ApplyFocusVisual` の座標一致比較 (`kvp.Key == _focusedCoord`) では、後から反復されるエイリアス座標が同じ `Image` の色を上書きしてしまい、Player+ から右移動で到達した場合（先に反復される行 0 座標が focused）だけハイライトが消えていた（Group+ からの右移動は偶然エイリアス側の行 1 座標が最後に反復されるため無症状だった — 内部の `_focusedCoord`/`ActivateFocused` 自体は両経路とも正しく機能していた）。座標一致ではなくボタン参照一致で判定するよう根本修正。あわせてステッパー〜アクション列間の余白を 10px→8px→4px の 2 段階で詰めてパネル幅を再計算 (410→408→404px)、押下フラッシュ色を青 (`#3D7EC9`) から濃灰 (`#555555`) に変更（パネルの黒 0.6α 背景と同化しないように）。Apply/Play=青/緑の非フォーカス時アクセント色は一旦追加したが、黄色のフォーカスハイライトと紛らわしかったため撤回し、他の全ボタンと同じ既定の半透明白（steppers と同色）に統一。Exit のみ赤 (`#B03A3A`) の区別用アクセントを維持。

### Changed（変更）

- Address Override の状態可視化を Settings ウィンドウから `HapbeatManagerEditor`（Manager インスペクタ）の "Address Override (this device)" ボックスへ移設。Edit / Play 両モードで常時表示し、レイアウトシフトを避けるため状態行は常に描画（テキストのみ切り替え）。
- Settings ウィンドウで `enableLogging` に誤って付いていた "Verbose Log" ラベルを "Enable Logging" に修正し、`verboseLogging`（PONG/keep-alive 等の詳細ログ）用の PropertyField を新規追加。
- `HapbeatAddressOverridePanel` の Player/Group 値ラベル・ステータス行（未適用時）を黄色で強調し、「編集中の変数」であることを一目で分かるように統一。Editor 側（`HapbeatAddressOverrideStatusGUI` 共有ヘルパー / `HapbeatRuntimeStatusWindow`）でも Saved/Active の player・group 値、appName プレビュー、Connection セクションの接続値をリッチテキストで同系色にハイライトし、Runtime パネルと Editor 表示で「値部分の強調」を統一。ラベル部分は白のまま変更なし。
- Showcase サンプルの `ZoneSwitcher` — Inspector の "Initial Zone" を 1..9 の生 int スライダーから、設定済み `_zones` のラベル名を選べるドロップダウンに変更（`ZoneSwitcherEditor` 新設）。`_zones` が空の場合は従来の int フィールドにフォールバックする。
- **`VRConfigExample` の操作体系を片手完結・L/R 対称に全面変更**（旧: 右手=player、左手=group、右スティック押込/メニュー=Exit という左右非対称のスキーム）。中間スキーム（両手重複バインドの per-function 割当: スティック傾け=フォーカス切替 / A・B=+/- / トリガー短押し=テスト再生・長押し=Apply）を経て、最終的に全操作を**全 GUI + 2D フォーカスグリッド**へ統合（本セクション末尾の再改訂を参照）: 全コントロール（Player -/+、Group -/+、Play、Apply、Exit）は `HapbeatAddressOverridePanel` 内のボタンとしてグリッド登録され、スティック傾け（左右いずれか）= `MoveFocus` でグリッド移動、トリガー/A(X)/B(Y)（いずれの手・いずれのボタンでも同一）= `ActivateFocused` でフォーカス中のボタンを押下する 2 アクションのみに簡略化。スティック押し込み = `Recenter()`（後述）は変わらず維持。
- `VRConfigExample` にパネル/ガイド recenter を追加。スティック押し込み（両手 + Keyboard R）でパネル + ガイドテキストをカメラ正面 1.5m・水平 yaw のみ追従（ピッチ/ロールは無視）の位置へ移動。シーン起動時にも 1 回自動実行。
- `VRConfigExample` のテスト再生を、直接 `HapbeatManager.Play(eventId)`（Command モード、Kit 必須）で呼んでいたものから、`BasicExample` と同じ経路（`HapbeatUnityEventTrigger` → EventMap の StreamClip エントリ）に変更。新設 `VRConfigExampleEventMap.asset`（100Hz sine、1 エントリ）を同梱し、デバイスに Kit を配備しなくても振動確認できる。
- `VRConfigExample` の診断行を `Controllers: R OK / L --` 形式に整理（旧: `R:OK L:--`）。
- シーン構成: `Hapbeat` GameObject を `ConfigSceneController` に改名し、`VRConfigExampleController`（旧 `VRBasicExampleController`）を `VRRig` から `ConfigSceneController` へ移設（`VRRig` は Main Camera の親としてのみ残る）。
- `HapbeatAddressOverridePanel` のステータス行プレビュー例を `player_1/pos_chest` → `player_1/pos_chest/group_1` に変更（`PreviewTarget` 定数）。group override 適用時の解決結果も一目で確認できるよう、position に加えて group セグメントを含む例に揃えた。
- **`HapbeatAddressOverridePanel` のレイアウトを再構成** — 左に Player -/値/+・Group -/値/+ の 2 行、その右側に Apply・Play・Exit を左からこの順で横並び、各 2 行分の高さを持つ正方形寄りのボタンとして配置（旧: Play・Apply を 1 行、Exit を単独の別行として左右ステッパーの下段に配置）。フォーカスグリッドは概念上 2 行 × 5 列（行0: Player-/Player+/Apply/Play/Exit、行1: Group-/Group+/Apply/Play/Exit）となり、Apply/Play/Exit は同一ボタンを両方の行座標に登録（`RegisterFocusableAlias`）することで、どちらの行からも右移動で到達でき、フォーカス中に上下移動すると（見た目は変化せず）内部座標だけ行間を往復し、その後の左移動で入ってきた行へ正しく戻れるようにした。`ScreenSpaceOverlay` のパネルサイズを 300×180 → 410×108px 相当に、`WorldSpace` の既定 `_worldSize` を `(0.5, 0.28)` → `(0.5, 0.13)` に、それぞれ新レイアウトのアスペクト比へ合わせて再計算。
- ボタン活性化時のフラッシュ色を白寄り（`rgba(1,1,1,0.95)`）から青（`#3D7EC9`, alpha 0.9）に変更。白フラッシュはボタン自身の白いラベル文字が一瞬消えて読めなくなる上、パネルの白/黄テキストとも被っていたため、背景（黒 0.6α）ともテキスト色（白ラベル・黄ハイライト）とも被らない色に変更しつつ、ボタンラベルは引き続き白のまま可読性を維持。
- `VRConfigExample` のガイドテキストから "Controllers: R OK / L --" 等の診断行を削除し、`GuideActionsText` の固定 1 行のみに戻した（旧: 1 秒ごとに更新される固定 2 行構成）。診断内容（検出状態 + last control）は毎秒ではなく内容が変化した時のみ `Debug.Log` へ出力するように変更（`PollControllerDiagnostics`、旧 `RefreshDiagnosticLine`）。ガイド用ワールドキャンバスの高さも 2 行分 (`0.22`) → 1 行分 (`0.12`) に縮小。
- **StreamClip の送出を専用 background thread 化**（実機でランダムに発生していた途切れの根本原因を修正）。`HapbeatManager` の multi-source mixer は従来 `MixerCoroutine`（`StartCoroutine` / Update 駆動）で実装されており、GC・レンダリング・物理演算などによる main-thread のフレームヒッチが chunk 送信を遅延させ、device 側のリングバッファを枯渇させて音切れを起こしていた（Wi-Fi・firmware mixer 自体は健全）。`System.Threading.Thread`（`StreamThreadLoop`）に置き換え、Unity の frame clock ではなく `Stopwatch` でペーシングすることでこの main-thread 依存を排除。chunk サイズも MTU 上限一杯（mono 約 44ms 相当）から目標 ~10ms（MTU 上限は引き続き上限として維持）に細分化し、より均等な送出間隔にした。Sleep 待機は Windows の Sleep(1) 粒度（最悪 ~15.6ms）を踏まえ、粗い `Thread.Sleep(1)` ループ + 最終 ~2ms の `SpinWait` によるハイブリッド方式。`HapbeatStreamPlayback` の Gain/Pan は既存の `Volatile` 実装のままスレッド安全に流用、`AudioClip.GetData` は従来通り main thread（`StreamSource` コンストラクタ）で読み切ってから thread に渡す。新規ソース追加（`StreamAudioClip`）は `_streamLock` 経由でロック保護し、セッション終了判定と `_sources` への追加を単一ロックでアトミックに実行（自然終了と新規追加が競合して source が消失するレースを排除）。`StopStream()` は thread への停止シグナル + `Thread.Join(500ms)` で同期的に完了を待ち、`Disconnect()` / `OnDestroy` / `OnApplicationQuit`（`Cleanup()`）からも呼ばれるようにして thread が確実に join されドメインリロード時にリークしないようにした。STREAM_END の二重送信/送信漏れは `Interlocked` ベースの一回限りフラグで防止。loop シーム部のクロスフェード（instruction 案 D）は本改修のスコープ外（別途フォローアップ）。

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
