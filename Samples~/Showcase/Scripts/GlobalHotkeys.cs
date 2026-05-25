using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Showcase-wide keyboard shortcuts used to demonstrate script-driven
    /// SDK calls without taking up a dedicated zone:
    ///   <list type="bullet">
    ///     <item><b>Q</b> — single Bridge.Play() of the manual_fire entry.</item>
    ///     <item><b>1-5</b> — Bridge.PlayScaled() with key value as gain factor (0.2 / 0.4 / ... / 1.0).</item>
    ///     <item><b>P</b> — HapbeatManager.Ping() with the round-trip latency shown in the HUD.</item>
    ///   </list>
    /// All calls go through ShowcaseBridge so they honour the active TargetPicker selection.
    /// </summary>
    public class GlobalHotkeys : MonoBehaviour
    {
        [SerializeField] private ShowcaseBridge _bridge;
        [SerializeField] private Text _pingResultText;
        [SerializeField] private string _manualFireEvent = "manual_fire";
        [SerializeField] private string _burstEvent = "burst";

        private static readonly Key[] s_digitKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
        };

        private void OnEnable()
        {
            if (HapbeatManager.Instance != null)
                HapbeatManager.Instance.OnPong += HandlePong;
        }

        private void OnDisable()
        {
            if (HapbeatManager.Instance != null)
                HapbeatManager.Instance.OnPong -= HandlePong;
        }

        private void Update()
        {
            if (_bridge == null) return;
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.qKey.wasPressedThisFrame)
                _bridge.PlayWithPickerTarget(_manualFireEvent);

            for (int i = 0; i < s_digitKeys.Length; i++)
            {
                if (kb[s_digitKeys[i]].wasPressedThisFrame)
                {
                    float gain = (i + 1) / 5f;
                    _bridge.PlayScaledWithPickerTarget(_burstEvent, gain * 5f, 0f, 5f);
                }
            }

            if (kb.pKey.wasPressedThisFrame)
                _bridge.SendPing();
        }

        private void HandlePong(long rttUs)
        {
            if (_pingResultText != null)
                _pingResultText.text = $"Ping: {rttUs / 1000f:F1} ms";
        }
    }
}
