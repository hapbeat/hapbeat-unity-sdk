using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Fires a haptic event when an Animator parameter meets a condition.
    /// Can be placed on a dedicated [Hapbeat Router] GameObject — the target Animator
    /// is specified by reference, not required to be on the same object.
    /// Non-invasive: only reads Animator parameters, never writes.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Animator Trigger")]
    public class HapbeatAnimatorTrigger : HapbeatTriggerBase
    {
        public enum Condition
        {
            BoolBecameTrue,
            BoolBecameFalse,
            IntEquals,
            IntNotEquals,
            FloatAbove,
            FloatBelow
        }

        [Header("Animator Settings")]
        [Tooltip("Target Animator to monitor. If null, searches this GameObject.")]
        [SerializeField]
        private Animator _targetAnimator;

        [Tooltip("Name of the Animator parameter to watch.")]
        [SerializeField]
        private string _parameterName = "";

        [Tooltip("Condition that triggers the haptic event.")]
        [SerializeField]
        private Condition _condition = Condition.BoolBecameTrue;

        [Tooltip("Threshold for Int/Float conditions.")]
        [SerializeField]
        private float _threshold = 0f;

        // Previous frame values for edge detection
        private bool _prevBool;
        private int _prevInt;
        private float _prevFloat;
        private bool _initialized;
        private int _parameterHash;

        private void Start()
        {
            if (_targetAnimator == null)
                _targetAnimator = GetComponent<Animator>();

            if (_targetAnimator != null && !string.IsNullOrEmpty(_parameterName))
            {
                _parameterHash = Animator.StringToHash(_parameterName);
                InitializePreviousValues();
            }
        }

        private void Update()
        {
            if (!_triggerEnabled) return;
            if (_targetAnimator == null || string.IsNullOrEmpty(_parameterName)) return;
            if (!_initialized)
            {
                InitializePreviousValues();
                return;
            }

            switch (_condition)
            {
                case Condition.BoolBecameTrue:
                case Condition.BoolBecameFalse:
                    CheckBool();
                    break;
                case Condition.IntEquals:
                case Condition.IntNotEquals:
                    CheckInt();
                    break;
                case Condition.FloatAbove:
                case Condition.FloatBelow:
                    CheckFloat();
                    break;
            }
        }

        private void InitializePreviousValues()
        {
            if (_targetAnimator == null) return;

            switch (_condition)
            {
                case Condition.BoolBecameTrue:
                case Condition.BoolBecameFalse:
                    _prevBool = _targetAnimator.GetBool(_parameterHash);
                    break;
                case Condition.IntEquals:
                case Condition.IntNotEquals:
                    _prevInt = _targetAnimator.GetInteger(_parameterHash);
                    break;
                case Condition.FloatAbove:
                case Condition.FloatBelow:
                    _prevFloat = _targetAnimator.GetFloat(_parameterHash);
                    break;
            }
            _initialized = true;
        }

        private void CheckBool()
        {
            bool current = _targetAnimator.GetBool(_parameterHash);
            if (current != _prevBool)
            {
                if (_condition == Condition.BoolBecameTrue && current)
                    FireHaptic();
                else if (_condition == Condition.BoolBecameFalse && !current)
                    FireHaptic();
            }
            _prevBool = current;
        }

        private void CheckInt()
        {
            int current = _targetAnimator.GetInteger(_parameterHash);
            if (current != _prevInt)
            {
                int t = (int)_threshold;
                if (_condition == Condition.IntEquals && current == t)
                    FireHaptic();
                else if (_condition == Condition.IntNotEquals && current != t)
                    FireHaptic();
            }
            _prevInt = current;
        }

        private void CheckFloat()
        {
            float current = _targetAnimator.GetFloat(_parameterHash);
            bool prevAbove = _prevFloat > _threshold;
            bool currAbove = current > _threshold;

            if (_condition == Condition.FloatAbove && !prevAbove && currAbove)
                FireHaptic();
            else if (_condition == Condition.FloatBelow && prevAbove && !currAbove)
                FireHaptic();

            _prevFloat = current;
        }
    }
}
