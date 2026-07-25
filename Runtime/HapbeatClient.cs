using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Hapbeat
{
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
            if (IsConnected)
                Disconnect();

            try
            {
                _udpClient = new UdpClient(0); // bind to OS-assigned local port
                _udpClient.EnableBroadcast = true;
                _targetEndPoint = new IPEndPoint(IPAddress.Broadcast, port);
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
            if (IsConnected)
                Disconnect();

            try
            {
                _udpClient = new UdpClient(0); // bind to OS-assigned local port
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
            if (!IsConnected)
                return;

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
        /// target string ("" = broadcast).</summary>
        public void SendPlay(string eventId, long targetTimeUs, float gain, string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildPlayPayload(eventId, targetTimeUs, gain, target);
            SendPacket(HapbeatProtocol.CMD_PLAY, payload);
        }

        /// <summary>Send a STOP command. <paramref name="target"/> is the device-addressing
        /// target string ("" = broadcast).</summary>
        public void SendStop(string eventId, string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildStopPayload(eventId, target);
            SendPacket(HapbeatProtocol.CMD_STOP, payload);
        }

        /// <summary>Send a STOP_ALL command. <paramref name="target"/> is the device-addressing
        /// target string ("" = broadcast).</summary>
        public void SendStopAll(string target = null)
        {
            target = ResolveTarget(target);
            byte[] payload = HapbeatProtocol.BuildStopAllPayload(target);
            SendPacket(HapbeatProtocol.CMD_STOP_ALL, payload);
        }

        /// <summary>
        /// Send a CONNECT_STATUS command so the device can show connection state on display/LED.
        /// </summary>
        public void SendConnectStatus(bool connected, byte group, string appName = "", string deviceName = "")
        {
            byte[] payload = HapbeatProtocol.BuildConnectStatusPayload(connected, group, appName, deviceName);
            SendPacket(HapbeatProtocol.CMD_CONNECT_STATUS, payload);
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
            SendRaw(packet);
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

        private void SendRaw(byte[] data)
        {
            if (!IsConnected || _udpClient == null)
                return;

            try
            {
                _udpClient.Send(data, data.Length, _targetEndPoint);
            }
            catch (SocketException ex)
            {
                UnityEngine.Debug.LogWarning($"[Hapbeat] Send failed: {ex.Message}");
                HandleDisconnection();
            }
            catch (ObjectDisposedException)
            {
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
            if (!IsConnected || _udpClient == null)
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
                    _udpClient.Send(data, data.Length, targets[i]);
                }
                catch (SocketException ex)
                {
                    // A single unreachable/offline target shouldn't tear down the
                    // whole session (other targets may still be fine) — log and
                    // keep sending to the rest.
                    UnityEngine.Debug.LogWarning(
                        $"[Hapbeat] Stream unicast send to {targets[i]} failed: {ex.Message}");
                }
                catch (ObjectDisposedException)
                {
                    HandleDisconnection();
                    return;
                }
            }
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
                catch (SocketException)
                {
                    if (_isRunning)
                    {
                        EnqueueMainThread(() => HandleDisconnection());
                    }
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
            // GetKnownDeviceAddress.
            if (!string.IsNullOrEmpty(address))
                _deviceAddresses[sender.Address] = address;

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
