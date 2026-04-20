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
    /// Output parameter to write to.
    /// </summary>
    public enum BindingOutputParameter
    {
        Volume,
        Pitch,
        Pan,
        BridgeGain
    }

    /// <summary>
    /// Maps an external variable (Transform position, velocity, etc.) to an AudioSource
    /// or HapbeatAudioBridge parameter for continuous haptic feedback control.
    ///
    /// Attach multiple instances to control multiple parameters independently.
    /// Works with StreamSource mode — the AudioSource's parameters are captured by
    /// HapbeatAudioBridge's OnAudioFilterRead.
    ///
    /// <para><b>Linked vs standalone:</b></para>
    /// <para>
    /// When <see cref="_linkedEventMap"/> is set, all tuning values (inputMin/Max,
    /// curve, outputMin/Max, debug options) are read live from the matching
    /// <see cref="HapbeatBindingPreset"/> in the EventMap — the local SerializedFields
    /// are ignored at runtime. This lets designers tune haptics in the EventMap
    /// during Play and see changes immediately.
    /// </para>
    /// <para>
    /// When unlinked, the local SerializedFields are used directly (standalone mode).
    /// </para>
    ///
    /// Example: PokeButton push depth → Volume → stronger vibration as button is pushed.
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
        [Tooltip("Which parameter to control.\n" +
                 "Volume: AudioSource.volume (affects haptic intensity)\n" +
                 "Pitch: AudioSource.pitch (affects vibration frequency)\n" +
                 "Pan: AudioSource.panStereo (L/R balance)\n" +
                 "BridgeGain: HapbeatAudioBridge.Gain (post-capture multiplier)")]
        [SerializeField]
        private BindingOutputParameter _outputParameter = BindingOutputParameter.Volume;

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

        // Cached references
        private AudioSource _audioSource;
        private HapbeatAudioBridge _audioBridge;
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

        /// <summary>Returns the linked preset, or null if standalone / id not found.</summary>
        public HapbeatBindingPreset ResolveLinkedPreset()
        {
            if (_linkedEventMap == null || string.IsNullOrEmpty(_linkedBindingId))
                return null;

            // Cached reference still valid?
            if (_cachedPreset != null &&
                ReferenceEquals(_cachedForMap, _linkedEventMap) &&
                _cachedForId == _linkedBindingId)
            {
                return _cachedPreset;
            }

            _cachedPreset = null;
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
                        return p;
                    }
                }
            }
            return null;
        }

        private float _lastDebugLogTime;
        private float _lastLoggedNormalized = float.NaN;
        private bool _warnedNoSource;      // one-shot: source Transform unset
        private bool _warnedNoSink;        // one-shot: no AudioSource/Bridge to write to
        private bool _warnedPresetNotFound;// one-shot: linked preset id not found in map

        private void Start()
        {
            Initialize();
            // One startup summary so the user can confirm the binding wired up correctly
            // without enabling per-frame debugLog.
            var preset = ResolveLinkedPreset();
            string linkInfo = preset != null
                ? $"linked(id={_linkedBindingId.Substring(0, Mathf.Min(8, _linkedBindingId.Length))}…)"
                : (_linkedEventMap != null ? "linked(PRESET NOT FOUND)" : "standalone");
            Debug.Log(
                $"[HapbeatBinding] Ready on {name}: " +
                $"source={(_sourceTransform != null ? _sourceTransform.name : "(null)")} " +
                $"property={EffectiveSourceProperty(preset)} " +
                $"\u2192 {EffectiveOutputParameter(preset)} " +
                $"sink={SinkDescription(preset)} " +
                $"[{linkInfo}]",
                this);
        }

        private string SinkDescription(HapbeatBindingPreset preset)
        {
            var outParam = EffectiveOutputParameter(preset);
            if (outParam == BindingOutputParameter.BridgeGain)
                return _audioBridge != null ? $"Bridge({_audioBridge.name})" : "(no HapbeatAudioBridge)";
            return _audioSource != null ? $"AudioSource({_audioSource.name})" : "(no AudioSource)";
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

            // Resolve preset (null = standalone / preset missing). Preset values override
            // the local SerializedFields.
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

            // Read input
            float raw = ReadSourceValue(srcProp);
            CurrentInput = raw;

            // Normalize to 0-1
            float range = inMax - inMin;
            float normalized = Mathf.Abs(range) > 0.0001f
                ? Mathf.Clamp01((raw - inMin) / range)
                : 0f;
            CurrentNormalized = normalized;

            // Apply curve
            float curved = ApplyCurve(normalized, curveType, customCurve);

            // Map to output range
            float output = Mathf.Lerp(outMin, outMax, curved);
            CurrentOutput = output;

            // Write output (warn once if there is nothing to write to).
            bool wrote = WriteOutput(outParam, output);
            if (!wrote && !_warnedNoSink)
            {
                Debug.LogWarning(
                    $"[HapbeatBinding] No valid sink for {outParam} on {name}. " +
                    (outParam == BindingOutputParameter.BridgeGain
                        ? "Add a HapbeatAudioBridge to this GameObject (or a parent/child)."
                        : "Add an AudioSource to this GameObject (or a parent/child)."),
                    this);
                _warnedNoSink = true;
            }

            // Debug log — only when value changed meaningfully AND throttle elapsed.
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
                    Debug.Log($"[HapbeatBinding] {srcProp} input={raw:F4} " +
                              $"normalized={normalized:F2} \u2192 {outParam} output={output:F3}",
                              this);
                }
            }
        }

        private void Initialize()
        {
            // Prefer AudioSource with HapbeatAudioBridge (the "haptic" AudioSource)
            _audioBridge = GetComponent<HapbeatAudioBridge>()
                ?? GetComponentInChildren<HapbeatAudioBridge>()
                ?? GetComponentInParent<HapbeatAudioBridge>();

            if (_audioBridge != null)
            {
                _audioSource = _audioBridge.GetComponent<AudioSource>();
            }
            else
            {
                // Fallback: any AudioSource on hierarchy
                _audioSource = GetComponent<AudioSource>()
                    ?? GetComponentInParent<AudioSource>()
                    ?? GetComponentInChildren<AudioSource>();
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
            Debug.Log(
                $"[HapbeatBinding] STATE on {name}:\n" +
                $"  mode={linkInfo}\n" +
                $"  source={srcName} property={srcProp}\n" +
                $"  input(raw)={raw:F4}  input range=[{EffectiveInputMin(preset):F4}..{EffectiveInputMax(preset):F4}]\n" +
                $"  current: input={CurrentInput:F4} normalized={CurrentNormalized:F2} output={CurrentOutput:F3}\n" +
                $"  output={EffectiveOutputParameter(preset)} range=[{EffectiveOutputMin(preset):F3}..{EffectiveOutputMax(preset):F3}]\n" +
                $"  sink={SinkDescription(preset)}",
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

        /// <summary>
        /// Writes the computed value to the configured output parameter.
        /// Returns true if the value was written (i.e. a valid sink existed), false
        /// otherwise. Used to trigger a one-shot misconfiguration warning.
        /// </summary>
        private bool WriteOutput(BindingOutputParameter outParam, float value)
        {
            switch (outParam)
            {
                case BindingOutputParameter.Volume:
                    if (_audioSource == null) return false;
                    _audioSource.volume = Mathf.Clamp01(value);
                    return true;
                case BindingOutputParameter.Pitch:
                    if (_audioSource == null) return false;
                    _audioSource.pitch = Mathf.Clamp(value, -3f, 3f);
                    return true;
                case BindingOutputParameter.Pan:
                    if (_audioSource == null) return false;
                    _audioSource.panStereo = Mathf.Clamp(value, -1f, 1f);
                    return true;
                case BindingOutputParameter.BridgeGain:
                    if (_audioBridge == null) return false;
                    _audioBridge.Gain = value;
                    return true;
                default:
                    return false;
            }
        }

        private void OnValidate()
        {
            // Re-initialize caches when Inspector values change.
            _initialized = false;
            _cachedPreset = null;
            _warnedPresetNotFound = false;
        }
    }
}
