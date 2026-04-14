# 指示書: VR サンプルシーンの作成

- **作成日**: 2026-04-07
- **作成元**: hapbeat-sdk-workspace マイルストーン計画セッション
- **対象リポジトリ**: hapbeat-unity-sdk
- **目的**: ユーザーが VR HMD（Quest 等）で Hapbeat をすぐに試せるサンプルシーンを提供する

---

## 背景

現在の SDK には `Samples~/BasicExample/HapbeatDemo.cs`（キーボード操作のみ）が1つあるだけで、.unity シーンファイルも存在しない。VR ユーザーが SDK を導入してすぐに試せるサンプルがない。

README には XR Interaction Toolkit との接続方法が詳しく書かれているが、実際に動くシーンがない状態。ユーザーが「既存の VR テンプレートに触覚を加える」手順を、動くサンプルで示す。

**方針**: 作り込まない。最小限のスクリプトとシーン構成で、SDK の各機能（直接呼び出し、EventMap + Trigger、UnityEventTrigger）が VR 環境で動くことを示す。

---

## CLAUDE.md の更新

まず、CLAUDE.md の「まだ作らないもの」セクションを更新する:

**変更前**:
```
## まだ作らないもの

- VR 専用機能
```

**変更後**:
```
## まだ作らないもの

- VR 専用の高度な機能（ボディトラッキング連動、ハンドトラッキング連動等）

## VR サンプル方針

- VR 専用 API は作らない。既存の API（Play, EventMap, Trigger）で VR 対応できることを示す
- XR Interaction Toolkit の UnityEvent と HapbeatUnityEventTrigger を繋ぐだけで動く
- サンプルは Samples~ に配置し、UPM の Import Samples で導入可能にする
```

---

## タスク一覧

### タスク 1: BasicExample にシーンファイルを追加

**対象**: `Samples~/BasicExample/`

現在 `BasicExample.unity.meta` のみ存在し、`BasicExample.unity` が欠落している。

**作成するシーン構成**:
```
BasicExample.unity
├── Main Camera
├── Directional Light
├── [Hapbeat Event Router]    ← GameObject > Hapbeat > Event Router と同等
│   ├── HapbeatManager (自動追加)
│   └── HapbeatDemo.cs (既存スクリプト)
├── Canvas (UI)
│   ├── Text - Title ("Hapbeat Basic Demo")
│   ├── Text - Status (HapbeatManager の接続状態表示)
│   ├── Text - Instructions ("Space: Play / S: Stop / X: Stop All / P: Ping")
│   └── Text - Log (直近の送信ログ表示)
└── EventSystem
```

**追加スクリプト**: `HapbeatDemoUI.cs`
- HapbeatManager の接続状態を Text に表示
- OnConnected / OnDisconnected / OnPong イベントをログ Text に表示
- 最小限の UI スクリプト（30-50行程度）

---

### タスク 2: VR サンプルの作成

**新規ディレクトリ**: `Samples~/VRExample/`

**目的**: XR Interaction Toolkit が入った Unity プロジェクトにインポートして、VR コントローラーで Hapbeat を操作できることを示す。

**ファイル構成**:
```
Samples~/VRExample/
├── VRExample.unity              ← サンプルシーン
├── VRExample.unity.meta
├── HapbeatVRDemo.cs             ← メインデモスクリプト
├── HapbeatVRDemo.cs.meta
├── HapbeatVRSetupGuide.cs       ← Editor ヘルパー（セットアップ検証）
├── HapbeatVRSetupGuide.cs.meta
├── VRExampleEventMap.asset      ← EventMap プリセット
├── VRExampleEventMap.asset.meta
└── README.md                    ← セットアップ手順
```

#### HapbeatVRDemo.cs

**方針**: XR Interaction Toolkit に直接依存しない（asmdef で参照しない）。UnityEvent / InputSystem 経由で接続する。

```csharp
using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Samples
{
    /// <summary>
    /// VR サンプル: コントローラー入力で Hapbeat を操作するデモ。
    /// XR Interaction Toolkit の UnityEvent から Fire メソッドを呼ぶ構成。
    /// </summary>
    public class HapbeatVRDemo : MonoBehaviour
    {
        [Header("Hapbeat Settings")]
        [SerializeField] private HapbeatEventMap _eventMap;
        
        [Header("Event Mappings")]
        [Tooltip("右トリガー押下時のイベント")]
        [SerializeField] private string _rightTriggerEvent = "input.trigger-right";
        
        [Tooltip("左トリガー押下時のイベント")]
        [SerializeField] private string _leftTriggerEvent = "input.trigger-left";
        
        [Tooltip("グリップ押下時のイベント")]
        [SerializeField] private string _gripEvent = "input.grip";
        
        [Tooltip("衝突時のイベント")]
        [SerializeField] private string _collisionEvent = "impact.hit";

        // UnityEvent から呼べる公開メソッド
        public void OnRightTrigger()
        {
            HapbeatManager.Instance?.Play(_rightTriggerEvent);
        }
        
        public void OnLeftTrigger()
        {
            HapbeatManager.Instance?.Play(_leftTriggerEvent);
        }
        
        public void OnGrip()
        {
            HapbeatManager.Instance?.Play(_gripEvent);
        }
        
        public void OnCollision(float velocity)
        {
            float gain = Mathf.Clamp01(velocity / 10f);
            HapbeatManager.Instance?.Play(_collisionEvent, gain);
        }
    }
}
```

**ポイント**:
- XR Interaction Toolkit を直接 using しない（依存を作らない）
- 各メソッドを UnityEvent から呼ぶだけの構成
- Inspector でイベント ID を設定変更可能

#### HapbeatVRSetupGuide.cs

**Editor 専用スクリプト**（`#if UNITY_EDITOR`）:
- シーンに必要なコンポーネントが揃っているかチェック
- HapbeatManager の存在確認
- XR Interaction Toolkit がプロジェクトにインストールされているか確認（Type.GetType で判定）
- 不足があれば Console に警告 + 修正手順を表示
- **シンプルに**: 30-50行の OnValidate / Awake レベル

#### VRExample.unity シーン構成

```
VRExample.unity
├── Directional Light
├── [Hapbeat Event Router]
│   ├── HapbeatManager
│   ├── HapbeatVRDemo.cs
│   ├── HapbeatVRSetupGuide.cs
│   └── UnityEventTrigger × 3 (右トリガー / 左トリガー / グリップ)
├── Floor (Plane, scale 10x10)
├── Cube (落下オブジェクト、CollisionTrigger 付き)
│   ├── Rigidbody
│   ├── Box Collider
│   └── HapbeatCollisionTrigger (Event: impact.hit, VelocityScaled)
├── Canvas (WorldSpace)
│   ├── Text - Title ("Hapbeat VR Demo")
│   ├── Text - Status (接続状態)
│   └── Text - Instructions
└── EventSystem
```

**注意**: シーンには XR Origin を含めない。ユーザーの既存 XR Origin（VR テンプレート由来）にこのシーンの要素を追加する、または Additive ロードする想定。

#### VRExampleEventMap.asset

以下のエントリを含む EventMap ScriptableObject:

| displayName | eventId | gain | notes |
|---|---|---|---|
| 右トリガー | input.trigger-right | 0.5 | 右コントローラートリガー |
| 左トリガー | input.trigger-left | 0.5 | 左コントローラートリガー |
| グリップ | input.grip | 0.7 | グリップボタン |
| 衝突 | impact.hit | 1.0 | 物体への衝突（速度連動） |
| 着地 | impact.landing | 0.3 | 地面への着地 |

#### README.md（VRExample 内）

以下の構成で記述:

```markdown
# Hapbeat VR Sample

## 前提条件
- Unity 2021.3 以上
- XR Interaction Toolkit がインストール済み
- XR Management で対象プラットフォーム（Quest / OpenXR）を設定済み

## セットアップ手順

### Step 1: サンプルをインポート
1. Package Manager > Hapbeat SDK > Samples > VR Example > Import

### Step 2: 既存 VR シーンに統合する方法

#### 方法 A: 既存シーンに要素を追加（推奨）
1. VR テンプレートのシーンを開く
2. Hapbeat Event Router を作成: GameObject > Hapbeat > Event Router
3. HapbeatVRDemo コンポーネントを追加
4. XR Controller の Interactor Events から HapbeatVRDemo のメソッドを接続:
   - Right Controller > XR Ray Interactor > On Activated → HapbeatVRDemo.OnRightTrigger()
   - Left Controller > XR Ray Interactor > On Activated → HapbeatVRDemo.OnLeftTrigger()

#### 方法 B: サンプルシーンを参考にする
1. VRExample シーンを開く
2. [Hapbeat Event Router] 以下の構成を確認
3. 自分のシーンに同じ構成をコピー

### Step 3: Hapbeat デバイスの準備
1. Hapbeat デバイスの電源を入れる
2. デバイスが同じ Wi-Fi ネットワークに接続されていることを確認
   - Quest の場合: Quest と Hapbeat が同じルーターに接続
   - ルーターなしの場合: Hapbeat を SoftAP モードにし、Quest から接続
3. Play モードに入ると自動的にデバイスを検出

### 接続シナリオ別の設定

| シナリオ | 設定 |
|---|---|
| Quest + ルーター + Hapbeat | 全デバイスを同じ Wi-Fi に接続。設定不要 |
| Quest + Hapbeat のみ（ルーターなし） | Hapbeat を SoftAP モードに。Quest から Hapbeat の AP に接続 |
| PC VR + ルーター + Hapbeat | 全デバイスを同じ LAN に接続。設定不要 |
```

---

### タスク 3: package.json の samples セクション更新

**対象**: `package.json`

現在 samples セクションがない場合は追加する:

```json
{
  "samples": [
    {
      "displayName": "Basic Example",
      "description": "キーボードで Hapbeat を操作する最小サンプル",
      "path": "Samples~/BasicExample"
    },
    {
      "displayName": "VR Example",
      "description": "VR コントローラーで Hapbeat を操作するサンプル（XR Interaction Toolkit 連携）",
      "path": "Samples~/VRExample"
    }
  ]
}
```

---

### タスク 4: README.md の更新

**対象**: `README.md`（ルート）

以下のセクションを追加:

```markdown
## サンプルシーン

Package Manager > Hapbeat SDK > Samples からインポートできます。

| サンプル | 内容 | 前提 |
|---|---|---|
| Basic Example | キーボード操作（Space/S/X/P）で基本 API を確認 | なし |
| VR Example | VR コントローラーで触覚を操作。XR Interaction Toolkit 連携 | XR Interaction Toolkit |

### VR クイックスタート

1. Unity Hub で VR テンプレートからプロジェクトを作成
2. Hapbeat SDK をインポート（Package Manager > Add from disk）
3. Samples > VR Example をインポート
4. 既存の VR シーンに Hapbeat Event Router を追加
5. XR Controller のイベントと HapbeatVRDemo のメソッドを接続
6. Hapbeat デバイスを同じネットワークに接続して Play
```

---

## 実装上の注意

1. **XR Interaction Toolkit に直接依存しない**: asmdef に XR パッケージの参照を追加しない。UnityEvent 経由の疎結合を維持する
2. **InputSystem パッケージへの依存も避ける**: Input.GetKeyDown は BasicExample のみ。VRExample は UnityEvent ベース
3. **.unity シーンファイル**: Unity Editor で作成する必要がある。スクリプトだけ先に作成し、シーンの組み立てはエディタ上で行う
4. **.meta ファイル**: Unity Editor が自動生成するので手動作成不要
5. **ScriptableObject (.asset)**: Unity Editor で Create する必要がある。README でユーザーに作成手順を示す。もしくはスクリプトで CreateAssetMenu を提供する

---

## 完了条件

- [ ] BasicExample に .unity シーンファイルが存在し、Play できる
- [ ] VRExample ディレクトリに全ファイルが揃っている
- [ ] HapbeatVRDemo.cs が XR Interaction Toolkit に直接依存していない
- [ ] package.json に samples セクションが追加されている
- [ ] README に VR クイックスタートが追記されている
- [ ] CLAUDE.md の「まだ作らないもの」が更新されている
