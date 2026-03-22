using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Hapbeat
{
    /// <summary>
    /// Internal UDP client for communicating with the Hapbeat Bridge.
    /// Handles packet sending, receiving, and sequence number management.
    /// Receive runs on a background thread; callbacks are queued for main-thread dispatch.
    /// </summary>
    public class HapbeatClient : IDisposable
    {
        /// <summary>Invoked on main thread when a PONG response is received.</summary>
        public event Action<long, long> OnPong; // (rttUs, serverTimeUs)

        /// <summary>Invoked on main thread when an ERROR response is received.</summary>
        public event Action<ushort, string> OnError; // (errorCode, message)

        /// <summary>Invoked on main thread when connection state changes.</summary>
        public event Action<bool> OnConnectionStateChanged; // (isConnected)

        /// <summary>Whether the client is currently connected to the Bridge.</summary>
        public bool IsConnected { get; private set; }

        private UdpClient _udpClient;
        private IPEndPoint _remoteEndPoint;
        private Thread _receiveThread;
        private volatile bool _isRunning;
        private ushort _sequenceNumber;
        private readonly object _seqLock = new object();
        private readonly Stopwatch _stopwatch;

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
        /// Connect to the Hapbeat Bridge at the specified host and port.
        /// </summary>
        /// <param name="host">Bridge hostname or IP address.</param>
        /// <param name="port">Bridge UDP port.</param>
        public void Connect(string host, int port)
        {
            if (IsConnected)
                Disconnect();

            try
            {
                _remoteEndPoint = new IPEndPoint(IPAddress.Parse(host), port);
                _udpClient = new UdpClient();
                _udpClient.Connect(_remoteEndPoint);

                _isRunning = true;
                _receiveThread = new Thread(ReceiveLoop)
                {
                    Name = "HapbeatReceive",
                    IsBackground = true
                };
                _receiveThread.Start();

                IsConnected = true;
                EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(true));
            }
            catch (Exception ex)
            {
                IsConnected = false;
                throw new InvalidOperationException(
                    $"Failed to connect to Hapbeat Bridge at {host}:{port}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Disconnect from the Hapbeat Bridge.
        /// </summary>
        public void Disconnect()
        {
            if (!IsConnected)
                return;

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
            _pendingPings.Clear();

            EnqueueMainThread(() => OnConnectionStateChanged?.Invoke(false));
        }

        /// <summary>
        /// Send a PLAY command to the Bridge.
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="targetTimeUs">Target time in microseconds.</param>
        /// <param name="group">Target group ID.</param>
        /// <param name="gain">Gain multiplier.</param>
        public void SendPlay(string eventId, long targetTimeUs, byte group, float gain)
        {
            byte[] payload = HapbeatProtocol.BuildPlayPayload(eventId, targetTimeUs, group, gain);
            SendPacket(HapbeatProtocol.CMD_PLAY, payload);
        }

        /// <summary>
        /// Send a STOP command to the Bridge.
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="group">Target group ID.</param>
        public void SendStop(string eventId, byte group)
        {
            byte[] payload = HapbeatProtocol.BuildStopPayload(eventId, group);
            SendPacket(HapbeatProtocol.CMD_STOP, payload);
        }

        /// <summary>
        /// Send a STOP_ALL command to the Bridge.
        /// </summary>
        /// <param name="group">Target group ID.</param>
        public void SendStopAll(byte group)
        {
            byte[] payload = HapbeatProtocol.BuildStopAllPayload(group);
            SendPacket(HapbeatProtocol.CMD_STOP_ALL, payload);
        }

        /// <summary>
        /// Send a PING command to the Bridge for keep-alive and time synchronization.
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
        /// <returns>Timestamp in microseconds.</returns>
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
                _udpClient.Send(data, data.Length);
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

                        IPEndPoint remoteEp = null;
                        byte[] data = _udpClient.Receive(ref remoteEp);

                        if (data != null && data.Length >= HapbeatProtocol.HEADER_SIZE)
                        {
                            ProcessReceivedPacket(data);
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

        private void ProcessReceivedPacket(byte[] data)
        {
            try
            {
                var (commandType, seq, payload) = HapbeatProtocol.ParsePacket(data);

                switch (commandType)
                {
                    case HapbeatProtocol.CMD_PONG:
                        HandlePong(seq, payload);
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

        private void HandlePong(ushort seq, byte[] payload)
        {
            var (timestamp, serverTime) = HapbeatProtocol.ParsePong(payload);
            long nowUs = GetLocalTimestampUs();

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

            EnqueueMainThread(() => OnPong?.Invoke(rttUs, serverTime));
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
