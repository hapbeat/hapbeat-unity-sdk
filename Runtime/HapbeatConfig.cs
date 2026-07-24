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

        [Header("App Info")]
        [Tooltip("Shown on the Hapbeat device display. Max 16 chars; the default app_name element (8x1) shows the first 8. Empty = use Application.productName.\n\n" +
                 "<p>/<g> are replaced with the current address-override player/group number ('-' when disabled) before sending.")]
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

        [Tooltip("Device-side jitter-buffer depth (ms) for CLIP (Wi-Fi UDP stream) playback, " +
                 "pushed to each known device via set_stream_buffer (TCP 7701) when a stream " +
                 "starts / a new device is discovered. 0 = device default (low-latency, " +
                 "re-primes on underrun — best for short haptic hits, don't send this setting). " +
                 ">0 = continuous mode (hold-decay + drift correction, no re-prime) — trades " +
                 "latency for far fewer dropouts on flaky Wi-Fi. Range 0–500 ms (contracts " +
                 "§4.20-pre). Typical: 30 ms.")]
        [Range(0, 500)]
        public int streamBufferMs = 30;

        [Header("Latency Compensation")]
        [Tooltip("Global delay (seconds) added to every Play / StreamClip call to match the audio output latency " +
                 "(e.g. Bluetooth headphones). Hapbeat haptics go out over UDP with very low latency (~10 ms), " +
                 "so on slow audio paths the haptic arrives before the sound. Use this offset to align them.\n\n" +
                 "Rule of thumb:\n" +
                 "  - Wired / USB DAC     → 0.00 (no compensation needed)\n" +
                 "  - Built-in speakers   → 0.02 – 0.05\n" +
                 "  - Bluetooth (aptX LL) → 0.03 – 0.05\n" +
                 "  - Bluetooth (SBC/AAC) → 0.15 – 0.20\n\n" +
                 "Each EventMap entry can add a ±0.2 s per-entry offset via delayOffsetSeconds.\n" +
                 "Default 0 (no delay).")]
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
