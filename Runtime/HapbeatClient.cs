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
        /// <summary>Sent via WifiUdp broadcast — commandUnicast is disabled or there
        /// was no live device to unicast to (none PONGed recently, or none whose
        /// reported address matched the target).</summary>
        Broadcast,

        /// <summary>Sent via unicast to one or more known devices — either their
        /// reported address matched the resolved target, or their address is unknown
        /// and was therefore kept (fail-open).</summary>
        Unicast,
    }

    /// <summary>
    /// Wire format used for one resolved stream endpoint. <see cref="V2"/> is the
    /// lease/generation format (stream-session-v2.md). <see cref="Legacy"/> is the
    /// pre-v2 format kept for devices on older firmware; it is chosen only when a
    /// matched direct reply to this client's extended PING carries no HBS2 tail
    /// (DEC-075) and is never inferred from a missing lease.
    /// </summary>
    internal enum StreamEndpointMode
    {
        V2 = 0,
        Legacy = 1,
    }

    /// <summary>
    /// Internal WifiUdp client for communicating with Hapbeat devices.
    /// Discovery is broadcast; addressed commands and streams can use device unicast.
    /// Receive runs on a background thread; callbacks are queued for main-thread dispatch.
    /// </summary>
    public class HapbeatClient : IDisposable
    {
        /// <summary>
        /// Invoked on the main thread for a PONG response to a local PING.
        /// Unsolicited identity PONGs do not invoke this event because they have
        /// no RTT or time-sync sample.
        /// </summary>
        public event Action<long, long> OnPong; // (rttUs, serverTimeUs)

        /// <summary>
        /// Invoked on main thread for each PONG, with the source endpoint.
        /// Use this to track per-device liveness (broadcast may yield multiple
        /// PONGs per PING — one per responsive device).
        /// The RTT argument is zero for an unsolicited identity PONG, which has
        /// no corresponding PING to measure.
        /// </summary>
        public event Action<IPEndPoint, long> OnPongFrom; // (sender, rttUs)

        /// <summary>
        /// Invoked on the main thread only when an endpoint's usable stream lease
        /// changes. Periodic PONGs for the same lease intentionally do not emit it.
        /// </summary>
        internal event Action<IPEndPoint, HapbeatProtocol.StreamLeaseIdentity, bool, bool> OnStreamLeaseChanged;

        /// <summary>
        /// Invoked on the main thread after a v2 STREAM_BEGIN is queued for an exact
        /// endpoint, once per session identity (the mixer re-sends an identical BEGIN
        /// to survive packet loss; repeats are not announced).
        /// </summary>
        internal event Action<IPEndPoint, HapbeatProtocol.StreamSessionIdentity> OnStreamSessionBegan;

        /// <summary>Invoked on main thread when an ERROR response is received.</summary>
        public event Action<ushort, string> OnError; // (errorCode, message)

        /// <summary>Invoked on main thread when connection state changes.</summary>
        public event Action<bool> OnConnectionStateChanged; // (isConnected)

        /// <summary>Whether the client is currently ready to send/receive.</summary>
        public bool IsConnected { get; private set; }

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
        // Wi-Fi AP DTIM power-save rationale as addressed streaming, applied to one-shot
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

        // Last known device-addressing address string per sender IP, learned from
        // the PONG extension fields (device-addressing.md §5.4). Written from the
        // background receive thread (HandlePong); read when explicit stream
        // endpoints are resolved. Address-unknown devices are excluded from stream
        // routing because STREAM_DATA cannot be target-filtered.
        private readonly ConcurrentDictionary<IPAddress, string> _deviceAddresses =
            new ConcurrentDictionary<IPAddress, string>();

        // Device IPs that have PONGed recently, mapped to the local timestamp (us,
        // same clock as GetLocalTimestampUs) of that most recent PONG. Recorded
        // regardless of whether the PONG reported an address (unlike
        // _deviceAddresses above, which only has an entry when the address
        // extension was present). Used by SendCommandRaw (PLAY/STOP/STOP_ALL
        // unicast routing) so an address-unknown device (e.g. older firmware
        // without the extension) still receives commands via unicast instead of
        // being silently dropped. Entries older than
        // _knownDeviceTtlSeconds are dropped on the next send (see SendCommandRaw)
        // so the set tracks *live* devices rather than growing forever. Written
        // from the background receive thread (HandlePong); read/pruned from the
        // main thread (SendCommandRaw).
        private readonly ConcurrentDictionary<IPAddress, long> _knownDeviceIps =
            new ConcurrentDictionary<IPAddress, long>();

        // STREAM_DATA has no target on the wire. Multi-stream callers must therefore
        // resolve an addressed device to one concrete endpoint before beginning a
        // session; reusing the mutable per-session broadcast target list is unsafe.
        private readonly ConcurrentDictionary<IPAddress, IPEndPoint> _knownDeviceEndpoints =
            new ConcurrentDictionary<IPAddress, IPEndPoint>();

        // Last v2 session identity announced through OnStreamSessionBegan per device IP.
        // Written only from the stream mixer thread (SendStreamBeginTo).
        private readonly ConcurrentDictionary<IPAddress, HapbeatProtocol.StreamSessionIdentity> _announcedStreamBegins =
            new ConcurrentDictionary<IPAddress, HapbeatProtocol.StreamSessionIdentity>();

        // Queue for dispatching callbacks to the main thread
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private readonly struct PendingPing
        {
            public readonly long TimestampUs;
            public readonly ulong ClientIncarnation;

            public PendingPing(long timestampUs, ulong clientIncarnation)
            {
                TimestampUs = timestampUs;
                ClientIncarnation = clientIncarnation;
            }
        }

        private readonly struct StreamLeaseState : IEquatable<StreamLeaseState>
        {
            public readonly HapbeatProtocol.StreamLeaseIdentity Identity;
            public readonly long LastAcceptedTimestampUs;
            public readonly long RouteOrder;
            public readonly ulong ClientIncarnation;
            public readonly bool IsLeaseValid;
            public readonly bool IsSuperseded;

            public StreamLeaseState(HapbeatProtocol.StreamLeaseIdentity identity,
                long lastAcceptedTimestampUs, long routeOrder, ulong clientIncarnation,
                bool isLeaseValid, bool isSuperseded)
            {
                Identity = identity;
                LastAcceptedTimestampUs = lastAcceptedTimestampUs;
                RouteOrder = routeOrder;
                ClientIncarnation = clientIncarnation;
                IsLeaseValid = isLeaseValid;
                IsSuperseded = isSuperseded;
            }

            public bool IsUsableFor(ulong incarnation) =>
                IsLeaseValid && !IsSuperseded && ClientIncarnation == incarnation && Identity.IsValid;

            public bool Equals(StreamLeaseState other) =>
                Identity.Equals(other.Identity) && IsLeaseValid == other.IsLeaseValid &&
                IsSuperseded == other.IsSuperseded && ClientIncarnation == other.ClientIncarnation;

            public override bool Equals(object obj) =>
                obj is StreamLeaseState other && Equals(other);

            public override int GetHashCode() => Identity.GetHashCode();
        }

        private readonly struct StreamLeaseRoute
        {
            public readonly IPAddress Address;
            public readonly StreamLeaseState State;

            public StreamLeaseRoute(IPAddress address, StreamLeaseState state)
            {
                Address = address;
                State = state;
            }
        }

        private readonly struct StreamLeaseUpdate
        {
            public readonly bool DiscardPong;
            public readonly bool Changed;
            public readonly StreamLeaseState State;

            public StreamLeaseUpdate(bool discardPong, bool changed, StreamLeaseState state)
            {
                DiscardPong = discardPong;
                Changed = changed;
                State = state;
            }
        }

        // A broadcast PING receives one PONG from every endpoint. Pending requests
        // must therefore survive the first reply long enough for every device to
        // correlate its HBS2 tail, and are pruned only by age / sequence replacement.
        private readonly ConcurrentDictionary<ushort, PendingPing> _pendingPings =
            new ConcurrentDictionary<ushort, PendingPing>();

        // Endpoint stream eligibility is deliberately separate from ordinary PONG
        // liveness/address discovery. A PONG without a valid correlated HBS2 tail
        // never becomes a stream destination and never erases an established lease.
        private readonly ConcurrentDictionary<IPAddress, StreamLeaseState> _streamLeases =
            new ConcurrentDictionary<IPAddress, StreamLeaseState>();
        private readonly Dictionary<HapbeatProtocol.StreamLeaseIdentity, uint> _nextGenerationByLease =
            new Dictionary<HapbeatProtocol.StreamLeaseIdentity, uint>();
        private readonly object _streamLeaseLock = new object();

        // Endpoints classified as pre-v2 firmware (stream-session-v2.md "Legacy
        // receiver fallback"), keyed like _streamLeases, value = echoed timestamp of
        // the matched reply that classified them. Mutually exclusive with
        // _streamLeases. Deliberately NOT cleared by RenewStreamLeaseIncarnation:
        // legacy firmware has no lease, so focus changes / reacquire must not
        // interrupt its streams. Cleared on Disconnect with other device knowledge.
        private readonly ConcurrentDictionary<IPAddress, long> _legacyStreamEndpoints =
            new ConcurrentDictionary<IPAddress, long>();

        // Echoed timestamp of the last accepted matched reply per endpoint across
        // BOTH classes, so a late reply can never flip v2<->legacy backwards.
        // Guarded by _streamLeaseLock. Timestamps are strictly increasing for the
        // client's lifetime, so this survives incarnation renewal safely.
        private readonly Dictionary<IPAddress, long> _lastMatchedStreamReplyUs =
            new Dictionary<IPAddress, long>();

        private ulong _clientIncarnation;
        private long _lastLeasePingTimestampUs;
        private long _nextAcceptedStreamLeaseRouteOrder;

        private bool _disposed;

        public HapbeatClient()
        {
            _stopwatch = Stopwatch.StartNew();
            _clientIncarnation = CreateClientIncarnation();
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
            RenewStreamLeaseIncarnation();

            try
            {
                _udpClient = new UdpClient(0); // bind to OS-assigned local port
                _udpClient.EnableBroadcast = true;
                SuppressUdpConnReset(_udpClient);
                _targetEndPoint = new IPEndPoint(IPAddress.Broadcast, port);
                _broadcastRoutes = EnumerateBroadcastRoutes(port);
                _lockedRoute = null;
                StartReceiveLoop();
                IsConnected = true;
                EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(true));
            }
            catch (Exception ex)
            {
                IsConnected = false;
                throw new InvalidOperationException(
                    $"Failed to open broadcast on port {port}: {ex.Message}", ex);
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
            lock (_streamLeaseLock)
            {
                _pendingPings.Clear();
                _streamLeases.Clear();
                _legacyStreamEndpoints.Clear();
                _lastMatchedStreamReplyUs.Clear();
                _loggedStreamModes.Clear();
            }

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
            _knownDeviceEndpoints.Clear();
            _announcedStreamBegins.Clear();
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
        /// Set the forced player/group applied to PLAY/STOP/STOP_ALL target strings
        /// via <see cref="ResolveTarget(string)"/>. The endpoint stream mixer applies
        /// the same values to each logical stream source before endpoint resolution.
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

        internal readonly struct StreamEndpoint
        {
            public readonly IPEndPoint EndPoint;
            public readonly string Address;
            public readonly HapbeatProtocol.StreamLeaseIdentity Lease;
            public readonly StreamEndpointMode Mode;

            public StreamEndpoint(IPEndPoint endPoint, string address,
                HapbeatProtocol.StreamLeaseIdentity lease = default,
                StreamEndpointMode mode = StreamEndpointMode.V2)
            {
                EndPoint = endPoint;
                Address = address;
                Lease = lease;
                Mode = mode;
            }
        }

        /// <summary>
        /// Returns only live PONG-backed endpoints whose reported address matches
        /// <paramref name="target"/>. Address-unknown devices are intentionally
        /// excluded: a STREAM_DATA packet cannot be target-filtered by firmware, so
        /// failing open here would leak another logical source to that device.
        /// </summary>
        internal List<StreamEndpoint> GetResolvedStreamEndpoints(string target)
        {
            var result = new List<StreamEndpoint>();
            long nowUs = GetLocalTimestampUs();
            long ttlUs = (long)(Math.Max(1f, _knownDeviceTtlSeconds) * 1_000_000d);
            var currentRoutes = new Dictionary<HapbeatProtocol.StreamLeaseIdentity, StreamLeaseRoute>();
            lock (_streamLeaseLock)
            {
                // A device may move IP addresses before the old PONG entry reaches
                // its discovery TTL. Select exactly one route for each lease before
                // applying the caller's address filter; otherwise a dictionary's
                // arbitrary enumeration order can let a stale route join a mixer.
                foreach (var pair in _streamLeases)
                {
                    if (!pair.Value.IsUsableFor(_clientIncarnation)) continue;
                    var candidate = new StreamLeaseRoute(pair.Key, pair.Value);
                    if (!currentRoutes.TryGetValue(pair.Value.Identity, out StreamLeaseRoute current) ||
                        IsMoreRecentStreamLeaseRoute(candidate, current))
                    {
                        currentRoutes[pair.Value.Identity] = candidate;
                    }
                }

                // Pre-v2 endpoints have no lease. A v2 state for the same IP (even a
                // deferred one) wins; the two maps are kept exclusive regardless.
                foreach (var pair in _legacyStreamEndpoints)
                {
                    if (_streamLeases.ContainsKey(pair.Key)) continue;
                    if (TryResolveStreamRoute(pair.Key, target, nowUs, ttlUs,
                            out IPEndPoint legacyEndpoint, out string legacyAddress))
                    {
                        result.Add(new StreamEndpoint(legacyEndpoint, legacyAddress, default,
                            StreamEndpointMode.Legacy));
                    }
                }
            }

            foreach (StreamLeaseRoute route in currentRoutes.Values)
            {
                if (TryResolveStreamRoute(route.Address, target, nowUs, ttlUs,
                        out IPEndPoint endpoint, out string address))
                    result.Add(new StreamEndpoint(endpoint, address, route.State.Identity));
            }
            return result;
        }

        private bool TryResolveStreamRoute(IPAddress ipAddress, string target, long nowUs, long ttlUs,
            out IPEndPoint endpoint, out string address)
        {
            endpoint = null;
            address = null;
            if (!_knownDeviceIps.TryGetValue(ipAddress, out long lastPongUs) ||
                nowUs - lastPongUs > ttlUs)
                return false;
            if (!_deviceAddresses.TryGetValue(ipAddress, out address) ||
                string.IsNullOrEmpty(address) ||
                !AddressMatches(target, address))
                return false;
            return _knownDeviceEndpoints.TryGetValue(ipAddress, out endpoint);
        }

        /// <summary>
        /// Whether a matched reply classified any device as v2 (it holds, or is
        /// waiting for, a stream lease that only periodic PING keeps alive).
        /// </summary>
        internal bool HasV2StreamEndpoints
        {
            get { lock (_streamLeaseLock) return !_streamLeases.IsEmpty; }
        }

        /// <summary>Whether a matched reply classified this device IP as pre-v2 firmware.</summary>
        internal bool IsLegacyStreamEndpoint(IPAddress address)
        {
            lock (_streamLeaseLock)
                return address != null && _legacyStreamEndpoints.ContainsKey(address);
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
        /// and <see cref="CommandSendResult"/> for the exact routing/fallback rules.
        /// <paramref name="pan"/> is the stereo balance (-1 left / 0 center / +1 right)
        /// applied device-side per contracts message-format.md §0x01 (DEC-055).</summary>
        public CommandSendResult SendPlay(string eventId, long targetTimeUs, float gain, string target = null,
            float pan = 0f)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildPlayPayload(eventId, targetTimeUs, gain, target, pan);
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

        internal void SendStreamBeginTo(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
            ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target = null)
        {
            if (!IsCurrentStreamLease(endpoint, identity.Lease)) return;
            byte[] payload = HapbeatProtocol.BuildStreamBeginPayload(identity,
                sampleRate, channels, format, totalSamples, gain, target);
            SendStreamPacketTo(endpoint, HapbeatProtocol.CMD_STREAM_BEGIN, payload);
            if (_announcedStreamBegins.TryGetValue(endpoint.Address, out var announced) &&
                announced.Lease.Equals(identity.Lease) && announced.Generation == identity.Generation)
                return;
            _announcedStreamBegins[endpoint.Address] = identity;
            var capturedEndpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            EnqueueMainThread(() => OnStreamSessionBegan?.Invoke(capturedEndpoint, identity));
        }

        internal void SendStreamDataTo(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
            uint byteOffset, byte[] audioData,
            int dataOffset, int dataLength)
        {
            if (!IsConnected || _udpClient == null) return;
            if (!IsCurrentStreamLease(endpoint, identity.Lease)) return;
            ushort seq = GetNextSequenceNumber();
            byte[] payload = HapbeatProtocol.BuildStreamDataPayload(
                identity, byteOffset, audioData, dataOffset, dataLength);
            byte[] packet = HapbeatProtocol.BuildStreamPacket(
                HapbeatProtocol.CMD_STREAM_DATA, seq, payload);
            SendStreamRawTo(endpoint, packet);
        }

        internal void SendStreamEndTo(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
        {
            SendStreamPacketTo(endpoint, HapbeatProtocol.CMD_STREAM_END,
                HapbeatProtocol.BuildStreamEndPayload(identity));
        }

        // Pre-v2 stream format for endpoints classified Legacy (stream-session-v2.md
        // "Legacy receiver fallback"): header protocol_version=1, no identity
        // envelope, empty END, exact unicast. No lease gating: legacy firmware has no
        // lease. The mixer owns the per-endpoint 300 ms END->BEGIN guard, and
        // OnStreamSessionBegan is deliberately not raised (it carries a v2 identity).

        internal void SendLegacyStreamBeginTo(IPEndPoint endpoint, ushort sampleRate, byte channels,
            byte format, uint totalSamples, float gain, string target = null)
        {
            byte[] payload = HapbeatProtocol.BuildLegacyStreamBeginPayload(
                sampleRate, channels, format, totalSamples, gain, target);
            SendLegacyStreamPacketTo(endpoint, HapbeatProtocol.CMD_STREAM_BEGIN, payload);
        }

        internal void SendLegacyStreamDataTo(IPEndPoint endpoint, uint byteOffset, byte[] audioData,
            int dataOffset, int dataLength)
        {
            if (!IsConnected || _udpClient == null) return;
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildLegacyStreamDataPacket(
                seq, byteOffset, audioData, dataOffset, dataLength);
            SendStreamRawTo(endpoint, packet);
        }

        internal void SendLegacyStreamEndTo(IPEndPoint endpoint)
        {
            SendLegacyStreamPacketTo(endpoint, HapbeatProtocol.CMD_STREAM_END, Array.Empty<byte>());
        }

        private void SendLegacyStreamPacketTo(IPEndPoint endpoint, byte commandType, byte[] payload)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildPacket(commandType, seq, payload);
            SendStreamRawTo(endpoint, packet);
        }

        /// <summary>
        /// Send a PING command for keep-alive and time synchronization.
        /// </summary>
        /// <returns>The sequence number of the ping packet.</returns>
        public ushort SendPing()
        {
            long timestampUs = GetLocalTimestampUs();
            ushort seq = GetNextSequenceNumber();
            byte[] packet;
            lock (_streamLeaseLock)
            {
                // Ordering of lease adoption is defined by the echoed PING
                // timestamp. Stopwatch resolution can yield equal values for two
                // immediate requests, so make locally-issued lease PING timestamps
                // strictly increasing without adding a network round trip.
                if (timestampUs <= _lastLeasePingTimestampUs)
                    timestampUs = _lastLeasePingTimestampUs + 1;
                _lastLeasePingTimestampUs = timestampUs;
                byte[] payload = HapbeatProtocol.BuildPingPayload(timestampUs, _clientIncarnation);
                packet = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_PING, seq, payload);
                PrunePendingPingsLocked(timestampUs);
                _pendingPings[seq] = new PendingPing(timestampUs, _clientIncarnation);
            }

            // Fans out until a device answers — this is what finds a Hapbeat the
            // limited broadcast cannot reach on a multi-homed host.
            SendDiscoveryRaw(packet);
            return seq;
        }

        /// <summary>
        /// Explicitly relinquish this application's current stream leases and request
        /// new ones. This is the only local automatic action after a device reports
        /// a lease as superseded; periodic discovery deliberately does not seize it.
        /// </summary>
        internal void ReacquireStreamLeases()
        {
            RenewStreamLeaseIncarnation();
        }

        /// <summary>
        /// Reacquire only when a device reported this incarnation's lease superseded.
        /// For an explicit user stream start (Editor Test Play); returns true when the
        /// incarnation was renewed, so the caller sends a fresh PING.
        /// </summary>
        internal bool ReacquireStreamLeasesIfSuperseded()
        {
            lock (_streamLeaseLock)
            {
                bool superseded = false;
                foreach (var pair in _streamLeases)
                {
                    if (pair.Value.IsSuperseded && pair.Value.ClientIncarnation == _clientIncarnation)
                    {
                        superseded = true;
                        break;
                    }
                }
                if (!superseded) return false;
                RenewStreamLeaseIncarnation();
                return true;
            }
        }

        internal bool TryGetPendingPing(ushort seq, out long timestampUs, out ulong clientIncarnation)
        {
            lock (_streamLeaseLock)
            {
                if (_pendingPings.TryGetValue(seq, out PendingPing pending))
                {
                    timestampUs = pending.TimestampUs;
                    clientIncarnation = pending.ClientIncarnation;
                    return true;
                }
            }

            timestampUs = 0;
            clientIncarnation = 0;
            return false;
        }

        /// <summary>
        /// Allocate the next monotonic generation for this exact device-issued
        /// lease. The counter belongs to the transport client, not a mixer, so a
        /// mixer disposal/recreation cannot reuse a generation under a live lease.
        /// </summary>
        internal bool TryAllocateStreamSessionIdentity(IPEndPoint endpoint,
            HapbeatProtocol.StreamLeaseIdentity lease,
            out HapbeatProtocol.StreamSessionIdentity identity)
        {
            lock (_streamLeaseLock)
            {
                if (!IsCurrentStreamLeaseLocked(endpoint, lease))
                {
                    identity = default;
                    return false;
                }

                _nextGenerationByLease.TryGetValue(lease, out uint previous);
                if (previous == uint.MaxValue)
                {
                    identity = default;
                    return false;
                }

                uint generation = previous + 1;
                _nextGenerationByLease[lease] = generation;
                identity = new HapbeatProtocol.StreamSessionIdentity(lease, generation);
                return true;
            }
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
            if (routes == null || routes.Count == 0)
            {
                // Interface enumeration may be unavailable; use limited broadcast.
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
            if (_lockedRoute != null || deviceAddress == null)
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
            // that this is the limited-broadcast destination.
            IPEndPoint destination = _targetEndPoint;
            BroadcastRoute locked = _lockedRoute;
            if (locked != null)
                destination = locked.EndPoint;

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

        private void SendStreamPacketTo(IPEndPoint endpoint, byte commandType, byte[] payload)
        {
            ushort seq = GetNextSequenceNumber();
            byte[] packet = HapbeatProtocol.BuildStreamPacket(commandType, seq, payload);
            SendStreamRawTo(endpoint, packet);
        }

        private void SendStreamRawTo(IPEndPoint endpoint, byte[] data)
        {
            UdpClient client = _udpClient;
            if (!IsConnected || client == null || endpoint == null) return;
            try
            {
                client.Send(data, data.Length, endpoint);
                NoteSendSucceeded();
            }
            catch (SocketException ex)
            {
                NoteSendFailed($"Stream unicast send to {endpoint}", ex.SocketErrorCode, ex.Message);
            }
            catch (ObjectDisposedException)
            {
                HandleDisconnection();
            }
        }

        // Command send path shared by SendPlay/SendStop/SendStopAll. Mirrors the
        // explicit STREAM_* endpoint design, but resolves destinations fresh on every
        // call from _knownDeviceIps/_deviceAddresses
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
        /// semantics keep stream routing distinct:
        /// <list type="bullet">
        /// <item>commandUnicast disabled -> WifiUdp broadcast via SendRaw.</item>
        /// <item>No device has ever PONGed this session (_knownDeviceIps empty) ->
        /// broadcast (fail open — nobody to unicast to yet).</item>
        /// <item>A known device with no reported address (older firmware, or its PONG
        /// hasn't been parsed yet) -> unicast to it anyway (fail open for one-shot
        /// commands only).</item>
        /// <item>A known device whose reported address matches <paramref name="resolvedTarget"/>
        /// -> unicast to it.</item>
        /// <item>At least one send above went out -> done, no broadcast (avoids the
        /// double-delivery a known-and-matching device would get if we also broadcast).</item>
        /// <item>Nothing was unicast (no live device known, or every known device's
        /// reported address failed to match) -> broadcast, exactly as before this
        /// feature existed. This deliberately differs from StreamClip's defer:
        /// firmware re-applies <c>addressMatch()</c> to every
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

            if (!_commandUnicastEnabled)
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

                _deviceAddresses.TryGetValue(kv.Key, out string knownAddress);
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
            HapbeatProtocol.PongExtendedInfo pong = HapbeatProtocol.ParsePongExtendedInfo(payload);
            long timestamp = pong.Timestamp;
            long serverTime = pong.ServerTime;
            string address = pong.Address;
            StreamLeaseUpdate leaseUpdate = EvaluateStreamLeasePong(sender, seq, pong);
            if (leaseUpdate.DiscardPong)
            {
                // This was a correctly correlated PONG for a retired request (or
                // an earlier incarnation). Do not let it roll endpoint address,
                // route, liveness, RTT, or main-thread callbacks backwards.
                return;
            }
            long nowUs = GetLocalTimestampUs();

            // sender.Address (IPAddress) is immutable, so caching it directly here
            // (unlike the mutable IPEndPoint captured below for the main-thread
            // closure) is safe even though it's read later from the main thread by
            // stream endpoint resolution and SendCommandRaw.
            //
            // Recorded regardless of whether this PONG reported an address —
            // SendCommandRaw needs the full set of live devices (see _knownDeviceIps),
            // not just the subset with a known address, so an address-unknown device
            // still gets PLAY/STOP/STOP_ALL unicast instead of being silently dropped.
            // The timestamp is what lets SendCommandRaw expire a device that stopped
            // answering PINGs instead of unicasting at it forever.
            _knownDeviceIps[sender.Address] = nowUs;
            _knownDeviceEndpoints[sender.Address] = new IPEndPoint(sender.Address, sender.Port);
            if (!string.IsNullOrEmpty(address))
                _deviceAddresses[sender.Address] = address;
            else
                _deviceAddresses.TryRemove(sender.Address, out _);

            // A reply proves which subnet a device is really on, so pin broadcasts
            // there. Until this happens PLAY still goes out as a limited broadcast,
            // which on a multi-homed host may be leaving through an interface with
            // no Hapbeat behind it (see BroadcastRoute).
            LockRouteFor(sender.Address);

            // timestamp=0 is the contracts sentinel for an unsolicited identity
            // update, not a response to any PING. It still reached this method so
            // the endpoint/identity/liveness registries above are refreshed, but
            // it must not consume a real pending PING (seq=0 is also a valid
            // sequence number) or publish a bogus RTT/time-sync sample.
            bool isUnsolicitedIdentityPong = timestamp == 0;
            long rttUs = 0;
            if (!isUnsolicitedIdentityPong)
            {
                // Calculate RTT using the original ping timestamp
                if (TryGetPendingPing(seq, out long pendingTimestamp, out _))
                {
                    rttUs = nowUs - pendingTimestamp;
                }
                else
                {
                    // Fallback: use the timestamp from the pong payload
                    rttUs = nowUs - timestamp;
                }
            }

            // broadcast 経路だと device 毎に複数 PONG が届く。送信元 endpoint を
            // 別 event で通知して per-device liveness 集計可能にする。
            // (sender は IPEndPoint で main thread で参照されるが mutable なので
            //  ここで複製して closure に閉じ込める)
            var capturedSender = new IPEndPoint(sender.Address, sender.Port);
            EnqueueMainThread(() =>
            {
                if (!isUnsolicitedIdentityPong)
                    OnPong?.Invoke(rttUs, serverTime);
                OnPongFrom?.Invoke(capturedSender, rttUs);
                if (leaseUpdate.Changed)
                    OnStreamLeaseChanged?.Invoke(capturedSender, leaseUpdate.State.Identity,
                        leaseUpdate.State.IsLeaseValid, leaseUpdate.State.IsSuperseded);
            });
        }

        private StreamLeaseUpdate EvaluateStreamLeasePong(IPEndPoint sender, ushort seq,
            HapbeatProtocol.PongExtendedInfo pong)
        {
            lock (_streamLeaseLock)
            {
                HapbeatProtocol.StreamLeasePongTail tail = pong.StreamLease;
                if (tail.Status == HapbeatProtocol.StreamLeaseTailStatus.Malformed)
                {
                    // An HBS2 marker with a bad length/version/flags/reserved field
                    // is neither a lease nor proof of pre-v2 firmware: no class or
                    // lease change. Ordinary discovery fields still refresh.
                    return default;
                }

                if (tail.Status == HapbeatProtocol.StreamLeaseTailStatus.Absent)
                {
                    // A normal unextended PONG, including an unsolicited identity
                    // update, is still permitted to refresh endpoint discovery. Only
                    // a matched direct reply to our extended PING may classify the
                    // endpoint as pre-v2 firmware.
                    EvaluateLegacyPongLocked(sender, seq, pong);
                    return default;
                }

                // HBS2 is only valid on a direct reply to the active incarnation.
                // A reply for a prior Connect/reacquire must not update even ordinary
                // address/liveness state after that session was retired.
                if (tail.EchoedClientIncarnation != _clientIncarnation)
                    return new StreamLeaseUpdate(true, false, default);

                if (pong.Timestamp == 0 ||
                    !_pendingPings.TryGetValue(seq, out PendingPing pending) ||
                    pending.TimestampUs != pong.Timestamp ||
                    pending.ClientIncarnation != tail.EchoedClientIncarnation)
                {
                    // HBS2 is a correlation-bearing direct reply. If any part of
                    // that correlation is absent or mismatched (including a reply
                    // delayed past pending-PING pruning), it cannot be allowed to
                    // rewrite endpoint identity as an ordinary discovery PONG.
                    return new StreamLeaseUpdate(true, false, default);
                }

                bool hadExisting = _streamLeases.TryGetValue(sender.Address, out StreamLeaseState existing);
                if ((hadExisting && pong.Timestamp <= existing.LastAcceptedTimestampUs) ||
                    !IsNewerMatchedStreamReplyLocked(sender.Address, pong.Timestamp))
                {
                    // A delayed PONG can be structurally valid but must never roll an
                    // endpoint back to an older boot/ticket identity, route or class.
                    return new StreamLeaseUpdate(true, false, default);
                }
                _lastMatchedStreamReplyUs[sender.Address] = pong.Timestamp;
                // A matched valid v2 reply clears pre-v2 classification (firmware
                // update). The mixer ENDs a live legacy session in its old format.
                _legacyStreamEndpoints.TryRemove(sender.Address, out _);
                NoteStreamModeLocked(sender.Address, StreamEndpointMode.V2);

                // A superseded writer must not turn a later periodic discovery
                // reply into an implicit takeover. Only RenewStreamLeaseIncarnation
                // (Connect/reconnect/foreground resume/explicit reacquire) clears
                // this latch and permits a new lease to become usable.
                bool keepSuperseded = hadExisting && existing.IsSuperseded &&
                    existing.ClientIncarnation == pending.ClientIncarnation;
                long routeOrder = ++_nextAcceptedStreamLeaseRouteOrder;
                var next = keepSuperseded
                    ? new StreamLeaseState(existing.Identity, pong.Timestamp,
                        routeOrder, pending.ClientIncarnation, false, true)
                    : new StreamLeaseState(tail.Lease, pong.Timestamp,
                        routeOrder, pending.ClientIncarnation, tail.IsLeaseValid, tail.IsLeaseSuperseded);
                bool changed = !hadExisting || !next.Equals(existing);
                _streamLeases[sender.Address] = next;
                return new StreamLeaseUpdate(false, changed, next);
            }
        }

        /// <summary>
        /// Classify <paramref name="sender"/> as pre-v2 firmware from a PONG with no
        /// HBS2 marker, but only when it is a matched direct reply to this client's
        /// current extended PING (header seq + echoed timestamp, pending request of
        /// the active incarnation) and newer than the last accepted matched reply for
        /// that endpoint in either class. Unsolicited (timestamp 0), unmatched or late
        /// replies never change the class.
        /// </summary>
        private void EvaluateLegacyPongLocked(IPEndPoint sender, ushort seq,
            HapbeatProtocol.PongExtendedInfo pong)
        {
            if (pong.Timestamp == 0 ||
                !_pendingPings.TryGetValue(seq, out PendingPing pending) ||
                pending.TimestampUs != pong.Timestamp ||
                pending.ClientIncarnation != _clientIncarnation ||
                !IsNewerMatchedStreamReplyLocked(sender.Address, pong.Timestamp))
                return;

            _lastMatchedStreamReplyUs[sender.Address] = pong.Timestamp;
            // A matched legacy reply clears the v2 lease (firmware rollback). This is
            // not reported through OnStreamLeaseChanged: a legacy endpoint has no
            // lease to be "unavailable", and the ordinary PONG callback already
            // reconciles the mixer, which ENDs the v2 session in its old format.
            _streamLeases.TryRemove(sender.Address, out _);
            _legacyStreamEndpoints[sender.Address] = pong.Timestamp;
            NoteStreamModeLocked(sender.Address, StreamEndpointMode.Legacy);
        }

        private bool IsNewerMatchedStreamReplyLocked(IPAddress address, long timestampUs) =>
            !_lastMatchedStreamReplyUs.TryGetValue(address, out long last) || timestampUs > last;

        // Last class logged per endpoint, so the mode line is printed once per class
        // change rather than once per periodic PONG or lease renewal.
        private readonly Dictionary<IPAddress, StreamEndpointMode> _loggedStreamModes =
            new Dictionary<IPAddress, StreamEndpointMode>();

        private void NoteStreamModeLocked(IPAddress address, StreamEndpointMode mode)
        {
            if (_loggedStreamModes.TryGetValue(address, out StreamEndpointMode logged) && logged == mode)
                return;
            _loggedStreamModes[address] = mode;
            UnityEngine.Debug.Log(mode == StreamEndpointMode.Legacy
                ? $"[Hapbeat] Stream mode legacy (pre-v2 firmware) at {address}"
                : $"[Hapbeat] Stream mode v2 at {address}");
        }

        private void RenewStreamLeaseIncarnation()
        {
            lock (_streamLeaseLock)
            {
                _clientIncarnation = CreateClientIncarnation();
                _pendingPings.Clear();
                // Legacy classification survives: pre-v2 firmware has no lease, so
                // focus/reacquire must not interrupt its streams.
                _streamLeases.Clear();
            }
        }

        private void PrunePendingPingsLocked(long nowUs)
        {
            const long maxPendingAgeUs = 15_000_000;
            foreach (var pair in _pendingPings)
            {
                if (nowUs - pair.Value.TimestampUs > maxPendingAgeUs)
                    _pendingPings.TryRemove(pair.Key, out _);
            }
        }

        private bool IsCurrentStreamLease(IPEndPoint endpoint,
            HapbeatProtocol.StreamLeaseIdentity identity)
        {
            lock (_streamLeaseLock)
                return IsCurrentStreamLeaseLocked(endpoint, identity);
        }

        private bool IsCurrentStreamLeaseLocked(IPEndPoint endpoint,
            HapbeatProtocol.StreamLeaseIdentity identity)
        {
            if (endpoint == null || !identity.IsValid ||
                !_streamLeases.TryGetValue(endpoint.Address, out StreamLeaseState current) ||
                !current.IsUsableFor(_clientIncarnation) || !current.Identity.Equals(identity))
                return false;

            // A new IP reporting this same lease retires the old route immediately
            // for BEGIN/DATA eligibility. END deliberately bypasses this check so
            // it can still stamp the original session identity on its old route.
            foreach (var pair in _streamLeases)
            {
                if (pair.Key.Equals(endpoint.Address) ||
                    !pair.Value.IsUsableFor(_clientIncarnation) ||
                    !pair.Value.Identity.Equals(identity))
                    continue;
                if (IsMoreRecentStreamLeaseRoute(
                    new StreamLeaseRoute(pair.Key, pair.Value),
                    new StreamLeaseRoute(endpoint.Address, current)))
                    return false;
            }
            return true;
        }

        private static bool IsMoreRecentStreamLeaseRoute(StreamLeaseRoute candidate,
            StreamLeaseRoute current)
        {
            return candidate.State.LastAcceptedTimestampUs > current.State.LastAcceptedTimestampUs ||
                (candidate.State.LastAcceptedTimestampUs == current.State.LastAcceptedTimestampUs &&
                 candidate.State.RouteOrder > current.State.RouteOrder);
        }

        private static ulong CreateClientIncarnation()
        {
            byte[] bytes = Guid.NewGuid().ToByteArray();
            ulong incarnation = BitConverter.ToUInt64(bytes, 0);
            return incarnation == 0 ? 1UL : incarnation;
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
            EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(false));
        }

        private void EnqueueMainThread(Action action)
        {
            _mainThreadQueue.Enqueue(action);
        }

        #endregion
    }
}
