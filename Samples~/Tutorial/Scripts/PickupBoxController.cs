using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z3 Pickup Box: mouse-left to grab the box (click to attach, click again to drop).
    /// While held, the box follows the camera at a fixed distance.
    /// HapbeatSequenceTrigger on the box drives Fire→Loop→Stop, and a
    /// HapbeatParameterBinding modulates the loop gain by box motion.
    /// </summary>
    public class PickupBoxController : MonoBehaviour
    {
        [SerializeField] private HapbeatSequenceTrigger _sequence;
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private Transform _holdAnchor;     // typically a child of the camera
        [SerializeField] private Transform _restPose;       // where to return when dropped
        [SerializeField] private float _followLerp = 12f;
        [SerializeField] private Key _toggleKey = Key.None; // mouse 0 by default

        private bool _held;

        private void Awake()
        {
            if (_rigidbody == null) _rigidbody = GetComponent<Rigidbody>();
        }

        private void Update()
        {
            var mouse = Mouse.current;
            var kb = Keyboard.current;
            bool pressed = (mouse != null && mouse.leftButton.wasPressedThisFrame)
                        || (_toggleKey != Key.None && kb != null && kb[_toggleKey].wasPressedThisFrame);
            if (pressed) Toggle();
        }

        private void FixedUpdate()
        {
            if (!_held || _holdAnchor == null || _rigidbody == null) return;

            Vector3 target = _holdAnchor.position;
            Vector3 delta = target - _rigidbody.position;
            _rigidbody.linearVelocity = delta * _followLerp;
        }

        private void Toggle()
        {
            if (_held) Drop();
            else Pickup();
        }

        private void Pickup()
        {
            if (_rigidbody == null) return;
            _held = true;
            _rigidbody.useGravity = false;
            _rigidbody.linearDamping = 6f;
            _sequence?.Fire();
        }

        private void Drop()
        {
            if (_rigidbody == null) return;
            _held = false;
            _rigidbody.useGravity = true;
            _rigidbody.linearDamping = 0f;
            _sequence?.Stop();

            if (_restPose != null)
            {
                _rigidbody.position = _restPose.position;
                _rigidbody.rotation = _restPose.rotation;
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }
    }
}
