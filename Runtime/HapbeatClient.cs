using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Hapbeat
{
    /// <summary>
    /// Outcome of a PLAY/STOP/STOP_ALL send (see <see cref="HapbeatClient.SendPlay"/>/
    /// <see cref="HapbeatClient.SendStop"/>/<see cref="HapbeatClient.SendStopAll"/> and
    /// the routing they share in <c>SendCommandRaw</c>). Returned so callers can tell
    /// which path a command actually took (diagnostics / tests) without
    /// <see cref="HapbeatClient"/> having to read config or log on their behalf.
    /// </summary>
    public enum CommandSendResult
    {
        /// <summary>Sent via plain broadcast (or Bridge/ESP-NOW unicast to the bridge
        /// host) — commandUnicast is disabled, the client isn't in broadcast mode, or
        /// there was no live device to unicast to (none PONGed recently, or none whose
        /// reported address matched the target).</summary>
        Broadcast,

        /// <summary>Sent via unicast to one or more known devices — either their
        /// reported address matched the resolved target, or their address is unknown
        /// and was therefore kept (fail-open).</summary>
        Unicast,
    }

    /// <summary>
    /// Internal UDP client for communicating with Hapbeat devices or Bridge.
    /// Supports broadcast sending (standard) and unicast sending (Bridge mode).
    /// Receive runs on a background thread; callbacks are queued for main-thread dispatch.
    /// </summary>
    public class HapbeatClient : IDisposable
    {
        /// <summary>Invoked on main thread when a PONG response is received.</summary>
        public event Action<long, long> OnPong; // (rttUs, serverTimeUs)

        /// <summary>
        /// Invoked on main thread for each PONG, with the source endpoint.
        /// Use this to track per-device liveness (broadcast may yield multiple
        /// PONGs per PING — one per responsive device).
        /// </summary>
        public event Action<IPEndPoint, long> OnPongFrom; // (sender, rttUs)

        /// <summary>Invoked on main thread when an ERROR response is received.</summary>
        public event Action<ushort, string> OnError; // (errorCode, message)

        /// <summary>Invoked on main thread when connection state changes.</summary>
        public event Action<bool> OnConnectionStateChanged; // (isConnected)

        /// <summary>Whether the client is currently ready to send/receive.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>Whether the client is in broadcast mode.</summary>
        public bool IsBroadcast { get; private set; }

        private UdpClient _udpClient;
        private IPEndPoint _targetEndPoint;
        private Thread _receiveThread;
        private volatile bool _isRunning;
        private ushort _sequenceNumber;
        private readonly object _seqLock = new object();
        private readonly Stopwatch _stopwatch;

        // Forced player/group applied to every outgoing target string. -1 = disabled.
        // Pushed from HapbeatManager (owner of HapbeatConfig / PlayerPrefs); the
        // client itself never reads config or PlayerPrefs directly.
        private int _overridePlayer = -1;
        private int _overrideGroup = -1;

        // Whether SendPlay/SendStop/SendStopAll should attempt unicast to already-
        // known devices instead of broadcasting (see SendCommandRaw) — same
        // Wi-Fi AP DTIM power-save rationale as streamUnicast, applied to one-shot
        // commands instead of a stream session. Defaults to true so a HapbeatClient
        // used standalone (no HapbeatManager) still gets the low-latency behavior.
        // Pushed from HapbeatConfig.commandUnicast by HapbeatManager (see
        // SetCommandUnicast) — same separation of concerns as
        // _overridePlayer/_overrideGroup above.
        private bool _commandUnicastEnabled = true;

        // How long a device stays in _knownDeviceIps after its last PONG. Keeps the
        // command-unicast destination set aligned with HapbeatManager's alive-device
        // window (pingInterval x 3, min 5 s) — the same window that seeds the stream
        // unicast targets. Without expiry the set only ever grows: a powered-off
        // device would keep absorbing datagrams forever AND keep the set non-empty,
        // permanently suppressing the broadcast fallback for a LAN with no live
        // device left. Pushed from HapbeatManager (see SetCommandUnicast); the
        // default matches the config default so a standalone client behaves the same.
        private float _knownDeviceTtlSeconds = 15f;

        // Windows-only socket ioctl that stops an ICMP "port unreachable" (drawn by
        // a unicast send to a device that is off/rebooting) from surfacing as a
        // WSAECONNRESET on the NEXT Receive() call. See SuppressUdpConnReset.
        private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        // One-shot guard so a recoverable receive error logs once per connection
        // instead of once per stale destination per command.
        private bool _loggedRecoverableReceiveError;

        // Same idea for the send path, but it matters far more here: a link that
        // fails keeps failing, and now that a send error no longer tears the
        // connection down, nothing stops the retries. PING/CONNECT_STATUS repeat
        // every pingInterval and stream chunks roughly 100x/s per destination, so
        // an unguarded warning would bury the very log an operator needs to read.
        // Written from the main thread and the stream mixer thread — volatile so
        // the "already logged" state is not cached per core.
        private volatile bool _loggedSendError;

        // Broadcast destinations, one per local IPv4 subnet plus the limited
        // broadcast catch-all (see BroadcastRoute / EnumerateBroadcastRoutes).
        // Discovery fans out to all of them; playback uses _lockedRoute once a
        // device has answered, so no device ever receives the same PLAY twice.
        private List<BroadcastRoute> _broadcastRoutes = new List<BroadcastRoute>();

        // The route a device actually replied on. Null until the first PONG.
        // Written from the receive thread, read from the main and mixer threads.
        private volatile BroadcastRoute _lockedRoute;

        // Unicast destinations for STREAM_BEGIN/DATA/END. Three states:
        //   null            -> no unicast snapshot taken (or explicitly cleared):
        //                      fall back to broadcast, same as before target
        //                      filtering existed.
        //   empty array     -> a snapshot WAS taken and every known device's
        //                      address failed to match the session target
        //                      (SetStreamUnicastTargets filtered all of them
        //                      out): send nowhere this session, do NOT fall
        //                      back to broadcast (that would defeat the point
        //                      of filtering — see SendStreamRaw).
        //   non-empty array -> unicast to exactly these endpoints.
        // Snapshotted once per stream session by HapbeatManager (see
        // SetStreamUnicastTargets) from known device PONG endpoints. Read from
        // the background stream mixer thread (SendStreamData) without locking —
        // array reference assignment is atomic, and the array itself is never
        // mutated in place after being published, only replaced.
        private volatile IPEndPoint[] _streamUnicastTargets;

        // Last known device-addressing address string per sender IP, learned from
        // the PONG extension fields (device-addressing.md §5.4). Written from the
        // background receive thread (HandlePong); read from the main thread
        // (GetKnownDeviceAddress, called by SetStreamUnicastTargets). A device with
        // no entry here is "unknown" — callers must fail open (treat as matching)
        // rather than as a hard mismatch, since firmware predating this extension,
        // or a PONG that hasn't arrived yet, shouldn't silently lose its stream.
        private readonly ConcurrentDictionary<IPAddress, string> _deviceAddresses =
            new ConcurrentDictionary<IPAddress, string>();

        // Device IPs that have PONGed recently, mapped to the local timestamp (us,
        // same clock as GetLocalTimestampUs) of that most recent PONG. Recorded
        // regardless of whether the PONG reported an address (unlike
        // _deviceAddresses above, which only has an entry when the address
        // extension was present). Used by SendCommandRaw (PLAY/STOP/STOP_ALL
        // unicast routing) so an address-unknown device (e.g. older firmware
        // without the extension) still receives commands via unicast instead of
        // being silently dropped — the same fail-open philosophy
        // SetStreamUnicastTargets applies for streaming. Entries older than
        // _knownDeviceTtlSeconds are dropped on the next send (see SendCommandRaw)
        // so the set tracks *live* devices rather than growing forever. Written
        // from the background receive thread (HandlePong); read/pruned from the
        // main thread (SendCommandRaw).
        private readonly ConcurrentDictionary<IPAddress, long> _knownDeviceIps =
            new ConcurrentDictionary<IPAddress, long>();

        // Queue for dispatching callbacks to the main thread
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        // Track ping timestamps for RTT calculation
        private readonly ConcurrentDictionary<ushort, long> _pendingPings =
            new ConcurrentDictionary<ushort, long>();

        private bool _disposed;

        public HapbeatClient()
        {
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Open for UDP broadcast sending (standard Wi-Fi UDP mode).
        /// Commands are sent to all devices on the LAN; each device filters by group ID.
        /// </summary>
        /// <param name="port">Target UDP port (default: 7700).</param>
        public void OpenBroadcast(int port)
        {
            // Unconditional: Disconnect() guards on the resources, so this also
            // clears a half-dead session (socket still open, IsConnected already
            // false after a socket error) that would otherwise leak the old socket
            // and leave its receive thread polling a field we replace below.
            Disconnect();

            try
            {
                _udpClient = new UdpClient(0); // bind to OS-assigned local port
                _udpClient.EnableBroadcast = true;
                SuppressUdpConnReset(_udpClient);
                _targetEndPoint = new IPEndPoint(IPAddress.Broadcast, port);
                _broadcastRoutes = EnumerateBroadcastRoutes(port);
                _lockedRoute = null;
                IsBroadcast = true;

                StartReceiveLoop();
                IsConnected = true;
                EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(true));
            }
            catch (Exception ex)
            {
                IsConnected = false;
                IsBroadcast = false;
                throw new InvalidOperationException(
                    $"Failed to open broadcast on port {port}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Connect to a specific host via UDP unicast (Bridge / ESP-NOW mode).
        /// </summary>
        /// <param name="host">Bridge hostname or IP address.</param>
        /// <param name="port">UDP port.</param>
        public void Connect(string host, int port)
        {
            // Unconditional — see OpenBroadcast for why.
            Disconnect();

            try
            {
                _udpClient = new UdpClient(0); // bind to OS-assigned local port
                SuppressUdpConnReset(_udpClient);
                _targetEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
                IsBroadcast = false;

                StartReceiveLoop();
                IsConnected = true;
                EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(true));
            }
            catch (Exception ex)
            {
                IsConnected = false;
                throw new InvalidOperationException(
                    $"Failed to connect to {host}:{port}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Disconnect and release resources.
        /// </summary>
        public void Disconnect()
        {
            // Resource-based guard, deliberately NOT IsConnected: a socket error
            // can flag the connection down (HandleDisconnection) without closing
            // anything, and returning early in that half-dead state would strand
            // the open socket and its live receive thread. The next OpenBroadcast
            // would then replace _udpClient while that thread still polls the
            // field. Cleaning up whatever actually exists makes this safe to call
            // from HapbeatManager's reconnect path.
            if (_udpClient == null && _receiveThread == null)
                return;

            // HandleDisconnection may already have flagged — and announced — the
            // drop without closing anything. Only announce a transition that
            // actually happens here, so cleaning up a half-dead session does not
            // raise a second OnConnectionStateChanged(false) for the same event.
            bool wasConnected = IsConnected;

            _isRunning = false;
            IsConnected = false;
            IsBroadcast = false;

            try
            {
                _udpClient?.Close();
            }
            catch
            {
                // Suppress exceptions during cleanup
            }

            if (_receiveThread != null && _receiveThread.IsAlive)
            {
                _receiveThread.Join(1000);
            }

            _udpClient = null;
            _receiveThread = null;
            _pendingPings.Clear();

            // Device knowledge is per-connection: after a reconnect (Wi-Fi change,
            // AP switch, network hand-off) the previous IPs may belong to a
            // different network entirely. Leaving them behind would make
            // SendCommandRaw unicast every command at unreachable hosts while the
            // non-empty set suppresses the broadcast fallback — i.e. total silence
            // on the new network until a fresh PONG lands. Same reasoning for the
            // stream snapshot, whose endpoints would otherwise outlive the socket
            // they were resolved for.
            _knownDeviceIps.Clear();
            _deviceAddresses.Clear();
            _streamUnicastTargets = null;
            // Routes belong to the network we were on: after a reconnect the host
            // may well have different interfaces (docking, VPN up, Wi-Fi switch).
            _broadcastRoutes = new List<BroadcastRoute>();
            _lockedRoute = null;
            _loggedRecoverableReceiveError = false;
            _loggedSendError = false;

            if (wasConnected)
                EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(false));
        }

        /// <summary>
        /// Set the forced player/group applied to every outgoing target string
        /// (Play/Stop/StopAll/StreamBegin) via <see cref="ResolveTarget(string)"/>.
        /// Pass -1 to disable either axis. Values outside 1..99 are normalized
        /// to -1 (disabled) — see <see cref="NormalizeOverride"/>.
        /// </summary>
        public void SetAddressOverride(int player, int group)
        {
            _overridePlayer = NormalizeOverride(player);
            _overrideGroup = NormalizeOverride(group);
        }

        /// <summary>
        /// Enable/disable unicast routing for <see cref="SendPlay"/>/<see cref="SendStop"/>/
        /// <see cref="SendStopAll"/> (see <see cref="SendCommandRaw"/>), and set how long a
        /// device stays a unicast destination after its last PONG. Defaults: enabled, 15 s.
        /// Pushed from <c>HapbeatConfig.commandUnicast</c> by <c>HapbeatManager</c> — the
        /// client itself never reads config directly (see <see cref="_overridePlayer"/>).
        /// </summary>
        /// <param name="enabled">Whether one-shot commands may unicast at all.</param>
        /// <param name="knownDeviceTtlSeconds">Liveness window for the unicast destination
        /// set. Should match the caller's alive-device window (HapbeatManager uses
        /// pingInterval x 3, min 5 s) so commands and streams target the same devices.</param>
        public void SetCommandUnicast(bool enabled, float knownDeviceTtlSeconds)
        {
            _commandUnicastEnabled = enabled;
            if (knownDeviceTtlSeconds > 0f)
                _knownDeviceTtlSeconds = knownDeviceTtlSeconds;
        }

        /// <summary>
        /// Sets (or clears) the unicast destination list used by STREAM_BEGIN/DATA/END
        /// while broadcast mode is active. Pass null or an empty collection to revert
        /// streaming to broadcast. Intended to be called once per stream session, from
        /// the main thread, before the session's first STREAM_BEGIN — see
        /// HapbeatManager.StreamAudioClip / StopStream.
        /// <para>
        /// <paramref name="deviceIps"/> is further filtered down to the devices whose
        /// last-known address (from PONG, see <see cref="GetKnownDeviceAddress"/>)
        /// matches <paramref name="target"/> per <see cref="AddressMatches"/> — this is
        /// what keeps a 1-person-multiple-device / many-simultaneous-pairs LAN from
        /// fanning every stream chunk out to everyone else's devices too. A device
        /// with no known address yet (no PONG parsed for it, or an older firmware
        /// that doesn't send the address extension) is kept in rather than dropped —
        /// fail-open, since firmware still applies its own target filter on receipt,
        /// so worst case is an extra unicast packet, never a silently lost stream.
        /// Pass <c>null</c>/<c>""</c> for <paramref name="target"/> (the default) to
        /// disable filtering entirely (matches everyone, same as before this existed).
        /// </para>
        /// <para>
        /// Devices that join after the snapshot is taken (e.g. their first PONG arrives
        /// mid-session) are not added retroactively; they'll be picked up starting with
        /// the next session. This keeps the hot path (SendStreamData, called every
        /// ~10 ms from the background mixer thread) lock-free.
        /// </para>
        /// </summary>
        /// <returns>
        /// The number of endpoints actually targeted this session — a non-negative
        /// count when a snapshot was taken (0 if every candidate's known address
        /// failed <paramref name="target"/>'s match), or -1 when no snapshot was
        /// taken at all (<paramref name="deviceIps"/> was null/empty) and streaming
        /// falls back to broadcast. Callers (see <see cref="HapbeatManager"/>) use
        /// this to log the actual outcome instead of guessing from the input count.
        /// </returns>
        /// <summary>
        /// Devices that answered a PING recently enough to still count as live —
        /// the same window command unicast uses (see <c>_knownDeviceTtlSeconds</c>).
        ///
        /// Exposed for callers that have no liveness bookkeeping of their own: the
        /// Editor test-play transport drives a bare client without a
        /// <see cref="HapbeatManager"/>, and would otherwise have to duplicate the
        /// PONG tracking just to seed <see cref="SetStreamUnicastTargets"/>.
        /// </summary>
        public List<IPAddress> GetKnownDeviceIps()
        {
            long nowUs = GetLocalTimestampUs();
            long ttlUs = (long)(_knownDeviceTtlSeconds * 1_000_000f);

            var live = new List<IPAddress>();
            foreach (var kv in _knownDeviceIps)
            {
                if (nowUs - kv.Value <= ttlUs)
                    live.Add(kv.Key);
            }
            return live;
        }

        public int SetStreamUnicastTargets(IReadOnlyCollection<IPAddress> deviceIps, string target = null)
        {
            if (deviceIps == null || deviceIps.Count == 0)
            {
                _streamUnicastTargets = null;
                return -1;
            }

            int port = _targetEndPoint != null ? _targetEndPoint.Port : 0;

            var filtered = new List<IPAddress>(deviceIps.Count);
            foreach (var ip in deviceIps)
            {
                string knownAddress = GetKnownDeviceAddress(ip);
                // Fail open: unknown address => keep. Known address => must match.
                if (knownAddress == null || AddressMatches(target, knownAddress))
                    filtered.Add(ip);
            }

            if (filtered.Count == 0)
            {
                // Every candidate had a *known* address and none matched target —
                // an explicit "nobody" result, not "we don't have info yet". Use the
                // empty-array sentinel so SendStreamRaw skips the session entirely
                // instead of falling back to broadcasting to unrelated devices.
                _streamUnicastTargets = Array.Empty<IPEndPoint>();
                return 0;
            }

            var targets = new IPEndPoint[filtered.Count];
            for (int i = 0; i < filtered.Count; i++)
                targets[i] = new IPEndPoint(filtered[i], port);
            _streamUnicastTargets = targets;
            return targets.Length;
        }

        /// <summary>
        /// Last known device-addressing address string for <paramref name="ip"/>, as
        /// reported in its most recent parsed PONG (device-addressing.md §5.4). Null
        /// if unknown — no PONG received/parsed from this IP yet, or its firmware
        /// predates the address extension field. See <see cref="SetStreamUnicastTargets"/>
        /// for how callers should treat "unknown" (fail open, not a mismatch).
        /// </summary>
        public string GetKnownDeviceAddress(IPAddress ip)
        {
            return _deviceAddresses.TryGetValue(ip, out var address) ? address : null;
        }

        /// <summary>
        /// Device-addressing target/address match, mirroring firmware's
        /// <c>addressMatch()</c> (hapbeat-device-firmware/src/address_match.cpp) and
        /// the contracts pseudocode (device-addressing.md §4.3). Firmware is the
        /// authority where the two differ, since it decides what actually plays:
        /// <list type="bullet">
        /// <item>An empty/null <paramref name="target"/> matches every address.</item>
        /// <item>Both strings are split on <c>/</c> and compared segment-by-segment,
        /// left to right.</item>
        /// <item>A <c>*</c> target segment matches any single address segment.</item>
        /// <item>If <paramref name="target"/> has fewer segments than
        /// <paramref name="deviceAddress"/>, matching the segments present is enough
        /// (front-match / prefix match) — extra address segments (e.g. an omitted
        /// group) don't cause a mismatch.</item>
        /// <item>If <paramref name="target"/> has *more* segments than
        /// <paramref name="deviceAddress"/>, it's a mismatch (target too specific for
        /// this device's address).</item>
        /// <item>A single trailing <c>/</c> on <paramref name="target"/> is ignored
        /// ("player_1/" == "player_1"), matching firmware's pointer walk. The §4.3
        /// pseudocode's naive split would mismatch here; we follow firmware so the
        /// SDK never filters out a device that would have accepted the packet.</item>
        /// </list>
        /// Pure, UnityEngine-independent — see Tests/Runtime/AddressMatchesTests.cs
        /// (transcribed from device-addressing.md §4.2's example table).
        /// </summary>
        public static bool AddressMatches(string target, string deviceAddress)
        {
            if (string.IsNullOrEmpty(target))
                return true;

            string[] targetSegments = target.Split('/');
            string[] addressSegments = (deviceAddress ?? string.Empty).Split('/');

            // A single trailing '/' terminates the target rather than adding an
            // empty segment: firmware's loop advances past the separator and then
            // exits on `while (*tp)`, so "player_1/" behaves exactly like
            // "player_1". Splitting naively would compare a "" segment against the
            // device's next real segment and mismatch — i.e. the SDK would drop a
            // device the firmware WOULD have accepted, silently losing its stream.
            // Only the last empty segment is dropped, and only once ("a//" really
            // does compare an empty segment in firmware, and still mismatches).
            int targetCount = targetSegments.Length;
            if (targetCount > 1 && targetSegments[targetCount - 1].Length == 0)
                targetCount--;

            for (int i = 0; i < targetCount; i++)
            {
                if (i >= addressSegments.Length)
                    return false; // target longer than address = mismatch

                if (targetSegments[i] != "*" && targetSegments[i] != addressSegments[i])
                    return false;
            }

            return true; // front-match or exact match
        }

        /// <summary>
        /// Clamp an override value to the valid device-addressing range (1..99).
        /// Anything outside that range (including the disabled sentinel -1) is
        /// normalized to -1 ("disabled").
        /// </summary>
        public static int NormalizeOverride(int value)
        {
            return (value >= 1 && value <= 99) ? value : -1;
        }

        /// <summary>
        /// Resolve a target string against forced player/group overrides. Pure,
        /// UnityEngine-independent function so it can be unit tested directly
        /// (see Tests/Runtime/ResolveTargetTests.cs). Both overrides disabled
        /// (&lt; 1) returns <paramref name="target"/> completely unchanged
        /// (including null) — this is what keeps existing projects' behavior
        /// byte-for-byte identical when the feature isn't used.
        /// <para>
        /// Grammar: <c>[prefix/] player_{N} / {position} [/group_{M}]</c>
        /// — see hapbeat-contracts/specs/device-addressing.md §2.
        /// </para>
        /// </summary>
        /// <param name="target">Original EventMap/API target string. May be null.</param>
        /// <param name="overridePlayer">Forced player number, or &lt; 1 to leave the player slot alone.</param>
        /// <param name="overrideGroup">Forced group number, or &lt; 1 to leave the group slot alone.</param>
        public static string ResolveTarget(string target, int overridePlayer, int overrideGroup)
        {
            if (overridePlayer < 1 && overrideGroup < 1)
                return target; // both disabled: full passthrough (BuildXxxPayload treats null as "")

            List<string> segs = new List<string>((target ?? string.Empty).Split('/'));
            segs.RemoveAll(string.IsNullOrEmpty);

            if (overridePlayer >= 1)
            {
                string playerSeg = "player_" + overridePlayer;
                int i = segs.FindIndex(s => s.StartsWith("player_", StringComparison.Ordinal));
                if (i >= 0)
                {
                    segs[i] = playerSeg;
                }
                else
                {
                    int j = segs.FindIndex(s => s.StartsWith("pos_", StringComparison.Ordinal));
                    if (j > 0)
                        segs[j - 1] = playerSeg; // replace the placeholder segment (e.g. "*") right before position
                    else
                        segs.Insert(0, playerSeg); // j == 0 (position at front) or j == -1 (no position segment)
                }
            }

            if (overrideGroup >= 1)
            {
                string groupSeg = "group_" + overrideGroup;
                int k = segs.FindIndex(s => s.StartsWith("group_", StringComparison.Ordinal));
                if (k >= 0)
                {
                    segs[k] = groupSeg;
                }
                else
                {
                    // Firmware/spec matching is positional (device-addressing.md §2):
                    // the i-th target segment is compared against the i-th address
                    // segment only, with "*" consuming exactly one slot. group_
                    // must therefore land in its grammar slot (immediately after
                    // {position}); a naive Add() at the end lands it in whatever
                    // slot happens to be next, so firmware never matches.
                    int posIdx = segs.FindIndex(s => s.StartsWith("pos_", StringComparison.Ordinal));
                    if (posIdx >= 0)
                    {
                        segs.Insert(posIdx + 1, groupSeg);
                    }
                    else
                    {
                        // No explicit position segment. Locate the player slot:
                        // an explicit player_ segment, or a leading bare "*"
                        // acting as the player wildcard.
                        int playerIdx = segs.FindIndex(s => s.StartsWith("player_", StringComparison.Ordinal));
                        if (playerIdx < 0 && segs.Count > 0 && segs[0] == "*")
                            playerIdx = 0; // leading wildcard occupies the player slot

                        if (playerIdx >= 0)
                        {
                            // Position slot is the segment right after the player
                            // slot. Only pad a "*" placeholder when that slot is
                            // actually empty — if the target already occupies it
                            // (e.g. a bare "*" that the player-override step left
                            // in the position slot for a target like "*"), reuse
                            // it so group_ stays in the 3rd slot instead of being
                            // pushed to a 4th, which would make the target longer
                            // than the device address and break the positional
                            // match entirely.
                            int posSlot = playerIdx + 1;
                            if (posSlot >= segs.Count)
                                segs.Insert(posSlot, "*"); // no position segment yet — pad it
                            segs.Insert(posSlot + 1, groupSeg);
                        }
                        else
                        {
                            // Everything present (if anything) is a free prefix
                            // with no player/position slot. Append player and
                            // position placeholders, then group, so group stays
                            // after position and the prefix is preserved ahead
                            // of it (e.g. "" -> "*/*/group_M",
                            // "red" -> "red/*/*/group_M").
                            segs.Add("*");
                            segs.Add("*");
                            segs.Add(groupSeg);
                        }
                    }
                }
            }

            return string.Join("/", segs);
        }

        /// <summary>Instance wrapper around <see cref="ResolveTarget(string, int, int)"/>
        /// using the overrides pushed via <see cref="SetAddressOverride"/>.</summary>
        private string ResolveTarget(string target)
        {
            return ResolveTarget(target, _overridePlayer, _overrideGroup);
        }

        /// <summary>Send a PLAY command. <paramref name="target"/> is the device-addressing
        /// target string ("" = broadcast). Unicasts to known matching devices instead of
        /// broadcasting when <c>commandUnicast</c> is enabled — see <see cref="SendCommandRaw"/>
        /// and <see cref="CommandSendResult"/> for the exact routing/fallback rules.</summary>
        public CommandSendResult SendPlay(string eventId, long targetTimeUs, float gain, string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildPlayPayload(eventId, targetTimeUs, gain, target);
            return SendCommandPacket(HapbeatProtocol.CMD_PLAY, payload, target);
        }

        /// <summary>Send a STOP command. <paramref name="target"/> is the device-addressing
        /// target string ("" = broadcast). See <see cref="SendPlay"/> for the unicast routing
        /// this shares.</summary>
        public CommandSendResult SendStop(string eventId, string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildStopPayload(eventId, target);
            return SendCommandPacket(HapbeatProtocol.CMD_STOP, payload, target);
        }

        /// <summary>Send a STOP_ALL command. <paramref name="target"/> is the device-addressing
        /// target string ("" = broadcast). See <see cref="SendPlay"/> for the unicast routing
        /// this shares.</summary>
        public CommandSendResult SendStopAll(string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildStopAllPayload(target);
            return SendCommandPacket(HapbeatProtocol.CMD_STOP_ALL, payload, target);
        }

        /// <summary>
        /// Send a CONNECT_STATUS command so the device can show connection state on display/LED.
        /// </summary>
        public void SendConnectStatus(bool connected, byte group, string appName = "", string deviceName = "")
        {
            byte[] payload = HapbeatProtocol.BuildConnectStatusPayload(connected, group, appName, deviceName);
            // Idempotent display state, so the discovery fan-out is safe here and
            // gets the device out of "app not connected" without waiting for a PONG.
            SendDiscoveryPacket(HapbeatProtocol.CMD_CONNECT_STATUS, payload);
        }

        /// <summary>
        /// Send a STREAM_BEGIN command to start audio streaming.
        /// </summary>
        public void SendStreamBegin(ushort sampleRate, byte channels, byte format,
            uint totalSamples, float gain, string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildStreamBeginPayload(
                sampleRate, channels, format, totalSamples, gain, target);
            SendStreamPacket(HapbeatProtocol.CMD_STREAM_BEGIN, payload);
        }

        /// <summary>
        /// Send a STREAM_DATA chunk. Uses larger MTU-safe packet size.
        /// </summary>
        public void SendStreamData(uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
        {
            if (!IsConnected || _udpClient == null) return;
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildStreamDataPacket(seq, byteOffset, audioData, dataOffset, dataLength);
            SendStreamRaw(packet);
        }

        /// <summary>
        /// Send a STREAM_END command to signal streaming completion.
        /// </summary>
        public void SendStreamEnd()
        {
            SendStreamPacket(HapbeatProtocol.CMD_STREAM_END, Array.Empty<byte>());
        }

        /// <summary>
        /// Send a PING command for keep-alive and time synchronization.
        /// </summary>
        /// <returns>The sequence number of the ping packet.</returns>
        public ushort SendPing()
        {
            long timestampUs = GetLocalTimestampUs();
            byte[] payload = HapbeatProtocol.BuildPingPayload(timestampUs);
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_PING, seq, payload);

            _pendingPings[seq] = timestampUs;
            // Fans out until a device answers — this is what finds a Hapbeat the
            // limited broadcast cannot reach on a multi-homed host.
            SendDiscoveryRaw(packet);
            return seq;
        }

        /// <summary>
        /// Process queued callbacks on the main thread. Call this from Update().
        /// </summary>
        public void DispatchMainThreadCallbacks()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogException(ex);
                }
            }
        }

        /// <summary>
        /// Get the current local timestamp in microseconds using a high-resolution timer.
        /// </summary>
        public long GetLocalTimestampUs()
        {
            return _stopwatch.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Disconnect();
        }

        #region Private Methods

        private void StartReceiveLoop()
        {
            _isRunning = true;
            _receiveThread = new Thread(ReceiveLoop)
            {
                Name = "HapbeatReceive",
                IsBackground = true
            };
            _receiveThread.Start();
        }

        private void SendPacket(byte commandType, byte[] payload)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(commandType, seq, payload);
            SendRaw(packet);
        }

        private void SendDiscoveryPacket(byte commandType, byte[] payload)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(commandType, seq, payload);
            SendDiscoveryRaw(packet);
        }

        /// <summary>
        /// Send on every candidate broadcast destination.
        ///
        /// Reserved for PING and CONNECT_STATUS: both are idempotent, so a device
        /// reachable on two of them simply gets the message twice with no visible
        /// effect — whereas duplicating PLAY would fire the haptic twice on firmware
        /// that predates seq de-duplication. This fan-out is what lets discovery
        /// reach a device the limited broadcast never gets to, and the resulting
        /// PONG is what lets <see cref="LockRouteFor"/> pin playback to the right
        /// subnet.
        /// </summary>
        private void SendDiscoveryRaw(byte[] data)
        {
            UdpClient client = _udpClient;
            if (!IsConnected || client == null)
                return;

            List<BroadcastRoute> routes = _broadcastRoutes;
            if (!IsBroadcast || routes == null || routes.Count == 0)
            {
                // Bridge mode, or nothing enumerated: one fixed destination.
                SendRaw(data);
                return;
            }

            // Already pinned to a subnet — no reason to keep probing the others.
            BroadcastRoute locked = _lockedRoute;
            if (locked != null)
            {
                SendRaw(data);
                return;
            }

            for (int i = 0; i < routes.Count; i++)
            {
                try
                {
                    client.Send(data, data.Length, routes[i].EndPoint);
                    NoteSendSucceeded();
                }
                catch (SocketException ex)
                {
                    NoteSendFailed($"Discovery send to {routes[i].EndPoint.Address}",
                                   ex.SocketErrorCode, ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    HandleDisconnection();
                    return;
                }
            }
        }

        /// <summary>
        /// One address the SDK can broadcast to.
        ///
        /// The limited broadcast address (255.255.255.255) leaves a multi-homed host
        /// through the single interface with the lowest metric. On a machine with
        /// Hyper-V / WSL2 / Docker that is often an always-up virtual switch with no
        /// Hapbeat behind it — and no Ethernet cable is needed for that to happen,
        /// which is why the symptom looks nothing like "multi-homed". A
        /// subnet-directed address (192.168.0.255) instead resolves through the
        /// directly-connected route for that subnet, so the metric never applies.
        /// That is also why hapbeat-helper kept working on such a host: it finds
        /// devices over mDNS and then unicasts, which resolves the same way.
        /// </summary>
        private sealed class BroadcastRoute
        {
            public IPEndPoint EndPoint;
            public uint Network;   // host byte order, already masked
            public uint Mask;      // host byte order; unused for the limited route
            public bool IsLimited;

            /// <summary>Whether <paramref name="address"/> sits on this subnet.</summary>
            public bool Contains(IPAddress address)
            {
                if (IsLimited || Mask == 0 || address == null
                    || address.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }
                return (ToUInt32(address) & Mask) == Network;
            }

            public static uint ToUInt32(IPAddress address)
            {
                byte[] b = address.GetAddressBytes();
                return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            }

            public static IPAddress ToAddress(uint value)
            {
                return new IPAddress(new[]
                {
                    (byte)(value >> 24), (byte)(value >> 16),
                    (byte)(value >> 8),  (byte)value,
                });
            }
        }

        /// <summary>
        /// Build one destination per local IPv4 subnet, plus the limited broadcast
        /// address as a catch-all.
        ///
        /// The broadcast address is derived from each interface's own mask rather
        /// than assumed to end in .255: a /16 broadcasts to x.y.255.255 and a /25 to
        /// x.y.z.127, and the subnet itself is whatever the router hands out
        /// (192.168.0.x, 192.168.11.x, 10.x.x.x, …). Deduplicated by address, since
        /// two interfaces on one subnet would otherwise double-deliver every packet.
        /// </summary>
        private static List<BroadcastRoute> EnumerateBroadcastRoutes(int port)
        {
            var routes = new List<BroadcastRoute>();
            var seen = new HashSet<string>();

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (UnicastIPAddressInformation info
                             in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        IPAddress mask;
                        try
                        {
                            mask = info.IPv4Mask;
                        }
                        catch (NotImplementedException)
                        {
                            // Some Unity player platforms don't surface the mask.
                            // The limited broadcast added below still covers them.
                            continue;
                        }
                        if (mask == null)
                            continue;

                        uint ip = BroadcastRoute.ToUInt32(info.Address);
                        uint m = BroadcastRoute.ToUInt32(mask);
                        if (m == 0)
                            continue;

                        IPAddress broadcast = BroadcastRoute.ToAddress((ip & m) | ~m);
                        if (!seen.Add(broadcast.ToString()))
                            continue;

                        routes.Add(new BroadcastRoute
                        {
                            EndPoint = new IPEndPoint(broadcast, port),
                            Network = ip & m,
                            Mask = m,
                            IsLimited = false,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // Best-effort: a platform that restricts interface enumeration still
                // works through the limited broadcast appended below.
                UnityEngine.Debug.LogWarning(
                    $"[Hapbeat] Could not enumerate network interfaces: {ex.Message}. " +
                    "Falling back to limited broadcast only.");
            }

            // Always keep the original behaviour available: SoftAP setups, unusual
            // masks and platforms without interface data all still reach devices
            // this way, and it is the only route on a single-NIC host anyway.
            routes.Add(new BroadcastRoute
            {
                EndPoint = new IPEndPoint(IPAddress.Broadcast, port),
                IsLimited = true,
            });

            return routes;
        }

        /// <summary>
        /// Remember which subnet a device answered on, so playback stops going out
        /// as a limited broadcast that may never reach it. First reply wins; the
        /// lock is dropped with the connection.
        /// </summary>
        private void LockRouteFor(IPAddress deviceAddress)
        {
            if (!IsBroadcast || _lockedRoute != null || deviceAddress == null)
                return;

            List<BroadcastRoute> routes = _broadcastRoutes;
            if (routes == null)
                return;

            for (int i = 0; i < routes.Count; i++)
            {
                if (!routes[i].Contains(deviceAddress))
                    continue;

                _lockedRoute = routes[i];
                UnityEngine.Debug.Log(
                    $"[Hapbeat] Broadcasting to {routes[i].EndPoint.Address} " +
                    $"(a device answered from {deviceAddress}).");
                return;
            }
        }

        /// <summary>
        /// Report a failed send, at most once per outage. Silence is preferable to
        /// a flood here (see <see cref="_loggedSendError"/>), but the flag is
        /// cleared again by <see cref="NoteSendSucceeded"/> so a second, unrelated
        /// incident later in the same session is still reported.
        /// </summary>
        private void NoteSendFailed(string what, SocketError code, string message)
        {
            if (_loggedSendError)
                return;

            _loggedSendError = true;
            UnityEngine.Debug.LogWarning(
                $"[Hapbeat] {what} failed ({code}): {message}. Keeping the socket " +
                "open; further send errors are silenced until sending recovers.");
        }

        /// <summary>
        /// Note that sending works again, and say so once. Without this line an
        /// unattended installation's log shows when haptics broke but never when
        /// (or whether) they came back.
        /// </summary>
        private void NoteSendSucceeded()
        {
            if (!_loggedSendError)
                return;

            _loggedSendError = false;
            UnityEngine.Debug.Log("[Hapbeat] Sending recovered.");
        }

        private void SendRaw(byte[] data)
        {
            // Snapshot the socket: this also runs on the stream mixer thread, and
            // a reconnect on the main thread can null the field between the guard
            // and the send. Reading it twice would surface as an unhandled
            // NullReferenceException on a background thread rather than the
            // ObjectDisposedException the catch below is written for.
            UdpClient client = _udpClient;
            if (!IsConnected || client == null)
                return;

            // Once a device has answered we know which subnet it is on, so send
            // there instead of relying on the limited broadcast reaching it. Before
            // that (and in Bridge mode) this is the unchanged single destination.
            IPEndPoint destination = _targetEndPoint;
            if (IsBroadcast)
            {
                BroadcastRoute locked = _lockedRoute;
                if (locked != null)
                    destination = locked.EndPoint;
            }

            try
            {
                client.Send(data, data.Length, destination);
                NoteSendSucceeded();
            }
            catch (SocketException ex)
            {
                // A failed UDP send says nothing about whether the socket is still
                // usable: the datagram is lost, the socket is not. Tearing the
                // connection down here is what made a single transient failure
                // terminal — a Wi-Fi re-association, a momentary route change or an
                // ICMP reply from a device that just powered off would flag the
                // connection down, the Update() keep-alive (gated on IsConnected)
                // would stop, and the device would fall back to "app not connected"
                // with nothing left to restore it but an app restart.
                //
                // Keep the socket and let the two paths that CAN tell a dead socket
                // apart handle it: ReceiveLoop reports non-recoverable errors, and
                // HapbeatManager.TryAutoReconnect reopens from there.
                NoteSendFailed("Send", ex.SocketErrorCode, ex.Message);
            }
            catch (ObjectDisposedException)
            {
                // The socket really is gone (Dispose raced with this send).
                HandleDisconnection();
            }
        }

        // Stream-only send path (STREAM_BEGIN/DATA/END). Identical to
        // SendPacket/SendRaw except it fans out to the per-session unicast
        // target list (see SetStreamUnicastTargets) instead of the broadcast
        // _targetEndPoint, when one is set. Falls back to SendRaw's normal
        // broadcast/bridge-unicast behavior when no targets are set (nobody has
        // PONGed yet, or we're not in broadcast mode to begin with).
        private void SendStreamPacket(byte commandType, byte[] payload)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(commandType, seq, payload);
            SendStreamRaw(packet);
        }

        private void SendStreamRaw(byte[] data)
        {
            // Snapshot — see SendRaw. This path runs on the mixer thread.
            UdpClient client = _udpClient;
            if (!IsConnected || client == null)
                return;

            // Read the volatile field once — the array itself is only ever
            // replaced wholesale (never mutated in place), so a single local
            // snapshot is safe even if another thread swaps it mid-loop below.
            IPEndPoint[] targets = _streamUnicastTargets;
            if (!IsBroadcast || targets == null)
            {
                // null = no snapshot taken this session (or explicitly cleared):
                // same broadcast fallback as before target filtering existed.
                SendRaw(data);
                return;
            }

            if (targets.Length == 0)
            {
                // Empty (not null) = SetStreamUnicastTargets took a snapshot and every
                // known device's address failed to match the session target. Falling
                // back to broadcast here would defeat the point of filtering (leak
                // this stream to unrelated devices on the LAN), so send nowhere.
                return;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    client.Send(data, data.Length, targets[i]);
                    NoteSendSucceeded();
                }
                catch (SocketException ex)
                {
                    // A single unreachable/offline target shouldn't tear down the
                    // whole session (other targets may still be fine) — log and
                    // keep sending to the rest. Rate-limited: this is the hottest
                    // send path (one chunk per ~10 ms per destination), so an
                    // unguarded warning here floods the log for the whole outage.
                    NoteSendFailed($"Stream unicast send to {targets[i]}",
                                   ex.SocketErrorCode, ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    HandleDisconnection();
                    return;
                }
            }
        }

        // Command send path shared by SendPlay/SendStop/SendStopAll. Mirrors the
        // STREAM_* unicast design above (SendStreamRaw/SetStreamUnicastTargets) but
        // resolves destinations fresh on every call from _knownDeviceIps/_deviceAddresses
        // instead of a session-snapshotted list: PLAY/STOP/STOP_ALL are one-shot
        // fire-and-forget packets, not a per-chunk hot path, so there's no equivalent
        // "session start" to snapshot against and no lock-free-hot-path constraint
        // to design around. PING/CONNECT_STATUS intentionally keep using
        // SendPacket/SendRaw (plain broadcast) since they exist for discovery/
        // liveness, not addressed playback commands.
        private CommandSendResult SendCommandPacket(byte commandType, byte[] payload, string resolvedTarget)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(commandType, seq, payload);
            return SendCommandRaw(packet, resolvedTarget);
        }

        /// <summary>
        /// Routes a single PLAY/STOP/STOP_ALL packet to unicast or broadcast. Fallback
        /// semantics deliberately match SetStreamUnicastTargets/SendStreamRaw exactly:
        /// <list type="bullet">
        /// <item>Not in broadcast mode (Bridge/ESP-NOW), or commandUnicast disabled ->
        /// plain broadcast/bridge-unicast via SendRaw (unchanged pre-feature behavior).</item>
        /// <item>No device has ever PONGed this session (_knownDeviceIps empty) ->
        /// broadcast (fail open — nobody to unicast to yet).</item>
        /// <item>A known device with no reported address (older firmware, or its PONG
        /// hasn't been parsed yet) -> unicast to it anyway (fail open — same treatment
        /// as the unknown-address case in SetStreamUnicastTargets).</item>
        /// <item>A known device whose reported address matches <paramref name="resolvedTarget"/>
        /// -> unicast to it.</item>
        /// <item>At least one send above went out -> done, no broadcast (avoids the
        /// double-delivery a known-and-matching device would get if we also broadcast).</item>
        /// <item>Nothing was unicast (no live device known, or every known device's
        /// reported address failed to match) -> broadcast, exactly as before this
        /// feature existed. This deliberately does NOT mirror SendStreamRaw's
        /// "send nowhere" sentinel: firmware re-applies <c>addressMatch()</c> to every
        /// PLAY/STOP/STOP_ALL it receives (udp_receiver.cpp handlePlay/handleStop/
        /// handleStopAll), so a broadcast can never actuate a device the target didn't
        /// address — the only thing skipping would buy is airtime, at the price of
        /// silently losing a command whenever our cached address is stale (the device's
        /// group/player was just changed and its next PONG hasn't landed) or our
        /// AddressMatches ever diverges from firmware's. For STOP/STOP_ALL that silent
        /// loss means a looping event never stops.</item>
        /// </list>
        /// </summary>
        private CommandSendResult SendCommandRaw(byte[] data, string resolvedTarget)
        {
            // Snapshot — see SendRaw.
            UdpClient client = _udpClient;
            if (!IsConnected || client == null)
                return CommandSendResult.Broadcast;

            if (!IsBroadcast || !_commandUnicastEnabled)
            {
                SendRaw(data);
                return CommandSendResult.Broadcast;
            }

            int port = _targetEndPoint != null ? _targetEndPoint.Port : 0;
            long nowUs = GetLocalTimestampUs();
            long ttlUs = (long)(_knownDeviceTtlSeconds * 1_000_000f);
            bool sentAny = false;

            foreach (var kv in _knownDeviceIps)
            {
                if (nowUs - kv.Value > ttlUs)
                {
                    // Device stopped answering PINGs (powered off, left the network,
                    // rebooting after an OTA). Drop it so we stop aiming datagrams at a
                    // dead host — each one draws an ICMP port-unreachable that Windows
                    // reports back on this socket (see SuppressUdpConnReset) — and so the
                    // set can empty out again and let the broadcast fallback below take
                    // over instead of unicasting into the void. Skipped rather than
                    // removed: the entry is revived by the device's next PONG, and
                    // leaving the collection untouched keeps this loop free of any
                    // race with the receive thread writing into it.
                    continue;
                }

                string knownAddress = GetKnownDeviceAddress(kv.Key);
                // Fail open: unknown address => keep (send). Known address => must match.
                if (knownAddress != null && !AddressMatches(resolvedTarget, knownAddress))
                    continue;

                sentAny = true;
                try
                {
                    client.Send(data, data.Length, new IPEndPoint(kv.Key, port));
                    NoteSendSucceeded();
                }
                catch (SocketException ex)
                {
                    // A single unreachable/offline target shouldn't block the rest —
                    // log and keep sending to the remaining known devices.
                    // Rate-limited for the same reason as the stream path.
                    NoteSendFailed($"Command unicast send to {kv.Key}",
                                   ex.SocketErrorCode, ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    HandleDisconnection();
                    return CommandSendResult.Unicast; // best-effort; some sends may already be out
                }
            }

            if (!sentAny)
            {
                SendRaw(data);
                return CommandSendResult.Broadcast;
            }

            return CommandSendResult.Unicast;
        }

        /// <summary>
        /// Ask Windows to stop reporting ICMP "port unreachable" from a previous
        /// unicast send as an error on this socket. Without it, sending a command to a
        /// device that is powered off or rebooting makes the *next* <c>Receive()</c>
        /// throw <c>SocketException</c> (WSAECONNRESET / 10054) even though the socket
        /// is perfectly healthy — which used to kill the receive thread outright and,
        /// with it, every subsequent PONG. hapbeat-helper root-caused and fixed exactly
        /// this failure (hapbeat-helper f06fa04, "recv スレッドが Windows ICMP reset
        /// (10054) で死にデバイス全ロストする問題"); the SDK now unicasts one-shot
        /// commands too, so it is exposed to the same ICMP feedback.
        /// Best-effort: the ioctl doesn't exist off Windows, and
        /// <see cref="ReceiveLoop"/> treats the error as non-fatal regardless.
        /// </summary>
        private static void SuppressUdpConnReset(UdpClient client)
        {
            try
            {
                client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            catch (Exception ex)
            {
                // On Windows this ioctl is the guard that keeps an offline device's
                // ICMP reply from surfacing as an error on this socket. It failing
                // silently is precisely how a field incident ends up with no
                // evidence of which layer broke, so make it visible. Still
                // non-fatal: ReceiveLoop treats the error as recoverable anyway.
                UnityEngine.Debug.LogWarning(
                    $"[Hapbeat] SIO_UDP_CONNRESET could not be applied: {ex.Message}. " +
                    "ICMP replies from offline devices may surface as socket errors.");
            }
#else
            catch
            {
                // The ioctl does not exist off Windows — throwing here is expected.
            }
#endif
        }

        /// <summary>
        /// Whether a receive-side <see cref="SocketException"/> describes ICMP feedback
        /// about one previously-sent datagram (a dead unicast destination) rather than a
        /// broken socket. These must not tear down the receive thread: nothing restarts
        /// it, and <c>HapbeatManager.EnsureConnected</c> only warns instead of
        /// reconnecting, so one powered-off device would otherwise disable haptics for
        /// the rest of the session.
        /// </summary>
        private static bool IsRecoverableReceiveError(SocketError error)
        {
            return error == SocketError.ConnectionReset      // WSAECONNRESET (10054) — ICMP port unreachable
                || error == SocketError.ConnectionRefused    // same class, reported differently by some stacks
                || error == SocketError.HostUnreachable
                || error == SocketError.NetworkUnreachable
                || error == SocketError.NetworkReset
                || error == SocketError.MessageSize;         // oversized datagram: drop it, keep the socket
        }

        private ushort GetNextSequenceNumber()
        {
            lock (_seqLock)
            {
                return _sequenceNumber++;
            }
        }

        private void ReceiveLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (_udpClient == null || _udpClient.Client == null)
                        break;

                    // Use polling to allow graceful shutdown
                    if (_udpClient.Client.Poll(100_000, SelectMode.SelectRead)) // 100ms timeout
                    {
                        if (!_isRunning)
                            break;

                        IPEndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = _udpClient.Receive(ref remoteEp);

                        if (data != null && data.Length >= HapbeatProtocol.HEADER_SIZE)
                        {
                            ProcessReceivedPacket(data, remoteEp);
                        }
                    }
                }
                catch (SocketException ex)
                {
                    if (!_isRunning)
                        break;

                    if (IsRecoverableReceiveError(ex.SocketErrorCode))
                    {
                        // Per-datagram ICMP feedback (typically a command unicast to a
                        // device that just powered off / is rebooting), not a dead
                        // socket. Breaking here would silently end PONG reception for
                        // the whole session — see IsRecoverableReceiveError. Log once
                        // per connection so a genuinely misconfigured LAN is still
                        // visible without spamming one line per stale destination.
                        if (!_loggedRecoverableReceiveError)
                        {
                            _loggedRecoverableReceiveError = true;
                            UnityEngine.Debug.LogWarning(
                                $"[Hapbeat] Ignoring recoverable receive error ({ex.SocketErrorCode}); " +
                                "a device is likely powered off or rebooting. Receive loop continues.");
                        }
                        continue;
                    }

                    EnqueueMainThread(() => HandleDisconnection());
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        UnityEngine.Debug.LogWarning($"[Hapbeat] Receive error: {ex.Message}");
                    }
                }
            }
        }

        private void ProcessReceivedPacket(byte[] data, IPEndPoint sender)
        {
            try
            {
                var (commandType, seq, payload) = HapbeatProtocol.ParsePacket(data);

                switch (commandType)
                {
                    case HapbeatProtocol.CMD_PONG:
                        HandlePong(seq, payload, sender);
                        break;

                    case HapbeatProtocol.CMD_ERROR:
                        HandleError(payload);
                        break;

                    default:
                        UnityEngine.Debug.LogWarning(
                            $"[Hapbeat] Unknown response command: 0x{commandType:X2}");
                        break;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Hapbeat] Failed to parse packet: {ex.Message}");
            }
        }

        private void HandlePong(ushort seq, byte[] payload, IPEndPoint sender)
        {
            var (timestamp, serverTime, _, address, _, _, _) = HapbeatProtocol.ParsePongExtended(payload);
            long nowUs = GetLocalTimestampUs();

            // sender.Address (IPAddress) is immutable, so caching it directly here
            // (unlike the mutable IPEndPoint captured below for the main-thread
            // closure) is safe even though it's read later from the main thread via
            // GetKnownDeviceAddress / SendCommandRaw.
            //
            // Recorded regardless of whether this PONG reported an address —
            // SendCommandRaw needs the full set of live devices (see _knownDeviceIps),
            // not just the subset with a known address, so an address-unknown device
            // still gets PLAY/STOP/STOP_ALL unicast instead of being silently dropped.
            // The timestamp is what lets SendCommandRaw expire a device that stopped
            // answering PINGs instead of unicasting at it forever.
            _knownDeviceIps[sender.Address] = nowUs;
            if (!string.IsNullOrEmpty(address))
                _deviceAddresses[sender.Address] = address;

            // A reply proves which subnet a device is really on, so pin broadcasts
            // there. Until this happens PLAY still goes out as a limited broadcast,
            // which on a multi-homed host may be leaving through an interface with
            // no Hapbeat behind it (see BroadcastRoute).
            LockRouteFor(sender.Address);

            // Calculate RTT using the original ping timestamp
            long rttUs;
            if (_pendingPings.TryRemove(seq, out long sentTimeUs))
            {
                rttUs = nowUs - sentTimeUs;
            }
            else
            {
                // Fallback: use the timestamp from the pong payload
                rttUs = nowUs - timestamp;
            }

            // broadcast 経路だと device 毎に複数 PONG が届く。送信元 endpoint を
            // 別 event で通知して per-device liveness 集計可能にする。
            // (sender は IPEndPoint で main thread で参照されるが mutable なので
            //  ここで複製して closure に閉じ込める)
            var capturedSender = new IPEndPoint(sender.Address, sender.Port);
            EnqueueMainThread(() =>
            {
                OnPong?.Invoke(rttUs, serverTime);
                OnPongFrom?.Invoke(capturedSender, rttUs);
            });
        }

        private void HandleError(byte[] payload)
        {
            var (errorCode, message) = HapbeatProtocol.ParseError(payload);
            EnqueueMainThread(() => OnError?.Invoke(errorCode, message));
        }

        private void HandleDisconnection()
        {
            if (!IsConnected)
                return;

            IsConnected = false;
            IsBroadcast = false;
            EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(false));
        }

        private void EnqueueMainThread(Action action)
        {
            _mainThreadQueue.Enqueue(action);
        }

        #endregion
    }
}
