using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Static on-screen help text. Displays movement keys, zone shortcuts,
    /// and the current connection / streaming status.
    /// </summary>
    public class HudGuide : MonoBehaviour
    {
        [SerializeField] private Text _guideText;
        [SerializeField] private Text _connectionStatusText;

        [TextArea(8, 16)]
        [SerializeField] private string _guideContent =
@"WASD: move    Mouse: look    Esc: quit
[Z1] LMB: launch ball   Space: reset pins
[Z2] F: open/close door
[Z3] LMB: pickup / drop box
[Z4] Space: stream toggle, sliders modulate gain/pan
[Z5] LMB hold: charge, release: shoot target

Q: manual fire   1-5: PlayScaled (gain step)   P: Ping";

        private void Start()
        {
            if (_guideText != null) _guideText.text = _guideContent;
            UpdateConnectionStatus();
        }

        private void Update()
        {
            UpdateConnectionStatus();
        }

        private void UpdateConnectionStatus()
        {
            if (_connectionStatusText == null) return;
            if (HapbeatManager.Instance == null)
            {
                _connectionStatusText.text = "Hapbeat: (no manager)";
                _connectionStatusText.color = Color.gray;
                return;
            }
            bool connected = HapbeatManager.Instance.IsConnected;
            _connectionStatusText.text = connected ? "Hapbeat: connected" : "Hapbeat: not connected";
            _connectionStatusText.color = connected ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.7f, 0.4f);
        }
    }
}
