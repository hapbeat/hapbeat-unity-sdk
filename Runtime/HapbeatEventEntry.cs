using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Preset configuration for a HapbeatParameterBinding component.
    /// Stored in EventEntry so bindings can be auto-generated on targets via Batch Setup.
    ///
    /// Each preset has a stable GUID (<see cref="id"/>) so runtime HapbeatParameterBinding
    /// components can link to the preset by id (not by index) and read values live.
    /// </summary>
    [Serializable]
    public class HapbeatBindingPreset
    {
        [SerializeField, HideInInspector]
        private string _id;

        /// <summary>
        /// Stable identifier for this preset. Used by <see cref="HapbeatParameterBinding"/>
        /// to locate the preset in the EventMap regardless of list position. Generated
        /// lazily on first access if empty.
        /// </summary>
        public string id
        {
            get
            {
                if (string.IsNullOrEmpty(_id))
                    _id = Guid.NewGuid().ToString("N");
                return _id;
            }
        }

        /// <summary>Force assignment of a fresh id (used when duplicating presets).</summary>
        public void RegenerateId() => _id = Guid.NewGuid().ToString("N");

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
        public float debugLogInterval = 0.1f;

        [Tooltip("Minimum normalized-value change (0-1 scale) required to emit a debug " +
                 "log line. 0 = log every interval regardless of change (may spam).")]
        public float debugLogChangeThreshold = 0.02f;
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

        [Tooltip("Loop playback.\n" +
                 "StreamClip: re-stream the clip continuously until Stop() is called " +
                 "(used by HapbeatSequenceTrigger's Loop phase).\n" +
                 "StreamSource: set AudioSource.loop on the captured source.")]
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

        // ---- Manifest intensity cache ----
        //
        // Studio authors <c>parameters.intensity</c> in each Kit's manifest.json. The
        // SDK needs to honour that value in stream modes (where the device just
        // replays the raw PCM it received) — otherwise every stream entry effectively
        // runs at full authored amplitude regardless of what the designer set.
        //
        // We cache it onto the entry (populated by the EventMap window in the editor)
        // so the runtime player — which doesn't ship the manifest.json — still has
        // access to the authored value. -1 means "not yet resolved" or "no matching
        // manifest entry found" (in which case the SDK falls back to 1.0).
        [SerializeField, HideInInspector]
        private float _cachedManifestIntensity = -1f;

        /// <summary>
        /// Authored intensity (0..1) cached from the Kit manifest. Returns -1 if
        /// unresolved / not yet looked up. Editor refreshes this when the EventMap
        /// is opened or when the entry's clip / eventId changes.
        /// </summary>
        public float CachedManifestIntensity => _cachedManifestIntensity;

        /// <summary>
        /// Set the cached intensity. Editor-only flow writes this via SerializedProperty
        /// so the field remains [HideInInspector]. This setter is here for edit-only
        /// helpers that work directly on the entry instance.
        /// </summary>
        internal void SetCachedManifestIntensity(float value) => _cachedManifestIntensity = value;

        /// <summary>
        /// Effective gain to send over the wire: <c>gain × cached intensity</c> for
        /// stream modes, or plain <c>gain</c> for Command (the device applies its
        /// own flashed intensity). If the cache is unresolved (-1), stream modes
        /// return plain <c>gain</c> and the caller should consider logging a warning.
        /// </summary>
        public float GetEffectiveGain()
        {
            switch (mode)
            {
                case HapticMode.StreamClip:
                case HapticMode.StreamSource:
                    return _cachedManifestIntensity > 0f
                        ? gain * _cachedManifestIntensity
                        : gain;
                case HapticMode.Command:
                default:
                    return gain;
            }
        }

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

        /// <summary>
        /// Single-character icon identifying the entry's mode. Always returns a glyph
        /// (even for Command mode) so the list UI can show a consistent prefix.
        ///   <list type="bullet">
        ///     <item>&gt; — Command (fire a device-side clip by eventId)</item>
        ///     <item>♪ — StreamClip (stream a Unity AudioClip over UDP)</item>
        ///     <item>~ — StreamSource (capture a live AudioSource)</item>
        ///   </list>
        /// </summary>
        public string GetModeIcon()
        {
            switch (mode)
            {
                case HapticMode.StreamClip:   return "\u266a";  // ♪
                case HapticMode.StreamSource: return "~";
                case HapticMode.Command:      return ">";
                default:                      return "\u25cf";  // ● fallback
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
