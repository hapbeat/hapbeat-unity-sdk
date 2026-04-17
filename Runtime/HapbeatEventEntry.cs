using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Preset configuration for a HapbeatParameterBinding component.
    /// Stored in EventEntry so bindings can be auto-generated on targets via Batch Setup.
    /// </summary>
    [Serializable]
    public class HapbeatBindingPreset
    {
        [Tooltip("Path to source Transform relative to target.\n" +
                 "Empty or '.' = target itself. Otherwise child path (e.g. \"Visual\", \"Body/Head\").")]
        public string sourceTransformPath = "";

        public BindingSourceProperty sourceProperty = BindingSourceProperty.LocalPositionY;
        public float inputMin = 0f;
        public float inputMax = 1f;

        public BindingCurveType curveType = BindingCurveType.Linear;
        public AnimationCurve customCurve = AnimationCurve.Linear(0, 0, 1, 1);

        public BindingOutputParameter outputParameter = BindingOutputParameter.Volume;
        public float outputMin = 0f;
        public float outputMax = 1f;

        public bool debugLog = false;
        public float debugLogInterval = 0.2f;
    }

    /// <summary>
    /// Haptic event mode. Determines how the trigger fires.
    /// </summary>
    public enum HapticMode
    {
        /// <summary>Send eventId command. Device resolves clip locally from installed Pack.</summary>
        Command,
        /// <summary>Stream an AudioClip over UDP as PCM16. No Pack needed on device.</summary>
        StreamClip,
        /// <summary>Capture AudioSource output and stream over UDP. For real-time/spatial audio.</summary>
        StreamSource
    }

    /// <summary>
    /// A single haptic event definition within a HapbeatEventMap.
    /// Supports three modes: Command (eventId), StreamClip (AudioClip), and StreamSource (AudioSource).
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

        // ---- Mode ----

        [Tooltip("How this event triggers haptic feedback.\n" +
                 "Command: send eventId, device plays local clip.\n" +
                 "StreamClip: stream AudioClip over UDP.\n" +
                 "StreamSource: capture AudioSource output and stream.")]
        public HapticMode mode = HapticMode.Command;

        // ---- Event ID (Command mode) ----

        [Tooltip("Human-readable label for this event (e.g. \"Landing Impact\").")]
        public string displayName = "";

        [Tooltip("Event category (e.g. clip, impact, ui). See hapbeat-contracts event-id spec.")]
        public string category = "";

        [Tooltip("Event name within the category (e.g. hit, click, grab).")]
        public string eventName = "";

        // ---- StreamClip / StreamSource mode ----

        [Tooltip("AudioClip.\n" +
                 "StreamClip mode: streamed over UDP as PCM16.\n" +
                 "StreamSource mode: used as the default AudioSource clip when adding AudioSource via Batch Setup.")]
        public AudioClip streamClip;

        // ---- StreamSource mode ----

        [Tooltip("Mute speaker output when streaming. Audio is captured for haptics only.")]
        public bool silentMode = true;

        [Tooltip("Loop the AudioSource playback.")]
        public bool loop = true;

        [Tooltip("Parameter bindings applied on the target GameObject via Batch Setup.\n" +
                 "Each binding creates a HapbeatParameterBinding component.")]
        public List<HapbeatBindingPreset> bindings = new List<HapbeatBindingPreset>();

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
        /// Only meaningful in Command mode.
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

        /// <summary>Whether this entry uses path-based target (vs legacy group).</summary>
        public bool HasTarget => !string.IsNullOrEmpty(target);

        /// <summary>Build a target string from player number and position.</summary>
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

        /// <summary>Short description for display in lists (no mode icon).</summary>
        public string GetSummary()
        {
            switch (mode)
            {
                case HapticMode.StreamClip:
                    return streamClip != null ? streamClip.name : "(no clip)";
                case HapticMode.StreamSource:
                    return "AudioSource";
                default:
                    return eventId;
            }
        }

        /// <summary>Mode icon shown next to display name (e.g. "Click ♪").</summary>
        public string GetModeIcon()
        {
            switch (mode)
            {
                case HapticMode.StreamClip: return "\u266a";
                case HapticMode.StreamSource: return "~";
                default: return "";
            }
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
