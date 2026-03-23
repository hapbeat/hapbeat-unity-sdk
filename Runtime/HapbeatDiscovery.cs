using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Discovers Hapbeat devices on the local network using UDP broadcast PING.
    /// </summary>
    public class HapbeatDiscovery : IDisposable
    {
        /// <summary>Invoked on main thread when a device is discovered.</summary>
        public event Action<HapbeatDevice> OnDeviceFound;

        /// <summary>Invoked on main thread when discovery completes.</summary>
        public event Action<List<HapbeatDevice>> OnDiscoveryComplete;

        private readonly List<HapbeatDevice> _discoveredDevices = new List<HapbeatDevice>();
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private Thread _discoveryThread;
        private volatile bool _isDiscovering;
        private bool _disposed;

        /// <summary>Whether a discovery scan is currently in progress.</summary>
        public bool IsDiscovering => _isDiscovering;

        /// <summary>List of devices found in the last discovery scan.</summary>
        public IReadOnlyList<HapbeatDevice> DiscoveredDevices => _discoveredDevices.AsReadOnly();

        /// <summary>
        /// Start a discovery scan. Sends broadcast PING and listens for PONG responses.
        /// </summary>
        /// <param name="timeoutMs">How long to listen for responses (default: 3000ms).</param>
        /// <param name="port">Target port (default: 7700).</param>
        public void Discover(int timeoutMs = 3000, int port = 7700)
        {
            if (_isDiscovering) return;

            _isDiscovering = true;
            _discoveredDevices.Clear();

            _discoveryThread = new Thread(() => DiscoveryLoop(timeoutMs, port))
            {
                Name = "HapbeatDiscovery",
                IsBackground = true
            };
            _discoveryThread.Start();
        }

        /// <summary>
        /// Process queued callbacks on the main thread. Call from Update().
        /// </summary>
        public void DispatchCallbacks()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        private void DiscoveryLoop(int timeoutMs, int port)
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = 500; // 500ms poll interval
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

                    // Build and send PING
                    long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                    byte[] pingPayload = HapbeatProtocol.BuildPingPayload(timestamp);
                    byte[] pingPacket = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_PING, 0, pingPayload);

                    var broadcastEp = new IPEndPoint(IPAddress.Broadcast, port);
                    udp.Send(pingPacket, pingPacket.Length, broadcastEp);

                    // Collect responses
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    var seenIPs = new HashSet<string>();

                    while (DateTime.UtcNow < deadline && _isDiscovering)
                    {
                        try
                        {
                            IPEndPoint remoteEp = null;
                            byte[] data = udp.Receive(ref remoteEp);

                            if (data.Length < HapbeatProtocol.HEADER_SIZE) continue;

                            // Parse header
                            var (cmdType, seq, payload) = HapbeatProtocol.ParsePacket(data);
                            if (cmdType != HapbeatProtocol.CMD_PONG) continue;

                            string ip = remoteEp.Address.ToString();
                            if (seenIPs.Contains(ip)) continue;
                            seenIPs.Add(ip);

                            // Parse extended PONG
                            var device = ParseDevicePong(payload, ip);
                            if (device != null)
                            {
                                _discoveredDevices.Add(device);
                                var d = device; // capture for lambda
                                _mainThreadQueue.Enqueue(() => OnDeviceFound?.Invoke(d));
                            }
                        }
                        catch (SocketException)
                        {
                            // Timeout, continue listening
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hapbeat] Discovery error: {ex.Message}");
            }
            finally
            {
                _isDiscovering = false;
                var devices = new List<HapbeatDevice>(_discoveredDevices);
                _mainThreadQueue.Enqueue(() => OnDiscoveryComplete?.Invoke(devices));
            }
        }

        /// <summary>
        /// Parse a device's extended PONG payload into a HapbeatDevice.
        /// Payload format: timestamp(8) + server_time(8) + device_name(null-term) + group(1) + fw_version(null-term)
        /// </summary>
        private HapbeatDevice ParseDevicePong(byte[] payload, string ipAddress)
        {
            if (payload.Length < 16) return null;

            int offset = 16; // Skip timestamp and server_time

            // Device name (null-terminated string)
            string deviceName = "";
            if (offset < payload.Length)
            {
                int nameEnd = Array.IndexOf(payload, (byte)0, offset);
                if (nameEnd < 0) return null;
                deviceName = Encoding.UTF8.GetString(payload, offset, nameEnd - offset);
                offset = nameEnd + 1;
            }

            // Group (uint8)
            byte group = 0;
            if (offset < payload.Length)
            {
                group = payload[offset];
                offset += 1;
            }

            // Firmware version (null-terminated string)
            string fwVersion = "";
            if (offset < payload.Length)
            {
                int fwEnd = Array.IndexOf(payload, (byte)0, offset);
                if (fwEnd < 0) fwEnd = payload.Length;
                fwVersion = Encoding.UTF8.GetString(payload, offset, fwEnd - offset);
            }

            return new HapbeatDevice
            {
                deviceId = ipAddress,
                name = deviceName,
                group = group,
                firmwareVersion = fwVersion,
                ipAddress = ipAddress,
                lastSeen = DateTime.UtcNow
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _isDiscovering = false;
        }
    }
}
