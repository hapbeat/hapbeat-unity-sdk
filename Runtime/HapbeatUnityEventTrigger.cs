using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Generic haptic trigger that exposes public methods for use with UnityEvents.
    /// Wire Fire() to any UnityEvent: UI Button OnClick, Animation Event,
    /// XR Interaction Toolkit events, etc.
    /// Can be placed on a dedicated [Hapbeat Router] GameObject.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat UnityEvent Trigger")]
    public class HapbeatUnityEventTrigger : HapbeatTriggerBase
    {
        /// <summary>
        /// Fire the haptic event. Call from any UnityEvent.
        /// </summary>
        public void Fire()
        {
            FireHaptic();
        }

        /// <summary>
        /// Fire with a gain override. Useful for Animation Events with float parameter.
        /// </summary>
        public void FireWithGain(float gain)
        {
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(_entryIndex);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            if (HapbeatManager.Instance != null)
            {
                string label = string.IsNullOrEmpty(entry.displayName) ? entry.eventId : entry.displayName;
                HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, label);
            }
        }

        /// <summary>
        /// Stop the haptic event. Call from any UnityEvent.
        /// </summary>
        public void Stop()
        {
            StopHaptic();
        }
    }
}
