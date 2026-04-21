using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Source property to read from a Transform or Rigidbody.
    /// </summary>
    public enum BindingSourceProperty
    {
        LocalPositionX,
        LocalPositionY,
        LocalPositionZ,
        LocalScaleX,
        LocalScaleY,
        LocalScaleZ,
        VelocityMagnitude,
        AngularVelocityMagnitude
    }

    /// <summary>
    /// Mapping curve type for input-to-output conversion.
    /// </summary>
    public enum BindingCurveType
    {
        Linear,
        EaseIn,
        EaseOut,
        Exponential,
        Custom
    }

    /// <summary>
    /// Output parameter to write on the active StreamClip playback.
    /// </summary>
    public enum BindingOutputParameter
    {
        /// <summary>
        /// Overall gain (0..2). Applied to every sample before sending. Use for
        /// intensity modulation (e.g. stronger haptics as an object is pressed
        /// harder or moved faster).
        /// </summary>
        StreamGain,
        /// <summary>
        /// Stereo pan (-1..+1). -1 = full left, 0 = centered, +1 = full right.
        /// Ignored for mono clips. Uses an equal-power pan law so centered pan
        /// preserves perceived loudness.
        /// </summary>
        StreamPan,
    }

    /// <summary>
    /// Maps an external variable (Transform position, velocity, …) onto the
    /// active <see cref="HapbeatStreamPlayback"/> on a sibling
    /// <see cref="HapbeatTriggerBase"/>. Write one parameter per component;
    /// attach multiple instances to control multiple parameters at once.
    ///
    /// <para>
    /// Requires the trigger entry to be in <c>StreamClip</c> mode. While the
    /// stream is active, <see cref="Update"/> reads the source value, applies
    /// the curve + output range, and writes it into
    /// <see cref="HapbeatStreamPlayback.Gain"/> or <see cref="HapbeatStreamPlayback.Pan"/>.
    /// No Unity AudioSource is involved — the modulation happens entirely on
    /// the SDK side just before samples hit the wire.
    /// </para>
    ///
    /// <para><b>Linked vs standalone:</b></para>
    /// <para>
    /// When <see cref="_linkedEventMap"/> is set, all tuning values (inputMin/Max,
    /// curve, outputMin/Max, debug options) are read live from the matching
    /// <see cref="HapbeatBindingPreset"/> in the EventMap — the local
    /// SerializedFields are ignored at runtime. This lets designers tune
    /// haptics in the EventMap during Play and see changes immediately.
    /// </para>
    /// <para>
    /// When unlinked, the local SerializedFields are used directly (standalone mode).
    /// </para>
    ///
    /// Example: held object velocity → StreamGain → stronger scrape haptic
    /// the faster you drag it along a surface.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Parameter Binding")]
    public class HapbeatParameterBinding : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("The Transform to read the input value from.")]
        [SerializeField]
        private Transform _sourceTransform;

        [Tooltip("Which property to read from the source.")]
        [SerializeField]
        private BindingSourceProperty _sourceProperty = BindingSourceProperty.LocalPositionY;

        [Tooltip("Input value at minimum (mapped to outputMin).")]
        [SerializeField]
        private float _inputMin = 0f;

        [Tooltip("Input value at maximum (mapped to outputMax).")]
        [SerializeField]
        private float _inputMax = 1f;

        [Header("Mapping")]
        [Tooltip("Curve type for input-to-output conversion.")]
        [SerializeField]
        private BindingCurveType _curveType = BindingCurveType.Linear;

        [Tooltip("Custom curve (used when Curve Type = Custom). X: 0-1 normalized input, Y: 0-1 output.")]
        [SerializeField]
        private AnimationCurve _customCurve = AnimationCurve.Linear(0, 0, 1, 1);

        [Header("Output")]
        [Tooltip("Which stream parameter to control.\n" +
                 "StreamGain: overall volume multiplier (0..2) on the active " +
                 "StreamClip playback.\n" +
                 "StreamPan: stereo pan (-1..+1). Ignored for mono clips.")]
        [SerializeField]
        private BindingOutputParameter _outputParameter = BindingOutputParameter.StreamGain;

        [Tooltip("Output value when input is at inputMin.")]
        [SerializeField]
        private float _outputMin = 0f;

        [Tooltip("Output value when input is at inputMax.")]
        [SerializeField]
        private float _outputMax = 1f;

        [Header("Event Map Link (optional)")]
        [Tooltip("Link to an EventMap preset. When set, ALL tuning values are read " +
                 "live from the preset every frame — the local fields above are ignored " +
                 "at runtime. Leave empty to use the local fields (standalone mode).")]
        [SerializeField]
        private HapbeatEventMap _linkedEventMap;

        [Tooltip("Stable id of the HapbeatBindingPreset to read from (inside Linked Event Map).")]
        [SerializeField]
        private string _linkedBindingId;

        [Header("Debug")]
        [Tooltip("Log input/output values to the Unity console. When enabled, a line " +
                 "is emitted each time the normalized value changes by at least " +
                 "'Change Threshold', throttled by 'Log Interval' (seconds).")]
        [SerializeField]
        private bool _debugLog = false;

        [Tooltip("Minimum seconds between debug log lines (throttle). 0.1\u20130.2 recommended.")]
        [SerializeField, Range(0.01f, 2f)]
        private float _debugLogInterval = 0.1f;

        [Tooltip("Minimum normalized-value change (0-1 scale) required before a log line is emitted.")]
        [SerializeField, Range(0f, 1f)]
        private float _debugLogChangeThreshold = 0.02f;

        // Cached references.
        //
        // _trigger is the nearest single trigger — used for standalone bindings
        // (no EventMap link) where there's nothing to scope against.
        //
        // _candidateTriggers holds ALL HapbeatTriggerBase instances found on
        // this GameObject plus its parents & children at init time. For a
        // linked binding we walk this list each frame and pick the one whose
        // EntryId matches the binding's owner entry — so a binding attached
        // to an event only modulates that event's stream, regardless of
        // which trigger component happens to live on the GameObject.
        private HapbeatTriggerBase _trigger;
        private readonly System.Collections.Generic.List<HapbeatTriggerBase> _candidateTriggers
            = new System.Collections.Generic.List<HapbeatTriggerBase>(4);
        private Rigidbody _sourceRigidbody;
        private bool _initialized;

        // Preset resolution cache — invalidated when _linkedEventMap or _linkedBindingId change.
        private HapbeatBindingPreset _cachedPreset;
        private HapbeatEventMap _cachedForMap;
        private string _cachedForId;

        /// <summary>Current raw input value (before mapping).</summary>
        public float CurrentInput { get; private set; }

        /// <summary>Current normalized value (0-1 after input range mapping).</summary>
        public float CurrentNormalized { get; private set; }

        /// <summary>Current output value (after mapping, written to parameter).</summary>
        public float CurrentOutput { get; private set; }

        /// <summary>Returns the linked EventMap, or null if this binding is standalone.</summary>
        public HapbeatEventMap LinkedEventMap => _linkedEventMap;

        /// <summary>Returns the id of the linked preset, or empty if standalone.</summary>
        public string LinkedBindingId => _linkedBindingId;

        // Id of the entry that owns the linked preset. Cached alongside the
        // preset itself. Used as the scope filter at write time — we only
        // modulate a stream whose firing trigger points at this entry.
        private string _cachedOwnerEntryId;

        /// <summary>Returns the linked preset, or null if standalone / id not found.</summary>
        public HapbeatBindingPreset ResolveLinkedPreset()
        {
            if (_linkedEventMap == null || string.IsNullOrEmpty(_linkedBindingId))
                return null;

            if (_cachedPreset != null &&
                ReferenceEquals(_cachedForMap, _linkedEventMap) &&
                _cachedForId == _linkedBindingId)
            {
                return _cachedPreset;
            }

            _cachedPreset = null;
            _cachedOwnerEntryId = null;
            _cachedForMap = _linkedEventMap;
            _cachedForId = _linkedBindingId;

            foreach (var entry in _linkedEventMap.entries)
            {
                if (entry == null || entry.bindings == null) continue;
                for (int i = 0; i < entry.bindings.Count; i++)
                {
                    var p = entry.bindings[i];
                    if (p != null && p.id == _linkedBindingId)
                    {
                        _cachedPreset = p;
                        _cachedOwnerEntryId = entry.id;
                        return p;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Id of the EventMap entry that contains the linked preset, or
        /// <c>null</c> when the binding is standalone or the preset is missing.
        /// The runtime write path only modulates a stream whose firing trigger
        /// is currently pointing at this entry — so a binding authored on a
        /// PokeButton's "push feedback" event doesn't accidentally modulate a
        /// sibling "grab" trigger's stream.
        /// </summary>
        public string LinkedOwnerEntryId
        {
            get
            {
                if (_cachedOwnerEntryId == null) ResolveLinkedPreset();
                return _cachedOwnerEntryId;
            }
        }

        private float _lastDebugLogTime;
        private float _lastLoggedNormalized = float.NaN;
        private bool _warnedNoSource;       // one-shot: source Transform unset
        private bool _warnedNoTrigger;      // one-shot: no sibling HapbeatTriggerBase
        private bool _warnedPresetNotFound; // one-shot: linked preset id not found in map

        private void Start()
        {
            Initialize();
            var preset = ResolveLinkedPreset();
            string linkInfo = preset != null
                ? $"linked(id={_linkedBindingId.Substring(0, Mathf.Min(8, _linkedBindingId.Length))}\u2026)"
                : (_linkedEventMap != null ? "linked(PRESET NOT FOUND)" : "standalone");
            Debug.Log(
                $"[HapbeatBinding] Ready on {name}: " +
                $"source={(_sourceTransform != null ? _sourceTransform.name : "(null)")} " +
                $"property={EffectiveSourceProperty(preset)} " +
                $"\u2192 {EffectiveOutputParameter(preset)} " +
                $"trigger={(_trigger != null ? _trigger.GetType().Name : "(none)")} " +
                $"[{linkInfo}]",
                this);

            // Authoring safety net: the runtime write path only modulates a
            // stream whose firing trigger matches LinkedOwnerEntryId, so a
            // binding linked to entry A on a GameObject whose trigger fires
            // entry B is effectively dead weight. Surface this at Start so
            // the user notices immediately instead of discovering it via
            // "why is nothing modulating" later.
            string ownerId = LinkedOwnerEntryId;
            if (!string.IsNullOrEmpty(ownerId))
            {
                bool anyMatch = false;
                foreach (var t in _candidateTriggers)
                {
                    if (t == null) continue;
                    if (t.EntryId == ownerId) { anyMatch = true; break; }
                }
                if (!anyMatch)
                {
                    Debug.LogWarning(
                        $"[HapbeatBinding] {name} is linked to entry id '{ownerId.Substring(0, Mathf.Min(8, ownerId.Length))}\u2026' " +
                        $"but no HapbeatTriggerBase on this GameObject (or its parents/children) " +
                        $"fires that entry. The binding will have no effect.\n" +
                        $"Fix: change the trigger's Event to the entry this binding belongs to, " +
                        $"or re-run 'Hapbeat/Batch Setup' to align them.",
                        this);
                }
            }
        }

        // --- Effective-value helpers: preset (if linked & resolved) wins over local. ---

        private BindingSourceProperty EffectiveSourceProperty(HapbeatBindingPreset p) =>
            p != null ? p.sourceProperty : _sourceProperty;
        private float EffectiveInputMin(HapbeatBindingPreset p) => p != null ? p.inputMin : _inputMin;
        private float EffectiveInputMax(HapbeatBindingPreset p) => p != null ? p.inputMax : _inputMax;
        private BindingCurveType EffectiveCurveType(HapbeatBindingPreset p) =>
            p != null ? p.curveType : _curveType;
        private AnimationCurve EffectiveCustomCurve(HapbeatBindingPreset p) =>
            p != null ? p.customCurve : _customCurve;
        private BindingOutputParameter EffectiveOutputParameter(HapbeatBindingPreset p) =>
            p != null ? p.outputParameter : _outputParameter;
        private float EffectiveOutputMin(HapbeatBindingPreset p) => p != null ? p.outputMin : _outputMin;
        private float EffectiveOutputMax(HapbeatBindingPreset p) => p != null ? p.outputMax : _outputMax;
        private bool EffectiveDebugLog(HapbeatBindingPreset p) => p != null ? p.debugLog : _debugLog;
        private float EffectiveDebugLogInterval(HapbeatBindingPreset p) =>
            p != null ? p.debugLogInterval : _debugLogInterval;
        private float EffectiveDebugLogChangeThreshold(HapbeatBindingPreset p) =>
            p != null ? p.debugLogChangeThreshold : _debugLogChangeThreshold;

        private void Update()
        {
            if (!_initialized) Initialize();

            if (_sourceTransform == null)
            {
                if (!_warnedNoSource)
                {
                    Debug.LogWarning(
                        $"[HapbeatBinding] Source Transform is NULL on {name}. " +
                        "Assign it in the Inspector, or use 'Hapbeat/Batch Setup' so the " +
                        "event entry's Source Path resolves to a child Transform.", this);
                    _warnedNoSource = true;
                }
                return;
            }

            var preset = ResolveLinkedPreset();
            if (preset == null && _linkedEventMap != null && !_warnedPresetNotFound)
            {
                Debug.LogWarning(
                    $"[HapbeatBinding] Linked preset id '{_linkedBindingId}' not found in " +
                    $"EventMap '{_linkedEventMap.name}' on {name}. Falling back to local values. " +
                    "Re-run 'Hapbeat/Batch Setup' to refresh the link.", this);
                _warnedPresetNotFound = true;
            }

            var srcProp = EffectiveSourceProperty(preset);
            float inMin = EffectiveInputMin(preset);
            float inMax = EffectiveInputMax(preset);
            var curveType = EffectiveCurveType(preset);
            var customCurve = EffectiveCustomCurve(preset);
            var outParam = EffectiveOutputParameter(preset);
            float outMin = EffectiveOutputMin(preset);
            float outMax = EffectiveOutputMax(preset);

            float raw = ReadSourceValue(srcProp);
            CurrentInput = raw;

            float range = inMax - inMin;
            float normalized = Mathf.Abs(range) > 0.0001f
                ? Mathf.Clamp01((raw - inMin) / range)
                : 0f;
            CurrentNormalized = normalized;

            float curved = ApplyCurve(normalized, curveType, customCurve);
            float output = Mathf.Lerp(outMin, outMax, curved);
            CurrentOutput = output;

            // Find the trigger that's currently firing THIS binding's linked
            // entry — not just any trigger on the GameObject. Without this
            // scope check, a PokeButton's binding for "push feedback" would
            // also modulate a sibling "grab" trigger's stream whenever that
            // fired, because both share the same ActivePlayback slot on the
            // GameObject. The linked-entry id filter makes the binding
            // inert toward unrelated streams.
            string ownerId = LinkedOwnerEntryId;
            HapbeatStreamPlayback playback = null;
            if (!string.IsNullOrEmpty(ownerId))
            {
                // Scoped: only write when a trigger is actively streaming the
                // exact entry this binding belongs to.
                foreach (var t in _candidateTriggers)
                {
                    if (t == null) continue;
                    if (t.EntryId != ownerId) continue;
                    var pb = t.ActivePlayback;
                    if (pb != null && !pb.IsStopped) { playback = pb; break; }
                }
            }
            else if (_trigger != null)
            {
                // Unscoped (standalone binding): fall back to the nearest
                // trigger's active playback. Matches pre-link behaviour.
                var pb = _trigger.ActivePlayback;
                if (pb != null && !pb.IsStopped) playback = pb;
            }

            if (playback != null)
            {
                switch (outParam)
                {
                    case BindingOutputParameter.StreamGain:
                        // Multiplicative on the entry's baseline (entry.gain ×
                        // manifest.intensity) so author intent is preserved:
                        //   final = entry.gain × intensity × bindingOutput
                        // Output range typically [0,1] where 1 = authored
                        // strength, 0 = silent. Can exceed 1 to boost past
                        // authored; Gain setter clamps to [0, 2].
                        playback.Gain = playback.BaselineGain * output;
                        break;
                    case BindingOutputParameter.StreamPan:
                        // Pan is a position (-1..+1), not a multiplier — assign directly.
                        playback.Pan = output;
                        break;
                }
            }
            else if (_trigger == null && _candidateTriggers.Count == 0 && !_warnedNoTrigger)
            {
                Debug.LogWarning(
                    $"[HapbeatBinding] No HapbeatTriggerBase found on {name} (or parent/child). " +
                    "Add a HapbeatUnityEventTrigger / HapbeatSequenceTrigger / ... to this " +
                    "GameObject so the binding has something to modulate.", this);
                _warnedNoTrigger = true;
            }

            if (EffectiveDebugLog(preset))
            {
                float threshold = EffectiveDebugLogChangeThreshold(preset);
                float interval = EffectiveDebugLogInterval(preset);
                bool valueChanged = float.IsNaN(_lastLoggedNormalized)
                    || Mathf.Abs(normalized - _lastLoggedNormalized) >= threshold;
                bool intervalElapsed = Time.unscaledTime - _lastDebugLogTime >= interval;
                if (valueChanged && intervalElapsed)
                {
                    _lastDebugLogTime = Time.unscaledTime;
                    _lastLoggedNormalized = normalized;
                    string streaming = playback != null && !playback.IsStopped ? "active" : "idle";
                    Debug.Log($"[HapbeatBinding] {srcProp} input={raw:F4} " +
                              $"normalized={normalized:F2} \u2192 {outParam} output={output:F3} " +
                              $"(stream {streaming})",
                              this);
                }
            }
        }

        private void Initialize()
        {
            _trigger = GetComponent<HapbeatTriggerBase>()
                ?? GetComponentInChildren<HapbeatTriggerBase>()
                ?? GetComponentInParent<HapbeatTriggerBase>();

            _candidateTriggers.Clear();
            // Collect every HapbeatTriggerBase visible to this binding so the
            // scoped write path (linked-entry filter) can pick the right one
            // at runtime. Self + children + parents covers nested setups like
            // "PokeButton with push trigger on root, grab trigger on parent".
            var self = GetComponents<HapbeatTriggerBase>();
            if (self != null) _candidateTriggers.AddRange(self);
            var children = GetComponentsInChildren<HapbeatTriggerBase>(includeInactive: true);
            if (children != null)
            {
                foreach (var t in children)
                    if (t != null && !_candidateTriggers.Contains(t)) _candidateTriggers.Add(t);
            }
            var parents = GetComponentsInParent<HapbeatTriggerBase>(includeInactive: true);
            if (parents != null)
            {
                foreach (var t in parents)
                    if (t != null && !_candidateTriggers.Contains(t)) _candidateTriggers.Add(t);
            }

            if (_sourceTransform != null)
                _sourceRigidbody = _sourceTransform.GetComponent<Rigidbody>();

            _initialized = true;
        }

        /// <summary>
        /// Dump the current state of the binding to the console. Useful for spot-checking
        /// without enabling per-frame debug logs. Invokable from the component's gear menu.
        /// </summary>
        [ContextMenu("Dump Current State")]
        public void DumpCurrentState()
        {
            if (!_initialized) Initialize();
            var preset = ResolveLinkedPreset();
            string srcName = _sourceTransform != null ? _sourceTransform.name : "(null)";
            var srcProp = EffectiveSourceProperty(preset);
            float raw = _sourceTransform != null ? ReadSourceValue(srcProp) : 0f;
            string linkInfo = preset != null
                ? $"linked(id={_linkedBindingId})"
                : (_linkedEventMap != null ? "linked(PRESET NOT FOUND)" : "standalone");
            var playback = _trigger != null ? _trigger.ActivePlayback : null;
            string playbackInfo = playback == null
                ? "(no active stream)"
                : $"gain={playback.Gain:F2} pan={playback.Pan:F2} stopped={playback.IsStopped}";
            Debug.Log(
                $"[HapbeatBinding] STATE on {name}:\n" +
                $"  mode={linkInfo}\n" +
                $"  source={srcName} property={srcProp}\n" +
                $"  input(raw)={raw:F4}  input range=[{EffectiveInputMin(preset):F4}..{EffectiveInputMax(preset):F4}]\n" +
                $"  current: input={CurrentInput:F4} normalized={CurrentNormalized:F2} output={CurrentOutput:F3}\n" +
                $"  output={EffectiveOutputParameter(preset)} range=[{EffectiveOutputMin(preset):F3}..{EffectiveOutputMax(preset):F3}]\n" +
                $"  trigger={(_trigger != null ? _trigger.GetType().Name : "(none)")} playback={playbackInfo}",
                this);
        }

        private float ReadSourceValue(BindingSourceProperty srcProp)
        {
            switch (srcProp)
            {
                case BindingSourceProperty.LocalPositionX: return _sourceTransform.localPosition.x;
                case BindingSourceProperty.LocalPositionY: return _sourceTransform.localPosition.y;
                case BindingSourceProperty.LocalPositionZ: return _sourceTransform.localPosition.z;
                case BindingSourceProperty.LocalScaleX: return _sourceTransform.localScale.x;
                case BindingSourceProperty.LocalScaleY: return _sourceTransform.localScale.y;
                case BindingSourceProperty.LocalScaleZ: return _sourceTransform.localScale.z;
                case BindingSourceProperty.VelocityMagnitude:
#if UNITY_6000_0_OR_NEWER
                    return _sourceRigidbody != null ? _sourceRigidbody.linearVelocity.magnitude : 0f;
#else
                    return _sourceRigidbody != null ? _sourceRigidbody.velocity.magnitude : 0f;
#endif
                case BindingSourceProperty.AngularVelocityMagnitude:
                    return _sourceRigidbody != null ? _sourceRigidbody.angularVelocity.magnitude : 0f;
                default: return 0f;
            }
        }

        private static float ApplyCurve(float t, BindingCurveType curveType, AnimationCurve customCurve)
        {
            switch (curveType)
            {
                case BindingCurveType.Linear:
                    return t;
                case BindingCurveType.EaseIn:
                    return t * t;
                case BindingCurveType.EaseOut:
                    return 1f - (1f - t) * (1f - t);
                case BindingCurveType.Exponential:
                    return (Mathf.Exp(3f * t) - 1f) / (Mathf.Exp(3f) - 1f);
                case BindingCurveType.Custom:
                    return customCurve != null ? customCurve.Evaluate(t) : t;
                default:
                    return t;
            }
        }

        private void OnValidate()
        {
            _initialized = false;
            _cachedPreset = null;
            _warnedPresetNotFound = false;
        }
    }
}
