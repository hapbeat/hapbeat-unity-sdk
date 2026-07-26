# Hapbeat Unity SDK

Hapbeat デバイスを Unity から制御する公式 SDK。2D / 3D / XR 対応。

> **📚 公式ドキュメント**: [https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/)
> Getting Started / Trigger コンポーネント / EventMap / Parameter Binding / ターゲティング等の解説はポータルに集約しています。本 README は概要と入口です。

要件: **Unity 6 (6000.0) 以上**

## インストール

Unity Package Manager → `+` → **`Add package from git URL...`** → 以下の URL を入力:

```
https://github.com/Hapbeat/hapbeat-unity-sdk.git
```

詳細は [インストール手順](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/installation/) を参照。

## クイックスタート

1. `Hapbeat > Initial Scene Setup` を実行（シーンに Event Router を配置し、EventMap アセットを生成）
   - 個別に作る場合は `GameObject > Hapbeat > Event Router` / `Assets > Create > Hapbeat > Event Map`
2. 起動時に自動で UDP ソケットが開き、デバイスの検出（PING / PONG）が始まる
3. 以下のいずれかの方法で触覚イベントを発火

## 送信の仕組み（0.3.0 以降）

`PLAY` / `STOP` / `STOP_ALL` / `STREAM_*` は、PONG で判明済みのデバイスへ**ユニキャスト**で送ります（`commandUnicast` / `streamUnicast`、いずれも既定 ON）。Wi-Fi AP の省電力バッファ（DTIM）でブロードキャストフレームが 100〜300 ms 保留され、遅延や CLIP の途切れとして現れる問題を避けるためです。既知デバイスが 0 台のときは自動でブロードキャストにフォールバックします。`PING` / `CONNECT_STATUS` は検出のため従来どおりブロードキャストです。

詳細は [通信モデル](https://devtools.hapbeat.com/docs/concepts/communication-model/) を参照。

## イベント割り当て方法

### 方法1: コード直接呼び出し（最小構成）

最もシンプル。通信テストや小規模プロジェクト向け。

```csharp
using Hapbeat;

// 即時再生（event ID は <kit-name>.<clip-name>）
HapbeatManager.Instance.Play("sample-kit.sine_100hz", gain: 0.3f);

// 停止
HapbeatManager.Instance.Stop("sample-kit.sine_100hz");

// 全停止
HapbeatManager.Instance.StopAll();
```

**利点**: 即座に動く、学習コストゼロ
**欠点**: 触覚コードが散在する、ID やゲインがハードコード

---

### 方法2: EventMap + Trigger コンポーネント（推奨・コード不要）

イベント定義を EventMap に一元管理し、発火は Inspector だけで設定します。既存コードの変更は不要です。

Trigger は EventMap のエントリを **stable GUID** で参照するため、エントリを並べ替えても配線は壊れません。

#### Step 1: EventMap 作成

`Assets > Create > Hapbeat > Event Map` → エントリを追加

| displayName | category | eventName | gain |
|---|---|---|---|
| 着地 | my-kit | landing | 0.3 |
| ジャンプ | my-kit | jump | 0.2 |
| 敵衝突 | my-kit | enemy_hit | 0.8 |

`eventId` は `category.eventName`（= `<kit-name>.<clip-name>`）として自動合成されます。

#### Step 2: Trigger コンポーネントを配置

`Add Component > Hapbeat/...` から追加します。

| コンポーネント | 用途 |
|---|---|
| **Hapbeat Collision Trigger** | 物理衝突 / Trigger Enter・Exit で発火。2D / 3D 自動判定、速度連動ゲイン対応。衝突する GO にアタッチ |
| **Hapbeat UnityEvent Trigger** | `Fire()` / `FireWithGain(float)` / `Stop()` を任意の UnityEvent から呼ぶ。UI Button の OnClick、XRI のイベント、Animation Event 等 |
| **Hapbeat Sequence Trigger** | 掴む → 保持（ループ）→ 離す の 3 フェーズを 1 コンポーネントで管理 |
| **Hapbeat Tick Trigger** | Slider / ScrollRect など連続値の変化からスナップ触覚を生成 |
| **Hapbeat Parameter Binding** | Transform / 値を再生中の StreamClip の gain / pan にリアルタイムマッピング |
| **Hapbeat Action Helper** | `Stop` / `StopAll` / `StopStream` / `Ping` を UnityEvent から呼べるようにするラッパー |
| **Hapbeat Key Dispatcher** | キー入力 → UnityEvent（Input System） |
| **Hapbeat Status Overlay** | 接続状態 / RTT を画面に表示するデバッグ HUD |

**Animator との連携は `HapbeatStateBehaviour`**（コンポーネントではありません）

Animator Controller の対象 state を選択 → `Add Behaviour` → `HapbeatStateBehaviour` を追加します。state の Enter / Exit にそれぞれ別エントリを割り当てられ、`Required Previous State` で A→B の遷移に限定した発火もできます。ループする StreamClip は state を抜けたときに自動停止します。

> v0.1 期の `HapbeatAnimatorTrigger`（Animator パラメータの変化を監視する方式）は v0.2.0 で廃止されました。

各 Trigger の詳細は [Trigger コンポーネント](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/triggers/) を参照。

#### 一覧管理

`Hapbeat > Open Event Map` でダッシュボードを開くと、全エントリと配線先（どの GameObject / Animator state / スクリプトが参照しているか）を一覧できます。Bulk Edit による一括編集や Test Play もここから行えます。

---

### 方法3: HapbeatBridge サブクラス（コードベース・任意）

速度連動や条件分岐など、Inspector だけでは表現しきれないロジックを 1 ファイルに集約したい場合の選択肢です（標準は方法2）。

```csharp
using Hapbeat;
using UnityEngine;

public class MyHapbeatBridge : HapbeatBridge
{
    public void OnPlayerLanded(Collision col)
    {
        float speed = col.relativeVelocity.magnitude;
        if (speed < 1f) return;
        PlayScaled("着地", speed, minVelocity: 1f, maxVelocity: 15f);
    }

    [SerializeField] private AnimationCurve _impactCurve;
    public void OnEnemyHit(Collision col)
    {
        PlayWithCurve("敵衝突", col.relativeVelocity.magnitude, _impactCurve);
    }
}
```

`HapbeatBridge` の提供メソッド（いずれも EventMap の `displayName` で発火）:

| メソッド | 用途 |
|---|---|
| `Play(displayName, gainOverride = -1f)` | 発火（`gainOverride` 省略時は EventMap の gain） |
| `PlayByIndex(entryIndex, gainOverride = -1f)` | インデックス指定で発火 |
| `PlayScaled(displayName, velocity, minVelocity, maxVelocity)` | 値を 0-1 に正規化してゲインに |
| `PlayWithCurve(displayName, inputValue, curve)` | AnimationCurve でゲイン変換 |
| `Stop(displayName)` / `StopAll()` | 停止 |

---

### 方法4: Animation Event（足音など特定フレーム発火）

Animation ウィンドウでクリップの特定フレームにイベントを追加し、`HapbeatUnityEventTrigger.Fire()` を呼びます。コード変更不要。

## 方法の使い分け

| 状況 | 推奨方法 |
|---|---|
| 通信テスト・プロトタイプ | 方法1（コード直接） |
| 既存ゲームへの後付け | 方法2（EventMap + Trigger） |
| 複雑なゲインロジック | 方法3（HapbeatBridge） |
| アニメーション同期 | 方法4（Animation Event） |

方法2〜4は組み合わせ可能です。大部分を Trigger コンポーネントで設定し、特殊なケースだけ Bridge サブクラスで処理する構成が実用的です。

## Address Override — 同一ビルドを複数台に配布する

同じビルドを複数の VR HMD に配布し、端末ごとに自分の Hapbeat へ 1:1 で送りたい場合の機能です。有効にすると EventMap 側の target に関わらず、すべての送信（Play / Stop / StopAll / StreamBegin）の宛先が指定した player / group に強制されます。

```csharp
// 実行時に切り替え（persist: true で PlayerPrefs に保存 → 次回起動時に復元）
HapbeatManager.Instance.SetAddressOverride(player: 3, group: 2, persist: true);

// 解除（保存値も削除）
HapbeatManager.Instance.ClearPersistedAddressOverride();

// 軸ごとに「上書きしない」を表す定数（= -1）
HapbeatManager.AddressOverrideDisabled;
```

- **端末ごと（実行時）** — `HapbeatAddressOverridePanel` を GameObject に 1 つ追加するだけで設定 UI が出ます。`ScreenSpaceOverlay` / `WorldSpace`（VR 用・遅延追従）の 2 モード対応。
- **ビルド全体で固定** — `Hapbeat > Open Settings` の *Override Addressing (this build)* で `buildOverridePlayer` / `buildOverrideGroup` を `1-99` にすると、その軸は端末側から変更できなくなります（既定 `-1` = 端末ごと）。複数デモの同時開催で group を分離する用途を想定しています。
- `appName` に `<p>` / `<g>` を含めると、送信時に現在の override 番号（無効時は `-`）に置換され、デバイスの OLED に表示されます。
- 現在値の確認は `Hapbeat > Open Runtime Status`（保存値 / 実行時値 / ビルド固定を表示）。

詳細は [ターゲティング](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/targeting/) を参照。

## サンプル

Package Manager > Hapbeat SDK > Samples からインポートできます。

> ⚠️ **SDK バージョンを上げて Sample を再 import する場合**
> 古いバージョンの `Assets/Samples/Hapbeat SDK/<旧バージョン>/` フォルダを **必ず削除** してください。Unity は package 更新時に古い import を自動削除しないため、放置すると同一クラスが二重定義になり compile error やシーン重複の原因になります。
> SDK は起動時に `Assets/Samples/Hapbeat SDK/` 配下を scan し、複数バージョンが見つかると Console に警告を出します（`Hapbeat > Diagnostics > Check Sample Versions` から手動再実行可）。

| サンプル | 内容 | 前提 |
|---|---|---|
| **BasicExample** | 最小シーン。キーボード（Space: CLIP 単発 / R: CLIP ループ / F: FIRE / S: 全停止 / C: Ping）で疎通確認 | なし |
| **Showcase** | 1 シーン 5 ゾーンで主要な配線パターンを一通り体験（衝突 / Animator state / シーケンス / Tick / スクリプト）。キーボード + マウスのみ | なし |
| **XR Helpers** | XR Interaction Toolkit 用のフィルタコンポーネント（`HapbeatXRGrabFilter` / `HapbeatXRSocketFilter`） | XRI |
| **XRI Hand Demo (haptics add-on)** | XRI の *Hands Interaction Demo* シーンに触覚を後付け。EventMap と Kit のみ同梱し、配線は Editor コマンドで適用 | XRI + XR Helpers サンプル |
| **VR Config Example** | Quest 等の実機で Address Override を設定・テスト再生する最小シーン。XRI 非依存（Input System のみ） | なし |

### XRI Hand Demo の使い方

XRI のサンプルシーンは Unity Companion License のため改変版を再配布できません。そのため配線を Editor コマンドで後付けする構成になっています。

1. XR Interaction Toolkit 側で *Hands Interaction Demo* を import してシーンを開く
2. `Hapbeat > Samples > Augment XRI Hand Demo` を実行

冪等（既存のコンポーネント・同一の配線はスキップ）で、全操作は 1 つの Undo にまとまります。診断用の Event Logger 配線が必要な場合は `Hapbeat > Samples > Augment XRI Hand Demo (+ diagnostic Event Logger)` を使います。

手順の詳細は [XRI Hand Demo クイックスタート](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/xri-handdemo-quickstart/) を参照。

### VR 実機での確認

`VR Config Example` は Quest 等で override を設定・確認するための最小シーンです。操作はスティックでフォーカス移動、トリガー / A(X) / B(Y) のいずれかで決定の 2 アクションのみ。テスト再生は StreamClip（100 Hz sine 同梱）なので、デバイスに Kit を配備しなくても振動を確認できます。戻り先シーンを設定すれば Exit で自分のシーンへ復帰できるため、自プロジェクトの設定画面としてそのまま流用できます。

詳細は [VR Config Example](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/vr-config-example/) を参照。

## 接続設定

`Hapbeat > Open Settings` または `Assets > Create > Hapbeat > Config`

| 設定 | デフォルト | 説明 |
|---|---|---|
| Port | 7700 | UDP ポート |
| App Name | (productName) | デバイス OLED に表示する名前。最大 16 文字。`<p>` / `<g>` は override 番号に置換 |
| Override Addressing (this build) | -1 / -1 | ビルド全体で player / group を固定（`1-99`）。`-1` = 端末ごと |
| Ping Interval (s) | 5 | キープアライブ間隔 |
| Stream Buffer (s) | 0.05 | ストリームの先行送信バッファ（10–200 ms） |
| Stream Unicast / Command Unicast | ON | 既知デバイスへユニキャスト送信（OFF でブロードキャスト） |
| Haptic Delay (ms) | 0 | 音声出力遅延に合わせた触覚の遅延補正（0–500 ms） |
| Enable Logging / Verbose Log | ON / OFF | Console へのログ出力 |
| Advanced: Bridge (ESP-NOW) | OFF | ESP-NOW 経由の場合のみ ON |

## Edit モード操作

プレイモードに入らなくても、Inspector の HapbeatManager から以下が可能:

- **Connect / Disconnect** — 送信ソケットの開閉
- **Discover (Edit)** — LAN 上の Hapbeat を検出
- **Play / Stop / Stop All / Ping** — テスト送信

## Editor メニュー

`Hapbeat` メニューの主な項目:

- `Open Event Map` / `Open Batch Setup` / `Open Settings` / `Open Runtime Status`
- `Create Event Router` / `Create Event Map` / `Create HapbeatSDK Folder` / `Initial Scene Setup`
- `Samples/Augment XRI Hand Demo`（および `(+ diagnostic Event Logger)`）
- `Export Event Map (Selected)` / `(All in Project)`、`Normalize Audio Folder (16kHz · 2ch · PCM16)`
- 診断系: `Attach Event Logger to Selected`、`Logs/...`、`Disable Verbose Log on All Hapbeat Components`、`Diagnostics/Check Sample Versions`

全項目の逆引きは [Editor メニュー](https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/editor-menus/) を参照。

## 対応プラットフォーム

- PC (Windows / macOS)
- Meta Quest 2 / 3 / Pro
- Pico 4 Ultra
- Apple Vision Pro
- その他 Android / iOS デバイス

## 変更履歴

[CHANGELOG.md](CHANGELOG.md) を参照。

## サウンド素材クレジット

サンプル（`Samples~/`）に同梱されているサウンドファイルには、以下のフリー効果音サイトの素材を触覚信号生成用に加工して使用しているものが含まれる場合があります（全てのファイルが下記由来というわけではありません）。

- 効果音ラボ — https://soundeffect-lab.info/
- 魔王魂 — https://maou.audio/
- 効果音辞典（小森平） — https://taira-komori.net/
- OtoLogic — https://otologic.jp/
- 音人 — https://on-jin.com/

各素材は触覚デバイス向けに編集（リサンプル・トリミング・ゲイン調整等）した上で再配布しています。配布前に作者・著作権関連のメタデータは除去しています。

なお、上記以外のサイト由来の素材が混入している可能性も完全には否定できません。出典が明確でないファイルについても、権利者・配布元からご連絡をいただければ整合を確認の上、削除・差し替え・クレジット追記など適宜対応いたします。Issue または GitHub の連絡先までお知らせください。
