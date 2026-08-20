# Changelog

Hapbeat Unity SDK の主要な変更点をまとめます。

形式は [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に、
バージョン付けは [Semantic Versioning](https://semver.org/lang/ja/) に従います。

---

## [Unreleased]

### Fixed（修正）

- **Unity 6000.0 LTS でエディタ拡張がコンパイルエラーになる問題を修正**しました。オブジェクト解決 API は Unity 6 の途中で `EditorUtility.InstanceIDToObject` から `EditorUtility.EntityIdToObject` に改名されており、**新しい名前は 6000.0 LTS に存在しません**（6000.0.59f2 の `UnityEditor.dll` には無く、6000.3.12f1 には有ることを確認）。そのため `package.json` が `"unity": "6000.0"` と宣言しているにもかかわらず、6000.0 のプロジェクトでは `Hapbeat.Editor` アセンブリが `error CS0117` で落ち、**プロジェクト全体のコンパイルが通らなくなっていました**（SDK のランタイムだけを使うこともできません）。

  - 呼び出しをバージョンガード付きヘルパー `HapbeatEditorCompat.IdToObject(int)` に集約しました（6000.3 以降は新 API、それ未満は旧 API。旧 API は 6000.3 にも残っているため、どちらの分岐も安全です）。
  - Showcase サンプルの `HierarchySeparator` も同じヘルパー経由に揃えました。こちらはインポート先のプロジェクトで同じエラーを起こしていました。

## [0.4.0] - 2026-08-06

**接続の安定性**に絞った更新です。展示・常設のような無人運用で「いつの間にか触覚が出なくなり、PC を再起動するまで戻らない」という状態を作っていた原因を取り除きました。あわせて、Hyper-V / WSL2 / Docker を入れた PC でデバイスを一切検出できない問題と、Test Play の再生が途切れる問題を修正しています。

いずれも設定変更は不要で、これまでどおりお使いいただけます。

### Added（追加）

- **SDK 更新の通知**: 新しい版が公開されると、Editor 起動時に Console へ 1 行だけお知らせを出すようになりました。表示は **Editor セッションごとに 1 回**で、スクリプト再コンパイル（domain reload）では重複しません。UPM の Git URL はタグを固定すると Package Manager が更新を検出できないため、これが実質的な唯一の気付き手段になります。
  - いつでも確認: `Hapbeat` → `Diagnostics` → `Check for SDK Updates`
  - 自動確認の ON/OFF: `Hapbeat` → `Diagnostics` → `Check for SDK Updates on Startup`
  - 取得は 3 秒でタイムアウトし、失敗しても何も出しません（オフライン環境で無害）。この SDK 自体を Local / Embedded で開発しているプロジェクトでは確認しません。

- **接続の診断ログ**: Windows で ICMP 応答の抑止設定（`SIO_UDP_CONNRESET`）の適用に失敗した場合に、警告を出すようになりました。従来は無言で握りつぶしていたため、現場で通信不良が起きても原因の手がかりが残りませんでした。Windows 以外では従来どおり何も出しません（この設定自体が存在しないため）。

### Fixed（修正）

- **一度の通信エラーで触覚が止まったままになる問題を修正**しました。Wi-Fi の瞬断や経路の一時的な変化など、送信エラーが 1 回起きるだけで接続が切断扱いになり、**アプリを再起動するまで復帰しません**でした。キープアライブの送信も止まるため、デバイスは 15 秒後に「アプリ未接続」表示（LED 緑）へ戻ります。無人運用の展示・常設では気付いて再起動する人がいないため、実質その日の稼働が止まります。

  - UDP の送信失敗はソケットの故障を意味しないため、**送信エラーで接続を切らない**ようにしました。
  - 接続が落ちた場合は**自動的に再接続**します（2 秒から始まり、最大 30 秒までの指数バックオフ）。`HapbeatConfig` の **Auto Reconnect** で無効化できます。
  - 明示的に `Disconnect()` を呼んだ場合は再接続しません（意図した切断を打ち消さないため）。
  - 起動時にネットワークがまだ使えない状態でも、使えるようになった時点で自動的に接続されます。
  - 送信エラーのログは**障害ごとに 1 回だけ**出力し、**復旧した時点で 1 行**出します。ストリーミング中は 1 秒あたり約 100 回送信するため、抑制しないとログが埋まって現場調査に使えなくなります。

- **切断状態のソケットが後始末されずに残る問題を修正**しました。送信・受信エラーで接続が切断扱いになってもソケットは開いたままで、その状態で再接続するとソケットと受信スレッドが取り残されていました。

- **再接続後にオフラインのデバイスが残り続ける問題を修正**しました。切断時にデバイスの生存情報を破棄するようにしたため、別のネットワークへ再接続した直後に、古い宛先へ送信し続けて無音になることがなくなりました。

- **仮想ネットワーク（Hyper-V / WSL2 / Docker）がある PC でデバイスを検出できない問題を修正**しました。これらが作る仮想アダプタは **LAN ケーブルを繋いでいなくても常時有効**で、Windows の既定では Wi-Fi より優先されることがあります。従来の送信方法ではそちらへ流れてしまい、Hapbeat には一切届きませんでした。

  - 各ネットワークアダプタの実際のサブネットに宛てて送るようにしたため、優先順位の設定を変更しなくても届きます。
  - デバイスから応答があった時点で、そのネットワークに送信先を固定します。
  - 従来の送信方法も併用するので、SoftAP 構成など特殊な環境でも従来どおり動作します。単一のネットワークアダプタしかない環境では挙動は変わりません。

- **Test Play（EventMap / Inspector）の再生が途切れる問題を修正**しました。エディタ用の送信経路だけがデバイスを探索しておらず、常にブロードキャストで送っていたためです。ブロードキャストは Wi-Fi アクセスポイントが一定間隔まで保留するため、ストリーム再生が途切れがちになります。実行時と同じくデバイスを探索し、見つかったデバイスへ直接送るようにしました。

## [0.3.0] - 2026-07-26

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
