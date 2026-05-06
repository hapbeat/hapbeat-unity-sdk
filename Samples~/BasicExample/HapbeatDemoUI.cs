using UnityEngine;
using UnityEngine.UI;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// BasicExample 用の UI 表示スクリプト。
    /// HapbeatManager の接続状態とイベントログを Text に表示する。
    /// </summary>
    public class HapbeatDemoUI : MonoBehaviour
    {
        [SerializeField] private Text _statusText;
        [SerializeField] private Text _logText;

        private string _logBuffer = "";
        private const int MaxLogLines = 8;
        private bool _wasStreaming;

        private void Start()
        {
            if (HapbeatManager.Instance == null) return;

            HapbeatManager.Instance.OnConnected += () => AppendLog("Connected (broadcast)");
            HapbeatManager.Instance.OnDisconnected += () => AppendLog("Disconnected");
            HapbeatManager.Instance.OnPong += (rttUs) => AppendLog($"Pong: RTT={rttUs / 1000.0:F1}ms");
            HapbeatManager.Instance.OnError += (msg) => AppendLog($"Error: {msg}");
        }

        private void Update()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;

            if (_statusText != null)
            {
                string streaming = mgr.IsStreaming ? " [STREAMING]" : "";
                _statusText.text = mgr.IsConnected
                    ? $"Status: Connected (group={mgr.DefaultGroup}){streaming}"
                    : "Status: Disconnected";
            }

            // Surface stream start/stop transitions in the log so the user can
            // visually confirm Space (start) and S (stop) had effect.
            bool nowStreaming = mgr.IsStreaming;
            if (nowStreaming != _wasStreaming)
            {
                AppendLog(nowStreaming ? "Stream started" : "Stream stopped");
                _wasStreaming = nowStreaming;
            }
        }

        /// <summary>Append a one-line entry to the on-screen log.</summary>
        public void Log(string message) => AppendLog(message);

        private void AppendLog(string message)
        {
            _logBuffer += $"[{Time.realtimeSinceStartup:F1}] {message}\n";
            var lines = _logBuffer.Split('\n');
            if (lines.Length > MaxLogLines + 1)
            {
                _logBuffer = string.Join("\n", lines, lines.Length - MaxLogLines - 1, MaxLogLines + 1);
            }
            if (_logText != null) _logText.text = _logBuffer;
        }
    }
}
