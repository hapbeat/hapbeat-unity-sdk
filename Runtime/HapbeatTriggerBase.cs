using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Abstract base for all Hapbeat trigger components.
    /// References a HapbeatEventMap and a specific entry index.
    /// Subclasses detect game events (collision, animator, etc.) and call FireHaptic().
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
        /// </summary>
        protected void FireHaptic()
        {
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(_entryIndex);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            // Cooldown check
            if (_cooldown > 0f && Time.unscaledTime - _lastFireTime < _cooldown)
                return;
            _lastFireTime = Time.unscaledTime;

            if (HapbeatManager.Instance != null)
            {
                HapbeatManager.Instance.Play(entry.eventId, entry.gain, entry.group);
            }
        }

        /// <summary>
        /// Stop the haptic event referenced by this trigger.
        /// </summary>
        protected void StopHaptic()
        {
            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(_entryIndex);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            if (HapbeatManager.Instance != null)
            {
                HapbeatManager.Instance.Stop(entry.eventId, entry.group);
            }
        }
    }
}
