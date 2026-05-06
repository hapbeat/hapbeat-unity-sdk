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

        /// <summary>
        /// Per-trigger gain multiplier applied on top of the entry's gain.
        /// 1.0 = use entry default. Range [0, 2].
        /// </summary>
        public float GainMultiplier
        {
            get => _gainMultiplier;
            set => _gainMultiplier = Mathf.Clamp(value, 0f, 2f);
        }

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
        /// Fire the haptic event referenced by this trigger.
        /// Behavior depends on the entry's mode (Command or StreamClip).
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

            string label = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
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
                        HapbeatManager.Instance.Play(entry.eventId, commandGain, entry.group, label, target);
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
                        // Same multiplier policy as Command above — applied on top
                        // of entry.gain × manifest.intensity (GetEffectiveGain).
                        float streamGain = entry.GetEffectiveGain() * _gainMultiplier;
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
                        // Pre-seed initial gain from any sibling binding that
                        // writes StreamGain for this entry. Without this the
                        // stream plays at full baseline for ~100 ms (while the
                        // first ~6 STREAM_DATA chunks go out before the first
                        // binding.Update() runs) and users hear a burst at
                        // stream start — especially noticeable on hover-Fire
                        // poke buttons where the binding should start at 0.
                        float initialGain = streamGain;
                        foreach (var b in GetComponents<HapbeatParameterBinding>())
                        {
                            if (b == null) continue;
                            if (b.LinkedOwnerEntryId != entry.id) continue;
                            if (!b.IsStreamGainOutput) continue;
                            initialGain = streamGain * b.EvaluateNow();
                            break;
                        }

                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire StreamClip: clip='{entry.streamClip.name}' " +
                                      $"target='{target ?? "(broadcast)"}' gain={entry.gain:F2} " +
                                      $"intensity={(entry.CachedManifestIntensity > 0f ? entry.CachedManifestIntensity.ToString("F2") : "?")} " +
                                      $"effective={streamGain:F2} initial={initialGain:F2} loop={entry.loop}", this);
                        _activePlayback = HapbeatManager.Instance.StreamAudioClip(
                            entry.streamClip, streamGain, initialGain, target, entry.loop);
                        if (_verboseLog)
                        {
                            var bindings = GetComponents<HapbeatParameterBinding>();
                            Debug.Log(
                                $"[Hapbeat] \u266a StreamClip start: \"{label}\" " +
                                $"(effective gain={streamGain:F2}, loop={entry.loop}, " +
                                $"{bindings.Length} binding(s))",
                                this);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Stop the haptic event referenced by this trigger.
        /// </summary>
        protected void StopHaptic()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] Stop() called on {name} ({GetType().Name} entryId={_entryId} idx={_entryIndex})", this);

            if (_eventMap == null) return;

            var entry = ResolveEntry();
            if (entry == null) return;

            if (HapbeatManager.Instance == null) return;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId)) return;
                    string label = string.IsNullOrEmpty(entry.displayName) ? entry.eventId : entry.displayName;
                    HapbeatManager.Instance.Stop(entry.eventId, entry.group, label);
                    break;

                case HapticMode.StreamClip:
                    _activePlayback?.Stop();
                    HapbeatManager.Instance.StopStream();
                    _activePlayback = null;
                    break;
            }
        }
    }
}
