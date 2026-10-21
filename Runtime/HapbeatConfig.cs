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

        [Tooltip("Interval in seconds between keep-alive ping messages. While any device " +
                 "uses the v2 stream format (firmware 0.5.0 and later), the SDK pings at " +
                 "least every 10 s regardless of this value: the device expires an idle " +
                 "stream lease 15 s after the last ping, and the first stream after a " +
                 "longer idle gap would otherwise be silent.")]
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

        [Header("Sending")]
        [Tooltip("Run the SDK without sending anything: no UDP socket is opened and no datagram " +
                 "(PLAY, STREAM, PING, CONNECT_STATUS, discovery broadcast) leaves this PC. " +
                 "Play / Fire / StreamAudioClip calls, triggers, logs, playback status and events " +
                 "still run as usual, so the game behaves the same; no device ever answers, so " +
                 "streams stay Deferred. Use it to record or test a scene without actuating a Hapbeat.\n\n" +
                 "The command-line argument " + NoNetworkCommandLineFlag + " turns it on for one run " +
                 "without editing this asset (Editor and player builds).")]
        public bool noNetwork = false;

        /// <summary>Command-line argument that forces <see cref="noNetwork"/> on for one run,
        /// whatever the asset says. Same name as the Unreal SDK's flag.</summary>
        public const string NoNetworkCommandLineFlag = "-HapbeatNoNetwork";

        private static int _noNetworkCommandLine = -1; // -1 = not read yet, 0 = absent, 1 = present

        /// <summary>True when this process was started with <see cref="NoNetworkCommandLineFlag"/>.</summary>
        public static bool IsNoNetworkCommandLine
        {
            get
            {
                if (_noNetworkCommandLine < 0)
                {
                    string[] args;
                    try { args = System.Environment.GetCommandLineArgs(); }
                    catch { args = null; }
                    _noNetworkCommandLine = HasNoNetworkFlag(args) ? 1 : 0;
                }
                return _noNetworkCommandLine == 1;
            }
        }

        /// <summary>Whether <paramref name="args"/> contains <see cref="NoNetworkCommandLineFlag"/>
        /// (case-insensitive). Pure, for tests.</summary>
        internal static bool HasNoNetworkFlag(string[] args)
        {
            if (args == null) return false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], NoNetworkCommandLineFlag, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Effective no-network state: <paramref name="config"/>'s <see cref="noNetwork"/>
        /// (null = default config) or the <see cref="NoNetworkCommandLineFlag"/> argument.</summary>
        public static bool ResolveNoNetwork(HapbeatConfig config) =>
            (config != null && config.noNetwork) || IsNoNetworkCommandLine;

        [Header("Debugging")]
        [Tooltip("Enable logging to the Unity console (Play, Stop, Connect, errors).")]
        public bool enableLogging = true;

        [Tooltip("Enable verbose logging (PONG, keep-alive, protocol details). Noisy — use for debugging only.")]
        public bool verboseLogging = false;

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
        }
    }
}
