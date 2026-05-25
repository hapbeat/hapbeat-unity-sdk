using System.Collections;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Abstract base for all Hapbeat trigger components.
    /// References a HapbeatEventMap and a specific entry by <b>stable GUID</b>
    /// (<see cref="HapbeatEventEntry.id"/>). The list-index (<see cref="_entryIndex"/>)
    /// is kept as a human-readable display cache and as a migration path for
    /// scenes authored before the id system existed.
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
        // dropdown / Batch Setup, and migrated from _entryIndex on first access
        // for legacy data.
        [SerializeField, HideInInspector]
        protected string _entryId;

        [Tooltip("List index of the entry (display cache — the authoritative reference " +
                 "is the hidden stable GUID). Safe to ignore unless debugging; reorders " +
                 "in the EventMap will not change which entry this trigger fires.")]
        [SerializeField]
        protected int _entryIndex;

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

        /// <summary>The event map this trigger references.</summary>
        public HapbeatEventMap EventMap => _eventMap;

        /// <summary>
        /// Stable GUID of the referenced entry. Authoritative reference — if empty,
        /// <see cref="EntryIndex"/> is used as a fallback and lazily migrated.
        /// </summary>
        public string EntryId => _entryId;

        /// <summary>
        /// List index of the referenced entry (display cache). The actual entry
        /// resolution happens via <see cref="EntryId"/>; this integer is only
        /// authoritative when <c>_entryId</c> is empty (legacy data).
        /// </summary>
        public int EntryIndex => _entryIndex;

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
        public void EditorSetupEntry(HapbeatEventMap map, string entryId, int entryIndex)
        {
            _eventMap   = map;
            _entryId    = entryId;
            _entryIndex = entryIndex;
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
        /// Resolve the referenced entry, preferring <see cref="_entryId"/> (stable
        /// GUID) and falling back to <see cref="_entryIndex"/> (legacy) when the
        /// id field is empty. Returns null if nothing matched.
        /// <para>
        /// Side effect: when the id is empty but index resolves to a valid entry,
        /// we write the entry's id back into <see cref="_entryId"/> so future
        /// lookups go through the stable path. This is a best-effort in-memory
        /// migration; editor-side tools (<c>HapbeatMigrateLegacyReferences</c>)
        /// persist the migration to disk.
        /// </para>
        /// </summary>
        public HapbeatEventEntry ResolveEntry()
        {
            if (_eventMap == null) return null;

            if (!string.IsNullOrEmpty(_entryId))
            {
                var byId = _eventMap.FindById(_entryId);
                if (byId != null)
                {
                    // Keep the index cache in sync for Inspector display.
                    int idx = _eventMap.IndexOfId(_entryId);
                    if (idx >= 0) _entryIndex = idx;
                    return byId;
                }
                // Stale id — entry was deleted. Don't silently fall back to index
                // (would fire the wrong haptic). Warn once.
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

            // Legacy path — no id stored. Use index and migrate.
            var byIndex = _eventMap.GetEntry(_entryIndex);
            if (byIndex != null)
            {
                _entryId = byIndex.id;
            }
            return byIndex;
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
                Debug.Log($"[Hapbeat] Fire() called on {name} ({GetType().Name} entryId={_entryId} idx={_entryIndex})", this);

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
                                 $"(id='{_entryId}', idx={_entryIndex}, map has {_eventMap.entries.Count} entries)", this);
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
                StartCoroutine(FireHapticAfterDelay(entry, delay));
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
                        // 初期 modulator: 同じ GO 上の binding (Output=StreamGain) があれば
                        // binding.EvaluateNow() を、無ければ script からの _gainMultiplier を使う。
                        // これで stream 開始直後の数 chunk が "baseline 100%" で鳴って burst するのを
                        // 防ぐ (race-free silent start)。script からも GainMultiplier を Fire 前に
                        // 0 にしておけば同等の挙動になる。
                        float initialMod = _gainMultiplier;
                        foreach (var b in GetComponents<HapbeatParameterBinding>())
                        {
                            if (b == null) continue;
                            if (b.LinkedOwnerEntryId != entry.id) continue;
                            if (!b.IsStreamGainOutput) continue;
                            initialMod = b.EvaluateNow();
                            break;
                        }
                        float initialGain = baselineGain * initialMod;

                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire StreamClip: clip='{entry.streamClip.name}' " +
                                      $"target='{target ?? "(broadcast)"}' gain={entry.gain:F2} " +
                                      $"intensity={(entry.CachedManifestIntensity > 0f ? entry.CachedManifestIntensity.ToString("F2") : "?")} " +
                                      $"baseline={baselineGain:F2} initialMod={initialMod:F2} initial={initialGain:F2} loop={entry.loop}", this);
                        _activePlayback = HapbeatManager.Instance.StreamAudioClip(
                            entry.streamClip, baselineGain, initialGain, target, entry.loop);

                        // 同 GO 上の pan binding (Output=StreamPan) があれば initial pan を pre-seed
                        // (gain と同様の race 対策)。無ければ script の _pan を初期値とする。
                        if (_activePlayback != null)
                        {
                            float initialPan = _pan;
                            foreach (var b in GetComponents<HapbeatParameterBinding>())
                            {
                                if (b == null) continue;
                                if (b.LinkedOwnerEntryId != entry.id) continue;
                                if (!b.IsStreamPanOutput) continue;
                                initialPan = b.EvaluateNow();
                                break;
                            }
                            _activePlayback.Pan = initialPan;
                        }

                        if (_verboseLog)
                        {
                            var bindings = GetComponents<HapbeatParameterBinding>();
                            Debug.Log(
                                $"[Hapbeat] \u266a StreamClip start: \"{label}\" " +
                                $"(baseline={baselineGain:F2}, initialMod={initialMod:F2}, loop={entry.loop}, " +
                                $"{bindings.Length} binding(s))",
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
                Debug.Log($"[Hapbeat] Stop() called on {name} ({GetType().Name} entryId={_entryId} idx={_entryIndex})", this);

            if (_eventMap == null) return;

            var entry = ResolveEntry();
            if (entry == null) return;

            if (HapbeatManager.Instance == null) return;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                StartCoroutine(StopHapticAfterDelay(entry, delay));
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
