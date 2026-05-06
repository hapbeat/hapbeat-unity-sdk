using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

#if HAPBEAT_HAS_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Hapbeat
{
    /// <summary>
    /// Maps single key presses to Inspector-wired UnityEvents. Useful as a
    /// drop-in replacement for short, single-key UI when full PlayerInput /
    /// InputAction setups are overkill (samples, prototypes, debug tools).
    ///
    /// <para>
    /// Pair with <see cref="HapbeatUnityEventTrigger"/> and
    /// <see cref="HapbeatActionHelper"/> to build keyboard-driven haptic demos
    /// without writing a custom MonoBehaviour.
    /// </para>
    ///
    /// <para>
    /// <b>Input system support</b>: works under all three <i>Active Input
    /// Handling</i> settings. When the project uses the legacy <see cref="Input"/>
    /// manager (Old / Both), key presses are read via <c>Input.GetKeyDown</c>.
    /// When the project uses the Input System Package only, the dispatcher
    /// falls back to <c>Keyboard.current</c> (requires the
    /// <c>com.unity.inputsystem</c> package, which is automatically referenced
    /// via the SDK's asmdef <i>versionDefines</i>). If neither input backend
    /// is active, the dispatcher silently no-ops.
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
#if ENABLE_LEGACY_INPUT_MANAGER
            // Legacy path is the cheapest and matches the KeyCode enum directly.
            return Input.GetKeyDown(legacyKey);
#elif HAPBEAT_HAS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var k = LegacyToNewKey(legacyKey);
            return k != Key.None
                   && Keyboard.current != null
                   && Keyboard.current[k].wasPressedThisFrame;
#else
            return false;
#endif
        }

#if HAPBEAT_HAS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // Minimal mapping of the keys typically used by Hapbeat samples /
        // demos. Extend if you bind additional keys.
        private static Key LegacyToNewKey(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Space:    return Key.Space;
                case KeyCode.A:        return Key.A;
                case KeyCode.B:        return Key.B;
                case KeyCode.C:        return Key.C;
                case KeyCode.D:        return Key.D;
                case KeyCode.E:        return Key.E;
                case KeyCode.F:        return Key.F;
                case KeyCode.G:        return Key.G;
                case KeyCode.H:        return Key.H;
                case KeyCode.I:        return Key.I;
                case KeyCode.J:        return Key.J;
                case KeyCode.K:        return Key.K;
                case KeyCode.L:        return Key.L;
                case KeyCode.M:        return Key.M;
                case KeyCode.N:        return Key.N;
                case KeyCode.O:        return Key.O;
                case KeyCode.P:        return Key.P;
                case KeyCode.Q:        return Key.Q;
                case KeyCode.R:        return Key.R;
                case KeyCode.S:        return Key.S;
                case KeyCode.T:        return Key.T;
                case KeyCode.U:        return Key.U;
                case KeyCode.V:        return Key.V;
                case KeyCode.W:        return Key.W;
                case KeyCode.X:        return Key.X;
                case KeyCode.Y:        return Key.Y;
                case KeyCode.Z:        return Key.Z;
                case KeyCode.Alpha0:   return Key.Digit0;
                case KeyCode.Alpha1:   return Key.Digit1;
                case KeyCode.Alpha2:   return Key.Digit2;
                case KeyCode.Alpha3:   return Key.Digit3;
                case KeyCode.Alpha4:   return Key.Digit4;
                case KeyCode.Alpha5:   return Key.Digit5;
                case KeyCode.Alpha6:   return Key.Digit6;
                case KeyCode.Alpha7:   return Key.Digit7;
                case KeyCode.Alpha8:   return Key.Digit8;
                case KeyCode.Alpha9:   return Key.Digit9;
                case KeyCode.Return:   return Key.Enter;
                case KeyCode.Escape:   return Key.Escape;
                case KeyCode.Tab:      return Key.Tab;
                case KeyCode.LeftShift:  return Key.LeftShift;
                case KeyCode.RightShift: return Key.RightShift;
                case KeyCode.LeftControl:  return Key.LeftCtrl;
                case KeyCode.RightControl: return Key.RightCtrl;
                case KeyCode.LeftAlt:  return Key.LeftAlt;
                case KeyCode.RightAlt: return Key.RightAlt;
                case KeyCode.UpArrow:    return Key.UpArrow;
                case KeyCode.DownArrow:  return Key.DownArrow;
                case KeyCode.LeftArrow:  return Key.LeftArrow;
                case KeyCode.RightArrow: return Key.RightArrow;
                default: return Key.None;
            }
        }
#endif
    }
}
