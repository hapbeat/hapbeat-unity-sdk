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
            if (_statusText != null && HapbeatManager.Instance != null)
            {
                string streaming = HapbeatManager.Instance.IsStreaming ? " [STREAMING]" : "";
                _statusText.text = HapbeatManager.Instance.IsConnected
                    ? $"Status: Connected (group={HapbeatManager.Instance.DefaultGroup}){streaming}"
                    : "Status: Disconnected";
            }
        }

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
