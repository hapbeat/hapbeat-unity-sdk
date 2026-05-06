# Editor メニュー監査 (v0.1.0 リリース整理用)

作業日: 2026-05-07

## 1. BasicExample テキスト調整箇所

すべて [`Samples~/BasicExample/Editor/BasicExampleSceneBuilder.cs`](../Samples~/BasicExample/Editor/BasicExampleSceneBuilder.cs) の `Build()` メソッド内 (行 94〜127 付近)。再実行で反映するには `Hapbeat → Build Samples → 1. Basic Example` を再生成。

| 要素 | 行 | パラメータ | 備考 |
|---|---|---|---|
| Title 文字列 | 105 | `"Hapbeat Basic Demo"` | `CreateText` 第 3 引数 |
| Title フォントサイズ | 106 | `28` | `CreateText` 第 5 引数 |
| Title 位置 | 106 | `Vector2(0, -40)` | 第 7 引数 (canvas 上端からの y オフセット) |
| Status 位置 | 108 | `Vector2(0, -80)` | 同上。Title から 40px 下 |
| Status フォントサイズ | 108 | `20` | |
| Instructions キー一覧 | 110 | `{ "Space", "R", "F", "S", "C" }` | 表示と `BindKey` の `KeyCode` を **同時に** 変更すること (130-138 行) |
| Instructions 説明一覧 | 111-118 | string[] | キーと同じ順序 |
| Instructions フォントサイズ | 119 | `fontSize: 16` | |
| Instructions y 位置 | 119 | `yOffset: -120` | |
| Instructions キー色 | 120 | `new Color(0.43f, 0.78f, 1.0f)` | soft cyan |
| Log 位置・サイズ | 121-127 | `Rect` を直接書換 | 下半分全域 |
| Log フォントサイズ | 122 | `16` | |
| Log の最大行数 | (overlay) | `_maxLogLines` | `HapbeatStatusOverlay` の SerializedField (default 8) |
| 完了ダイアログのキー表記 | 155 | `"Space / R / F / S / C"` | UI と独立しているので忘れがち |

ヘルパー関数:
- `CreateText(parent, name, text, alignment, fontSize, anchorPos, offset)` — 単純な中央寄せ Text
- `CreateInstructions(parent, keys, descriptions, fontSize, yOffset, keyColor)` — 2 カラム (右寄せキー / 左寄せ説明)。コロン位置は canvas 中央固定

---

## 2. 現状のメニュー一覧

### `Hapbeat/` (トップレベル)

| 項目 | order | 出所 | 種別 | ユーザー向け? |
|---|---|---|---|---|
| Settings | - | HapbeatSettingsWindow.cs | Window | ◯ |
| Event Map | - | HapbeatEventMapWindow.cs | Window | ◯ |
| Batch Setup | 20 | HapbeatBatchSetupWindow.cs | Window | ◯ |
| Create Event Router | 50 | HapbeatEventMapWindow.cs | Action (シーン配置) | ◯ |
| Setup/Create HapbeatSDK Folder | 90 | HapbeatSDKFolderCreator.cs | Action (フォルダ生成) | ◯ |
| Build Samples/1. Basic Example | 100 | BasicExampleSceneBuilder.cs | Action (シーン生成) | ◯ |
| Build Samples/2. Tutorial (full scene) | 110 | TutorialSceneBuilder.cs | Action (シーン生成) | ◯ |
| **Tutorial/Strip Hapbeat (Tutorial → Tutorial_Plain)** | 200 | TutorialSceneBuilder.cs | 開発用 (Hapbeat 抜きシーン生成) | **✗** |
| **Start Log Recording** | 520 | HapbeatDebugLogRecorder.cs | Debug (トップ階層に露出) | △ (Debug 配下に集約すべき) |
| **Stop Log Recording** | 521 | HapbeatDebugLogRecorder.cs | Debug | △ 同上 |
| Debug/Log Drag&Drop Events | 500 | HapbeatEventMapWindow.cs | Debug toggle | ✗ (開発用) |
| Debug/Close Edit-mode Transport | 510 | HapbeatEditorTransport.cs | Debug | △ |
| Debug/Attach Event Logger to Selected | 510 | HapbeatEventLoggerMenu.cs | Debug | ◯ (ユーザーも使う) |
| Debug/Remove Event Logger Wiring from Selected | 511 | HapbeatEventLoggerMenu.cs | Debug | ◯ |
| Debug/Logs/Reveal Current File | 522 | HapbeatDebugLogRecorder.cs | Debug | △ |
| Debug/Logs/Open Logs Folder | 523 | HapbeatDebugLogRecorder.cs | Debug | △ |
| Debug/Logs/Dump Last Recording to Console | 524 | HapbeatDebugLogRecorder.cs | Debug | △ |
| **Migrate Legacy Entry References** | 900 | HapbeatMigrateLegacyReferences.cs | One-shot migration | ✗ (リリース前 repo なので不要) |

### `Window/Hapbeat/` (重複)

| 項目 | 出所 | 種別 |
|---|---|---|
| Settings | HapbeatSettingsWindow.cs | Window (重複) |
| Event Map | HapbeatEventMapWindow.cs | Window (重複) |
| Batch Setup | HapbeatBatchSetupWindow.cs | Window (重複) |

### `GameObject/Hapbeat/`

| 項目 | order | 出所 | 種別 |
|---|---|---|---|
| Event Router | 10 | HapbeatEventMapWindow.cs | GameObject 生成 (Unity 慣例的に正) |

### `Assets/Create/Hapbeat/`

| 項目 | order | 出所 | 種別 |
|---|---|---|---|
| Config | 1 | HapbeatConfig.cs | ScriptableObject 生成 |
| Event Map | 2 | HapbeatEventMap.cs | ScriptableObject 生成 |

### コンポーネント (`AddComponentMenu("Hapbeat/...")`)

Runtime/ 配下 11 個。`Add Component → Hapbeat/` 配下に並ぶ。
HapbeatActionHelper / AnimatorTrigger / CollisionTrigger / Event / EventLogger / KeyDispatcher / ParameterBinding / SequenceTrigger / StatusOverlay / TickEmitter / UnityEventTrigger

---

## 3. 整理案

### Q3 (Window/Hapbeat の重複) について

私の推奨は **トップレベル `Hapbeat/` に統一して `Window/Hapbeat/` を廃止**。

**理由:**

- 経路が二つあると「Settings ってどっちから開くんだっけ？」と判断コストが発生。Hapbeat は触覚専用 SDK なので、トップレベルにメニューを持つ前提なら Window 配下は冗長
- Unity 標準の `Window` メニューは「すでに長大」(Asset Store / Package Manager / TextMeshPro / Rendering / ...)。そこに 1 行追加してもスクロールに埋もれる
- 比較対象として ProBuilder / Cinemachine もトップレベルか GameObject メニューに集約しており、Window 配下に出す SDK は減ってきている

**例外:** ユーザーが Unity の慣例に強くこだわるなら **逆に `Window/Hapbeat/` のみに統一** という選択肢もあり。ただし「アクション系メニュー (Build Samples・Create Event Router・Setup) は Window 配下には置きにくい」のでハイブリッドになり、結局現状と変わらない。

→ **トップレベル `Hapbeat/` 一本化** が最も筋がよい。

### 整理後の提案構成

```
Hapbeat/                                    ← Window 起動 (シングルトン)
  Settings
  Event Map
  Batch Setup
  ─────────────                              ← (order separator)
  Create Event Router                       ← Action (シーン配置)
  ─────────────
  Setup/                                    ← セットアップ系
    Create HapbeatSDK Folder
  Build Samples/                            ← サンプル生成
    1. Basic Example
    2. Tutorial (full scene)
  ─────────────
  Debug/                                    ← Debug 系全集約
    Attach Event Logger to Selected         (ユーザーも使うのでここに残す)
    Remove Event Logger Wiring from Selected
    ─
    Start Log Recording                     (トップ階層から移動)
    Stop Log Recording                      (トップ階層から移動)
    Logs/
      Reveal Current File
      Open Logs Folder
      Dump Last Recording to Console
    ─
    Close Edit-mode Transport               (advanced debug)
    Log Drag&Drop Events                    (advanced debug)
```

### 削除候補

| メニュー | 理由 |
|---|---|
| `Hapbeat/Tutorial/Strip Hapbeat ...` | 開発内部用 (Tutorial 完成形から Hapbeat を剥がした学習用シーンを生成する開発タスク)。リリース後は不要 → **削除** |
| `Hapbeat/Migrate Legacy Entry References` | `_entryIndex` → `_entryId` の one-shot migration。プロジェクトはリリース前 (DEC: 後方互換コードを作らない) なので不要 → **削除** |
| `Window/Hapbeat/Settings`, `.../Event Map`, `.../Batch Setup` | トップレベル `Hapbeat/` と重複 → **削除** |

### 残置 (現状維持)

- `GameObject/Hapbeat/Event Router` — Unity 慣例的に正しい場所
- `Assets/Create/Hapbeat/Config` / `Event Map` — 同上
- `AddComponentMenu("Hapbeat/...")` 配下 11 個 — Add Component 経由でユーザーが触る正規の入口

### Order 番号設計 (separator 整形)

Unity Editor は MenuItem の `order` 差が 11 以上で自動 separator が入る。提案構成では:

```
Settings        order=1
Event Map       order=2
Batch Setup     order=3       ← Window 系まとめ
Create Event Router  order=20  ← +separator
Setup/...       order=40
Build Samples/...  order=50    ← +separator (40 → 50 だと出ないので 60 にするか別途)
Debug/...       order=200      ← +separator (大きく離す)
```

実装時に微調整。

---

## 4. 確認お願いしたい点

1. **`Window/Hapbeat/*` を全廃** で OK か (推奨案)
2. **`Hapbeat/Tutorial/Strip Hapbeat ...`** を削除して良いか (開発用なので Y と推測)
3. **`Hapbeat/Migrate Legacy Entry References`** を削除して良いか (リリース前 repo なので Y と推測)
4. **トップ階層の Start/Stop Log Recording** を `Debug/` 配下に移動して良いか
5. Builder のテキスト調整箇所はこのドキュメントで網羅できているか / 他に欲しい項目はあるか

OK が出れば実装に移る。
