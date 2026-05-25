using System.Collections;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Component for triggering haptic events from GameObjects.
    /// Attach to any GameObject and configure the event settings in the Inspector.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Event")]
    public class HapbeatEvent : MonoBehaviour
    {
        [Header("Event Settings")]
        [Tooltip("The event ID to trigger.")]
        [SerializeField]
        private string _eventId = "";

        [Tooltip("Gain multiplier for the haptic effect (0.0 to 1.0+).")]
        [Range(0f, 2f)]
        [SerializeField]
        private float _gain = 1.0f;

        [Tooltip("Device-addressing target string. Empty = broadcast.\n" +
                 "Examples: player_1, */pos_neck, player_1/pos_chest")]
        [SerializeField]
        private string _target = "";

        [Header("Behavior")]
        [Tooltip("Automatically trigger Play when this component is enabled.")]
        [SerializeField]
        private bool _triggerOnStart = false;

        /// <summary>The event ID to trigger.</summary>
        public string EventId
        {
            get => _eventId;
            set => _eventId = value;
        }

        /// <summary>Gain multiplier for the haptic effect.</summary>
        public float Gain
        {
            get => _gain;
            set => _gain = value;
        }

        /// <summary>Device-addressing target string. Empty = broadcast.</summary>
        public string Target
        {
            get => _target;
            set => _target = value;
        }

        private void OnEnable()
        {
            if (_triggerOnStart)
            {
                TriggerPlay();
            }
        }

        /// <summary>
        /// Trigger the PLAY command for this event.
        /// </summary>
        public void TriggerPlay()
        {
            if (HapbeatManager.Instance == null)
            {
                Debug.LogWarning("[Hapbeat] HapbeatManager not found. " +
                                 "Add a HapbeatManager to the scene.");
                return;
            }

            if (string.IsNullOrEmpty(_eventId))
            {
                Debug.LogWarning("[Hapbeat] Event ID is empty. Set it in the Inspector.", this);
                return;
            }

            float delay = HapbeatManager.Instance.HapticDelaySeconds;
            if (delay > 0f)
            {
                StartCoroutine(PlayAfterDelay(_eventId, _gain, _target, delay));
                return;
            }
            HapbeatManager.Instance.Play(_eventId, _gain, target: _target);
        }

        /// <summary>
        /// Trigger the STOP command for this event.
        /// </summary>
        public void TriggerStop()
        {
            if (HapbeatManager.Instance == null)
            {
                Debug.LogWarning("[Hapbeat] HapbeatManager not found. " +
                                 "Add a HapbeatManager to the scene.");
                return;
            }

            if (string.IsNullOrEmpty(_eventId))
            {
                Debug.LogWarning("[Hapbeat] Event ID is empty. Set it in the Inspector.", this);
                return;
            }

            float delay = HapbeatManager.Instance.HapticDelaySeconds;
            if (delay > 0f)
            {
                StartCoroutine(StopAfterDelay(_eventId, _target, delay));
                return;
            }
            HapbeatManager.Instance.Stop(_eventId, target: _target);
        }

        // --- Delay helpers (mirror HapbeatBridge / HapbeatTriggerBase) ---
        // HapbeatEvent has no EventMap binding so only the global
        // HapbeatConfig.hapticDelaySeconds applies (no per-entry offset).

        private static IEnumerator PlayAfterDelay(string eventId, float gain, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.Play(eventId, gain, target: target);
        }

        private static IEnumerator StopAfterDelay(string eventId, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.Stop(eventId, target: target);
        }
    }
}
