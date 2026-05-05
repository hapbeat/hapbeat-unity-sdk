using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Minimal keyboard / mouse FPS controller for the Tutorial sample.
    /// WASD or arrow keys to move, mouse look (right-click to toggle look mode
    /// so left-click stays available for interactions like ball launch / pickup),
    /// Space to jump (optional).
    ///
    /// Falls back gracefully whether the project uses the new Input System
    /// or the legacy Input Manager.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SimpleFPSController : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float _moveSpeed = 4.0f;
        [SerializeField] private float _lookSensitivity = 2.0f;
        [SerializeField] private float _gravity = -9.81f;

        [Header("Look")]
        [Tooltip("If true, mouse look is always active. If false, hold right mouse button to look.")]
        [SerializeField] private bool _alwaysLook = true;
        [SerializeField] private Transform _cameraPivot;

        private CharacterController _controller;
        private float _pitch;
        private Vector3 _velocity;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            if (_cameraPivot == null && Camera.main != null)
                _cameraPivot = Camera.main.transform;
        }

        private void Start()
        {
            if (_alwaysLook)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Update()
        {
            HandleLook();
            HandleMove();
        }

        private void HandleLook()
        {
            bool active = _alwaysLook || Input.GetMouseButton(1);
            if (!active) return;

            float mx = Input.GetAxisRaw("Mouse X") * _lookSensitivity;
            float my = Input.GetAxisRaw("Mouse Y") * _lookSensitivity;

            transform.Rotate(0f, mx, 0f);
            _pitch = Mathf.Clamp(_pitch - my, -85f, 85f);
            if (_cameraPivot != null)
                _cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void HandleMove()
        {
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 dir = (transform.right * h + transform.forward * v).normalized;

            if (_controller.isGrounded && _velocity.y < 0f) _velocity.y = -1f;
            _velocity.y += _gravity * Time.deltaTime;

            Vector3 motion = dir * _moveSpeed + new Vector3(0f, _velocity.y, 0f);
            _controller.Move(motion * Time.deltaTime);
        }
    }
}
