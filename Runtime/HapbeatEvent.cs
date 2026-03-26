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

        [Tooltip("Target group ID. -1 = use config default, 0 = all devices.")]
        [SerializeField]
        private int _group = -1;

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

        /// <summary>Target group ID. -1 = use config default.</summary>
        public int Group
        {
            get => _group;
            set => _group = value;
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

            HapbeatManager.Instance.Play(_eventId, _gain, _group);
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

            HapbeatManager.Instance.Stop(_eventId, _group);
        }
    }
}
