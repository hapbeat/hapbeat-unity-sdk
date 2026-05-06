using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

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
    /// <b>Input system support</b>: this component uses the legacy
    /// <see cref="Input"/> API (<c>Input.GetKeyDown</c>) so the SDK runtime
    /// assembly can avoid a hard reference to the optional <c>Unity.InputSystem</c>
    /// package. That means it works when <i>Project Settings → Player → Active
    /// Input Handling</i> is set to <b>Both</b> (the Unity 2022+ default) or
    /// <b>Old</b>. If the project is configured for <b>Input System Package</b>
    /// only, swap this component for a custom dispatcher that calls
    /// <c>Keyboard.current[Key.Xxx].wasPressedThisFrame</c> against the same
    /// UnityEvents.
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
                if (Input.GetKeyDown(b.key))
                    b.onPressed?.Invoke();
            }
        }
    }
}
