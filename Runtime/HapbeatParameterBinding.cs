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

        // Cached references
        private AudioSource _audioSource;
        private HapbeatAudioBridge _audioBridge;
        private Rigidbody _sourceRigidbody;
        private bool _initialized;

        [Header("Debug")]
        [Tooltip("Log input/output values to the Unity console at a fixed interval.")]
        [SerializeField]
        private bool _debugLog = false;

        [Tooltip("Log interval in seconds (when Debug Log is enabled).")]
        [SerializeField, Range(0.05f, 2f)]
        private float _debugLogInterval = 0.2f;

        /// <summary>Current raw input value (before mapping).</summary>
        public float CurrentInput { get; private set; }

        /// <summary>Current normalized value (0-1 after input range mapping).</summary>
        public float CurrentNormalized { get; private set; }

        /// <summary>Current output value (after mapping, written to parameter).</summary>
        public float CurrentOutput { get; private set; }

        private float _lastDebugLogTime;

        private void Start()
        {
            Initialize();
        }

        private void Update()
        {
            if (!_initialized) Initialize();
            if (_sourceTransform == null) return;

            // Read input
            float raw = ReadSourceValue();
            CurrentInput = raw;

            // Normalize to 0-1
            float range = _inputMax - _inputMin;
            float normalized = Mathf.Abs(range) > 0.0001f
                ? Mathf.Clamp01((raw - _inputMin) / range)
                : 0f;
            CurrentNormalized = normalized;

            // Apply curve
            float curved = ApplyCurve(normalized);

            // Map to output range
            float output = Mathf.Lerp(_outputMin, _outputMax, curved);
            CurrentOutput = output;

            // Write output
            WriteOutput(output);

            // Debug log
            if (_debugLog && Time.unscaledTime - _lastDebugLogTime >= _debugLogInterval)
            {
                _lastDebugLogTime = Time.unscaledTime;
                Debug.Log($"[HapbeatBinding] {_sourceProperty} input={raw:F4} " +
                          $"normalized={normalized:F2} \u2192 {_outputParameter} output={output:F3}",
                          this);
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

        private float ReadSourceValue()
        {
            switch (_sourceProperty)
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

        private float ApplyCurve(float t)
        {
            switch (_curveType)
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
                    return _customCurve.Evaluate(t);
                default:
                    return t;
            }
        }

        private void WriteOutput(float value)
        {
            switch (_outputParameter)
            {
                case BindingOutputParameter.Volume:
                    if (_audioSource != null)
                        _audioSource.volume = Mathf.Clamp01(value);
                    break;
                case BindingOutputParameter.Pitch:
                    if (_audioSource != null)
                        _audioSource.pitch = Mathf.Clamp(value, -3f, 3f);
                    break;
                case BindingOutputParameter.Pan:
                    if (_audioSource != null)
                        _audioSource.panStereo = Mathf.Clamp(value, -1f, 1f);
                    break;
                case BindingOutputParameter.BridgeGain:
                    if (_audioBridge != null)
                        _audioBridge.Gain = value;
                    break;
            }
        }

        private void OnValidate()
        {
            // Re-initialize when Inspector values change
            _initialized = false;
        }
    }
}
