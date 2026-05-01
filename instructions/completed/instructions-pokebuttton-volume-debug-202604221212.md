# Instructions: PokeButton (StreamClip) volume が binding で反映されない問題の調査・修正

**発行日:** 2026-04-22
**起票:** session-unity-sdk-eventmap-and-stream-follow-ups (2026-04-21 持ち越し)
**優先度:** 即着手

## 背景

HandsDemoScene の PokeButton で HapbeatParameterBinding の Volume 出力が haptic ストリームに反映されない問題。
`OnAudioFilterRead` に `AudioSource.volume` 乗算を追加したが、それでも「常に同じ強度」とユーザーが報告。

現在の実装:
- `HapbeatAudioBridge.OnAudioFilterRead` — `_audioSource.volume` を ring buffer サンプルに乗算
- `HapbeatTriggerBase.StartAudioSourceStream` — `entry.GetEffectiveGain()` で stream 開始時に bridge.Gain を設定
- `HapbeatParameterBinding` — 毎 Update で `audioSource.volume` に書き込み

## タスク

### 1. PokeButton の構成を確認

PokeButton GameObject を開いて以下を確認する:
- AudioSource, HapbeatAudioBridge, HapbeatParameterBinding が**同じ GameObject に乗っているか**
- HapbeatParameterBinding の Source と Output が何に設定されているか
- `_autoStart` フラグが ON になっているか（bridge が trigger を経由せず自動開始していると effective gain が取れない）
- Binding の Output が `Volume`（audioSource.volume 経路）か `BridgeGain`（bridge.Gain 直接書き込み）か

### 2. Play mode でログを確認

Play mode に入り、PokeButton を操作して Console を確認:
- `∼ StreamSource start: ... gain=X.XX × intensity=Y.YY → effective=Z.ZZ` が出るか（intensity が解決されているか）
- `[HapbeatBinding] ... Volume output=X.XX` が出るか（binding が動いているか）
- `HapbeatParameterBinding.DumpCurrentState` を ContextMenu から実行してランタイム値を確認

### 3. 切り分け: BridgeGain に切り替えてテスト

Binding の Output を一時的に `BridgeGain` に変更してテスト。
- これで効けば → AudioSource.volume 経路が壊れている
- これでも効かなければ → binding 自体が動いていない（sourceProperty の解決が失敗等）

### 4. HapbeatAudioBridge._audioSource の null チェック

`HapbeatAudioBridge.cs` の `Awake()` で `_audioSource` をキャッシュしているはず。
`OnAudioFilterRead` 内で null だと volume 乗算がスキップされ常に 1.0 になる。
null ガードの有無と、実際に null でないかをログで確認。

### 5. 修正と確認

原因が判明したら修正し、以下を確認:
- PokeButton を深く押すと haptic が強くなる
- 浅いと弱い / 離すと止まる

## 完了条件

- [ ] 原因が特定されてログまたはコードで説明できる
- [ ] volume binding が haptic 強度に反映される（押し込み量に連動）
- [ ] 原因がコード上の既知パターンであれば `feedback_*` memory に記録
- [ ] 本ファイルを `instructions/completed/` に移動

## 依存関係

- **Required**: なし
- **Downstream**: PokeButton が動けば PawnController の velocity binding も同じ経路で実装できる
