using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat
{
    /// <summary>
    /// Input source kind. Unified handling for Transform / Rigidbody / UI Slider
    /// / external script float. Picking one in the Inspector shows the matching source field.
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
        AngularVelocityMagnitude,
        /// <summary>
        /// World-space speed of the source Transform, computed from position
        /// delta per frame (<c>(pos - prevPos).magnitude / Time.deltaTime</c>).
        /// Use this instead of <see cref="VelocityMagnitude"/> when the source
        /// Rigidbody is kinematic or its MovementType is Instantaneous / Kinematic
        /// (e.g. XRI grabbed objects) — in those modes the physics engine does
        /// not compute <c>Rigidbody.linearVelocity</c>, so the velocity-based
        /// sources stay at 0 even while the object is visibly moving.
        /// </summary>
        PositionDeltaMagnitude,
        /// <summary>
        /// Reads UI Slider.value. Assign a Slider component to the
        /// <c>_sourceSlider</c> field in the Inspector. Returns 0 when null.
        /// </summary>
        SliderValue,
        /// <summary>
        /// Reads a value pushed from any script via
        /// <see cref="HapbeatParameterBinding.SetValue"/>. The typical setup is
        /// to wire a UnityEvent&lt;float&gt; to the SetValue method in the Inspector.
        /// e.g. call binding.SetValue(x) every Update from a custom MonoBehaviour.
        /// </summary>
        External,
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
        [Tooltip("Target for Transform/Rigidbody source properties (LocalPosition*, Velocity*, etc.).")]
        [SerializeField]
        private Transform _sourceTransform;

        [Tooltip("UI Slider to read when SourceProperty = SliderValue.")]
        [SerializeField]
        private Slider _sourceSlider;

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

        // World-position cache for PositionDeltaMagnitude. Reset on Initialize
        // so that re-enabling the component doesn't emit a spurious spike from
        // a stale position captured before the source moved elsewhere.
        private Vector3 _prevSourcePosition;
        private bool _hasPrevSourcePosition;

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
                        $"or re-run 'Hapbeat/Open Batch Setup' to align them.",
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

            // Check that the source reference required by srcProp is set (warn and skip if not).
            var presetForSrc = ResolveLinkedPreset();
            var srcPropEarly = EffectiveSourceProperty(presetForSrc);
            if (!IsSourceReady(srcPropEarly))
            {
                if (!_warnedNoSource)
                {
                    Debug.LogWarning(
                        $"[HapbeatBinding] Source ({srcPropEarly}) is not set on {name}. " +
                        "Assign the matching source field (Transform / Slider) in the Inspector.", this);
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
                    "Re-run 'Hapbeat/Open Batch Setup' to refresh the link.", this);
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
                        // authored; ApplyGainModulation clamps Gain to [0, 2].
                        //
                        // Calls the same entry point as the imperative path
                        // (HapbeatTriggerBase.GainMultiplier setter) so the
                        // computation lives in one place.
                        playback.ApplyGainModulation(output);
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

            // Reset PositionDeltaMagnitude cache so the first sample after
            // (re-)initialize returns 0 rather than a spike from a stale prev.
            _hasPrevSourcePosition = false;

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

        /// <summary>
        /// Compute the current output value WITHOUT writing to any playback.
        /// Used by <see cref="HapbeatTriggerBase"/> at stream start to pre-seed
        /// the initial gain so the first audio chunks go out already modulated
        /// (otherwise ~100 ms of full-baseline audio plays before Update()
        /// writes the first binding value → audible "burst" at stream start).
        /// <para>
        /// Returns 0..∞ (typically 0..1). For non-StreamGain outputs the value
        /// is still the computed modulation factor — caller decides how to apply.
        /// </para>
        /// </summary>
        public float EvaluateNow()
        {
            if (!_initialized) Initialize();
            var presetForCheck = ResolveLinkedPreset();
            var srcPropCheck = EffectiveSourceProperty(presetForCheck);
            if (!IsSourceReady(srcPropCheck)) return 0f;
            var preset = ResolveLinkedPreset();
            var srcProp = EffectiveSourceProperty(preset);
            float inMin = EffectiveInputMin(preset);
            float inMax = EffectiveInputMax(preset);
            var curveType = EffectiveCurveType(preset);
            var customCurve = EffectiveCustomCurve(preset);
            float outMin = EffectiveOutputMin(preset);
            float outMax = EffectiveOutputMax(preset);

            float raw = ReadSourceValue(srcProp);
            float range = inMax - inMin;
            float normalized = Mathf.Abs(range) > 0.0001f
                ? Mathf.Clamp01((raw - inMin) / range)
                : 0f;
            float curved = ApplyCurve(normalized, curveType, customCurve);
            float output = Mathf.Lerp(outMin, outMax, curved);
            // Edit-mode の Inspector live preview や FireHaptic の initial-seed で
            // 値を表示できるよう、副作用で Current* も更新する。
            // Play 中は次フレームの Update() で同じ値を再計算するので冗長だが無害。
            CurrentInput = raw;
            CurrentNormalized = normalized;
            CurrentOutput = output;
            return output;
        }

        /// <summary>
        /// Whether this binding's effective output parameter is
        /// <see cref="BindingOutputParameter.StreamGain"/> — relevant to the
        /// pre-stream-start initial-gain pre-seed path.
        /// </summary>
        public bool IsStreamGainOutput
        {
            get
            {
                if (!_initialized) Initialize();
                return EffectiveOutputParameter(ResolveLinkedPreset())
                    == BindingOutputParameter.StreamGain;
            }
        }

        /// <summary>
        /// Whether this binding's effective output parameter is
        /// <see cref="BindingOutputParameter.StreamPan"/> — relevant to the
        /// pre-stream-start initial-pan pre-seed path (gain と対称)。
        /// </summary>
        public bool IsStreamPanOutput
        {
            get
            {
                if (!_initialized) Initialize();
                return EffectiveOutputParameter(ResolveLinkedPreset())
                    == BindingOutputParameter.StreamPan;
            }
        }

        private float ReadSourceValue(BindingSourceProperty srcProp)
        {
            switch (srcProp)
            {
                case BindingSourceProperty.LocalPositionX:
                    return _sourceTransform != null ? _sourceTransform.localPosition.x : 0f;
                case BindingSourceProperty.LocalPositionY:
                    return _sourceTransform != null ? _sourceTransform.localPosition.y : 0f;
                case BindingSourceProperty.LocalPositionZ:
                    return _sourceTransform != null ? _sourceTransform.localPosition.z : 0f;
                case BindingSourceProperty.LocalScaleX:
                    return _sourceTransform != null ? _sourceTransform.localScale.x : 0f;
                case BindingSourceProperty.LocalScaleY:
                    return _sourceTransform != null ? _sourceTransform.localScale.y : 0f;
                case BindingSourceProperty.LocalScaleZ:
                    return _sourceTransform != null ? _sourceTransform.localScale.z : 0f;
                case BindingSourceProperty.VelocityMagnitude:
#if UNITY_6000_0_OR_NEWER
                    return _sourceRigidbody != null ? _sourceRigidbody.linearVelocity.magnitude : 0f;
#else
                    return _sourceRigidbody != null ? _sourceRigidbody.velocity.magnitude : 0f;
#endif
                case BindingSourceProperty.AngularVelocityMagnitude:
                    return _sourceRigidbody != null ? _sourceRigidbody.angularVelocity.magnitude : 0f;
                case BindingSourceProperty.PositionDeltaMagnitude:
                {
                    if (_sourceTransform == null) return 0f;
                    // World-space speed estimated from frame-to-frame position
                    // delta. Bypasses Rigidbody.velocity so it works with
                    // Instantaneous / Kinematic XRGrabInteractable objects.
                    Vector3 current = _sourceTransform.position;
                    float dt = Time.deltaTime;
                    if (!_hasPrevSourcePosition || dt <= 0f)
                    {
                        _prevSourcePosition = current;
                        _hasPrevSourcePosition = true;
                        return 0f;
                    }
                    float speed = (current - _prevSourcePosition).magnitude / dt;
                    _prevSourcePosition = current;
                    return speed;
                }
                case BindingSourceProperty.SliderValue:
                    return _sourceSlider != null ? _sourceSlider.value : 0f;
                case BindingSourceProperty.External:
                    return _externalValue;
                default: return 0f;
            }
        }

        /// <summary>
        /// Whether the source reference required by <paramref name="srcProp"/> is set.
        /// External always returns true (no source reference needed for pushed values).
        /// </summary>
        private bool IsSourceReady(BindingSourceProperty srcProp)
        {
            switch (srcProp)
            {
                case BindingSourceProperty.SliderValue:
                    return _sourceSlider != null;
                case BindingSourceProperty.External:
                    return true;
                default:
                    return _sourceTransform != null;
            }
        }

        // -------- External source push support --------

        // 外部 script から SetValue で push された値。SourceProperty=External 時に読まれる。
        private float _externalValue;

        /// <summary>
        /// Push a float value into this binding from an external source
        /// (script / UnityEvent). Only used at runtime when SourceProperty is
        /// <see cref="BindingSourceProperty.External"/>.
        ///
        /// Can be wired from a UnityEvent&lt;float&gt; in the Inspector:
        ///   e.g. Slider.onValueChanged → binding.SetValue (dynamic float)
        ///   e.g. call binding.SetValue(myFloat) from a custom MonoBehaviour.
        /// </summary>
        public void SetValue(float v)
        {
            _externalValue = v;
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

        /// <summary>
        /// MonoBehaviour.Reset — called once when the component is attached.
        /// Pre-fills the common "use the host GameObject as the source" case:
        ///   - _sourceTransform = self
        ///   - _sourceSlider    = Slider on self (if any)
        /// The user can override either in the Inspector.
        /// </summary>
        private void Reset()
        {
            if (_sourceTransform == null) _sourceTransform = transform;
            if (_sourceSlider == null) _sourceSlider = GetComponent<UnityEngine.UI.Slider>();
        }

#if UNITY_EDITOR
        // ---- 1:1 双方向同期: GO 側 component 削除 → linked preset も削除 ----
        // (EventMap 側で preset 削除 → linked component 削除は HapbeatEventMapWindow 側)

        private static bool s_isSceneClosingOrLoading;

        [UnityEditor.InitializeOnLoadMethod]
        private static void InitLifecycleTracker()
        {
            // scene unload / domain reload 中の OnDestroy で誤って preset を消さないためのガード
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing += (s, b) => s_isSceneClosingOrLoading = true;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpening += (path, mode) => s_isSceneClosingOrLoading = true;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += (s, m) => s_isSceneClosingOrLoading = false;
            UnityEditor.SceneManagement.EditorSceneManager.activeSceneChangedInEditMode += (a, b) => s_isSceneClosingOrLoading = false;
            UnityEditor.EditorApplication.playModeStateChanged += (state) => s_isSceneClosingOrLoading = false;
        }

        private void OnDestroy()
        {
            if (UnityEngine.Application.isPlaying) return;
            if (UnityEditor.EditorApplication.isCompiling) return;
            if (UnityEditor.EditorApplication.isUpdating) return;
            if (s_isSceneClosingOrLoading) return;

            var map = _linkedEventMap;
            var id = _linkedBindingId;
            if (map == null || string.IsNullOrEmpty(id)) return;

            // OnDestroy 時点では this はまだ scene 列挙に出てくる可能性があるため
            // delayCall で「destroy 確定後」に判定する。
            UnityEditor.EditorApplication.delayCall += () => CleanupOrphanPreset(map, id);
        }

        private static void CleanupOrphanPreset(HapbeatEventMap map, string presetId)
        {
            if (map == null || string.IsNullOrEmpty(presetId)) return;

            // 他にも同じ preset id を link している component が居れば preset は残す
            var all = UnityEngine.Object.FindObjectsByType<HapbeatParameterBinding>(
                UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
            foreach (var b in all)
            {
                if (b == null) continue;
                if (ReferenceEquals(b.LinkedEventMap, map) && b.LinkedBindingId == presetId)
                    return; // still in use → keep the preset
            }

            // 居なくなった → preset を map から除去
            for (int ei = 0; ei < map.entries.Count; ei++)
            {
                var entry = map.entries[ei];
                if (entry?.bindings == null) continue;
                for (int bi = entry.bindings.Count - 1; bi >= 0; bi--)
                {
                    if (entry.bindings[bi]?.id == presetId)
                    {
                        UnityEditor.Undo.RecordObject(map, "Remove Orphan Binding Preset");
                        entry.bindings.RemoveAt(bi);
                        UnityEditor.EditorUtility.SetDirty(map);
                        UnityEditor.AssetDatabase.SaveAssetIfDirty(map);
                        string shortId = presetId.Length > 8 ? presetId.Substring(0, 8) + "…" : presetId;
                        UnityEngine.Debug.Log(
                            $"[Hapbeat] Removed orphan preset (id={shortId}) from " +
                            $"EventMap '{map.name}' after its component was deleted (Ctrl+Z to undo).");
                        return;
                    }
                }
            }
        }
#endif
    }
}
