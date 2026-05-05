using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Tutorial-wide keyboard shortcuts used to demonstrate script-driven
    /// SDK calls without taking up a dedicated zone:
    ///   <list type="bullet">
    ///     <item><b>Q</b> — single Bridge.Play() of the manual_fire entry.</item>
    ///     <item><b>1-5</b> — Bridge.PlayScaled() with key value as gain factor (0.2 / 0.4 / ... / 1.0).</item>
    ///     <item><b>P</b> — HapbeatManager.Ping() with the round-trip latency shown in the HUD.</item>
    ///   </list>
    /// All calls go through TutorialBridge so they honour the active TargetPicker selection.
    /// </summary>
    public class GlobalHotkeys : MonoBehaviour
    {
        [SerializeField] private TutorialBridge _bridge;
        [SerializeField] private Text _pingResultText;
        [SerializeField] private string _manualFireEvent = "manual_fire";
        [SerializeField] private string _burstEvent = "burst";

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

            if (Input.GetKeyDown(KeyCode.Q))
                _bridge.PlayWithPickerTarget(_manualFireEvent);

            for (int i = 1; i <= 5; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha0 + i))
                {
                    float gain = i / 5f;
                    _bridge.PlayScaledWithPickerTarget(_burstEvent, gain * 5f, 0f, 5f);
                }
            }

            if (Input.GetKeyDown(KeyCode.P))
                _bridge.SendPing();
        }

        private void HandlePong(long rttUs)
        {
            if (_pingResultText != null)
                _pingResultText.text = $"Ping: {rttUs / 1000f:F1} ms";
        }
    }
}
