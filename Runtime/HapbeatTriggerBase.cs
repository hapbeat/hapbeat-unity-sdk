using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Abstract base for all Hapbeat trigger components.
    /// References a HapbeatEventMap and a specific entry index.
    /// Supports Command, StreamClip, and StreamSource modes.
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

        // Cached AudioBridge/Source for StreamSource mode
        private HapbeatAudioBridge _cachedAudioBridge;
        private AudioSource _cachedAudioSource;

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
        /// Fire the haptic event referenced by this trigger.
        /// Behavior depends on the entry's mode (Command, StreamClip, StreamSource).
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
                        HapbeatManager.Instance.StreamAudioClip(entry.streamClip, streamGain, target, entry.loop);
                    }
                    break;

                case HapticMode.StreamSource:
                    if (_verboseLog)
                        Debug.Log($"[Hapbeat] Fire StreamSource: entry='{label}' target='{target ?? "(broadcast)"}' gain={entry.gain:F2}", this);
                    StartAudioSourceStream(entry);
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
                    HapbeatManager.Instance.StopStream();
                    break;

                case HapticMode.StreamSource:
                    StopAudioSourceStream();
                    break;
            }
        }

        /// <summary>
        /// Find the Hapbeat-tagged AudioSource (one with HapbeatAudioBridge) and start streaming.
        /// Falls back to any AudioSource if no bridge exists yet.
        /// </summary>
        private void StartAudioSourceStream(HapbeatEventEntry entry)
        {
            // Priority 1: existing HapbeatAudioBridge identifies the target AudioSource
            var bridge = GetComponent<HapbeatAudioBridge>() ?? GetComponentInChildren<HapbeatAudioBridge>();
            AudioSource audioSource;

            if (bridge != null)
            {
                audioSource = bridge.GetComponent<AudioSource>();
                if (audioSource == null)
                {
                    Debug.LogWarning($"[Hapbeat] StreamSource: HapbeatAudioBridge on {bridge.gameObject.name} has no AudioSource.");
                    return;
                }
            }
            else
            {
                // Fallback: find any AudioSource and add a bridge to it
                audioSource = GetComponent<AudioSource>() ?? GetComponentInChildren<AudioSource>();
                if (audioSource == null)
                {
                    Debug.LogWarning($"[Hapbeat] StreamSource: No AudioSource found on {gameObject.name} or children.");
                    return;
                }
                bridge = audioSource.gameObject.AddComponent<HapbeatAudioBridge>();
            }

            // Effective gain = entry.gain × manifest.intensity (cached). Stream modes
            // must apply intensity themselves — see HapbeatEventEntry.GetEffectiveGain.
            float effectiveGain = entry.GetEffectiveGain();
            bridge.Gain = effectiveGain;
            bridge.Target = entry.HasTarget ? entry.target : null;
            bridge.SilentMode = entry.silentMode;
            audioSource.loop = entry.loop;
            bridge.StartStreaming();

            if (!audioSource.isPlaying)
                audioSource.Play();

            _cachedAudioBridge = bridge;
            _cachedAudioSource = audioSource;

            string label = string.IsNullOrEmpty(entry.displayName) ? entry.eventId : entry.displayName;

            // Summarize attached ParameterBindings so the user can verify continuous-control
            // wiring at trigger time (instead of having to open each binding's Inspector).
            var bindings = audioSource.GetComponents<HapbeatParameterBinding>();
            string bindingSummary;
            if (bindings.Length == 0)
            {
                bindingSummary = "no bindings (static level)";
            }
            else
            {
                var parts = new System.Text.StringBuilder();
                for (int i = 0; i < bindings.Length; i++)
                {
                    if (i > 0) parts.Append(", ");
                    var b = bindings[i];
                    // Use the Inspector's serialized field via SerializedObject? Avoid —
                    // Runtime. Instead rely on the binding's startup log for full detail.
                    parts.Append($"#{i}");
                }
                bindingSummary = $"{bindings.Length} binding(s): {parts}";
            }

            // Include both raw and effective gain so the user can see at a glance
            // whether manifest intensity is being applied. "intensity=?" means the
            // cache is unresolved — deploy the Kit from Studio and Refresh the
            // EventMap (↻ toolbar) to populate it.
            string intensityPart = entry.CachedManifestIntensity > 0f
                ? $"intensity={entry.CachedManifestIntensity:F2}"
                : "intensity=? (not cached)";
            Debug.Log(
                $"[Hapbeat] \u223c StreamSource start: \"{label}\" on {audioSource.gameObject.name} " +
                $"(gain={entry.gain:F2} \u00d7 {intensityPart} \u2192 effective={effectiveGain:F2}, " +
                $"loop={entry.loop}, silent={entry.silentMode}, {bindingSummary})",
                this);
        }

        private void StopAudioSourceStream()
        {
            if (_cachedAudioBridge != null && _cachedAudioBridge.IsStreaming)
            {
                _cachedAudioBridge.StopStreaming();
                Debug.Log($"[Hapbeat] \u223c StreamSource stop");
            }
            if (_cachedAudioSource != null && _cachedAudioSource.isPlaying)
                _cachedAudioSource.Stop();
        }
    }
}
