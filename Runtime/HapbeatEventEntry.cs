using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// A single haptic event definition within a HapbeatEventMap.
    /// Centralizes event ID, gain, and group so all triggers reference one source of truth.
    /// </summary>
    [Serializable]
    public class HapbeatEventEntry : ISerializationCallbackReceiver
    {
        /// <summary>Standard categories defined by hapbeat-contracts.</summary>
        public static readonly string[] StandardCategories =
            { "impact", "vibration", "texture", "ambient", "ui", "custom" };

        [Tooltip("Human-readable label for this event (e.g. \"Landing Impact\").")]
        public string displayName = "";

        [Tooltip("Event category (e.g. impact, ui, vibration). See hapbeat-contracts event-id spec.")]
        public string category = "";

        [Tooltip("Event name within the category (e.g. hit, click, grab).")]
        public string eventName = "";

        [Tooltip("Gain multiplier. 0.0 to 2.0.")]
        [Range(0f, 2f)]
        public float gain = 1.0f;

        [Tooltip("Target group ID. -1 = use config default, 0 = all devices.")]
        public int group = -1;

        [Tooltip("Designer notes (not sent to devices).")]
        [TextArea(1, 3)]
        public string notes = "";

        // Legacy field kept for migration from old serialized data.
        [SerializeField, HideInInspector]
        private string _eventId = "";

        /// <summary>
        /// Computed event ID in category.name format (contracts-compliant).
        /// Read-only. Set category and eventName separately.
        /// </summary>
        public string eventId
        {
            get
            {
                if (string.IsNullOrEmpty(eventName)) return "";
                if (string.IsNullOrEmpty(category)) return eventName;
                return $"{category}.{eventName}";
            }
        }

        /// <summary>
        /// Validate that a segment (category or name) follows the contracts spec.
        /// Lowercase a-z start, then lowercase alphanumeric + hyphen + underscore, max 64 chars.
        /// </summary>
        public static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            return Regex.IsMatch(segment, @"^[a-z][a-z0-9_-]{0,63}$");
        }

        /// <summary>
        /// Validate the full computed eventId against the contracts spec.
        /// </summary>
        public bool IsValid()
        {
            return IsValidSegment(category) && IsValidSegment(eventName);
        }

        // --- ISerializationCallbackReceiver ---

        public void OnBeforeSerialize()
        {
            // Keep _eventId in sync for debugging; not used at runtime.
            _eventId = eventId;
        }

        public void OnAfterDeserialize()
        {
            // Migrate legacy data: if category/eventName are empty but _eventId has data,
            // split the old _eventId into the new fields.
            if (!string.IsNullOrEmpty(_eventId) && string.IsNullOrEmpty(category) && string.IsNullOrEmpty(eventName))
            {
                int dotIndex = _eventId.IndexOf('.');
                if (dotIndex > 0 && dotIndex < _eventId.Length - 1)
                {
                    category = _eventId.Substring(0, dotIndex);
                    eventName = _eventId.Substring(dotIndex + 1);
                }
                else if (_eventId.Length > 0 && !_eventId.Contains("."))
                {
                    // No dot: treat as custom category with this name
                    category = "custom";
                    eventName = _eventId;
                }
            }
        }
    }
}
