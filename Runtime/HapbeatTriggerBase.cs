using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Abstract base for all Hapbeat trigger components.
    /// References a HapbeatEventMap and a specific entry index.
    /// Supports Command and StreamClip modes. When a StreamClip entry fires,
    /// the resulting <see cref="HapbeatStreamPlayback"/> handle is exposed via
    /// <see cref="ActivePlayback"/> so <see cref="HapbeatParameterBinding"/>
    /// components can modulate gain / pan in real time.
    /// </summary>
    public abstract class HapbeatTriggerBase : MonoBehaviour
    {
        [Header("Hapbeat Event")]
        [Tooltip("The event map asset containing haptic event definitions.")]
        [SerializeField]
        protected HapbeatEventMap _eventMap;

        [Tooltip("Index of the entry in the event map to trigger.")]
        [SerializeField]
        protected int _entryIndex;

        [Header("Trigger Settings")]
        [Tooltip("Enable or disable this trigger.")]
        [SerializeField]
        protected bool _triggerEnabled = true;

        [Tooltip("Minimum time between firings (seconds). 0 = no cooldown.")]
        [SerializeField]
        protected float _cooldown = 0f;

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

        /// <summary>The event map this trigger references.</summary>
        public HapbeatEventMap EventMap => _eventMap;

        /// <summary>The entry index within the event map.</summary>
        public int EntryIndex => _entryIndex;

        /// <summary>Whether this trigger is enabled.</summary>
        public bool TriggerEnabled
        {
            get => _triggerEnabled;
            set => _triggerEnabled = value;
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
                // Clear the cached handle once the stream has ended so bindings
                // don't keep writing into a zombie object.
                if (_activePlayback != null && _activePlayback.IsStopped)
                    _activePlayback = null;
                return _activePlayback;
            }
        }

        /// <summary>
        /// Fire the haptic event referenced by this trigger.
        /// Behavior depends on the entry's mode (Command or StreamClip).
        /// </summary>
        protected void FireHaptic()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] Fire() called on {name} ({GetType().Name} #{_entryIndex})", this);

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

            var entry = _eventMap.GetEntry(_entryIndex);
            if (entry == null)
            {
                Debug.LogWarning($"[Hapbeat] Fire rejected: entry index {_entryIndex} out of range on {name} " +
                                 $"(EventMap has {_eventMap.entries.Count} entries)", this);
                return;
            }

            // Cooldown check
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
                    if (_verboseLog)
                        Debug.Log($"[Hapbeat] Fire Command: eventId='{entry.eventId}' target='{target ?? "(broadcast)"}' gain={entry.gain:F2}", this);
                    HapbeatManager.Instance.Play(entry.eventId, entry.gain, entry.group, label, target);
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire rejected: StreamClip mode but streamClip is null on entry '{label}'", this);
                        return;
                    }
                    {
                        // Effective gain = entry.gain × manifest.intensity (cached at author-time).
                        // See HapbeatEventEntry.GetEffectiveGain — stream modes must apply intensity
                        // themselves because the device just replays the raw PCM it receives.
                        float streamGain = entry.GetEffectiveGain();
                        if (_verboseLog)
                            Debug.Log($"[Hapbeat] Fire StreamClip: clip='{entry.streamClip.name}' " +
                                      $"target='{target ?? "(broadcast)"}' gain={entry.gain:F2} " +
                                      $"intensity={(entry.CachedManifestIntensity > 0f ? entry.CachedManifestIntensity.ToString("F2") : "?")} " +
                                      $"effective={streamGain:F2} loop={entry.loop}", this);
                        _activePlayback = HapbeatManager.Instance.StreamAudioClip(
                            entry.streamClip, streamGain, target, entry.loop);
                        // Summarize parameter bindings that will modulate this stream so
                        // the user can verify wiring at trigger time.
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
                Debug.Log($"[Hapbeat] Stop() called on {name} ({GetType().Name} #{_entryIndex})", this);

            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(_entryIndex);
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
                    // Stop via the playback handle if it's still alive (which
                    // also lets bindings notice via IsStopped), then tell the
                    // manager so it tears down the coroutine.
                    _activePlayback?.Stop();
                    HapbeatManager.Instance.StopStream();
                    _activePlayback = null;
                    break;
            }
        }
    }
}
