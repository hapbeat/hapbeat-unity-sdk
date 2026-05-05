using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z5 Target Range: hold left mouse button to charge, release to fire a
    /// projectile. Charge level (0..1) is mapped through an AnimationCurve
    /// for the haptic gain via TutorialBridge.PlayWithCurveAndPickerTarget.
    /// On hit, TargetReceiver fires its own UnityEvent-wired trigger.
    /// </summary>
    public class ChargeShooter : MonoBehaviour
    {
        [SerializeField] private TutorialBridge _bridge;
        [SerializeField] private Transform _muzzle;
        [SerializeField] private Rigidbody _projectilePrefab;
        [SerializeField] private float _maxLaunchSpeed = 18f;
        [SerializeField] private float _maxChargeSeconds = 1.5f;
        [SerializeField] private AnimationCurve _gainCurve = AnimationCurve.EaseInOut(0f, 0.1f, 1f, 1f);
        [SerializeField] private string _eventName = "charge_release";

        [Header("UI")]
        [SerializeField] private Slider _chargeBar;

        private bool _charging;
        private float _chargeStartTime;

        private void Update()
        {
            if (Input.GetMouseButtonDown(0))
                BeginCharge();

            if (_charging)
            {
                float t = Mathf.Clamp01((Time.time - _chargeStartTime) / _maxChargeSeconds);
                if (_chargeBar != null) _chargeBar.value = t;

                if (Input.GetMouseButtonUp(0))
                    Release(t);
            }
        }

        private void BeginCharge()
        {
            _charging = true;
            _chargeStartTime = Time.time;
            if (_chargeBar != null) _chargeBar.value = 0f;
        }

        private void Release(float chargeT)
        {
            _charging = false;
            if (_chargeBar != null) _chargeBar.value = 0f;

            if (_bridge != null)
                _bridge.PlayWithCurveAndPickerTarget(_eventName, chargeT, _gainCurve);

            if (_projectilePrefab != null && _muzzle != null)
            {
                var p = Instantiate(_projectilePrefab, _muzzle.position, _muzzle.rotation);
                p.linearVelocity = _muzzle.forward * (_maxLaunchSpeed * Mathf.Lerp(0.3f, 1f, chargeT));
                Destroy(p.gameObject, 4f);
            }
        }
    }
}
