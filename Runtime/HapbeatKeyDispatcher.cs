using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hapbeat
{
    /// <summary>
    /// Maps single key presses to Inspector-wired UnityEvents. Useful as a
    /// drop-in replacement for short, single-key UI when full PlayerInput /
    /// InputAction setups are overkill (samples, prototypes, debug tools).
    ///
    /// Each <see cref="Binding"/> serializes a legacy <see cref="KeyCode"/>
    /// (used directly when the legacy Input Manager is enabled) and the
    /// dispatcher transparently routes through <see cref="UnityEngine.InputSystem.Keyboard"/>
    /// when the new Input System is the only active backend.
    ///
    /// <para>
    /// Pair with <see cref="HapbeatUnityEventTrigger"/> and
    /// <see cref="HapbeatActionHelper"/> to build keyboard-driven haptic demos
    /// without writing a custom MonoBehaviour.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Key Dispatcher")]
    public class HapbeatKeyDispatcher : MonoBehaviour
    {
        [Serializable]
        public class Binding
        {
            [Tooltip("Optional label shown in Inspector and on-screen log.")]
            public string label;

            [Tooltip("Key that triggers this binding.")]
            public KeyCode key = KeyCode.None;

            [Tooltip("UnityEvents fired when the key is pressed this frame.")]
            public UnityEvent onPressed = new UnityEvent();
        }

        [SerializeField] private List<Binding> _bindings = new List<Binding>();

        /// <summary>Mutable binding list. Editor scripts may modify directly.</summary>
        public List<Binding> Bindings => _bindings;

        private void Update()
        {
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                if (b == null || b.key == KeyCode.None) continue;
                if (IsKeyPressedThisFrame(b.key))
                    b.onPressed?.Invoke();
            }
        }

        // ---------------------------------------------------------------
        // Input compatibility
        // ---------------------------------------------------------------

        private static bool IsKeyPressedThisFrame(KeyCode legacyKey)
        {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var k = LegacyToNew(legacyKey);
            return k != Key.None
                   && Keyboard.current != null
                   && Keyboard.current[k].wasPressedThisFrame;
#else
            return Input.GetKeyDown(legacyKey);
#endif
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        // Minimal mapping of the keys used by Hapbeat samples. Extend if a
        // sample binds additional keys.
        private static Key LegacyToNew(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Space:    return Key.Space;
                case KeyCode.E:        return Key.E;
                case KeyCode.L:        return Key.L;
                case KeyCode.P:        return Key.P;
                case KeyCode.S:        return Key.S;
                case KeyCode.Q:        return Key.Q;
                case KeyCode.Alpha1:   return Key.Digit1;
                case KeyCode.Alpha2:   return Key.Digit2;
                case KeyCode.Alpha3:   return Key.Digit3;
                case KeyCode.Alpha4:   return Key.Digit4;
                case KeyCode.Alpha5:   return Key.Digit5;
                case KeyCode.Return:   return Key.Enter;
                case KeyCode.Escape:   return Key.Escape;
                default:               return Key.None;
            }
        }
#endif
    }
}
