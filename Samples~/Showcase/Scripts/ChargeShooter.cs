using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using UnityEngine.UI;
using Hapbeat;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z5 Target Range: LMB hold で charge → release で発射。
    ///
    /// === Tutorial 教育目的: script-only haptic ===
    /// 他ゾーン (Z1-Z4) は HapbeatXxxTrigger コンポーネントを Inspector で wire する
    /// **declarative** パターン。Z5 はあえてコンポーネントを使わず、
    /// HapbeatManager の API を script から直接叩く **imperative** パターン。
    ///
    /// **重要**: いずれのパターンでも **音声 / gain / target / loop の設定は
    /// HapbeatEventMap が single source of truth**。script は eventId で entry を
    /// 解決して entry.streamClip / entry.GetEffectiveGain() / entry.target などを
    /// 使う。EventMap window での編集は両 path 共通に効く。
    ///
    /// → 触覚関連コンポーネントは **このスクリプト 1 つだけ** が Blaster に attach され、
    ///    HapbeatEventMap への参照 + 3 つの event ID + 各 GO 参照を [SerializeField] で集約。
    /// </summary>
    public class ChargeShooter : MonoBehaviour
    {
        // ────────────────────────────────────────────────────────
        // Scene refs (各 GO への参照を 1 コンポーネントに集約)
        // ────────────────────────────────────────────────────────
        [Header("Scene refs")]
        [Tooltip("発射の起点 (forward 方向に弾が飛ぶ)。未割当なら Camera.main を使用。")]
        [SerializeField] private Transform _muzzle;
        [Tooltip("Light (chargeT < threshold) 用の弾 Prefab。Tag = ProjectileLight。" +
                 "null なら fallback で Sphere primitive 生成。")]
        [SerializeField] private Rigidbody _projectilePrefabLight;
        [Tooltip("Heavy (chargeT >= threshold) 用の弾 Prefab。Tag = ProjectileHeavy。" +
                 "null なら Light で代替。")]
        [SerializeField] private Rigidbody _projectilePrefabHeavy;
        [Tooltip("チャージ進捗を表示する Slider。")]
        [SerializeField] private Slider _chargeBar;
        [Tooltip("Slider Fill の Image。閾値で色を切り替える。")]
        [SerializeField] private Image _chargeBarFill;

        // ────────────────────────────────────────────────────────
        // Haptic config (EventMap で集中管理、script は eventId で参照)
        // ────────────────────────────────────────────────────────
        [Header("Haptic (resolved via HapbeatEventMap)")]
        [Tooltip("触覚 entry を引く EventMap。Mode/Clip/Gain/Target/Loop は全て entry 側で設定する。")]
        [SerializeField] private HapbeatEventMap _eventMap;

        // 4 つの entry 参照は stable GUID (id) + display cache index のペアで保持。
        // HapbeatTriggerBase と同じ規約で Custom Editor (ChargeShooterEditor) が
        // dropdown を描画して両方を書き込む。HideInInspector で default の文字列描画を抑止。
        [SerializeField, HideInInspector] private string _chargeLoopEntryId = "";
        [SerializeField, HideInInspector] private int _chargeLoopEntryIndex = -1;
        // Shot は Light (chargeT < threshold) / Heavy (>= threshold) で 2 entry に分岐。
        // Heavy 未設定なら Light が両方に使われる。
        // FormerlySerializedAs で旧名 (_chargeReleaseXxx) の wiring を保持。
        [SerializeField, HideInInspector, FormerlySerializedAs("_chargeReleaseLightEntryId")]
        private string _chargeShotLightEntryId = "";
        [SerializeField, HideInInspector, FormerlySerializedAs("_chargeReleaseLightEntryIndex")]
        private int _chargeShotLightEntryIndex = -1;
        [SerializeField, HideInInspector, FormerlySerializedAs("_chargeReleaseHeavyEntryId")]
        private string _chargeShotHeavyEntryId = "";
        [SerializeField, HideInInspector, FormerlySerializedAs("_chargeReleaseHeavyEntryIndex")]
        private int _chargeShotHeavyEntryIndex = -1;
        [SerializeField, HideInInspector] private string _chargeThresholdEntryId = "";
        [SerializeField, HideInInspector] private int _chargeThresholdEntryIndex = -1;

        [Header("Charge modulator")]
        [Tooltip("Loop の動的 gain に使う curve。default EaseInOut(0→0, 1→1): chargeT 0 で\n" +
                 "silent、満タンで full intensity。entry.gain × intensity (= baseline) に対する\n" +
                 "modulator (0..1 ≒ silent..authored)。\n" +
                 "Shot Follows ChargeT が ON のとき shot 側でも同じ curve を再利用する。")]
        [SerializeField] private AnimationCurve _gainCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Threshold 1-shot の追加 gain modulator (entry.gain × intensity に乗る)。\n" +
                 "固定値 (chargeT 連動なし)。entry 側 gain を絶対値、こちらは Tutorial 側の調整係数。")]
        [SerializeField, Range(0f, 2f)] private float _chargeThresholdModulator = 1.0f;

        [Tooltip("Shot 1-shot の追加 gain modulator。\n" +
                 "Shot Follows ChargeT が OFF (default) のとき使われる固定値。\n" +
                 "Loop の curve と独立して shot 強度を調整できる。")]
        [SerializeField, Range(0f, 2f)] private float _chargeShotModulator = 1.0f;

        [Tooltip("ON: shot gain modulator = _gainCurve.Evaluate(chargeT)。loop と同じ curve に追従し、\n" +
                 "chargeT が低いほど shot も弱くなる (legacy behavior、charge level = shot power)。\n" +
                 "OFF (default): _chargeShotModulator を固定で使う。Loop curve とは無関係に shot 強度を\n" +
                 "決められるので Loop 用 curve を弄っても shot に影響しない。")]
        [SerializeField] private bool _shotFollowsChargeT = false;

        // ─── Packet-burst 対策 delays (HapbeatSequenceTrigger の startShotDelay /
        //     stopShotDelay と同等のロール) ────────────────────────────────
        [Tooltip("BeginCharge() 呼び出しから loop stream 開始までの待ち時間 (秒)。\n" +
                 " 0 (default) = 即 loop 開始。\n" +
                 " >0 = LMB 押下から N 秒後に loop stream を開始。\n\n" +
                 "ChargeShooter は SequenceTrigger と違って start one-shot を持たないので、\n" +
                 "通常は 0 で OK (LMB 押下と同時に rumble が始まる)。\n" +
                 "直前の shot session がまだ device 側 ring buffer に残っているような\n" +
                 "高速連射シナリオで、loop の最初の数 chunk が shot tail と合成されるのを\n" +
                 "避けたい場合に 0.03–0.05 を入れると安定する。")]
        [SerializeField, Range(0f, 0.5f)] private float _loopStartDelay = 0f;

        [Tooltip("Loop stop と Shot 発火の間に挟む待ち時間 (秒)。\n" +
                 " 0     = 即発火 (shot が時々鳴らなかったり強度がばらつく場合あり)。\n" +
                 " 0.05  = default — device の ring buffer flush 処理を待つ安全マージン。\n" +
                 " 0.10  = Wi-Fi 帯域が厳しい環境向けに広めに取る場合。\n\n" +
                 "Release() の packet 送信順:\n" +
                 "  STREAM_END (loop) → STREAM_BEGIN/END (ring-flush pair) → shot (BEGIN+DATA or PLAY)\n" +
                 "これらが ~10ms 以内にバースト送信されるため:\n" +
                 "  StreamClip shot: flush 前後で shot が loop tail と device 側 mixer で合成 →\n" +
                 "                   試行ごとに強度がばらつく\n" +
                 "  Command/Fire shot: PLAY が flush 処理中に到着 → 時折ドロップして無音\n" +
                 "0.05 default で flush 処理時間を確保し両症状を解消。\n" +
                 "HapbeatSequenceTrigger._stopShotDelay と同一ロール・同一 default。")]
        [SerializeField, Range(0f, 0.5f)] private float _shotDelayAfterLoop = 0.05f;

        // ────────────────────────────────────────────────────────
        // Charge / Projectile / UI tuning
        // ────────────────────────────────────────────────────────
        [Header("Charge tuning")]
        [Tooltip("Light/Heavy projectile + chargeBar 色変更の閾値 (0..1)。")]
        [SerializeField, Range(0f, 1f)] private float _heavyThreshold = 0.7f;
        [SerializeField] private float _maxChargeSeconds = 1.5f;

        [Header("Projectile tuning")]
        [SerializeField] private float _maxLaunchSpeed = 18f;
        [Tooltip("chargeT=0 のときの projectile スケール倍率 (prefab 元 scale に掛かる)。")]
        [SerializeField] private float _minChargeScale = 0.5f;
        [Tooltip("chargeT=1 のときの projectile スケール倍率。")]
        [SerializeField] private float _maxChargeScale = 1.5f;

        [Header("UI tuning")]
        [Tooltip("chargeT < threshold のときの ChargeBar Fill 色 (青系)。")]
        [SerializeField] private Color _chargeBarColorLow = new Color(0.3f, 0.6f, 1f);
        [Tooltip("chargeT >= threshold のときの ChargeBar Fill 色 (赤系)。")]
        [SerializeField] private Color _chargeBarColorHigh = new Color(1f, 0.3f, 0.2f);

        [Header("Audio (optional, parallel to haptic)")]
        [Tooltip("Charge 中のループ音 (AudioSource、Loop ON 推奨)。null なら無音。" +
                 "BeginCharge で Play, Release で Stop。")]
        [SerializeField] private AudioSource _chargeAudio;

        [Tooltip("発射時 Light variant 用 AudioSource (chargeT < threshold で PlayOneShot)。" +
                 "Clip は Inspector の AudioClip 欄に設定。null なら無音。")]
        [SerializeField] private AudioSource _releaseAudioLight;

        [Tooltip("発射時 Heavy variant 用 AudioSource (chargeT >= threshold で PlayOneShot)。" +
                 "null なら Light で代替。")]
        [SerializeField] private AudioSource _releaseAudioHeavy;

        [Tooltip("Release 音の音量を chargeT で scale するか。true なら 0..1 で線形 scale (満チャージで full)。")]
        [SerializeField] private bool _scaleReleaseVolumeByCharge = true;

        [Header("Debug")]
        [SerializeField] private bool _verboseLog = true;

        // ────────────────────────────────────────────────────────
        // Private state
        // ────────────────────────────────────────────────────────
        private bool _charging;
        private float _chargeStartTime;
        private bool _thresholdReached;
        // 再生中の loop stream の handle。Stop() / ApplyGainModulation() に使う。
        // 1-shot は handle を保持しない (fire-and-forget)。
        private HapbeatStreamPlayback _loopPlayback;
        // _loopStartDelay > 0 のとき BeginCharge から実 loop 開始までを橋渡す coroutine。
        // 遅延中に Release が来たら CancelPendingLoopStart で中止して loop を開かない。
        private Coroutine _pendingLoopStart;

        // ────────────────────────────────────────────────────────
        // Update — input + haptic modulation
        // ────────────────────────────────────────────────────────
        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (Cursor.lockState != CursorLockMode.Locked) return;

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (_verboseLog) Debug.Log("[Charge] LMB down → BeginCharge");
                BeginCharge();
            }

            if (_charging)
            {
                float t = Mathf.Clamp01((Time.time - _chargeStartTime) / _maxChargeSeconds);
                if (_chargeBar != null) _chargeBar.value = t;
                if (_chargeBarFill != null)
                    _chargeBarFill.color = t >= _heavyThreshold ? _chargeBarColorHigh : _chargeBarColorLow;

                // Loop gain modulation: 再生中の handle に直接 ApplyGainModulation を呼ぶ。
                // 内部で Gain = BaselineGain × clamp(modulator) を計算 (declarative の
                // HapbeatParameterBinding と同一 entry point)。BaselineGain は entry.GetEffectiveGain()
                // が Fire 時にセットされている。
                if (_loopPlayback != null && !_loopPlayback.IsStopped)
                    _loopPlayback.ApplyGainModulation(_gainCurve.Evaluate(t));

                // Threshold cross 1-shot
                if (!_thresholdReached && t >= _heavyThreshold)
                {
                    _thresholdReached = true;
                    FireOneShotFromEntry(_chargeThresholdEntryId, _chargeThresholdEntryIndex,
                                         _chargeThresholdModulator, "threshold");
                    if (_verboseLog) Debug.Log($"[Charge] Threshold crossed (t={t:F2})");
                }

                if (mouse.leftButton.wasReleasedThisFrame)
                {
                    if (_verboseLog) Debug.Log($"[Charge] LMB up → Release (chargeT={t:F2})");
                    Release(t);
                }
            }
        }

        // ────────────────────────────────────────────────────────
        // Haptic helpers (EventMap entry を引いて HapbeatManager に dispatch)
        // ────────────────────────────────────────────────────────

        private void OnDisable()
        {
            // 遅延 coroutine を component teardown / disable で leak させない。
            // 既に loop が走っていれば次の Update / Release で正規に止まる。
            CancelPendingLoopStart();
        }

        private void BeginCharge()
        {
            _charging = true;
            _chargeStartTime = Time.time;
            _thresholdReached = false;
            if (_chargeBar != null) _chargeBar.value = 0f;
            if (_chargeBarFill != null) _chargeBarFill.color = _chargeBarColorLow;

            // Audio: charge loop 開始 (haptic と並行)
            if (_chargeAudio != null) _chargeAudio.Play();

            // Loop stream の開始は _loopStartDelay > 0 のとき coroutine 経由で遅延させる。
            // 通常 0 で即開始 (LMB と同時に rumble)。直前 shot session の tail が残って
            // 合成されるのを避けたいケースで 0.03–0.05 を入れる。
            CancelPendingLoopStart();
            if (_loopStartDelay > 0f)
            {
                if (_verboseLog)
                    Debug.Log($"[Charge] loop を {_loopStartDelay:F3}s 遅延開始 " +
                              "(直前 session の tail を device 側で消費させるため)");
                _pendingLoopStart = StartCoroutine(StartLoopStreamAfterDelay(_loopStartDelay));
            }
            else
            {
                StartLoopStream();
            }
        }

        /// <summary>
        /// EventMap から charge_loop entry を引いて loop stream を開く本体。
        /// BeginCharge から直接 or _loopStartDelay coroutine 経由で呼ばれる。
        /// baselineGain = entry.GetEffectiveGain() (= entry.gain × intensity、modulator 抜き)、
        /// initialGain = baseline × curve(0) で race-free silent start。
        /// </summary>
        private void StartLoopStream()
        {
            var entry = ResolveEntry(_chargeLoopEntryId, _chargeLoopEntryIndex, "charge_loop");
            if (entry == null) return;
            if (entry.mode != HapticMode.StreamClip || entry.streamClip == null)
            {
                Debug.LogWarning($"[Charge] charge_loop entry (idx={_chargeLoopEntryIndex}) must be StreamClip with a clip assigned");
                return;
            }
            if (HapbeatManager.Instance == null)
            {
                Debug.LogWarning("[Charge] HapbeatManager 未起動");
                return;
            }

            float baselineGain = entry.GetEffectiveGain();
            float initialMod = _gainCurve.Evaluate(0f);
            string target = entry.HasTarget ? entry.target : null;
            _loopPlayback = HapbeatManager.Instance.StreamAudioClip(
                entry.streamClip,
                baselineGain: baselineGain,
                initialGain: baselineGain * initialMod,
                target: target,
                loop: entry.loop);

            if (_verboseLog)
            {
                Debug.Log(_loopPlayback != null
                    ? $"[Charge] Loop stream started (clip='{entry.streamClip.name}', baseline={baselineGain:F2}, initialMod={initialMod:F2}, loop={entry.loop})"
                    : "[Charge] Loop stream rejected (session mismatch / 未接続)");
            }
        }

        private IEnumerator StartLoopStreamAfterDelay(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _pendingLoopStart = null;
            // 遅延中に Release が呼ばれて _charging が false に戻っていた場合は loop を
            // 開かない (Release が pending coroutine を Cancel するので通常はここに到達しない
            // が、二重防御として check)。
            if (!_charging) yield break;
            StartLoopStream();
        }

        private void CancelPendingLoopStart()
        {
            if (_pendingLoopStart != null)
            {
                StopCoroutine(_pendingLoopStart);
                _pendingLoopStart = null;
                if (_verboseLog)
                    Debug.Log("[Charge] pending loop-start を中止 " +
                              "(Release が _loopStartDelay 経過前に到着)");
            }
        }

        private void Release(float chargeT)
        {
            _charging = false;
            _thresholdReached = false;
            if (_chargeBar != null) _chargeBar.value = 0f;

            // _loopStartDelay 中の Release は pending loop coroutine を中止して loop を開かない。
            // (Release が loop 開く前に来たので shot だけ発火するか、何もしないかを決める必要あり
            // — ここでは shot は発火する仕様にしておく。loop が無くても LMB を押した事実は
            // ユーザーに体感させたい。)
            CancelPendingLoopStart();

            // Loop stream を per-source stop + device ring buffer flush。
            // 通常 Stop() だけだと device 側 buffer の drain で ~50-250ms の tail が残るため、
            // StopStreamWithFlush で STREAM_BEGIN+END pair を送って即時 flush する。
            if (_loopPlayback != null)
            {
                _loopPlayback.Stop();
                _loopPlayback = null;
                if (HapbeatManager.Instance != null)
                    HapbeatManager.Instance.StopStreamWithFlush();
                if (_verboseLog) Debug.Log("[Charge] Loop stream stopped + ring buffer flushed");
            }

            // Audio: charge loop stop + release one-shot (Light / Heavy 分岐)
            if (_chargeAudio != null && _chargeAudio.isPlaying) _chargeAudio.Stop();
            var releaseAudio = PickReleaseAudio(chargeT);
            if (releaseAudio != null && releaseAudio.clip != null)
            {
                float vol = _scaleReleaseVolumeByCharge ? Mathf.Clamp01(chargeT) : 1f;
                releaseAudio.PlayOneShot(releaseAudio.clip, vol);
            }

            // Shot 1-shot haptic (Light / Heavy 分岐)。
            // Modulator は _shotFollowsChargeT で切替:
            //   OFF (default) → 固定 _chargeShotModulator (loop curve と独立)
            //   ON            → _gainCurve.Evaluate(chargeT) (loop と同 curve に追従)
            bool isHeavy = chargeT >= _heavyThreshold;
            float shotModulator = _shotFollowsChargeT
                ? _gainCurve.Evaluate(chargeT)
                : _chargeShotModulator;
            string shotLabel = $"shot-{(isHeavy ? "heavy" : "light")} ({chargeT:F2}, mod={shotModulator:F2})";

            // Loop stop の flush burst と shot の BEGIN/PLAY packet が衝突して
            // device 側で取りこぼし / mixer 合成が起きるのを防ぐため、_shotDelayAfterLoop
            // > 0 のときは coroutine 経由で待ってから shot 発火する。
            if (_shotDelayAfterLoop > 0f)
            {
                if (_verboseLog)
                    Debug.Log($"[Charge] shot を {_shotDelayAfterLoop:F3}s 遅延発火 " +
                              "(loop stop の flush packet を device に処理させるため)");
                StartCoroutine(FireShotAfterDelay(isHeavy, shotModulator, shotLabel, _shotDelayAfterLoop));
            }
            else
            {
                FireShotEntry(isHeavy, shotModulator, shotLabel);
            }

            // 視覚的な発射 (haptic とは無関係)
            FireProjectile(chargeT);
        }

        /// <summary>
        /// Release 用 AudioSource を chargeT で選ぶ API。
        /// chargeT >= _heavyThreshold かつ Heavy が設定済なら Heavy、それ以外は Light。
        /// 外部 script からも参照できるよう public で公開。
        /// </summary>
        public AudioSource PickReleaseAudio(float chargeT)
        {
            return (chargeT >= _heavyThreshold && _releaseAudioHeavy != null)
                ? _releaseAudioHeavy
                : _releaseAudioLight;
        }

        /// <summary>
        /// Shot 発火本体 (Release 内で _shotDelayAfterLoop の前後どちらでも呼ばれる
        /// 共通エントリポイント)。
        /// </summary>
        private void FireShotEntry(bool isHeavy, float shotModulator, string shotLabel)
        {
            if (isHeavy && !string.IsNullOrEmpty(_chargeShotHeavyEntryId))
                FireOneShotFromEntry(_chargeShotHeavyEntryId, _chargeShotHeavyEntryIndex,
                                     shotModulator, shotLabel);
            else
                FireOneShotFromEntry(_chargeShotLightEntryId, _chargeShotLightEntryIndex,
                                     shotModulator, shotLabel);
        }

        /// <summary>
        /// _shotDelayAfterLoop 秒待ってから shot を発火。WaitForSecondsRealtime
        /// を使うことで Time.timeScale の影響を受けない (haptic は音と同じく
        /// 物理時間で動かしたいため)。
        /// </summary>
        private IEnumerator FireShotAfterDelay(bool isHeavy, float shotModulator, string shotLabel, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            FireShotEntry(isHeavy, shotModulator, shotLabel);
        }

        /// <summary>
        /// Shot 用 haptic entry を chargeT で選ぶ API。
        /// 内部の Resolve 結果を返す (null 可)。
        /// </summary>
        public HapbeatEventEntry PickShotEntry(float chargeT)
        {
            bool isHeavy = chargeT >= _heavyThreshold;
            if (isHeavy && !string.IsNullOrEmpty(_chargeShotHeavyEntryId))
                return ResolveEntry(_chargeShotHeavyEntryId, _chargeShotHeavyEntryIndex, "shot-heavy");
            return ResolveEntry(_chargeShotLightEntryId, _chargeShotLightEntryIndex, "shot-light");
        }

        /// <summary>
        /// EventMap から entry を引いて 1-shot fire する共通経路。
        /// entry.mode に応じて Command (Play) / StreamClip (StreamAudioClip loop=false) に分岐。
        /// gain = entry.GetEffectiveGain() (author intent) × modulator (script 側調整値)。
        /// </summary>
        private void FireOneShotFromEntry(string entryId, int entryIndex, float modulator, string label)
        {
            var entry = ResolveEntry(entryId, entryIndex, label);
            if (entry == null) return;
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;

            float gain = entry.GetEffectiveGain() * modulator;
            string target = entry.HasTarget ? entry.target : null;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        Debug.LogWarning($"[Charge] {label}: Command mode だが eventId が空");
                        return;
                    }
                    mgr.Play(entry.eventId, gain, label, target);
                    if (_verboseLog) Debug.Log($"[Charge] {label} Command fired (eventId='{entry.eventId}', gain={gain:F2})");
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        Debug.LogWarning($"[Charge] {label}: StreamClip mode だが clip が null");
                        return;
                    }
                    // 1-shot stream (loop=false fire-and-forget)。session 既に active なら
                    // 同 session に source 追加で再生。rate/channel mismatch なら SDK が
                    // warning を出して null を返す (reject)。
                    var pb = mgr.StreamAudioClip(entry.streamClip, gain, target, loop: false);
                    if (pb == null)
                    {
                        // 通常は SDK 側 warning が既に出ている (rate/channel mismatch / not connected)。
                        // ここでは原因の hint と組み合わせて報告。
                        Debug.LogWarning($"[Charge] {label}: StreamClip 棄却 (clip='{entry.streamClip.name}', " +
                                         $"{entry.streamClip.frequency}Hz/{entry.streamClip.channels}ch)。" +
                                         "active session と format が違う場合は entry を Command mode にするか " +
                                         "clip を session format に揃えてください。");
                        return;
                    }
                    if (_verboseLog) Debug.Log($"[Charge] {label} StreamClip fired (clip='{entry.streamClip.name}', gain={gain:F2})");
                    break;
            }
        }

        /// <summary>
        /// EventMap から entry を解決。HapbeatTriggerBase と同じ規約で
        /// stable id (GUID) を先に試し、空なら index cache に fallback する。
        /// </summary>
        private HapbeatEventEntry ResolveEntry(string entryId, int entryIndex, string label)
        {
            if (_eventMap == null)
            {
                Debug.LogWarning($"[Charge] {label}: _eventMap 未割当");
                return null;
            }
            if (!string.IsNullOrEmpty(entryId))
            {
                var e = _eventMap.FindById(entryId);
                if (e != null) return e;
            }
            if (entryIndex < 0)
            {
                if (_verboseLog) Debug.LogWarning($"[Charge] {label}: entry 未選択 (skip)");
                return null;
            }
            var entry = _eventMap.GetEntry(entryIndex);
            if (entry == null)
                Debug.LogWarning($"[Charge] {label}: entry idx={entryIndex} が EventMap で見つからない");
            return entry;
        }

        // ────────────────────────────────────────────────────────
        // Projectile (haptic 無関係 — 見た目の発射ロジック)
        // ────────────────────────────────────────────────────────

        private void FireProjectile(float chargeT)
        {
            Transform muzzle = _muzzle != null ? _muzzle :
                (Camera.main != null ? Camera.main.transform : null);
            if (muzzle == null)
            {
                Debug.LogWarning("[Charge] muzzle/Camera.main 無く視覚発射スキップ");
                return;
            }

            Vector3 spawnPos = muzzle.position + muzzle.forward * 0.6f;
            // 閾値で prefab を切替 (Heavy が null なら Light で代替)
            var prefab = (chargeT >= _heavyThreshold && _projectilePrefabHeavy != null)
                ? _projectilePrefabHeavy
                : _projectilePrefabLight;

            Rigidbody projectile;
            if (prefab != null)
            {
                var spawnRot = muzzle.rotation * prefab.transform.rotation;
                projectile = Instantiate(prefab, spawnPos, spawnRot);
                float scale = Mathf.Lerp(_minChargeScale, _maxChargeScale, chargeT);
                projectile.transform.localScale = prefab.transform.localScale * scale;
            }
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Projectile_Fallback";
                go.transform.position = spawnPos;
                float scale = Mathf.Lerp(_minChargeScale, _maxChargeScale, chargeT);
                go.transform.localScale = Vector3.one * (0.2f * scale);
                projectile = go.AddComponent<Rigidbody>();
                projectile.mass = 0.2f;
            }
            projectile.linearVelocity = muzzle.forward * (_maxLaunchSpeed * Mathf.Lerp(0.3f, 1f, chargeT));
            Destroy(projectile.gameObject, 4f);
        }
    }
}
