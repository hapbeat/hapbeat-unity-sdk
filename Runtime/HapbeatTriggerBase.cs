using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Abstract base for all Hapbeat trigger components.
    /// References a HapbeatEventMap and a specific entry by <b>stable GUID</b>
    /// (<see cref="HapbeatEventEntry.id"/>) so reordering / inserting / duplicating
    /// entries cannot silently break wiring.
    /// <para>
    /// Supports Command and StreamClip modes. When a StreamClip entry fires, the
    /// resulting <see cref="HapbeatStreamPlayback"/> handle is exposed via
    /// <see cref="ActivePlayback"/> so <see cref="HapbeatParameterBinding"/>
    /// components can modulate gain / pan in real time.
    /// </para>
    /// </summary>
    public abstract class HapbeatTriggerBase : MonoBehaviour
    {
        [Header("Hapbeat Event")]
        [Tooltip("The event map asset containing haptic event definitions.")]
        [SerializeField]
        protected HapbeatEventMap _eventMap;

        // Stable GUID reference into _eventMap.entries. Authored by the Inspector
        // dropdown / Batch Setup. Authoritative — no index fallback.
        [SerializeField, HideInInspector]
        protected string _entryId;

        [Header("Trigger Settings")]
        [Tooltip("Enable or disable this trigger.")]
        [SerializeField]
        protected bool _triggerEnabled = true;

        [Tooltip("Minimum time between firings (seconds). 0 = no cooldown.")]
        [SerializeField]
        protected float _cooldown = 0f;

        [Tooltip("Per-trigger gain multiplier. Final gain = entry.gain × this.\n" +
                 "Default 1.0 (no override). Use when the same entry is wired " +
                 "to multiple GameObjects but each needs a different intensity " +
                 "without authoring a per-object EventMap entry.")]
        [SerializeField, Range(0f, 2f)]
        protected float _gainMultiplier = 1f;

        [Header("Diagnostics")]
        [Tooltip("Log every Fire()/Stop() call and all early-return reasons to the " +
                 "Unity console. Enable while wiring a trigger to verify the event is " +
                 "reaching the Hapbeat trigger at all. Disable for normal operation.")]
        [SerializeField]
        protected bool _verboseLog = false;

        protected float _lastFireTime = float.NegativeInfinity;

        // Handle to the currently-playing StreamClip (if any). Exposed so
        // HapbeatParameterBinding can modulate gain / pan each frame.
        private HapbeatStreamPlayback _activePlayback;

        // One-shot warning gates (so misconfiguration prints once, not every frame).
        private bool _warnedNoManager;
        private bool _warnedNoEventMap;
        private bool _warnedStaleId;
        private bool _warnedMissingIntensity;

        // Coroutines spawned via StartHapticDelayCoroutine, representing
        // Fire / Stop requests waiting on hapticDelaySeconds. Tracked so
        // HapbeatManager.OnHapticDelayChanged can flush them when the user
        // edits latency mid-Play (otherwise stale coroutines fire late with
        // the previous delay, causing timing chaos / burst delivery).
        private readonly List<Coroutine> _pendingDelayCoroutines = new List<Coroutine>(4);

        protected virtual void OnEnable()
        {
            HapbeatManager.OnHapticDelayChanged += FlushPendingDelayCoroutines;
        }

        protected virtual void OnDisable()
        {
            HapbeatManager.OnHapticDelayChanged -= FlushPendingDelayCoroutines;
            FlushPendingDelayCoroutines();
        }

        /// <summary>
        /// Stop and discard all Fire / Stop coroutines that are currently
        /// waiting on the global <see cref="HapbeatConfig.hapticDelaySeconds"/>
        /// (or per-entry offset) before delivering to the device.
        /// <para>
        /// Active StreamClip playbacks (loops, ongoing sequences), the mixer
        /// session, and per-trigger state (_isActive, _activePlayback) are
        /// <b>not</b> touched. Only the per-call deferral that captured the
        /// previous delay value is cancelled.
        /// </para>
        /// </summary>
        public void FlushPendingDelayCoroutines()
        {
            for (int i = 0; i < _pendingDelayCoroutines.Count; i++)
            {
                var c = _pendingDelayCoroutines[i];
                if (c != null) StopCoroutine(c);
            }
            _pendingDelayCoroutines.Clear();
        }

        /// <summary>
        /// Start a coroutine that represents a Fire / Stop request waiting on
        /// the latency-compensation delay. Tracks the coroutine so it can be
        /// flushed when <c>hapticDelaySeconds</c> changes during Play, and
        /// auto-removes itself from the tracker on natural completion.
        /// </summary>
        protected Coroutine StartHapticDelayCoroutine(IEnumerator routine)
        {
            // Use a single-element array as a mutable holder so the wrapper
            // can reference its own Coroutine handle. The Coroutine reference
            // is returned by StartCoroutine and stored into the holder before
            // the wrapper runs (Unity yields control back before the first
            // iteration), so the wrapper sees the handle once it executes.
            var holder = new Coroutine[1];
            holder[0] = StartCoroutine(WrapHapticDelayCoroutine(routine, holder));
            _pendingDelayCoroutines.Add(holder[0]);
            return holder[0];
        }

        private IEnumerator WrapHapticDelayCoroutine(IEnumerator inner, Coroutine[] selfHolder)
        {
            yield return StartCoroutine(inner);
            // Natural completion → drop ourselves from the tracker list.
            // If we were cancelled via StopCoroutine, this line never runs
            // and the (now invalid) Coroutine ref stays in the list until
            // the next Flush, which is harmless (StopCoroutine on a stopped
            // coroutine is a no-op and Clear() drops the entry).
            _pendingDelayCoroutines.Remove(selfHolder[0]);
        }

        /// <summary>The event map this trigger references.</summary>
        public HapbeatEventMap EventMap => _eventMap;

        /// <summary>Stable GUID of the referenced entry. Empty = unassigned.</summary>
        public string EntryId => _entryId;

        /// <summary>Whether this trigger is enabled.</summary>
        public bool TriggerEnabled
        {
            get => _triggerEnabled;
            set => _triggerEnabled = value;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Direct field assignment for use by scene-builder Editor scripts.
        /// Bypasses SerializedObject to avoid inheritance-traversal issues.
        /// Caller must call EditorUtility.SetDirty(this) after.
        /// </summary>
        public void EditorSetupEntry(HapbeatEventMap map, string entryId)
        {
            _eventMap = map;
            _entryId  = entryId;
        }
#endif

        /// <summary>
        /// Per-trigger gain multiplier applied on top of the entry's gain.
        /// 1.0 = use entry default. Range [0, 2].
        /// </summary>
        public float GainMultiplier
        {
            get => _gainMultiplier;
            set
            {
                _gainMultiplier = Mathf.Clamp(value, 0f, 2f);
                // 再生中の stream playback がある場合、playback の単一経路
                // ApplyGainModulation を通じて Gain を更新する
                // (HapbeatParameterBinding と同じ entry point を共有することで
                // 計算式の二重定義を避ける。Interface だけ違って内部処理は集約)。
                if (_activePlayback != null && !_activePlayback.IsStopped)
                    _activePlayback.ApplyGainModulation(_gainMultiplier);
            }
        }

        /// <summary>
        /// Per-trigger stereo pan, [-1 (full left), +1 (full right)]. Mono clip では無視される。
        /// 再生中なら setter で即時 Playback.Pan に push (script-driven pan modulation 用)。
        /// HapbeatParameterBinding (Output=StreamPan) と対称の役割を持つ imperative API。
        /// </summary>
        public float Pan
        {
            get => _pan;
            set
            {
                _pan = Mathf.Clamp(value, -1f, 1f);
                if (_activePlayback != null && !_activePlayback.IsStopped)
                    _activePlayback.Pan = _pan;
            }
        }
        private float _pan = 0f;

        /// <summary>
        /// Active StreamClip playback handle, or null if nothing is streaming.
        /// Used by <see cref="HapbeatParameterBinding"/> to write Gain / Pan
        /// each frame. Cleared automatically when the stream stops.
        /// </summary>
        public HapbeatStreamPlayback ActivePlayback
        {
            get
            {
                if (_activePlayback != null && _activePlayback.IsStopped)
                    _activePlayback = null;
                return _activePlayback;
            }
        }

        /// <summary>
        /// Resolve the referenced entry by stable GUID. Returns null if the id
        /// is unassigned or stale (entry was deleted from the EventMap).
        /// </summary>
        public HapbeatEventEntry ResolveEntry()
        {
            if (_eventMap == null) return null;
            if (string.IsNullOrEmpty(_entryId)) return null;

            var byId = _eventMap.FindById(_entryId);
            if (byId != null) return byId;

            // Stale id — entry was deleted from the map. Warn once so the user
            // knows to re-assign in the Inspector.
            if (!_warnedStaleId)
            {
                Debug.LogWarning(
                    $"[Hapbeat] Trigger on {name}: entry id '{_entryId}' not found " +
                    $"in EventMap '{_eventMap.name}'. The referenced entry may have " +
                    "been deleted. Open the EventMap and re-assign the entry in the " +
                    "Inspector.", this);
                _warnedStaleId = true;
            }
            return null;
        }

        /// <summary>
        /// Effective fire-time deferral (seconds) for this entry. Combines the
        /// global <see cref="HapbeatConfig.hapticDelaySeconds"/> with the
        /// per-entry <see cref="HapbeatEventEntry.delayOffsetSeconds"/> and
        /// clamps to >= 0 (no time-travel).
        /// <para>
        /// Used to compensate audio-output latency (Bluetooth ヘッドホン等) by
        /// holding back the Hapbeat haptic so it aligns with the speakers /
        /// headphones output. See <c>HapbeatConfig.hapticDelaySeconds</c> for
        /// device-specific guidance.
        /// </para>
        /// </summary>
        protected float ComputeEffectiveDelaySeconds(HapbeatEventEntry entry)
        {
            float global = HapbeatManager.Instance != null
                ? HapbeatManager.Instance.HapticDelaySeconds
                : 0f;
            float offset = entry != null ? entry.delayOffsetSeconds : 0f;
            return Mathf.Max(0f, global + offset);
        }

        /// <summary>
        /// Find a <see cref="HapbeatParameterBinding"/> linked to <paramref name="entryId"/>
        /// (and matching the requested output type) by walking the local hierarchy.
        /// <para>
        /// Scope mirrors <see cref="HapbeatParameterBinding.OnEnable"/>'s trigger
        /// lookup: <b>Self → Children → Parent</b>. This keeps the trigger ↔ binding
        /// pairing symmetric so a binding placed on a child / parent GameObject is
        /// still picked up by the trigger's pre-seed pass at Fire() time
        /// (otherwise the first chunk would send <c>baseline × _gainMultiplier (1.0)</c>
        /// instead of <c>baseline × binding.EvaluateNow()</c>, causing a 1-chunk
        /// burst before the binding's per-frame Update takes over).
        /// </para>
        /// </summary>
        private HapbeatParameterBinding FindBindingForEntry(string entryId, bool wantStreamGain)
        {
            foreach (var b in GetComponentsInChildren<HapbeatParameterBinding>(true))
            {
                if (BindingMatches(b, entryId, wantStreamGain)) return b;
            }
            foreach (var b in GetComponentsInParent<HapbeatParameterBinding>(true))
            {
                if (BindingMatches(b, entryId, wantStreamGain)) return b;
            }
            return null;
        }

        private static bool BindingMatches(HapbeatParameterBinding b, string entryId, bool wantStreamGain)
        {
            if (b == null) return false;
            if (b.LinkedOwnerEntryId != entryId) return false;
            return wantStreamGain ? b.IsStreamGainOutput : b.IsStreamPanOutput;
        }

        /// <summary>
        /// Fire the haptic event referenced by this trigger.
        /// Behavior depends on the entry's mode (Command or StreamClip).
        /// <para>
        /// If <see cref="ComputeEffectiveDelaySeconds"/> returns > 0, the actual
        /// device send is deferred via a coroutine. Validation / cooldown happens
        /// at call time; deferral is transparent to the caller. Stop arriving
        /// during the deferral window queues its own delayed coroutine, so the
        /// Fire→Stop interval is preserved.
        /// </para>
        /// </summary>
        protected void FireHaptic()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] Fire() called on {name} ({GetType().Name} entryId={_entryId})", this);

            if (!_triggerEnabled)
            {
                if (_verboseLog) Debug.Log($"[Hapbeat] Fire rejected: trigger disabled on {name}", this);
                return;
            }
            if (_eventMap == null)
            {
                if (!_warnedNoEventMap)
                {
                    Debug.LogWarning($"[Hapbeat] Fire rejected: no EventMap assigned on {name} ({GetType().Name}). " +
                                     "Assign an event map in the Inspector.", this);
                    _warnedNoEventMap = true;
                }
                return;
            }

            var entry = ResolveEntry();
            if (entry == null)
            {
                Debug.LogWarning($"[Hapbeat] Fire rejected: entry not found on {name} " +
                                 $"(id='{_entryId}', map has {_eventMap.entries.Count} entries)", this);
                return;
            }

            if (_cooldown > 0f && Time.unscaledTime - _lastFireTime < _cooldown)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] Fire rejected: cooldown ({_cooldown:F2}s) on {name}", this);
                return;
            }
            _lastFireTime = Time.unscaledTime;

            if (HapbeatManager.Instance == null)
            {
                if (!_warnedNoManager)
                {
                    Debug.LogWarning($"[Hapbeat] Fire rejected: no HapbeatManager in scene (required for '{entry.displayName}' on {name}). " +
                                     "Add one via 'Hapbeat > Create Event Router' or 'GameObject > Hapbeat > Event Router'.", this);
                    _warnedNoManager = true;
                }
                return;
            }

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] Fire deferred by {delay * 1000f:F0}ms on {name} " +
                              $"(global={HapbeatManager.Instance.HapticDelaySeconds:F3}s + " +
                              $"entry.offset={entry.delayOffsetSeconds:F3}s)", this);
                StartHapticDelayCoroutine(FireHapticAfterDelay(entry, delay));
                return;
            }

            FireHapticImmediate(entry);
        }

        private IEnumerator FireHapticAfterDelay(HapbeatEventEntry entry, float delay)
        {
            // Realtime: 出力 audio は Time.timeScale に影響されないので、触覚側も
            // unscaled で測る (ペアの Stop も同じ delay で遅らせるので Fire-Stop
            // インターバルは元のまま保たれる)。
            yield return new WaitForSecondsRealtime(delay);

            // Coroutine 中に MB が destroy された場合は Unity が自動停止する。
            // 残った状態チェック: Manager が消えた / trigger が無効化された等を再評価。
            if (!_triggerEnabled || HapbeatManager.Instance == null) yield break;

            FireHapticImmediate(entry);
        }

        private void FireHapticImmediate(HapbeatEventEntry entry)
        {
            string baseName = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            // GameObject 名を prefix してログでどの trigger 由来か分かるようにする
            string label = $"{gameObject.name}: {baseName}";
            string target = entry.HasTarget ? entry.target : null;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire rejected: Command mode but eventId is empty on entry '{label}'", this);
                        return;
                    }
                    {
                        // GetEffectiveGain() = entry.gain × manifest.intensity.
                        // Device no longer reads manifest.intensity at runtime —
                        // the sender (SDK) is responsible for the multiplication.
                        // Per-trigger multiplier composes on top so the same
                        // EventMap entry can run at different intensities across
                        // GameObjects without authoring extra entries.
                        if (entry.CachedManifestIntensity < 0f && !_warnedMissingIntensity)
                        {
                            Debug.LogWarning(
                                $"[Hapbeat] Command entry '{label}' has no cached manifest " +
                                $"intensity; firing at plain gain={entry.gain:F2} " +
                                "(intensity factor skipped). Open the EventMap window to refresh the cache, " +
                                "and confirm the Kit is deployed on this device.", this);
                            _warnedMissingIntensity = true;
                        }
                        float commandGain = entry.GetEffectiveGain() * _gainMultiplier;
                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire Command: eventId='{entry.eventId}' target='{target ?? "(broadcast)"}' " +
                                      $"gain={entry.gain:F2} × intensity={(entry.CachedManifestIntensity >= 0f ? entry.CachedManifestIntensity.ToString("F2") : "?")} " +
                                      $"× triggerMult={_gainMultiplier:F2} = {commandGain:F2}", this);
                        HapbeatManager.Instance.Play(entry.eventId, commandGain, label, target);
                    }
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire rejected: StreamClip mode but streamClip is null on entry '{label}'", this);
                        return;
                    }
                    {
                        // baselineGain = author intent (entry.gain × manifest.intensity)。
                        // _gainMultiplier / binding output は live modulator として後乗せするので
                        // baselineGain には焼き込まない (これにより script から GainMultiplier を
                        // 毎フレーム変えても playback.Gain = baseline × multiplier で対称に効く)。
                        float baselineGain = entry.GetEffectiveGain();
                        // Warn once per trigger if the manifest intensity cache is
                        // unresolved — the fire is still going through but at plain
                        // entry.gain, which is a common source of "the runtime
                        // stream is louder than what I authored" confusion.
                        if (entry.CachedManifestIntensity < 0f && !_warnedMissingIntensity)
                        {
                            Debug.LogWarning(
                                $"[Hapbeat] StreamClip entry '{label}' has no cached manifest " +
                                $"intensity; firing at plain gain={entry.gain:F2} " +
                                "(intensity factor skipped). Open the EventMap window to refresh the cache, " +
                                "and confirm the clip is in a deployed Kit.", this);
                            _warnedMissingIntensity = true;
                        }
                        // 初期 modulator: 自己 + 子孫 + 親 を辿って同 entry を modulate する binding
                        // (Output=StreamGain) を探し、binding.EvaluateNow() を pre-seed 値とする。
                        // 無ければ script からの _gainMultiplier を使う。これで stream 開始直後の
                        // 数 chunk が "baseline 100%" で鳴って burst するのを防ぐ (race-free silent start)。
                        // HapbeatParameterBinding.OnEnable の trigger 探索と対称にスコープを取る
                        // (Self → Children → Parent)。同 GO だけだと、binding が trigger と別 GO に
                        // 置かれている (例: SequenceTrigger は root、binding は child 物体) 構成で
                        // 見つからず初回 chunk が 1.0 で送信される問題を回避。
                        var gainBind = FindBindingForEntry(entry.id, wantStreamGain: true);
                        float initialMod = gainBind != null ? gainBind.EvaluateNow() : _gainMultiplier;
                        float initialGain = baselineGain * initialMod;

                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire StreamClip: clip='{entry.streamClip.name}' " +
                                      $"target='{target ?? "(broadcast)"}' gain={entry.gain:F2} " +
                                      $"intensity={(entry.CachedManifestIntensity > 0f ? entry.CachedManifestIntensity.ToString("F2") : "?")} " +
                                      $"baseline={baselineGain:F2} initialMod={initialMod:F2} initial={initialGain:F2} loop={entry.loop}", this);
                        _activePlayback = HapbeatManager.Instance.StreamAudioClip(
                            entry.streamClip, baselineGain, initialGain, target, entry.loop);

                        // pan binding (Output=StreamPan) も同様に Self → Children → Parent で探索し
                        // initial pan を pre-seed (gain と同様の race 対策)。無ければ script の _pan。
                        if (_activePlayback != null)
                        {
                            var panBind = FindBindingForEntry(entry.id, wantStreamGain: false);
                            float initialPan = panBind != null ? panBind.EvaluateNow() : _pan;
                            _activePlayback.Pan = initialPan;
                        }

                        if (_verboseLog)
                        {
                            int bindingCount = GetComponentsInChildren<HapbeatParameterBinding>(true).Length;
                            Debug.Log(
                                $"[Hapbeat] \u266a StreamClip start: \"{label}\" " +
                                $"(baseline={baselineGain:F2}, initialMod={initialMod:F2}, loop={entry.loop}, " +
                                $"{bindingCount} binding(s))",
                                this);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Stop the haptic event referenced by this trigger.
        /// <para>
        /// Symmetric to <see cref="FireHaptic"/>: if effective delay > 0, the
        /// stop is deferred by the same amount so the perceived Fire→Stop
        /// interval matches the caller's intent (otherwise an early Stop would
        /// silently win the race against a still-pending Fire).
        /// </para>
        /// </summary>
        protected void StopHaptic()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] Stop() called on {name} ({GetType().Name} entryId={_entryId})", this);

            if (_eventMap == null) return;

            var entry = ResolveEntry();
            if (entry == null) return;

            if (HapbeatManager.Instance == null) return;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                StartHapticDelayCoroutine(StopHapticAfterDelay(entry, delay));
                return;
            }

            StopHapticImmediate(entry);
        }

        private IEnumerator StopHapticAfterDelay(HapbeatEventEntry entry, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            StopHapticImmediate(entry);
        }

        private void StopHapticImmediate(HapbeatEventEntry entry)
        {
            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId)) return;
                    string label = string.IsNullOrEmpty(entry.displayName) ? entry.eventId : entry.displayName;
                    // Use entry.target so Stop matches the same scope the Play targeted.
                    string stopTarget = entry.HasTarget ? entry.target : null;
                    HapbeatManager.Instance.Stop(entry.eventId, label, stopTarget);
                    break;

                case HapticMode.StreamClip:
                    // Per-source stop only。 Manager.StopStream() は全 source を
                    // 巻き込むので呼ばない (multi-source mixing 対応, 2026-05-18)。
                    // Mixer は次 chunk で IsStopped を検知して当該 source を
                    // 自動除去 + 最後の source が消えたら session 終了する。
                    _activePlayback?.Stop();
                    _activePlayback = null;
                    break;
            }
        }
    }
}
