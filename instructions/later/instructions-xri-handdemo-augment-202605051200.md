# XRI HandDemo Augment 機能の実装

**作成日:** 2026-05-05
**起点セッション:** workspace `wizardly-rhodes-acf40e` worktree (Tutorial sample 再設計と並行で着手)
**優先度:** Backlog (Tutorial sample 配備後に着手)
**関連 doc:** `docs/xri-handdemo-quickstart.md` (commit `8b238c9` で merge 済み)
**関連サンプル:** `Samples~/Tutorial/` (本指示書とは別建て、自前で SDK 機能を網羅する代替サンプル)

---

## 目的

Unity 公式 XR Interaction Toolkit (XRI) の `HandsDemoScene` に、メニュー1クリックで Hapbeat 触覚コンポーネントを後付けする仕組みを提供する。

XRI HandDemo シーンは Unity の配布物で Hapbeat 側からそのまま再配布できないため、
ユーザーが自分で Package Manager から HandDemo をインポートし、Hapbeat SDK が提供する Editor メニューでコンポーネントを自動配線する方式とする。

ユーザーフロー:
1. Package Manager から XR Interaction Toolkit + Hands Interaction Demo を Import
2. Hapbeat Unity SDK を Add Package
3. **Hapbeat → Samples → "Augment XRI HandDemo with Haptics"** を実行
4. Play で Quest をかぶる → 掴み・押し・スライド操作で触覚が返る

## 設計骨子

### Editor メニュー

`Editor/HandDemoAugmentor.cs` に以下を実装:

- メニュー: `Hapbeat/Samples/Augment XRI HandDemo with Haptics`
- 処理:
  1. **環境チェック**
     - `com.unity.xr.interaction.toolkit` のバージョンを Package manifest から取得
     - 期待バージョン (実装時に pin) でなければ警告ダイアログ + abort
     - `AssetDatabase.FindAssets("HandsDemoScene t:Scene")` で HandDemo シーン検出
     - 見つからなければ「Package Manager から XRI HandDemo を Import してください」
  2. **シーンを開く** (現在シーンが dirty なら保存を促す)
  3. **Wiring テーブル適用**
     - `Editor/HandDemoWiringTable.cs` をデータ駆動で持つ
     - 各 entry: `{ scenePath, triggerType, eventEntryGuid, parameters }`
     - `GameObject.Find` で対象解決 → 見つからなければ skip + warning に蓄積
     - `Undo.AddComponent<T>` で Hapbeat Trigger を追加 (Undo 1 回で全配線取消可能に)
     - EventMap & Event Entry GUID を `SerializedObject` で wire
     - 必要なら `[Hapbeat Event Router]` をシーン root に 1 つ追加
  4. **完了レポート**: 適用件数 / skip 件数 / warning 一覧をダイアログ表示
  5. **保存**: 「シーンを保存しますか？」を確認 (デフォルト no、ユーザーが Undo を残せるようにする)

### 同梱 EventMap.asset

`Samples~/XriHandDemoAugment/EventMap/HandDemoEventMap.asset`:
- pinch_grab (FIRE)
- pinch_release (FIRE)
- slider_drag (CLIP loop, ParameterBinding 対象)
- button_press (FIRE)
- toggle_on / toggle_off (FIRE)
- poke_button (FIRE)
- (実装時に HandDemo の対象オブジェクト一覧から追加)

target は kit に合わせて event 設計時点で振る (arm / neck / both)。

### 対象 XRI バージョン

実装着手時に決定。`com.unity.xr.interaction.toolkit` 3.x 系を想定するが、
Hands Interaction Demo の GameObject 構造が変わると wiring が破綻するため、
動作確認したバージョンを `package.json` の `samples[].description` および
`docs/xri-handdemo-quickstart.md` に明記する。

### Wiring テーブル例 (実装時に確定)

```csharp
new WiringEntry {
    scenePath = "XR Origin/Camera Offset/Right Hand/Pinch",
    triggerType = typeof(HapbeatUnityEventTrigger),
    eventDisplayName = "pinch_grab",
    sourceEvent = "XRGrabInteractable.selectEntered",
},
new WiringEntry {
    scenePath = "Slider Demo/Slider",
    triggerType = typeof(HapbeatTickEmitter),
    eventDisplayName = "slider_drag",
    sourceEvent = "Slider.onValueChanged",
},
// ...
```

`GameObject.Find` の path は HandDemo の hierarchy を実調査して固定する。

### Strip / Undo

- Augmentor 実行直後の Undo (Ctrl+Z 1 回) で全配線が巻き戻る設計
- 保存後にやり直す場合は HandDemo シーンを Package Manager から再 Import

## ファイル構成

```
Samples~/XriHandDemoAugment/
├── README.md                        ← サンプル目的 + XRI バージョン
├── EventMap/
│   └── HandDemoEventMap.asset
├── Editor/
│   ├── HandDemoAugmentor.cs         ← メニュー本体
│   ├── HandDemoWiringTable.cs       ← データ駆動 wiring 表
│   └── HandDemoEnvironmentCheck.cs  ← XRI version / scene 存在確認
└── Audio/                           ← 必要に応じて (Tutorial で同種を持つなら共通化検討)
```

`package.json` `samples` 配列に追加:
```json
{
  "displayName": "XRI HandDemo Augment",
  "description": "XRI Hands Interaction Demo に Hapbeat 触覚をメニュー1クリックで追加する Editor ツール (XRI x.y.z 対応)",
  "path": "Samples~/XriHandDemoAugment"
}
```

## 検証項目

- [ ] XRI HandDemo Import 済み環境で Augmentor 実行 → エラーなく完了
- [ ] HandDemo Import なしで実行 → 警告ダイアログで abort
- [ ] XRI バージョンミスマッチ → 警告ダイアログで abort
- [ ] Augmentor 後 Play → Quest で各インタラクションに触覚が乗る
- [ ] Undo (Ctrl+Z) で全配線が巻き戻る
- [ ] Package Manager から HandDemo を再 Import すると初期化できる
- [ ] Augmentor を 2 回実行しても重複配線にならない (idempotent)

## Tutorial sample との位置づけ

| サンプル | 想定ユーザー | 学習コスト | 触れる SDK 要素 |
|---|---|---|---|
| `Tutorial` | XR 持ってない / SDK 全機能を最速で触りたい | 30 分 | 全網羅 (キーマウスで完結) |
| `XriHandDemoAugment` | Quest 持ち / 公式 sample に触覚乗せたい | 5 分 | XRI grab / poke 中心 |

どちらも独立して機能し、ユーザーは興味と環境に応じて選択する。

## 着手前の確認事項

- 対象 XRI バージョンを 1 つ pin する (HandDemo シーンの GameObject path 検査)
- HandDemo Import 後の hierarchy をスクリーンショットで取得し、wiring テーブルに固定 path を埋める
- EventMap target は実機でのテスト時に arm/neck の感触を見て調整
