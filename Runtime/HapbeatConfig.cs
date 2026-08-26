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

        // No [Header] here: the Hapbeat Settings window (the normal editing route)
        // draws its own "Override Addressing (this build)" section heading, and a
        // header attribute would render a second one above the PropertyFields.

        /// <summary>Lower bound for <see cref="buildOverridePlayer"/> / <see cref="buildOverrideGroup"/>
        /// (-1 = per-device). Clamped in OnValidate since the fields are drawn as plain
        /// int fields, not a [Range] slider.</summary>
        public const int MinBuildOverride = -1;

        /// <summary>Upper bound for <see cref="buildOverridePlayer"/> / <see cref="buildOverrideGroup"/>.
        /// Matches the device address range (player_1..99 / group_1..99).</summary>
        public const int MaxBuildOverride = 99;

        [Tooltip("Build-wide forced player number.\n\n" +
                 "  1-99 = force this player for the whole build (cannot be changed on the device —\n" +
                 "         the runtime panel / SetAddressOverride / PlayerPrefs are all ignored for this axis)\n" +
                 "  -1   = per-device (the runtime panel / SetAddressOverride / PlayerPrefs decide)\n\n" +
                 "Typical setup for running several demos at once: force 'group' in the build so each " +
                 "demo build only ever reaches its own devices, and leave 'player' at -1 so each " +
                 "headset can be paired with its own Hapbeat on site.")]
        public int buildOverridePlayer = -1;

        [Tooltip("Build-wide forced group number.\n\n" +
                 "  1-99 = force this group for the whole build (cannot be changed on the device —\n" +
                 "         the runtime panel / SetAddressOverride / PlayerPrefs are all ignored for this axis)\n" +
                 "  -1   = per-device (the runtime panel / SetAddressOverride / PlayerPrefs decide)\n\n" +
                 "Forcing the group here is the intended way to keep simultaneous demos from " +
                 "cross-talking: every build ships with its own group number baked in.")]
        public int buildOverrideGroup = -1;

        [Header("Behavior")]
        [Tooltip("Reopen the connection automatically if a socket error drops it " +
                 "(exponential backoff, 2 s doubling up to 30 s). Leave this on for " +
                 "unattended installations: without it a single transient network " +
                 "error silences the SDK until the application is restarted.")]
        public bool autoReconnect = true;

        [Tooltip("Interval in seconds between keep-alive ping messages.")]
        [Range(1f, 60f)]
        public float pingInterval = 5.0f;

        [Tooltip("Audio data the SDK keeps queued ahead of real-time playback while " +
                 "streaming a clip. Smaller values stop the haptic faster after " +
                 "StopStream() but increase risk of stutter on slow links. " +
                 "Range: 10–200 ms. Typical LAN: 30–60 ms. Default: 50 ms.")]
        [Range(0.01f, 0.2f)]
        public float streamSendAheadSeconds = 0.05f;

        [Tooltip("Send Play/Stop/StopAll directly (unicast) to each device already known from a " +
                 "PONG response, instead of UDP broadcast. As with addressed StreamClip delivery, Wi-Fi AP " +
                 "power-save (DTIM) batching can hold a broadcast frame for one beacon interval, " +
                 "showing up as ~100-300 ms of extra latency before a haptic fires; unicast avoids " +
                 "that batching (the device itself has modem-sleep disabled, so this is purely an " +
                 "AP-side effect). Falls back to broadcast automatically when no device has responded " +
                 "yet or when every known device's address mismatches the command's " +
                 "target -- the device applies the same target filter on receipt, so a broadcast " +
                 "can never actuate a device the target didn't address. Default: enabled.")]
        public bool commandUnicast = true;

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

            // buildOverridePlayer / buildOverrideGroup are drawn as plain int fields
            // (not a [Range] slider) so they line up with the Runtime Status window's
            // Player/Group row, so the bounds have to be enforced here instead.
            // 0 stays as typed: NormalizeOverride() already treats anything outside
            // 1..99 as "disabled" (-1), so it needs no special case.
            buildOverridePlayer = Mathf.Clamp(buildOverridePlayer, MinBuildOverride, MaxBuildOverride);
            buildOverrideGroup = Mathf.Clamp(buildOverrideGroup, MinBuildOverride, MaxBuildOverride);

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
