# Instructions: Z3 Sequence Loop 関連不具合の対処

**発行日:** 2026-05-25
**起票:** Tier 1/2 cleanup + Play-mode latency root-fix セッション (commits 5997c1b...6776cf0)
**優先度:** 即着手

## 背景

Tier 1/2 cleanup と latency 補正実装の過程で、Z3 Fishing の Sequence loop 周りに複数の不具合 / UX 課題が露見した。本セッションでは Play-mode latency 変更の root-fix と binding pre-seed scope 拡張までで力尽きたので、loop 系の残課題を次セッションでまとめて処理する。

## 既知の不具合と推測される原因

### Issue A: Loop の初期 gain が 1.0 で発火 (期待値 0)

**症状**: Z3 で物体を attach した直後、binding が「速度 0 想定 → gain 0」を意図しているのに **初回 chunk は gain 1.0 (initialMod=1.00 ログ確認済み)** で送信される。2 chunk 目以降は ParameterBinding.Update が書き換えるので 0 になる。

**ログ証拠**:
```
[Hapbeat] Fire StreamClip: clip='z3_hook_loop' ... initialMod=1.00 ...
[Hapbeat] ♪ StreamClip start: "Z3_Fishing: Z3_hook_loop" (baseline=0.50, initialMod=1.00, loop=True, 0 binding(s))
```

**判明済みの修正**: `HapbeatTriggerBase.FindBindingForEntry()` の scope を Self → Children → Parent に拡張済 (commit `8920cea`)。**ただしログでは `0 binding(s)` のまま** → 実際に Z3_Fishing 配下に binding component が存在しないことが原因。

**ユーザ確認結果**: EventMap window で binding が 2 つあった。
1. `Z3_Fishing` (FishingObject、ユーザ追加、正しい)
2. **`[orphan] Rod (no wired trigger)`** (FishingObject_RestPose、想定外)

**TODO**:
- [ ] "Rod" GameObject の HapbeatParameterBinding を削除 (or trigger を Rod に追加して使う)
- [ ] 再 build / コンパイル後、Verbose Log で `N binding(s)` の N が 1 以上になることを確認
- [ ] initialMod が 0 (binding.EvaluateNow() の戻り値) で発火することを確認

### Issue B: On Stop の release haptic が体感できない

**症状**: Z3 で物体を release した時、`Z3_hook_release` entry の haptic を設定しているのに felt できない。

**ログ証拠**:
```
[Hapbeat] SequenceTrigger.Stop() invoked on Z3_Fishing (active=True)
[Hapbeat] Sequence end-shot: 'Z3_hook_release' mode=StreamClip gain=0.55
[Hapbeat] ♪ Stream source added: z3_hook_release (active sources=2, loop=False)  ← 追加されている
[Hapbeat] Stream session ended (501200 bytes sent).
```

**つまり SDK / Manager 側は正しく送信している**。release source が mixer に追加され、loop と並列で 2 source mix されている。

**推測原因**:
1. **Loop と release が同 session で mix されて release が loop の余韻に紛れる** — loop は initial gain 1.0 (Issue A の状態) で強く鳴っていた + release (gain 0.55) が重畳 → 区別が付かない
2. **`streamSendAheadSeconds = 50ms` の send-ahead buffer** で Stop 後も loop の tail が device に残っており、release はそれに重なって発音される
3. **release clip 自体の amplitude が小さい / 立ち上がりが弱い**

**TODO**:
- [ ] Issue A を先に修正して loop の initial gain が 0 になることを確認 (mix 競合を減らす)
- [ ] それでも release が体感できないなら、release entry を **Command mode に変更** して試す (低遅延 + Stream 経路を経由しない)
- [ ] release clip の波形を確認 (鋭い立ち上がりが有るか)
- [ ] `streamSendAheadSeconds` を 0.02s 程度に下げて loop tail を短くした場合の挙動を確認 (副作用: stutter リスク増)

### Issue C: device-side packet drop の可能性 (Slider tick の散発ロス)

**症状**: Z4 で slider を高速ドラッグすると、tick haptic が時々鳴らない。一度 Stop → Play で症状が大幅に改善する。Audio 版 (TickAudioEmitter) は欠落しない。

**前セッションでの分析**:
1. 当初 target mismatch と推測したが **誤り** (撤回済み)。
2. 正しい仮説候補:
   - **A. 古い delay で spawn された pending coroutine が累積** → mixer に active source がモリモリ追加 → UDP 送信レート跳ね上がり → device receive buffer overflow → packet drop
   - **B. UDP packet loss が高頻度で発生** → device で tick がロスト
   - **C. mixer の chunk-merge ロジックが過度に多数の source を mix する際の処理コスト**

**已済対策**:
- Play-mode latency 変更で pending coroutine 自動 flush 機構を実装 (commit `6776cf0`)。これで「Play 中に latency を弄ってから症状が悪化」のシナリオは緩和されたはず

**TODO**:
- [ ] `6776cf0` の状態で Slider 高速ドラッグ + latency 変更を試して、症状が解消するか確認
- [ ] 改善しないなら **per-trigger active source soft-cap** を検討 (例: 同一 trigger からの active source が N 個を超えたら古いものを drop)
- [ ] Verbose log で `Fire deferred by Xms` の出現頻度と device-side miss が相関しているか確認
- [ ] (可能なら) Wireshark / device 側 log で UDP packet loss rate を測る

### Issue D: Sequence loop の binding pre-seed が race する可能性

Issue A の延長。`HapbeatParameterBinding.OnEnable` の `_trigger` 解決と、`HapbeatTriggerBase.FireHaptic` の binding pre-seed (commit `8920cea`) が同期しているか要確認。

- binding は `Self → Children → Parent` で trigger を探す
- trigger は同じ scope で binding を探す
- でも binding 側の `_trigger` 解決は OnEnable のタイミング — もし binding が disabled だったら trigger の pre-seed では検出されない可能性
- 同様に、binding がまだ component として attach されたばかり (initialize 未完) のタイミングで Fire が走ると `LinkedOwnerEntryId` が空のまま

**TODO**:
- [ ] Trigger.Fire → binding 探索 → 検出された binding の `LinkedOwnerEntryId` が正しいか verbose log で確認
- [ ] binding が disable 状態で trigger が fire した場合の挙動 (現状: binding 認識されず、initialMod = _gainMultiplier に fallback)

## タスク (実装手順)

1. **Issue A 確定**: ユーザに「Rod orphan binding 削除」を実施してもらい、`N binding(s)` の N が正しい数字になることを Verbose Log で確認
2. **Issue B 検証**: Issue A 修正後に release が体感できるか確認。ダメなら release を Command mode に変えて再試行
3. **Issue C 検証**: Play-mode latency 変更を伴う tick storm シナリオで commit `6776cf0` の効果を確認。改善しないなら per-trigger source soft-cap を実装
4. **Issue D 確認**: binding 未 enable 時の trigger fire path を検証、必要なら防御コード追加

## 完了条件

- [ ] Issue A: Z3 attach 直後の haptic が gain 0 (速度 0 想定) で始まる
- [ ] Issue B: Z3 release 時に明確に体感できる haptic が来る
- [ ] Issue C: Z4 slider 高速ドラッグ中に latency を変えても tick ロスが起きない
- [ ] Issue D: binding が disable / 未初期化の場合に発生する fallback 挙動が想定通り
- [ ] 本ファイルを `instructions/completed/` に移動

## 依存関係

- **Required**: 本セッションの全 commit (`5997c1b` 〜 `6776cf0`) が反映されていること
- **Downstream**: per-trigger source soft-cap 実装は別 instruction として切り出す可能性あり (Issue C 検証後)

## 関連ログ / scene 状態

ユーザの local Unity project (M:\GameEngine\Unity\Projects\HapbeatSDKSamples) で:
- Z3_Fishing GameObject に SequenceTrigger + ParameterBinding (`FishingObject` source) 配置済
- "Rod" GameObject に orphan binding (`FishingObject_RestPose` source) が存在
- グローバル `hapticDelaySeconds = 0.12` (検証時)
- `Z3_hook_release` entry の mode = StreamClip, gain = 0.55
- Stream session 16kHz / 2ch / broadcast target

## メモ

本セッションは情報量が多く、loop 系の不具合は時間切れで持ち越し。次セッション開始時にこの instruction を起点にする。
