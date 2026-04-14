# Hapbeat SDK サンプルシーン制作ガイド

Editor スクリプトによる自動生成 + 手動仕上げの手順書。

- **対象 Unity バージョン**: Unity 6 (6000.3.12f1)
- **対象 HMD**: Meta Quest 3S（OpenXR 経由で他 HMD も対応）

---

## 全体の流れ

```
1. Unity プロジェクト作成 + パッケージインストール  ← 手動（1回だけ）
2. Hapbeat SDK のサンプルを Import                   ← 手動（1回だけ）
3. メニューからシーン自動生成                         ← 1クリック
4. XR Origin 配置 + Audio 設定 + 見た目調整          ← 手動（微調整）
5. Samples~/ にファイルをコピーして git push          ← 手動
```

**自動生成されるもの**:
- 全 GameObject の配置とコンポーネント設定
- EventMap アセット（.asset）の作成とエントリ設定
- HapbeatCollisionTrigger / UnityEventTrigger のパラメータ設定
- UnityEvent の接続（OnShoot → Fire() 等）
- UI の作成（Canvas, Text, Toggle）

**手動で行うもの**:
- XR Origin の配置（XRI Starter Assets プレハブを使用）
- Audio Clip の設定（各 AudioSource にドラッグ）
- Drone / Projectile Prefab の作成と設定（PlayerDemo のみ）
- 見た目の最終調整（位置・スケール・色・ライティング）
- テレポートアンカーの配置

---

## Step 1: Unity プロジェクトの作成

### 1.1 新規プロジェクト

開発用 Unity プロジェクトは **SDK リポジトリとは別の場所** に作成する。
パスが長いと Windows の 260 文字制限に引っかかるため、浅いパスを推奨。

```
推奨構成:
C:\UnityProjects\HapbeatSamples\    ← Unity プロジェクト（git 管理しない）
C:\GitHub\Hapbeat\hapbeat-unity-sdk\ ← SDK リポジトリ（git 管理）
```

1. **Unity Hub** を開く
2. 左の「Projects」タブ > 右上の **New project**
3. テンプレート: **VR** を選択
   - 見つからない場合は **3D (Built-in Render Pipeline)** でもOK
4. プロジェクト名: `HapbeatSamples`
5. **保存先**: `C:\UnityProjects\` 等の浅いパス（SDK リポジトリとは別の場所）
6. **Create project**

> **パスの長さに注意**: Windows はデフォルトで 260 文字のパス制限がある。
> `C:\Users\ユーザー名\Documents\...` のような深い場所だと
> Unity の Library/ 内で制限に引っかかる場合がある。
> ドライブ直下の短いパスを推奨。

### 1.2 XR Interaction Toolkit のサンプルをインポート

XRI 本体は VR テンプレートに入っているが、**サンプル（プレハブ集）は別途インポートが必要**。

1. メニューバー: **Window > Package Manager**
2. 左上のドロップダウンが「Packages: In Project」になっていることを確認
3. 一覧から **XR Interaction Toolkit** を選択
4. 右側パネルの **Samples** タブをクリック
5. 以下をそれぞれ **Import**:
   - **Starter Assets** — XR Origin プレハブ、テレポート等
   - **Hands Interaction Demo** — ポーク・ピンチの参考用
   - **XR Device Simulator** — HMD なしでテスト可能にする

> **Note**: パッケージ本体のインストールとサンプルのインポートは別の操作。
> パッケージは `manifest.json` に依存関係が書かれて自動インストールされるが、
> サンプルは Samples タブから手動で Import しないとプロジェクトに入らない。

### 1.3 XR プラットフォーム設定

#### 1.3.1 XR プラグインを有効にする

XR Plug-in Management には **Windows (PC)** タブと **Android** タブがあり、用途が異なる。

```
Edit > Project Settings > XR Plug-in Management
├── Windows タブ  ← Quest Link（Editor Play）で使われる
└── Android タブ  ← Build And Run（Quest スタンドアロン）で使われる
```

**設定手順**:

1. メニューバー: **Edit > Project Settings...**
2. 左パネル: **XR Plug-in Management**

3. **Windows タブ** を選択:
   - **Oculus** にチェック
   - Quest Link 経由で Editor Play する場合はこちらが使われる

4. **Android タブ** を選択:
   - **OpenXR** にチェック
   - Build And Run で Quest にビルドする場合はこちらが使われる

> **なぜ PC = Oculus、Android = OpenXR なのか**:
> Quest Link は PC 側の XR ランタイムを使う。Oculus プラグインは Meta 独自のランタイムを直接使うため、
> OpenXR ランタイムの設定に依存せず確実に動く。
> Android（Quest スタンドアロン）では OpenXR が標準で、Pico 等の他 HMD にも対応できる。
>
> **既知の問題（2026-04 時点）**: Unity 6 (6000.3.x) + Meta Horizon Link の OpenXR ランタイム +
> Quest Link の組み合わせで、コントローラートラッキングが動作しない事象を確認。
> Meta Horizon Link の OpenXR ランタイム設定は正常でも発生する。
> PC タブを Oculus に設定することで回避可能。Quest スタンドアロンビルド（Android = OpenXR）には影響しない。
> Pico / Vive 等の他 HMD 対応時に再検証が必要。

#### 1.3.2 OpenXR の詳細設定を開く

OpenXR にチェックを入れると、左パネルの **XR Plug-in Management** の下に **OpenXR** という子項目が現れる。これをクリックすると、右側に OpenXR の設定パネルが表示される。

```
左パネルの階層:
  XR Plug-in Management
  └── OpenXR           ← ★ ここをクリック
```

#### 1.3.3 Interaction Profiles の確認

右側パネルの中央付近に **Enabled Interaction Profiles** という一覧がある。
ここにはプロジェクトが対応するコントローラーの種類が登録されている。

VR テンプレートから作成した場合、以下が最初から入っていることが多い:
- **Meta Quest Touch Plus Controller Profile**（Quest 3S / 3 のコントローラー）
- **Oculus Touch Controller Profile**（Quest 2 のコントローラー）

**追加する Profile**:
- **Hand Interaction Profile** — 一覧になければ **+** ボタンをクリックして追加する。Zone C（ポークボタン等）でハンドトラッキングを使用するため必須。

その他、Vive や Pico にも対応したい場合は対応する Profile を追加する。

> **Note**: 「実際にテストするコントローラーの Profile だけを追加してください」
> という警告メッセージが表示されるが、Quest 用 Profile が入っていれば Quest 3S では動作する。

#### 1.3.4 OpenXR Feature Groups の確認

同じ画面の下部に **OpenXR Feature Groups** のタブがある。
ここではプラットフォーム固有の機能（パススルー、ハンドトラッキング等）を有効化できる。

VR テンプレートから作成した場合、基本的な項目は有効になっている。
**特に変更は不要。** Quest 3S の標準的な使い方であればデフォルトのままでOK。

### 1.4 ビルド設定

1. メニューバー: **File > Build Profiles...**
2. **Android** を選択 > **Switch Platform**（初回は数分かかる）
3. Texture Compression: **ASTC**

### 1.5 Hapbeat SDK のインポート

1. **Window > Package Manager**
2. 左上 **+** > **Add package from disk...**
3. `hapbeat-unity-sdk/package.json` を選択 > **Open**
4. "Hapbeat SDK" が一覧に表示されれば完了

---

## Step 2: サンプルのインポート

1. **Window > Package Manager**
2. **Hapbeat SDK** を選択
3. **Samples** タブを開く
4. 以下を全て **Import**:
   - **Basic Example**
   - **Player Demo**
   - **Creator Tutorial**

Import すると `Assets/Samples/Hapbeat SDK/0.1.0/` 以下にファイルがコピーされる。
同時に Editor スクリプト（シーンビルダー）も読み込まれ、メニューに項目が追加される。

---

## Step 3: シーン自動生成

### 3.1 BasicExample

1. メニューバー: **Hapbeat > Build Samples > 1. Basic Example**
2. 確認ダイアログで「生成する」をクリック
3. シーンが自動生成・保存される

**生成されるもの**:
- Main Camera + Directional Light
- [Hapbeat Event Router]（HapbeatManager + HapbeatDemo + HapbeatDemoUI）
- Canvas（Title / Status / Instructions / Log テキスト）
- 全ての参照が接続済み

**自動生成される Hierarchy 構造**:

```
BasicExample.unity
├── Main Camera
├── Directional Light
├── [Hapbeat Event Router]
│   ├── HapbeatManager         ← 自動追加
│   ├── HapbeatDemo            ← キーボード操作デモ
│   └── HapbeatDemoUI          ← UI 表示（Status/Log 接続済み）
├── Canvas (Screen Space - Overlay)
│   ├── Title                  ← "Hapbeat Basic Demo"
│   ├── Status                 ← 接続状態（HapbeatDemoUI が更新）
│   ├── Instructions           ← "Space: Play / S: Stop / X: Stop All / P: Ping"
│   └── Log                    ← イベントログ（HapbeatDemoUI が更新）
└── EventSystem
```

**テスト**: Play ボタンを押し、Space キーで動作確認。

**シーンの保存**:

シーンビルダーがサンプルフォルダ内への自動保存を試みるが、パスが見つからない場合は
未保存状態のままになる。次のシーン（3.2）を生成する際に保存を求められるので、
その前に手動で保存しておく。

1. メニューバー: **File > Save As...**
2. 保存先: `Assets/Samples/Hapbeat SDK/0.1.0/BasicExample/`
3. ファイル名: `BasicExample`
4. **Save**

> **Samples~ に直接保存できない？**
> `~` で終わるフォルダは Unity のファイルダイアログに表示されない。
> まず Assets/ 内に保存し、Step 5 でまとめて Samples~/ にコピーする。

### 3.2 PlayerDemo

PlayerDemo はゾーンごとに独立したシーンで構成される。メニュー UI でワンタップで切り替え。

#### シーン一括生成

1. メニューバー: **Hapbeat > Build Samples > 2a. Player Demo - All Scenes**
2. 確認ダイアログで「生成する」をクリック
3. Hub + Zone A/B/C/D の **5シーン** が連続で生成・保存される

> 個別に生成したい場合: `2b. Hub Only` / `2c. Zone A Only` / ... のメニューも使える

**生成される5シーン**:

| シーン | ファイル名 | 内容 |
|---|---|---|
| Hub | PlayerDemoHub.unity | ゾーン選択メニュー（大きなボタンパネル） |
| Zone A | PlayerDemoZoneA.unity | 能動フィードバック（パンチ・射撃・ドラム） |
| Zone B | PlayerDemoZoneB.unity | 受動フィードバック（雨・爆発・ドローン） |
| Zone C | PlayerDemoZoneC.unity | 操作系フィードバック（ポーク・グラブ） |
| Zone D | PlayerDemoZoneD.unity | 定位感デモ（空間音響ストリーミング） |

**各シーンの Hierarchy 構造**:

```
PlayerDemoHub.unity（ゾーン選択画面）
├── >>> XR Origin をここに配置 <<<
├── [Hapbeat Event Router]
│   └── HapbeatManager
├── Floor                            ← 暗めの Plane
├── Zone Select                      ← WorldSpace Canvas（大きなメニュー）
│   ├── Background
│   ├── SceneMenu                    ← ボタン→シーン切り替え（全接続済み）
│   └── Hub / Zone A / B / C / D    ← Button × 5
├── Welcome                          ← 説明テキスト
├── EventSystem
└── Directional Light
```

```
PlayerDemoZoneA.unity（能動フィードバック）
├── >>> XR Origin をここに配置 <<<
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   ├── DemoManager
│   └── UnityEventTrigger            ← 射撃反動 (action.shoot)
├── Floor                            ← 赤系の Plane
├── Navigation                       ← WorldSpace Canvas（小さいメニュー、他ゾーンに即移動）
│   └── SceneMenu
├── PunchingBag                      ← Cylinder
│   ├── Rigidbody (Mass=5)
│   ├── PunchingBag.cs               ← 物理復元 + 音 + ストリーミング
│   ├── AudioSource                  ← punch_impact.wav（自動接続）
│   └── HapbeatCollisionTrigger      ← impact.punch, VelocityScaled
├── Gun                              ← Cube(銃型)
│   ├── Rigidbody, XR Grab Interactable
│   ├── ShootingRange.cs             ← OnShoot → ストリーミング + Fire()
│   ├── AudioSource                  ← gunshot.wav（自動接続）
│   └── Muzzle
├── Target_1 〜 Target_3             ← Cube(板状)
│   ├── Rigidbody (Kinematic)
│   └── HapbeatCollisionTrigger      ← impact.target-hit
├── DrumPad_1 〜 DrumPad_4           ← Cylinder(薄い円盤, 色分け)
│   ├── DrumPad.cs                   ← drum_hit_N.wav（自動接続）
│   ├── AudioSource
│   └── HapbeatCollisionTrigger      ← impact.drum, VelocityScaled
├── EventSystem
└── Directional Light
```

```
PlayerDemoZoneB.unity（受動フィードバック）
├── >>> XR Origin をここに配置 <<<
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   └── DemoManager
├── Floor                            ← 青系の Plane
├── Navigation                       ← SceneMenu
├── Rain Room                        ← BoxCollider (Is Trigger)
│   ├── RainZone.cs                  ← Enter→ストリーミング, Exit→Stop
│   ├── AudioSource (Loop)           ← rain_loop.wav
│   └── Rain VFX                     ← ParticleSystem
├── Explosion Field
│   ├── ExplosionField.cs            ← ランダム間隔, 距離減衰ストリーミング
│   ├── AudioSource                  ← explosion.wav
│   ├── Explosion VFX
│   └── ExplosionPoint_1 〜 _3
├── Drone Defense
│   └── DroneDefense.cs              ← ※ Prefab は手動設定
├── EventSystem
└── Directional Light
```

```
PlayerDemoZoneC.unity（操作系フィードバック）
├── >>> XR Origin をここに配置 <<<
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   ├── DemoManager
│   └── UnityEventTrigger × 4       ← ポークボタン用
├── Floor                            ← 緑系の Plane
├── Navigation                       ← SceneMenu
├── PokeButton_1 〜 _4               ← Cube(小, 色分け)
│   ├── PokeButton.cs                ← ストリーミング + Fire()
│   └── AudioSource                  ← ui_click.wav
├── Shelf Board + GrabObject_1 〜 _3  ← Cube/Sphere/Cylinder
│   ├── XR Grab Interactable
│   ├── GrabFeedback.cs              ← grab.wav / release.wav
│   └── HapbeatCollisionTrigger      ← 投げた時
├── EventSystem
└── Directional Light
```

```
PlayerDemoZoneD.unity（定位感デモ）
├── >>> XR Origin をここに配置 <<<
├── [Hapbeat Event Router]
│   └── HapbeatManager
├── Floor                            ← 黄系の Plane
├── Navigation                       ← SceneMenu
├── Activation Zone                  ← SphereCollider (Is Trigger, r=5)
│   └── ZoneActivator                ← Enter→Activate, Exit→Deactivate
├── Orbiting Sound Source            ← Sphere(水色, プレイヤー追従なし、ゾーン中心を固定周回)
│   ├── AudioSource (Spatial Blend=1, Loop)
│   │   └── sine_100hz_1s.wav        ← 自動接続
│   ├── HapbeatAudioBridge           ← リアルタイムストリーミング（L/R パンニング反映）
│   └── SpatialAudioDemo.cs          ← ゾーン進入で開始、退出で停止
├── Stand Here                       ← Cylinder(薄い黄色)
├── Orbit Ring                       ← Cylinder(薄い水色, 軌道の可視化)
├── EventSystem
└── Directional Light
```

---

#### 3.2.1 シーン生成後に手動で行うこと

**全シーン共通**:

##### A. XR Origin の配置（全5シーン）

各シーンで同じ操作を行う:

1. Hierarchy で `>>> XR Origin をここに配置 <<<` を**削除**
2. Project: `Assets/Samples/XR Interaction Toolkit/<version>/Starter Assets/Prefabs/`
3. **XR Origin (XR Rig)** プレハブを Hierarchy にドラッグ
4. Position を `(0, 0, 0)` に設定

> **XR Device Simulator**: Edit > Project Settings > XR Plug-in Management > XR Interaction Toolkit で
> 「Use XR Device Simulator in scenes」を有効にすると HMD なしでテスト可能。

##### B. Build Settings にシーンを登録（必須）

SceneMenu でシーン切り替えするために、**Editor Play でも Build And Run でも**全シーンの登録が必要。
Unity 6 では Editor Play 時でも `SceneManager.LoadScene()` は Build Settings に登録されたシーンのみ読み込める。

1. メニューバー: **File > Build Profiles...**
2. **Scene List** セクションで **Add Open Scenes** または Project からドラッグで以下を追加:
   - `PlayerDemoHub`
   - `PlayerDemoZoneA`
   - `PlayerDemoZoneB`
   - `PlayerDemoZoneC`
   - `PlayerDemoZoneD`

> これを忘れるとシーン切り替え時に「Scene couldn't be loaded」エラーになる。

##### C. Player タグの設定（Zone B, D で必要）

RainZone / ZoneActivator がプレイヤーを `Player` タグで検知する。

1. Zone B, D の各シーンで XR Origin 内: `XR Origin > Camera Offset > Main Camera` を選択
2. Inspector 上部の **Tag** > `Player`

##### D. Audio Clip の確認

Audio/ フォルダにファイルを事前配置してあれば自動接続される。
未接続の場合は Inspector から手動でドラッグ。

| シーン | 対象 GO | フィールド | AudioClip |
|---|---|---|---|
| Zone A | PunchingBag | Impact Clip | punch_impact.wav |
| Zone A | Gun (ShootingRange) | Shot Clip | gunshot.wav |
| Zone A | DrumPad_1〜4 | Drum Clip | drum_hit_1〜4.wav |
| Zone B | Rain Room (RainZone) | Haptic Clip + AudioSource | rain_loop.wav |
| Zone B | Explosion Field | Explosion Clip | explosion.wav |
| Zone C | PokeButton_1〜4 | Press Clip | ui_click.wav |
| Zone C | GrabObject_1〜3 | Grab/Release Clip | grab.wav / release.wav |
| Zone D | Orbiting Sound Source | AudioClip | sine_100hz_1s.wav |

##### E. Zone B: Drone / Projectile Prefab の作成

1. **Drone**: Sphere (0.5, 0.2, 0.5), 赤系マテリアル → Prefab 化
2. **Projectile**: Sphere (0.1, 0.1, 0.1), Rigidbody (Gravity OFF), SphereCollider (Is Trigger), HapbeatCollisionTrigger (被弾, TriggerEnter) → Prefab 化
3. Zone B シーンの DroneDefense Inspector に両 Prefab をドラッグ

##### F. 見た目の調整（任意）

- オブジェクトの位置・スケール
- マテリアルの色・ライティング

### 3.3 Creator Tutorial

1. メニューバー: **Hapbeat > Build Samples > 3. Creator Tutorial (Before + After)**
2. 2シーンが連続で生成される

**Before シーン（触覚なし）の Hierarchy**:

```
CreatorTutorial_Before.unity
├── >>> XR Origin をここに配置 <<<
├── Environment
│   ├── Floor                    ← Plane
│   ├── Wall Back                ← Cube（奥の壁）
│   ├── Wall Left                ← Cube（左壁）
│   └── Wall Right               ← Cube（右壁）
├── Gun                          ← Cube(銃型)
│   ├── Rigidbody
│   ├── XR Grab Interactable
│   ├── SimpleShooter.cs         ← Raycast 射撃, OnShoot イベント
│   ├── AudioSource
│   └── Muzzle                   ← 空 GO（銃口位置）
├── Targets
│   └── Target_1 〜 Target_5     ← Cube(板状, 横並び)
│       ├── Rigidbody (Kinematic)
│       ├── Target.cs            ← スコア加算 + 倒れて復活
│       └── AudioSource
├── Obstacle                     ← Cube(赤, 往復移動用)
│   ├── Rigidbody (Kinematic)
│   ├── BoxCollider              ← 物理判定
│   └── BoxCollider (Is Trigger) ← 被弾判定（大きめ）
├── Canvas (Screen Space)
│   ├── ScoreText                ← "Score: 0"
│   ├── TimerText                ← "Time: 60s"
│   └── ScoreUI.cs               ← スコア・タイマー管理
├── EventSystem
└── Directional Light
```

**After シーン（Hapbeat 統合済み）の Hierarchy**:

Before と同じ構成に、以下が**追加**される:

```
追加要素:
├── [Hapbeat Event Router]       ← 新規追加
│   ├── HapbeatManager
│   └── UnityEventTrigger        ← 射撃反動 (action.shoot)
│       └── Gun の OnShoot() → Fire() 接続済み
│
├── Target_1 〜 Target_5 に追加:
│   └── HapbeatCollisionTrigger  ← impact.target-hit
│
└── Obstacle に追加:
    └── HapbeatCollisionTrigger  ← impact.hit-received, TriggerEnter
```

**シーンの保存**:

Creator Tutorial は Before → After の順で2シーンが連続生成される。
各シーンはビルダーが自動保存を試みるが、見つからない場合は手動で保存する。

- Before: `Assets/Samples/Hapbeat SDK/0.1.0/CreatorTutorial/CreatorTutorial_Before.unity`
- After: `Assets/Samples/Hapbeat SDK/0.1.0/CreatorTutorial/CreatorTutorial_After.unity`

---

## Step 4: Audio ファイルの準備

各サンプルの Audio フォルダにファイルを事前に配置しておくと、
Step 3 のシーン生成時に自動接続される（ファイル名で検索される）。
手動でも Inspector からドラッグで設定可能。

> **対応フォーマット**: wav / mp3 / ogg いずれも使用可能。Unity がインポート時に内部形式に変換する。
> wav が最も確実（非圧縮のため品質劣化なし）。mp3 もデモ用途なら問題ない。

> **ストリーミング用の推奨仕様**: モノラル・16kHz 程度・短め。
> デバイスに直接送信されるため、サイズが小さいほうが遅延が少ない。

入手先: [freesound.org](https://freesound.org)（CC0 のものを選ぶ）

### SDK 同梱の Audio ファイル一覧

**BasicExample** (`Samples~/BasicExample/Audio/`):

| ファイル名 | 内容 | 備考 |
|---|---|---|
| sine_100hz_1s.wav | 100Hz 正弦波 1秒 | 同梱済み |

**PlayerDemo** (`Samples~/PlayerDemo/Audio/`):

| ファイル名 | 用途 | スクリプト | 推奨仕様 |
|---|---|---|---|
| punch_impact.wav | パンチ衝突 | PunchingBag | 短い打撃音, 0.1-0.3秒 |
| gunshot.wav | 射撃反動 | ShootingRange | 銃声, 0.1-0.3秒 |
| drum_hit_1.wav | ドラム（パッド1） | DrumPad | パーカッション, 0.1-0.2秒 |
| drum_hit_2.wav | ドラム（パッド2） | DrumPad | 同上、異なる音色 |
| drum_hit_3.wav | ドラム（パッド3） | DrumPad | 同上 |
| drum_hit_4.wav | ドラム（パッド4） | DrumPad | 同上 |
| rain_loop.wav | 雨のループ | RainZone | 環境音ループ, 3-5秒 |
| explosion.wav | 爆発 | ExplosionField | 爆発音, 0.5-1秒 |
| ui_click.wav | ボタン押下 | PokeButton | クリック音, 0.05-0.1秒 |
| grab.wav | 掴む | GrabFeedback | 短い「カチッ」, 0.05-0.1秒 |
| release.wav | 離す | GrabFeedback | 短い「パッ」, 0.05-0.1秒 |
| target_hit.wav | ターゲット命中 | (CollisionTrigger) | 命中音, 0.1-0.3秒 |

**CreatorTutorial** (`Samples~/CreatorTutorial/Audio/`):

| ファイル名 | 用途 | スクリプト |
|---|---|---|
| gunshot.wav | 射撃反動 | SimpleShooter |
| target_hit.wav | ターゲット命中 | Target |
| hit_received.wav | 被弾 | (CollisionTrigger) |

> **Note**: CreatorTutorial は PlayerDemo の Audio ファイルを流用可能。
> Before シーンでは `_hapticClip` が未設定（触覚なし）、
> After シーンで `_hapticClip` を設定して触覚ありにする、という Before/After 比較の構成。

---

## Step 5: Samples~/ にコピーして push

Unity プロジェクトで作成・調整したシーンファイル等を SDK リポジトリにコピーする。

### 5.1 パスの対応

```
コピー元（Unity プロジェクト内）:
  C:\UnityProjects\HapbeatSamples\Assets\Samples\Hapbeat SDK\0.1.0\<サンプル名>\

コピー先（SDK リポジトリ内）:
  hapbeat-unity-sdk\Samples~\<サンプル名>\
```

### 5.2 コピースクリプト

以下を実行（パスは自分の環境に合わせて変更）:

```bash
PROJ="C:/UnityProjects/HapbeatSamples"
SDK="C:/GitHub/Hapbeat/hapbeat-sdk-workspace/hapbeat-unity-sdk"
SRC="${PROJ}/Assets/Samples/Hapbeat SDK/0.1.0"

# ---- BasicExample ----
cp "${SRC}/BasicExample/BasicExample.unity"      "${SDK}/Samples~/BasicExample/"
cp "${SRC}/BasicExample/BasicExample.unity.meta"  "${SDK}/Samples~/BasicExample/"
cp "${SRC}/BasicExample/"*.cs.meta                "${SDK}/Samples~/BasicExample/" 2>/dev/null
cp "${SRC}/BasicExample/Editor/"*.meta            "${SDK}/Samples~/BasicExample/Editor/" 2>/dev/null

# ---- PlayerDemo ----
cp "${SRC}/PlayerDemo/PlayerDemo.unity"           "${SDK}/Samples~/PlayerDemo/"
cp "${SRC}/PlayerDemo/PlayerDemo.unity.meta"      "${SDK}/Samples~/PlayerDemo/"
cp -r "${SRC}/PlayerDemo/EventMaps/"              "${SDK}/Samples~/PlayerDemo/EventMaps/"
cp -r "${SRC}/PlayerDemo/Prefabs/"                "${SDK}/Samples~/PlayerDemo/Prefabs/"
cp -r "${SRC}/PlayerDemo/Audio/"                  "${SDK}/Samples~/PlayerDemo/Audio/"
cp "${SRC}/PlayerDemo/Scripts/"*.meta             "${SDK}/Samples~/PlayerDemo/Scripts/" 2>/dev/null
cp "${SRC}/PlayerDemo/Editor/"*.meta              "${SDK}/Samples~/PlayerDemo/Editor/" 2>/dev/null

# ---- CreatorTutorial ----
cp "${SRC}/CreatorTutorial/CreatorTutorial_Before.unity"      "${SDK}/Samples~/CreatorTutorial/"
cp "${SRC}/CreatorTutorial/CreatorTutorial_Before.unity.meta" "${SDK}/Samples~/CreatorTutorial/"
cp "${SRC}/CreatorTutorial/CreatorTutorial_After.unity"       "${SDK}/Samples~/CreatorTutorial/"
cp "${SRC}/CreatorTutorial/CreatorTutorial_After.unity.meta"  "${SDK}/Samples~/CreatorTutorial/"
cp -r "${SRC}/CreatorTutorial/EventMaps/"                     "${SDK}/Samples~/CreatorTutorial/EventMaps/"
cp "${SRC}/CreatorTutorial/Scripts/"*.meta         "${SDK}/Samples~/CreatorTutorial/Scripts/" 2>/dev/null
cp "${SRC}/CreatorTutorial/Editor/"*.meta          "${SDK}/Samples~/CreatorTutorial/Editor/" 2>/dev/null
```

**重要**: `.meta` ファイルも必ずコピーする。欠落すると Missing Script になる。

### 5.3 git commit

```bash
cd hapbeat-unity-sdk
git add Samples~/
git commit -m "[unity-sdk] サンプルシーン追加 (BasicExample / PlayerDemo / CreatorTutorial)"
```

---

## Quest 3 でのプレビュー

開発中のテスト方法は3つある。用途に応じて使い分ける。

| 方法 | 速度 | ビルド | 用途 |
|---|---|---|---|
| **Editor Play + XR Simulator** | 即時 | 不要 | ロジック・UI の確認（VR 体験なし、キーボード＋マウス操作） |
| **Editor Play + Quest Link** | 即時 | 不要 | PC の Editor で Play → Quest で VR 表示。開発中の気軽なテストに最適 |
| **Build And Run** | 数分 | 必要 | Quest スタンドアロン動作の最終確認。PC 不要で動く |

### Quest Link（開発中の推奨方法）

PC の Unity Editor で Play ボタンを押すだけで Quest に VR 表示される。ビルド不要で即時反映。

#### セットアップ（初回のみ）

1. PC に **Meta Quest Link** アプリをインストール
   - [meta.com/quest/setup](https://www.meta.com/quest/setup/) からダウンロード
   - インストール後、起動しておく
2. Quest 3 側: **設定 > Quest Link**
   - Quest Link を**有効化**
   - **Air Link を有効化**（無線接続の場合）
3. PC と Quest 3 を**同じ Wi-Fi** に接続

#### 使い方

1. Quest 3 を装着
2. Quest 3 のホーム画面で **Quest Link** を選択
3. 利用可能な PC が表示される → **接続**
4. Quest の画面に PC のデスクトップが表示される
5. PC 側で Unity Editor の **Play** ボタンを押す
6. Quest に VR シーンが表示される

> **Quest Link と Build And Run の違い**:
> - Quest Link: **PC で処理**し Quest は表示のみ。Editor のコード変更が即反映。開発のイテレーションが速い
> - Build And Run: **Quest 単体で動作**。PC 不要。パフォーマンスの実機確認やデモ展示に使う
>
> **注意**: Quest Link 使用中は PC のグラフィック性能で動くため、
> Quest スタンドアロンでのパフォーマンス（72fps 維持等）は Build And Run で別途確認する。

### Build And Run（スタンドアロン確認・デモ展示用）

PC と Quest 3 を同じ Wi-Fi に接続し、無線でビルド＆実行する手順。

### 事前準備（初回のみ）

1. **Quest 3 を開発者モードにする**:
   - スマホの Meta Quest アプリ > デバイス > 開発者モード > ON
   - Meta 開発者アカウントが必要（[developer.oculus.com](https://developer.oculus.com) で無料登録）

2. **ADB のインストール**:
   - Android SDK Platform Tools をダウンロード（Unity Hub でインストール済みの場合もある）
   - Unity のパス: `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\`
   - このパスを環境変数 PATH に追加しておくと便利

3. **USB で初回ペアリング**:
   - Quest 3 を USB-C で PC に接続
   - Quest 3 に「USB デバッグを許可しますか？」と表示される → **許可**
   - ターミナルで `adb devices` → デバイスが表示されることを確認

### 無線接続の設定

1. USB 接続した状態で以下を実行:
   ```bash
   adb tcpip 5555
   ```
   > **「error: more than one device/emulator」が出る場合**:
   > XR Simulator 等の他デバイスが認識されている。`adb devices` で一覧を確認し、
   > Quest のシリアル番号を指定して実行:
   > ```bash
   > adb devices                              # シリアル番号を確認
   > adb -s <シリアル番号> tcpip 5555         # 実機を指定
   > ```
2. Quest 3 の IP アドレスを確認:
   - Quest 3: 設定 > Wi-Fi > 接続中のネットワーク > IP アドレスをメモ
   - または: `adb -s <シリアル番号> shell ip addr show wlan0` で確認
3. USB ケーブルを外す
4. 無線で接続:
   ```bash
   adb connect <Quest3のIPアドレス>:5555
   ```
5. `adb devices` でデバイスが表示されれば成功

### Unity からのビルド＆実行

1. **File > Build Profiles...**
2. Android プラットフォームが選択されていることを確認
3. **Run Device** ドロップダウンで Quest 3 を選択
   - 表示されない場合: **Refresh** ボタンを押す
   - それでも出ない場合: `adb devices` で接続を確認
4. **Build And Run** を押す
5. ビルドが完了すると Quest 3 に自動的にインストール・起動される

> **ビルド時間の短縮**:
> - Development Build にチェック（デバッグ用、ビルドが速い）
> - Scripting Backend: IL2CPP のままでOK（Quest はIL2CPP必須）
> - 初回ビルドは数分かかるが、2回目以降はインクリメンタルで速くなる

> **Hapbeat デバイスとの接続**:
> Quest 3 と Hapbeat デバイスが同じ Wi-Fi ネットワーク上にあれば、
> HapbeatManager が UDP ブロードキャストで自動的にデバイスと通信する。
> Quest 3 の Wi-Fi と Hapbeat の Wi-Fi が同じネットワークであることを確認。

---

## トラブルシューティング

### Q: メニューに「Hapbeat > Build Samples」が表示されない

→ サンプルが Import されていない。Package Manager > Hapbeat SDK > Samples タブから Import する。
→ Console にコンパイルエラーがないか確認。エラーがあるとメニューが登録されない。

### Q: XRGrabInteractable の警告が出る

→ XR Interaction Toolkit がインストールされていない場合に出る。
→ Package Manager から XRI をインストールし、メニューから再度シーンを生成する。

### Q: Samples~ に Save As できない

→ `~` で終わるフォルダは Unity Editor の File ダイアログに表示されない。
→ エクスプローラで直接パスを入力するか、Assets 内に保存して後でコピーする。

### Q: Missing Script が出る

→ `.meta` ファイルが欠落している。Samples~/ にコピーする際、全ての `.meta` を含めること。

### Q: Play しても VR で動かない

→ Edit > Project Settings > XR Plug-in Management で OpenXR が有効か確認。
→ HMD なしテスト: XR Device Simulator を有効にする（Edit > Project Settings > XR Interaction Toolkit）。

---

## 更新履歴

- 2026-04-13: SDK 内プロジェクト方式を撤回、独立プロジェクト方式に戻す（Windows パス長制限のため）
- 2026-04-13: Editor スクリプトによる自動生成方式に全面改訂（Unity 6000.3.12f1 対応）
- 2026-04-13: 初版作成（手動方式）
