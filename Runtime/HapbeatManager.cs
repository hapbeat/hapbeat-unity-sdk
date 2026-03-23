using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Main singleton manager for the Hapbeat SDK.
    /// Provides the public API for triggering haptic events via Wi-Fi UDP (standard) or Bridge (ESP-NOW).
    /// Attach this component to a GameObject in your scene, or it will create itself automatically.
    /// </summary>
    public class HapbeatManager : MonoBehaviour
    {
        /// <summary>Singleton instance of HapbeatManager.</summary>
        public static HapbeatManager Instance { get; private set; }

        /// <summary>Invoked when the client connects to a device or Bridge.</summary>
        public event Action OnConnected;

        /// <summary>Invoked when the client disconnects.</summary>
        public event Action OnDisconnected;

        /// <summary>Invoked when an error is received.</summary>
        public event Action<string> OnError;

        /// <summary>Invoked when a PONG response is received. Parameter is round-trip time in microseconds.</summary>
        public event Action<long> OnPong;

        [Header("Configuration")]
        [Tooltip("Hapbeat configuration asset. If not set, default settings will be used.")]
        [SerializeField]
        private HapbeatConfig _config;

        /// <summary>Whether the client is currently connected.</summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>
        /// Estimated time offset between local clock and remote clock in microseconds.
        /// Calculated from PONG responses: remoteTime = localTime + TimeOffsetUs.
        /// </summary>
        public long TimeOffsetUs { get; private set; }

        private HapbeatClient _client;
        private HapbeatDiscovery _discovery;
        private float _lastPingTime;
        private bool _isInitialized;

        /// <summary>List of discovered devices from the last scan.</summary>
        public IReadOnlyList<HapbeatDevice> DiscoveredDevices => _discovery?.DiscoveredDevices ?? new List<HapbeatDevice>().AsReadOnly();

        #region Unity Lifecycle

        private void Awake()
        {
            // Singleton pattern
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Hapbeat] Duplicate HapbeatManager detected. Destroying this instance.");
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            Initialize();
        }

        private void Update()
        {
            // Dispatch discovery callbacks
            _discovery?.DispatchCallbacks();

            if (_client == null)
                return;

            // Dispatch queued callbacks from the receive thread
            _client.DispatchMainThreadCallbacks();

            // Periodic ping for keep-alive and time synchronization
            if (IsConnected && _config != null && _config.pingInterval > 0)
            {
                if (Time.realtimeSinceStartup - _lastPingTime >= _config.pingInterval)
                {
                    Ping();
                    _lastPingTime = Time.realtimeSinceStartup;
                }
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Cleanup();
                Instance = null;
            }
        }

        private void OnApplicationQuit()
        {
            Cleanup();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Play a haptic event immediately.
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="gain">Gain multiplier (0.0 to 1.0+). Default is 1.0.</param>
        /// <param name="group">Target group ID. 0 = broadcast to all devices.</param>
        public void Play(string eventId, float gain = 1.0f, byte group = 0)
        {
            if (!EnsureConnected())
                return;

            long targetTimeUs = 0; // 0 means play immediately
            _client.SendPlay(eventId, targetTimeUs, group, gain);
            Log($"Play: eventId={eventId}, gain={gain}, group={group}");
        }

        /// <summary>
        /// Schedule a haptic event to play at a specific target time.
        /// </summary>
        /// <param name="eventId">Event identifier registered in the Bridge.</param>
        /// <param name="targetTimeUs">Target time in microseconds (Bridge clock).</param>
        /// <param name="gain">Gain multiplier (0.0 to 1.0+). Default is 1.0.</param>
        /// <param name="group">Target group ID. 0 = broadcast to all devices.</param>
        public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, byte group = 0)
        {
            if (!EnsureConnected())
                return;

            _client.SendPlay(eventId, targetTimeUs, group, gain);
            Log($"PlayScheduled: eventId={eventId}, targetTimeUs={targetTimeUs}, gain={gain}, group={group}");
        }

        /// <summary>
        /// Stop a specific haptic event.
        /// </summary>
        /// <param name="eventId">Event identifier to stop.</param>
        /// <param name="group">Target group ID. 0 = broadcast to all devices.</param>
        public void Stop(string eventId, byte group = 0)
        {
            if (!EnsureConnected())
                return;

            _client.SendStop(eventId, group);
            Log($"Stop: eventId={eventId}, group={group}");
        }

        /// <summary>
        /// Stop all haptic events.
        /// </summary>
        /// <param name="group">Target group ID. 0 = broadcast to all devices.</param>
        public void StopAll(byte group = 0)
        {
            if (!EnsureConnected())
                return;

            _client.SendStopAll(group);
            Log($"StopAll: group={group}");
        }

        /// <summary>
        /// Send a PING for keep-alive and time synchronization.
        /// </summary>
        public void Ping()
        {
            if (!EnsureConnected())
                return;

            _client.SendPing();
        }

        /// <summary>
        /// Connect using the current configuration.
        /// In standard mode (Wi-Fi UDP): discovers devices and connects to the first one found.
        /// In Bridge mode (ESP-NOW): connects to the configured Bridge host.
        /// </summary>
        public void Connect()
        {
            if (IsConnected)
            {
                Log("Already connected.");
                return;
            }

            if (_config != null && _config.useBridge)
            {
                ConnectToBridge();
            }
            else
            {
                AutoConnect(_config != null ? _config.discoveryTimeoutMs : 3000);
            }
        }

        /// <summary>
        /// Connect to the Hapbeat Bridge (ESP-NOW mode).
        /// </summary>
        public void ConnectToBridge()
        {
            if (_client == null)
            {
                _client = CreateClient();
            }

            if (IsConnected)
            {
                Log("Already connected.");
                return;
            }

            string host = _config != null ? _config.bridgeHost : "127.0.0.1";
            int port = _config != null ? _config.port : 7700;

            try
            {
                _client.Connect(host, port);
                Log($"Connecting to Bridge at {host}:{port}...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Hapbeat] Connection failed: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Disconnect from the current device or Bridge.
        /// </summary>
        public void Disconnect()
        {
            if (_client == null)
                return;

            _client.Disconnect();
            Log("Disconnected.");
        }

        /// <summary>
        /// Discover Hapbeat devices on the local network via UDP broadcast.
        /// Results are available via DiscoveredDevices and OnDeviceFound event.
        /// </summary>
        /// <param name="timeoutMs">Discovery timeout in milliseconds.</param>
        public void Discover(int timeoutMs = 3000)
        {
            if (_discovery == null)
            {
                _discovery = new HapbeatDiscovery();
                _discovery.OnDeviceFound += (device) =>
                {
                    Log($"Device found: {device.name} at {device.ipAddress} (group={device.group})");
                };
                _discovery.OnDiscoveryComplete += (devices) =>
                {
                    Log($"Discovery complete: {devices.Count} device(s) found");
                };
            }

            int port = _config != null ? _config.port : 7700;
            _discovery.Discover(timeoutMs, port);
            Log("Starting device discovery...");
        }

        /// <summary>
        /// Connect directly to a Hapbeat device by IP address (Wi-Fi UDP direct).
        /// </summary>
        /// <param name="ipAddress">Device IP address.</param>
        /// <param name="port">Device UDP port (default: 7700).</param>
        public void ConnectToDevice(string ipAddress, int port = 7700)
        {
            if (_client == null)
            {
                _client = CreateClient();
            }

            if (IsConnected)
            {
                Disconnect();
            }

            try
            {
                _client.Connect(ipAddress, port);
                Log($"Connecting to device at {ipAddress}:{port}...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Hapbeat] Connection to device failed: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Discover devices and auto-connect to the first one found.
        /// </summary>
        /// <param name="timeoutMs">Discovery timeout in milliseconds.</param>
        public void AutoConnect(int timeoutMs = 3000)
        {
            if (_discovery == null)
            {
                _discovery = new HapbeatDiscovery();
            }

            _discovery.OnDiscoveryComplete += OnAutoConnectDiscoveryComplete;

            int port = _config != null ? _config.port : 7700;
            _discovery.Discover(timeoutMs, port);
            Log("Auto-connect: starting discovery...");
        }

        #endregion

        #region Private Methods

        private void OnAutoConnectDiscoveryComplete(List<HapbeatDevice> devices)
        {
            // Unsubscribe to avoid multiple connections on repeated calls
            _discovery.OnDiscoveryComplete -= OnAutoConnectDiscoveryComplete;

            if (devices.Count > 0)
            {
                var device = devices[0];
                int port = _config != null ? _config.port : 7700;
                ConnectToDevice(device.ipAddress, port);
                Log($"Auto-connect: connecting to {device.name} at {device.ipAddress}");
            }
            else
            {
                Log("Auto-connect: no devices found, falling back to config host");
                Connect(); // Fall back to configured bridgeHost
            }
        }

        private void Initialize()
        {
            if (_isInitialized)
                return;

            // Find config asset if not assigned
            if (_config == null)
            {
                _config = Resources.Load<HapbeatConfig>("HapbeatConfig");
            }

            // Create default config if none found
            if (_config == null)
            {
                _config = ScriptableObject.CreateInstance<HapbeatConfig>();
                Log("No HapbeatConfig found. Using default settings.");
            }

            _client = CreateClient();
            _isInitialized = true;

            // Auto-connect on startup
            Connect();
        }

        private HapbeatClient CreateClient()
        {
            var client = new HapbeatClient();

            client.OnConnectionStateChanged += (connected) =>
            {
                if (connected)
                {
                    Log("Connected to Bridge.");
                    _lastPingTime = Time.realtimeSinceStartup;
                    OnConnected?.Invoke();
                }
                else
                {
                    Log("Disconnected from Bridge.");
                    OnDisconnected?.Invoke();
                }
            };

            client.OnPong += (rttUs, serverTimeUs) =>
            {
                // Calculate time offset: bridgeTime = localTime + offset
                long localTimeUs = _client.GetLocalTimestampUs();
                long halfRtt = rttUs / 2;
                BridgeTimeOffsetUs = serverTimeUs - (localTimeUs - halfRtt);

                Log($"PONG received: RTT={rttUs}us, offset={BridgeTimeOffsetUs}us");
                OnPong?.Invoke(rttUs);
            };

            client.OnError += (errorCode, message) =>
            {
                string errorMsg = $"Bridge error (code={errorCode}): {message}";
                Debug.LogWarning($"[Hapbeat] {errorMsg}");
                OnError?.Invoke(errorMsg);
            };

            return client;
        }

        private bool EnsureConnected()
        {
            if (IsConnected)
                return true;

            Debug.LogWarning("[Hapbeat] Not connected to Bridge. Call Connect() first.");
            return false;
        }

        private void Cleanup()
        {
            _discovery?.Dispose();
            _discovery = null;

            if (_client != null)
            {
                _client.Dispose();
                _client = null;
            }

            _isInitialized = false;
        }

        private void Log(string message)
        {
            if (_config != null && _config.enableLogging)
            {
                Debug.Log($"[Hapbeat] {message}");
            }
        }

        #endregion
    }
}
