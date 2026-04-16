using System;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// A single haptic event definition within a HapbeatEventMap.
    /// Centralizes event ID, gain, target, and group so all triggers reference one source of truth.
    /// </summary>
    [Serializable]
    public class HapbeatEventEntry : ISerializationCallbackReceiver
    {
        /// <summary>Standard categories defined by hapbeat-contracts.</summary>
        public static readonly string[] StandardCategories =
            { "clip", "impact", "vibration", "texture", "ambient", "ui", "custom" };

        /// <summary>Standard body positions defined by hapbeat-contracts device-addressing spec.</summary>
        public static readonly string[] StandardPositions =
        {
            "pos_neck", "pos_chest", "pos_abd",
            "pos_l_arm", "pos_r_arm", "pos_l_wrist", "pos_r_wrist",
            "pos_hip", "pos_l_thigh", "pos_r_thigh", "pos_l_ankle", "pos_r_ankle"
        };

        /// <summary>Human-readable labels for StandardPositions (same order).</summary>
        public static readonly string[] PositionLabels =
        {
            "Neck", "Chest", "Abdomen",
            "Left Arm", "Right Arm", "Left Wrist", "Right Wrist",
            "Hip", "Left Thigh", "Right Thigh", "Left Ankle", "Right Ankle"
        };

        // ---- Event ID ----

        [Tooltip("Human-readable label for this event (e.g. \"Landing Impact\").")]
        public string displayName = "";

        [Tooltip("Event category (e.g. clip, impact, ui). See hapbeat-contracts event-id spec.")]
        public string category = "";

        [Tooltip("Event name within the category (e.g. hit, click, grab).")]
        public string eventName = "";

        // ---- Gain ----

        [Tooltip("Gain multiplier. 0.0 to 2.0.")]
        [Range(0f, 2f)]
        public float gain = 1.0f;

        // ---- Targeting ----

        [Tooltip("Target filter for device addressing. Empty = broadcast to all.\n" +
                 "Examples: player_1, */pos_neck, player_1/pos_chest")]
        public string target = "";

        [Tooltip("(Legacy) Target group ID. -1 = use config default, 0 = all devices.\n" +
                 "Ignored when target is set.")]
        public int group = -1;

        // ---- Notes ----

        [Tooltip("Designer notes (not sent to devices).")]
        [TextArea(1, 3)]
        public string notes = "";

        // Legacy field kept for migration from old serialized data.
        [SerializeField, HideInInspector]
        private string _eventId = "";

        /// <summary>
        /// Computed event ID in category.name format (contracts-compliant).
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
        /// Whether this entry uses the new path-based target (vs legacy group).
        /// </summary>
        public bool HasTarget => !string.IsNullOrEmpty(target);

        /// <summary>
        /// Build a target string from player number and position.
        /// </summary>
        public static string BuildTarget(int player = -1, string position = null)
        {
            string playerPart = player > 0 ? $"player_{player}" : null;
            string posPart = !string.IsNullOrEmpty(position) ? position : null;

            if (playerPart != null && posPart != null)
                return $"{playerPart}/{posPart}";
            if (playerPart != null)
                return playerPart;
            if (posPart != null)
                return $"*/{posPart}";
            return "";
        }

        public static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;
            return Regex.IsMatch(segment, @"^[a-z][a-z0-9_-]{0,63}$");
        }

        public bool IsValid()
        {
            return IsValidSegment(category) && IsValidSegment(eventName);
        }

        // --- ISerializationCallbackReceiver ---

        public void OnBeforeSerialize()
        {
            _eventId = eventId;
        }

        public void OnAfterDeserialize()
        {
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
                    category = "custom";
                    eventName = _eventId;
                }
            }
        }
    }
}
