using UnityEngine;
using UnityEngine.InputSystem;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Minimal keyboard / mouse FPS controller for the Tutorial sample.
    /// WASD or arrow keys to move, mouse look (right-click to toggle look mode
    /// so left-click stays available for interactions like ball launch / pickup),
    /// Space to jump (optional). Uses Unity Input System (Keyboard.current /
    /// Mouse.current); requires the com.unity.inputsystem package.
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

        // Mouse.current.delta はピクセル/フレーム。旧 Input.GetAxisRaw("Mouse X")
        // の既定 sensitivity 0.1 にざっくり揃えるため内部で掛ける。
        private const float PixelsToLegacy = 0.1f;

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
            var mouse = Mouse.current;
            if (mouse == null) return;
            bool active = _alwaysLook || mouse.rightButton.isPressed;
            if (!active) return;

            Vector2 delta = mouse.delta.ReadValue();
            float mx = delta.x * _lookSensitivity * PixelsToLegacy;
            float my = delta.y * _lookSensitivity * PixelsToLegacy;

            transform.Rotate(0f, mx, 0f);
            _pitch = Mathf.Clamp(_pitch - my, -85f, 85f);
            if (_cameraPivot != null)
                _cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        private void HandleMove()
        {
            var kb = Keyboard.current;
            float h = 0f, v = 0f;
            if (kb != null)
            {
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  h -= 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    v += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  v -= 1f;
            }
            Vector3 dir = (transform.right * h + transform.forward * v).normalized;

            if (_controller.isGrounded && _velocity.y < 0f) _velocity.y = -1f;
            _velocity.y += _gravity * Time.deltaTime;

            Vector3 motion = dir * _moveSpeed + new Vector3(0f, _velocity.y, 0f);
            _controller.Move(motion * Time.deltaTime);
        }
    }
}
