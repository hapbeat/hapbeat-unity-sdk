using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z1 Bowling Lane: ball physics 制御のみ。Haptic は各 Pin に attach
    /// された <c>HapbeatCollisionTrigger</c> が velocity-scaled で自動発火
    /// するため、このスクリプトは触覚に関与しない (= "haptic = Trigger 任せ"
    /// パターンの最小例)。
    /// </summary>
    public class BallLauncher : MonoBehaviour
    {
        [SerializeField] private Rigidbody _ball;
        [SerializeField] private Transform _spawnPose;
        [SerializeField] private Transform _aimReference; // usually the player camera
        [SerializeField] private float _launchSpeed = 8f;
        [SerializeField] private List<Rigidbody> _pins = new List<Rigidbody>();
        [SerializeField] private bool _verboseLog = true;

        private struct PoseSnapshot
        {
            public Vector3 pos;
            public Quaternion rot;
        }

        private PoseSnapshot _ballRest;
        private List<PoseSnapshot> _pinRest = new List<PoseSnapshot>();

        private void Start()
        {
            if (_ball != null)
                _ballRest = new PoseSnapshot { pos = _ball.transform.position, rot = _ball.transform.rotation };

            foreach (var pin in _pins)
            {
                if (pin == null) continue;
                _pinRest.Add(new PoseSnapshot { pos = pin.transform.position, rot = pin.transform.rotation });
            }
        }

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                if (_verboseLog) Debug.Log("[Bowling] LMB → Launch");
                Launch();
            }
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                if (_verboseLog) Debug.Log("[Bowling] Space → Reset");
                ResetPose();
            }
        }

        private void Launch()
        {
            if (_ball == null)
            {
                Debug.LogWarning("[Bowling] _ball が未割当");
                return;
            }

            _ball.transform.position = _spawnPose != null ? _spawnPose.position : _ballRest.pos;
            _ball.transform.rotation = _spawnPose != null ? _spawnPose.rotation : _ballRest.rot;
            _ball.linearVelocity = Vector3.zero;
            _ball.angularVelocity = Vector3.zero;

            Vector3 dir = _aimReference != null
                ? Vector3.ProjectOnPlane(_aimReference.forward, Vector3.up).normalized
                : Vector3.forward;
            _ball.linearVelocity = dir * _launchSpeed;
        }

        private void ResetPose()
        {
            if (_ball != null)
            {
                _ball.transform.position = _ballRest.pos;
                _ball.transform.rotation = _ballRest.rot;
                _ball.linearVelocity = Vector3.zero;
                _ball.angularVelocity = Vector3.zero;
            }

            for (int i = 0; i < _pins.Count && i < _pinRest.Count; i++)
            {
                var pin = _pins[i];
                if (pin == null) continue;
                pin.transform.position = _pinRest[i].pos;
                pin.transform.rotation = _pinRest[i].rot;
                pin.linearVelocity = Vector3.zero;
                pin.angularVelocity = Vector3.zero;
            }
        }
    }
}
