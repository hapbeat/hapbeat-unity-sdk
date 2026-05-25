using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// ScriptableObject holding Hapbeat SDK connection and behavior settings.
    /// Create via Assets > Create > Hapbeat > Config.
    /// </summary>
    [CreateAssetMenu(fileName = "HapbeatConfig", menuName = "Hapbeat/Config", order = 1)]
    public class HapbeatConfig : ScriptableObject
    {
        [Header("Connection")]
        [Tooltip("UDP port for communication with Hapbeat devices.")]
        public int port = 7700;

        [Tooltip("Target group ID. -1 = no group filter (default). 0 = broadcast to all. 1-254 = specific group.")]
        [Range(-1, 254)]
        public int group = -1;

        [Header("App Info")]
        [Tooltip("Hapbeat デバイスのディスプレイに表示されるクライアントアプリ名。\n" +
                 "Max 16 文字 (display grid 幅)。デフォルトの app_name 要素 (8x1) では先頭 8 文字のみ表示。\n" +
                 "空欄の場合は Application.productName を自動使用。")]
        [Delayed]
        public string appName = "";

        /// <summary>Maximum number of characters allowed for <see cref="appName"/>.
        /// Matches the device display grid width (16 cols × 1 row).
        /// Longer strings are truncated when serialized.</summary>
        public const int MaxAppNameLength = 16;

        [Header("Bridge (ESP-NOW)")]
        [Tooltip("Use Bridge for ESP-NOW multi-device transmission. When disabled (default), connects directly to devices via Wi-Fi UDP.")]
        public bool useBridge = false;

        [Tooltip("Hostname or IP address of the Hapbeat Bridge server. Only used when useBridge is enabled.")]
        public string bridgeHost = "127.0.0.1";

        [Header("Discovery")]
        [Tooltip("Discovery timeout in milliseconds.")]
        [Range(1000, 10000)]
        public int discoveryTimeoutMs = 3000;

        [Header("Behavior")]
        [Tooltip("Interval in seconds between keep-alive ping messages.")]
        [Range(1f, 60f)]
        public float pingInterval = 5.0f;

        [Tooltip("Audio data the SDK keeps queued ahead of real-time playback while " +
                 "streaming a clip. Smaller values stop the haptic faster after " +
                 "StopStream() but increase risk of stutter on slow links. " +
                 "Range: 10–200 ms. Typical LAN: 30–60 ms. Default: 50 ms.")]
        [Range(0.01f, 0.2f)]
        public float streamSendAheadSeconds = 0.05f;

        [Header("Latency Compensation")]
        [Tooltip("Audio 出力デバイス (Bluetooth 等) の latency に合わせて、全 Play / StreamClip 呼び出しに " +
                 "加算するグローバル遅延 (秒)。Hapbeat 触覚は UDP 直送で非常に低遅延 (~10ms) なので、" +
                 "speakers / headphones が遅い環境では触覚が先に来てしまう。これを補正するための offset。\n\n" +
                 "目安:\n" +
                 "  ・有線 / USB DAC      → 0.00 (補正不要)\n" +
                 "  ・内蔵スピーカー       → 0.02〜0.05\n" +
                 "  ・Bluetooth (aptX LL) → 0.03〜0.05\n" +
                 "  ・Bluetooth (SBC/AAC) → 0.15〜0.20\n\n" +
                 "各 EventMap entry の delayOffsetSeconds でさらに ±0.2 秒の個別調整も可能。\n" +
                 "デフォルト 0 (遅延なし)。")]
        [Range(0f, 0.5f)]
        public float hapticDelaySeconds = 0f;

        [Header("Debugging")]
        [Tooltip("Enable logging to the Unity console (Play, Stop, Connect, errors).")]
        public bool enableLogging = true;

        [Tooltip("Enable verbose logging (PONG, keep-alive, protocol details). Noisy — use for debugging only.")]
        public bool verboseLogging = false;

        // Last observed value of hapticDelaySeconds, captured at OnEnable and
        // refreshed inside OnValidate. Used to detect Play-mode latency edits
        // so the SDK can flush pending delay coroutines that captured the old
        // value (without this, stale Fire/Stop coroutines from before the edit
        // continue waiting on the previous delay, causing timing chaos / burst
        // delivery when the user is tuning latency live).
        [System.NonSerialized]
        private bool _hapticDelayTracked = false;
        [System.NonSerialized]
        private float _previousHapticDelaySeconds = 0f;

        private void OnEnable()
        {
            // Capture the initial value so the first OnValidate after a load
            // doesn't fire a spurious "changed" notification.
            _previousHapticDelaySeconds = hapticDelaySeconds;
            _hapticDelayTracked = true;
        }

        // Validate / clamp serialized fields. Inspector edits and asset import both
        // trigger this. We only enforce bounded fields that the inspector cannot
        // already constrain via [Range] / dropdown.
        private void OnValidate()
        {
            if (!string.IsNullOrEmpty(appName) && appName.Length > MaxAppNameLength)
            {
                Debug.LogWarning(
                    $"[Hapbeat] appName '{appName}' exceeds the {MaxAppNameLength}-char display limit; " +
                    $"truncated to '{appName.Substring(0, MaxAppNameLength)}'.", this);
                appName = appName.Substring(0, MaxAppNameLength);
            }

            // Detect Play-mode hapticDelaySeconds edits and notify the SDK so
            // it can flush pending delay coroutines. Only fires during Play to
            // avoid Editor-time noise (asset import / domain reload also call
            // OnValidate but don't represent a user-initiated edit).
            if (_hapticDelayTracked
                && Application.isPlaying
                && !Mathf.Approximately(_previousHapticDelaySeconds, hapticDelaySeconds))
            {
                _previousHapticDelaySeconds = hapticDelaySeconds;
                HapbeatManager.NotifyHapticDelayChanged();
            }
        }
    }
}
