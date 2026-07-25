同一ビルドを複数の端末に配布し、端末ごとに送信先の Hapbeat を選べるようにする **Address Override** を中心に、Wi-Fi 送信経路の安定化（ユニキャスト送信・ストリーム送出のスレッド化）と、VR 実機で設定を行うための新サンプルを追加しました。

### ⚠ 破壊的変更（移行ガイド）

- `HapbeatConfig` の `group` / `overridePlayer` / `overrideGroup` / `discoveryTimeoutMs` を削除しました。`group` と `discoveryTimeoutMs` はどこからも読まれていない dead field で、値を変えても挙動は変わりませんでした。旧 `overridePlayer` / `overrideGroup`（起動時の既定値）は、端末ごとの実行時 API（`SetAddressOverride`）と、ビルド全体を固定する `buildOverridePlayer` / `buildOverrideGroup`（下記 Added）に役割を分けています。
- `HapbeatManager.DefaultGroup` / `EffectiveGroup` を削除しました。CONNECT_STATUS (0x20) の group バイトはデバイス側で保存後に一切読まれないレガシーフィールドであることが確認できたため、内部処理に単純化しています。
- `HapbeatClient.SendPlay` / `SendStop` / `SendStopAll` の戻り値が `void` から `CommandSendResult`（`Broadcast` / `Unicast`）に変わりました。戻り値を使わない既存の呼び出しはそのままコンパイルできます。

### Added（追加）

- **サンプル「XRI Hand Demo (haptics add-on)」と `Hapbeat > Samples > Augment XRI Hand Demo`** — XR Interaction Toolkit の *Hands Interaction Demo* シーンに触覚を後付けするサンプルです。XRI のサンプルは Unity Companion License のため改変シーンを再配布できません。そこで**シーンは配らず、EventMap（`HandsDemoEventMap.asset`・10 エントリ）と Kit（`hand-demo-kit`・stream clip 9 本）だけを同梱し、配線は Editor コマンドで後付けする**構成にしています。ユーザーは XRI 側で `HandsDemoScene` を import して開き、このコマンドを実行するだけで、掴む / 擦る / 押し込む / スナップ / UI クリックの触覚が入ります。
  - 追加されるのは Hapbeat コンポーネント 33 個（`HapbeatUnityEventTrigger` 18・`HapbeatSequenceTrigger` 6・`HapbeatParameterBinding` 4・`HapbeatTickEmitter` 2・`HapbeatManager` 1・`XR Helpers` のフィルタ 2）と、UnityEvent 配線 41 本です。
  - **冪等**です。既にあるコンポーネント・同じ対象/メソッドを指す配線はスキップし、追加数 / スキップ数をサマリログに出します。全操作は 1 つの Undo にまとまります。
  - 実行前にシーンが *Hands Interaction Demo* かを検証し、違えば中断します。XRI のバージョン差で階層が変わっている場合は、**見つからなかったパスを 1 件ずつ警告として列挙**したうえで、見つかった分だけを適用します（黙って部分適用しません）。
  - XRI 側の select イベントには手を加えません。ソケット周りは `XR Helpers` サンプルのフィルタコンポーネント経由で配線します。
  - 診断用の `HapbeatEventLogger` 配線（PokeButton の XRI イベント 14 種）は別メニュー **`Augment XRI Hand Demo (+ diagnostic Event Logger)`** に分離しました。
  - この Editor ツールは **XRI への asmdef 参照を持ちません**。XRI コンポーネントは型名で探索し、UnityEvent は `SerializedObject` のプロパティパス経由で編集するため、XRI 未導入のプロジェクトでもコンパイルできます。
- **ビルド単位の Address Override 固定（`Hapbeat > Settings` > Override Addressing (this build)）** — 複数のデモを同時開催しても混線しないよう、player / group を**ビルド全体で固定**できるようにしました。軸ごとに独立して指定します。端末単位の `Override Addressing (this device)`（Runtime Status ウィンドウ）と対になる名前で、Player / Group は同じ横並びの数値入力で編集します。
  - `HapbeatConfig.buildOverridePlayer` / `buildOverrideGroup`（既定 `-1`）— `1-99` = そのビルド全体で強制（端末側の設定パネル / `SetAddressOverride` / `PlayerPrefs` では変更不可）。`-1` = 従来どおり端末ごと。値は `OnValidate` で `-1..99` にクランプされます。
  - 想定運用: `group` をビルドで固定してデモを分離し、`player` は `-1` のまま端末ごとにペアリングする。
  - 優先順位は軸ごとに「config が `1-99` → それを強制 / config が `-1` → 保存値（`PlayerPrefs`）→ 無効」。`HapbeatManager.ResolveEffectiveOverride(int, int)`（static・純粋関数）として切り出し、ユニットテスト（`AddressOverrideResolutionTests`）を追加しています。
  - `HapbeatManager.BuildOverridePlayer` / `BuildOverrideGroup` / `IsPlayerForcedByBuild` / `IsGroupForcedByBuild` — UI が「この軸はビルド固定」と表示するための公開判定。
  - 強制軸は `SetAddressOverride` が無視し（`PlayerPrefs` にも書きません）、`ClearPersistedAddressOverride` 後も config 値のまま維持されます。`HapbeatAddressOverridePanel` は該当軸の -/+ を無効化し値に `(build)` を付記、`Hapbeat > Open Runtime Status` は `Build (forced)` 行を表示します。
  - 既定値（`-1` / `-1`）では従来の挙動と完全に同一です。
- **Address Override — 実行時に player / group を上書き**
  同一ビルドを複数の VR HMD に配布し、端末ごとに自分の Hapbeat へ 1:1 で送りたい、というユースケース向けの機能です。有効にすると EventMap 側の target に関わらず、すべての送信（Play / Stop / StopAll / StreamBegin）の宛先が指定した player / group に強制されます。
  - `HapbeatManager.SetAddressOverride(int player, int group, bool persist = false)` — 実行時に切り替え。`persist: true` で `PlayerPrefs` に保存し、次回起動時に復元します（端末ごとの設定なので、プロジェクト共有の `HapbeatConfig` には保存しません）。
  - `HapbeatManager.OverridePlayer` / `OverrideGroup` / `TryGetPersistedAddressOverride(out int, out int)`（static）/ `ClearPersistedAddressOverride()`。
  - `HapbeatManager.AddressOverrideDisabled`（`= -1`）— 「その軸は上書きしない（EventMap の target をそのまま使う）」ことを表す定数。target が `-1` という値に書き換わるわけではありません。
  - `HapbeatClient.ResolveTarget(string target, int overridePlayer, int overrideGroup)`（static・UnityEngine 非依存）— target 文字列の `player_<N>` / `group_<M>` を置換・挿入します。
  - `appName` に `<p>` / `<g>` を含めると、送信時に現在の override 番号（無効時は `-`）へ置換されます（`HapbeatManager.ApplyAddressPlaceholders`）。デバイスの OLED にペア番号が表示されるので、現場で対応関係を確認できます。
- **`HapbeatAddressOverridePanel`（Runtime コンポーネント）** — GameObject に 1 つ追加するだけで override 設定 UI が出ます。`ScreenSpaceOverlay` / `WorldSpace`（VR 用）の 2 モードに対応。Player -/+、Group -/+、Play、Apply、Exit のボタンを内蔵し、2D フォーカスグリッド（`RegisterFocusable` / `MoveFocus` / `ActivateFocused`）でコントローラー操作にも対応します。Play / Exit の実処理は `OnPlayRequested` / `OnExitRequested` で外部から注入します。
  - `WorldSpace` 時の **lazy follow（遅延追従）**（`World Attach Mode` = `LazyFollow` / `WorldFixed`、既定 `LazyFollow`）— 視界中央から `Follow Deadzone Degrees`（既定 `10°`）以内にある間はパネルを**ワールド固定のまま**にし、それを超えて見回したときだけ `Follow Smooth Seconds`（既定 `0.25` s）の時定数で正面へ滑らかに移動します。Canvas をカメラ Transform の子にする**ハードなヘッドロックは採用していません**（頭に追従して動く面に XR コンポジタの再投影が重ねて掛かるため、頭を振るたびに UI が泳いで見えます）。カメラは `Follow Camera`（未設定なら `Camera.main`）、位置は `Follow Distance`（既定 `1.5` m）/ `Follow Vertical Offset`（既定 `0` m）で調整します。カメラが見つからない場合は警告を 1 回出して `WorldFixed` 相当で動作します。
  - `PanelCanvasTransform` / `IsFollowingView` / `FollowVerticalOffset` / `SnapToView()`（public）— 外部コントローラーが自前の world-space UI をパネル Canvas の下にぶら下げたり、「視界中央へ戻す」操作を実装するために公開しています。
- **`Hapbeat > Open Runtime Status` ウィンドウ** — 端末ごとに変わる値を 1 画面に集約。Address Override（保存値 / 実行時値 / 直接編集 + 保存 / Clear）、appName のプレースホルダー解決後プレビュー、接続状態（broadcast・unicast / ポート / 生存デバイス数 / 発見済み一覧）を確認できます。Manager インスペクタからも開けます。
- **ユニキャスト送信（`streamUnicast` / `commandUnicast`、いずれも既定 `true`）**
  Wi-Fi のブロードキャストは、同じアクセスポイントに省電力状態の端末が 1 台でもいると AP 側で DTIM まで保留されるため、100〜300ms 級の遅延が周期的に発生します（CLIP では可聴な途切れとして現れていました）。PONG で判明している既知デバイスへ直接送ることでこれを回避します。
  - `STREAM_BEGIN` / `STREAM_DATA` / `STREAM_END` と `PLAY` / `STOP` / `STOP_ALL` が対象。`PING` / `CONNECT_STATUS` は discovery のため従来どおりブロードキャストです。
  - 宛先は解決後の target で絞り込みます（`HapbeatClient.AddressMatches` — firmware の照合と同一セマンティクス）。1 人が複数台装着する構成や、複数ペアが同一 LAN にいる構成でも、自分のペアにだけ送信します。
  - 既知デバイスが 0 台のときはブロードキャストへフォールバックします。二重配送はしません。
  - `HapbeatProtocol.ParsePongExtended` — PONG からデバイスの address / device_name / firmware_version 等を取得します（プロトコル変更はありません。従来 SDK 側が読んでいなかっただけです）。
- **新サンプル `VRConfigExample`** — Quest 等の VR 実機で override を設定・確認するための最小シーン。XR Interaction Toolkit に依存せず、Input System のみで動作します。操作はスティックでフォーカス移動、トリガー / A(X) / B(Y) のいずれかで決定の 2 アクションのみ。パネルとガイドは一体で lazy follow（遅延追従）するので、起動時の recenter は行いません（スティック押し込みの recenter は「今すぐ正面へスナップ」の意味になります）。テスト再生は EventMap の CLIP エントリ（100Hz sine 同梱）なので、デバイスに Kit を配備しなくても振動を確認できます。戻り先シーンを設定すれば Exit で自分のシーンへ復帰できるため、**自プロジェクトの設定画面としてそのまま使えます**。
- ユニットテスト（EditMode）— `ResolveTargetTests` / `AddressMatchesTests` / `AddressPlaceholderTests` と `Tests/Runtime` アセンブリ定義を新設しました。
- `package.json` に `com.unity.ugui` 依存、Runtime asmdef に `UnityEngine.UI` 参照を追加（`HapbeatAddressOverridePanel` の uGUI 利用のため）。

### Changed（変更）

- **StreamClip の送出を専用スレッド化** — 従来はコルーチン（Update 駆動）で送っていたため、GC やレンダリングによるフレームヒッチがそのまま送信の空白になり、デバイス側のリングバッファを枯渇させて不定期な途切れを起こしていました。`Stopwatch` を基準にした専用スレッドへ移し、チャンクも MTU 上限一杯（mono 約 44ms 相当）から約 10ms に細分化して、フレームレートに依存しない等間隔送出にしています。
- Settings ウィンドウ: `enableLogging` に誤って付いていた "Verbose Log" ラベルを修正し、`verboseLogging` の項目を追加しました。
- Showcase サンプル: `ZoneSwitcher` の Initial Zone を、生の数値スライダーから設定済みゾーン名のドロップダウンに変更しました。

### Fixed（修正）

- **group を指定した送信がデバイスに届かない** — デバイスのアドレス照合は位置ベース（i 番目のセグメント同士を比較）ですが、`group_<M>` を target の末尾に付けるだけだったため、player / position を省略した target では group が本来と違うスロットに入り、常に不一致になっていました。position スロットを `*` で補ってから追加するよう修正しています（例: `"" → "*/*/group_2"`）。
  なお、デバイスのアドレスは常に `player_<N>/<position>/group_<M>` の正規形で、**既定は `player_1` / `group_1`** です（firmware DEC-048 以降、group が省略されることはありません）。設定し忘れた機体は既定の 1 番に合流するので、デモを分けるときは **1 以外の番号から振る**と設定漏れに気付けます。
- **UDP 受信スレッドが Windows の ICMP reset で停止する** — 電源 OFF や再起動中のデバイスへユニキャスト送信すると ICMP port unreachable が返り、Windows ではそれが次の `Receive()` の例外として現れます。従来はこれを致命エラーとして受信ループを終了しており、以後デバイスを一切検出できなくなっていました（`SIO_UDP_CONNRESET` の設定と、回復可能なエラーでループを止めない処理を追加）。
- **world-space パネルがプレイヤーに正対しない** — 目標姿勢がヘッドの yaw に合わせるだけだったため、パネルが目線より上下にある（`Follow Vertical Offset` を付けた、あるいは立ち位置の高さが違う）と斜めを向いていました。カメラ位置を見る look-at に変更しています（ロールは常に 0 のままなので、頭を傾けてもパネルは傾きません）。デッドゾーン内で静止する挙動は従来どおりです。
- `HapbeatAddressOverridePanel` が既存の Canvas の子に配置された場合、生成する Canvas が入れ子になって表示位置がずれる問題を修正しました（Unity の仕様上、子 Canvas は独自の RenderMode を持てないため、シーンルートへ退避します）。
- フォーカス移動の不具合 2 件を修正 — 下方向へ移動できない（`Mathf.Sign(0f)` が `+1` を返す仕様に起因）、および複数座標に登録したボタンでハイライトが消える問題。
- `VRConfigExample` の VR 実機不具合 — UI の微振動（`TrackedPoseDriver` による姿勢取得へ変更）、スティック押し込み / Menu ボタンが反応しない（OpenXR の実コントロール名にバインド）、右手 Menu ボタンは Quest の OS 予約でアプリに届かないため左手のみに変更。起動時 UI が実際の頭の位置から左下にずれる問題（`OnEnable` の recenter が XR トラッキング確立前に走り、シーン上の初期カメラ位置を基準にしていた）は、lazy follow 化（パネル自身が `LateUpdate` で配置する）と起動時 recenter の削除で解消しました。パネルが視界中央より上にずれていた問題も修正しています（`VerticalLayoutGroup` が既定の `UpperLeft` 揃えで、コンテンツより背景が高いときに上端へ寄っていたため）。
- Showcase の `AddressOverrideDemo` を `Z4_Stream` 直下の独立した GameObject に配線し直しました（Canvas 配下にあったため上記の入れ子問題が発生していました）。
- **`VRConfigExample` のガイドテキストがパネルに追従しない（Quest スタンドアロンビルドのみ）** — ガイド Canvas をパネル Canvas の子にする処理を `OnEnable` で 1 回だけ試みており、その時点で前提（パネル Canvas の生成・スケール確定）が未了だと黙って諦めていました。実行順序はエディタと実機ビルドで異なるため、実機だけ親子付けに失敗していました。成功するまで `LateUpdate` で再試行し（上限 5 秒）、打ち切り時には**どの前提で失敗し続けたか**を警告に出すようにしています。成功時も 1 行だけログを出すので、追従しない症状が再発しても親子付けの成否を切り分けられます。

---