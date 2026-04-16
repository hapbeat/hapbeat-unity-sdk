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

        protected float _lastFireTime = float.NegativeInfinity;

        // Cached AudioBridge for StreamSource mode (added dynamically)
        private HapbeatAudioBridge _cachedAudioBridge;

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
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(_entryIndex);
            if (entry == null) return;

            // Cooldown check
            if (_cooldown > 0f && Time.unscaledTime - _lastFireTime < _cooldown)
                return;
            _lastFireTime = Time.unscaledTime;

            if (HapbeatManager.Instance == null) return;

            string label = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            string target = entry.HasTarget ? entry.target : null;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId)) return;
                    HapbeatManager.Instance.Play(entry.eventId, entry.gain, entry.group, label, target);
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null) return;
                    HapbeatManager.Instance.StreamAudioClip(entry.streamClip, entry.gain, target);
                    break;

                case HapticMode.StreamSource:
                    StartAudioSourceStream(entry);
                    break;
            }
        }

        /// <summary>
        /// Stop the haptic event referenced by this trigger.
        /// </summary>
        protected void StopHaptic()
        {
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
        /// Find AudioSource on this GameObject (or children) and start streaming via AudioBridge.
        /// </summary>
        private void StartAudioSourceStream(HapbeatEventEntry entry)
        {
            var audioSource = GetComponent<AudioSource>() ?? GetComponentInChildren<AudioSource>();
            if (audioSource == null)
            {
                Debug.LogWarning($"[Hapbeat] StreamSource: No AudioSource found on {gameObject.name} or children.");
                return;
            }

            // Get or add HapbeatAudioBridge on the AudioSource's GameObject
            var bridge = audioSource.GetComponent<HapbeatAudioBridge>();
            if (bridge == null)
                bridge = audioSource.gameObject.AddComponent<HapbeatAudioBridge>();

            bridge.Gain = entry.gain;
            bridge.Target = entry.HasTarget ? entry.target : null;
            bridge.StartStreaming();
            _cachedAudioBridge = bridge;
        }

        private void StopAudioSourceStream()
        {
            if (_cachedAudioBridge != null && _cachedAudioBridge.IsStreaming)
                _cachedAudioBridge.StopStreaming();
        }
    }
}
