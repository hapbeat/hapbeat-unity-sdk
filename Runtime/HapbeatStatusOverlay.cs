using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat
{
    /// <summary>
    /// Drop-in debug HUD that surfaces <see cref="HapbeatManager"/> state on
    /// two UI Text elements:
    /// <list type="bullet">
    ///   <item><b>Status</b> — live connection / streaming flag.</item>
    ///   <item><b>Log</b> — one-line history of OnConnected / OnDisconnected /
    ///         OnPong / OnError + Stream transitions, capped at <see cref="maxLogLines"/>.</item>
    /// </list>
    ///
    /// Public <see cref="Log(string)"/> lets external scripts (UI Buttons,
    /// <see cref="HapbeatKeyDispatcher"/> bindings, custom logic) push their
    /// own messages onto the same overlay without re-implementing the buffer.
    ///
    /// <para>
    /// Place on a Canvas GameObject and wire <c>statusText</c> / <c>logText</c>
    /// in the Inspector. The overlay is purely additive — multiple instances
    /// can coexist if you want separate logs per zone.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Status Overlay")]
    public class HapbeatStatusOverlay : MonoBehaviour
    {
        [Tooltip("Single-line label showing connection / streaming state.")]
        [SerializeField] private Text _statusText;

        [Tooltip("Multi-line log of recent SDK events (Connect / Pong / Error / Stream / user messages).")]
        [SerializeField] private Text _logText;

        [Tooltip("Maximum number of log lines kept on screen. Older lines are dropped.")]
        [SerializeField] private int _maxLogLines = 8;

        private string _logBuffer = "";
        private bool _wasStreaming;

        private void Start()
        {
            if (HapbeatManager.Instance == null) return;

            HapbeatManager.Instance.OnConnected    += () => AppendLog("Connected (broadcast)");
            HapbeatManager.Instance.OnDisconnected += () => AppendLog("Disconnected");
            HapbeatManager.Instance.OnPong         += (rttUs) => AppendLog($"Pong: RTT={rttUs / 1000.0:F1}ms");
            HapbeatManager.Instance.OnError        += (msg) => AppendLog($"Error: {msg}");
        }

        private void Update()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;

            if (_statusText != null)
            {
                string streaming = mgr.IsStreaming ? " [STREAMING]" : "";
                string overrideText;
                if (mgr.OverridePlayer < 1 && mgr.OverrideGroup < 1)
                {
                    overrideText = "override: off";
                }
                else
                {
                    string p = mgr.OverridePlayer >= 1 ? mgr.OverridePlayer.ToString() : "-";
                    string g = mgr.OverrideGroup >= 1 ? mgr.OverrideGroup.ToString() : "-";
                    overrideText = $"override: P{p} / G{g}";
                }
                _statusText.text = mgr.IsConnected
                    ? $"Status: Connected ({overrideText}){streaming}"
                    : "Status: Disconnected";
            }

            // Surface stream start / stop transitions so callers don't have to
            // log them explicitly from their key bindings or buttons.
            bool nowStreaming = mgr.IsStreaming;
            if (nowStreaming != _wasStreaming)
            {
                AppendLog(nowStreaming ? "Stream started" : "Stream stopped");
                _wasStreaming = nowStreaming;
            }
        }

        /// <summary>Append a one-line entry to the on-screen log. Safe to call from UnityEvents.</summary>
        public void Log(string message) => AppendLog(message);

        /// <summary>Clear all log lines.</summary>
        public void Clear()
        {
            _logBuffer = "";
            if (_logText != null) _logText.text = "";
        }

        private void AppendLog(string message)
        {
            int maxLines = Mathf.Max(1, _maxLogLines);
            _logBuffer += $"[{Time.realtimeSinceStartup:F1}] {message}\n";
            var lines = _logBuffer.Split('\n');
            if (lines.Length > maxLines + 1)
            {
                _logBuffer = string.Join("\n", lines, lines.Length - maxLines - 1, maxLines + 1);
            }
            if (_logText != null) _logText.text = _logBuffer;
        }
    }
}
