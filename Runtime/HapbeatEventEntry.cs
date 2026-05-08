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

        [Tooltip("Optional GameObject name that scopes this binding to a single wired " +
                 "trigger object.\n" +
                 "Empty = shared: applies to every GameObject wired to this entry.\n" +
                 "Set to a wired GameObject's name to limit the binding to that object.\n\n" +
                 "Used by the EventMap UI to group bindings under the wired object that " +
                 "owns them. The runtime filter is by name match, so renaming a wired " +
                 "GameObject also requires updating this field.")]
        public string ownerObjectName = "";

        [Tooltip("Path to source Transform relative to target.\n" +
                 "Empty or '.' = target itself. Otherwise child path (e.g. \"Visual\", \"Body/Head\").")]
        public string sourceTransformPath = "";

        public BindingSourceProperty sourceProperty = BindingSourceProperty.LocalPositionY;
        public float inputMin = 0f;
        public float inputMax = 1f;

        public BindingCurveType curveType = BindingCurveType.Linear;
        public AnimationCurve customCurve = AnimationCurve.Linear(0, 0, 1, 1);

        public BindingOutputParameter outputParameter = BindingOutputParameter.StreamGain;
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
        /// <summary>Send eventId command. Device resolves clip locally from installed Kit.</summary>
        Command,
        /// <summary>Stream an AudioClip over UDP as PCM16. No Kit needed on device.
        /// Dynamic gain / pan modulation via HapbeatParameterBinding is supported
        /// through the returned <see cref="HapbeatStreamPlayback"/> handle.</summary>
        StreamClip,
    }

    /// <summary>
    /// A single haptic event definition within a HapbeatEventMap.
    /// Supports two modes: Command (eventId) and StreamClip (AudioClip + optional
    /// ParameterBinding modulation for dynamic gain / pan).
    /// </summary>
    [Serializable]
    public class HapbeatEventEntry : ISerializationCallbackReceiver
    {
        // ---- Stable identifier ----
        //
        // Triggers reference entries by this id (not by list index), so
        // reordering, inserting, or duplicating entries in the EventMap
        // cannot silently break existing trigger wiring. Lazy-assigned on
        // first access; callers that trigger the lazy-assign should mark
        // the owning EventMap dirty so the new id is persisted.
        [SerializeField, HideInInspector]
        private string _id;

        /// <summary>
        /// Stable GUID identifying this entry across reorders and reloads.
        /// Lazy-assigned on first access. Callers that trigger assignment
        /// (first time any code reads <c>.id</c> on a freshly deserialized
        /// entry) are responsible for marking the EventMap dirty.
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

        /// <summary>Returns true iff the id field has been assigned (i.e. reading it now won't side-effect).</summary>
        public bool HasId => !string.IsNullOrEmpty(_id);

        /// <summary>Force assignment of a fresh id (used when duplicating entries).</summary>
        public void RegenerateId() => _id = Guid.NewGuid().ToString("N");

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
                 "StreamClip: stream AudioClip over UDP. Supports runtime " +
                 "modulation via HapbeatParameterBinding (StreamGain / StreamPan).")]
        public HapticMode mode = HapticMode.Command;

        // ---- Event ID (Command mode) ----

        [Tooltip("Human-readable label for this event (e.g. \"Landing Impact\").")]
        public string displayName = "";

        [Tooltip("Event category (e.g. clip, impact, ui). See hapbeat-contracts event-id spec.")]
        public string category = "";

        [Tooltip("Event name within the category (e.g. hit, click, grab).")]
        public string eventName = "";

        // ---- StreamClip mode ----

        [Tooltip("AudioClip streamed over UDP as PCM16 (StreamClip mode only).")]
        public AudioClip streamClip;

        [Tooltip("Loop playback. Re-streams the clip continuously until Stop() is " +
                 "called (used by HapbeatSequenceTrigger's Loop phase and by " +
                 "continuously-modulated effects like drag / scrape).\n\n" +
                 "Default is off so a typical StreamClip entry fires as a one-shot. " +
                 "Turn on for sustained / hold-style effects.")]
        public bool loop = false;

        [Tooltip("Parameter bindings applied on the target GameObject via Batch Setup.\n" +
                 "Each binding creates a HapbeatParameterBinding component that " +
                 "modulates StreamGain / StreamPan on the active StreamClip playback.")]
        public List<HapbeatBindingPreset> bindings = new List<HapbeatBindingPreset>();

        // ---- Gain ----

        [Tooltip("Gain multiplier. 0.0 to 2.0.")]
        [Range(0f, 2f)]
        public float gain = 1.0f;

        // ---- Targeting ----

        [Tooltip("Target filter for device addressing. Empty = broadcast to all.\n" +
                 "Examples: player_1, */pos_neck, player_1/pos_chest")]
        public string target = "";

        // Legacy: kept on the data model for backward compat with existing
        // serialized assets. Not used on the wire — current contracts spec
        // (device-addressing.md §5) uses the `target` string only.
        [System.Obsolete("Use 'target' string. The legacy group byte was removed from the wire protocol.")]
        [HideInInspector]
        public int group = -1;

        // ---- Notes ----

        [Tooltip("Designer notes (not sent to devices).")]
        [TextArea(1, 3)]
        public string notes = "";

        // ---- Manifest intensity cache ----
        //
        // Studio authors <c>parameters.intensity</c> in each Kit's manifest.json. The
        // SDK needs to honour that value in both Command and StreamClip modes — the
        // device no longer reads manifest.intensity at runtime (device is a pure
        // executor that plays req.gain as-is). The sender (SDK) is responsible for
        // multiplying gain × intensity before putting it on the wire.
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
        /// Editor-only helper for writing the cached intensity (e.g. when
        /// duplicating an entry the caller wants the copy to inherit the
        /// resolved cache). Runtime code should never call this — the cache
        /// is populated by <c>HapbeatMigrateLegacyReferences</c> or the
        /// EventMap window's refresh pass.
        /// </summary>
        public void SetCachedManifestIntensity(float value) => _cachedManifestIntensity = value;

        /// <summary>
        /// Effective gain to send over the wire: <c>gain × cached intensity</c>.
        /// Applies to both Command and StreamClip modes — the device is a pure
        /// executor (req.gain only) and no longer reads manifest.intensity itself.
        /// <para>
        /// If the cache is unresolved (<c>CachedManifestIntensity &lt; 0</c>),
        /// returns plain <c>gain</c>; callers should warn when the sentinel is -1
        /// because it means the Kit has not been deployed yet or the EventMap
        /// window's refresh has not run.
        /// </para>
        /// <para>
        /// A manifest intensity of exactly 0 is honoured (returns 0), because the
        /// designer may intentionally silence an event in the manifest.
        /// </para>
        /// </summary>
        public float GetEffectiveGain()
        {
            // _cachedManifestIntensity < 0 is the "not yet resolved" sentinel.
            // 0 is a valid authored intensity (silence), so we only use plain gain
            // for the strictly-negative sentinel case.
            return _cachedManifestIntensity < 0f
                ? gain
                : gain * _cachedManifestIntensity;
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
        ///   </list>
        /// </summary>
        public string GetModeIcon()
        {
            switch (mode)
            {
                case HapticMode.StreamClip: return "\u266a";  // ♪
                case HapticMode.Command:    return ">";
                default:                    return "\u25cf";  // ● fallback
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
