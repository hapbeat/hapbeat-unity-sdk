using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Main singleton manager for the Hapbeat SDK.
    /// Provides the public API for triggering haptic events via Wi-Fi UDP broadcast (standard) or Bridge (ESP-NOW).
    /// Attach this component to a GameObject in your scene, or it will create itself automatically.
    /// </summary>
    public class HapbeatManager : MonoBehaviour
    {
        /// <summary>Singleton instance of HapbeatManager.</summary>
        public static HapbeatManager Instance { get; private set; }

        /// <summary>Invoked when the client is ready to send.</summary>
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

        /// <summary>Whether the client is currently ready to send.</summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>Whether the client is in broadcast mode.</summary>
        public bool IsBroadcast => _client != null && _client.IsBroadcast;

        /// <summary>
        /// Estimated time offset between local clock and remote clock in microseconds.
        /// Calculated from PONG responses: remoteTime = localTime + TimeOffsetUs.
        /// </summary>
        public long TimeOffsetUs { get; private set; }

        /// <summary>The default group ID from configuration.</summary>
        /// <summary>The default group ID. -1 maps to 0 (broadcast).</summary>
        public byte DefaultGroup => _config != null && _config.group >= 0 ? (byte)_config.group : (byte)0;

        /// <summary>App name shown on device OLED. Uses config value or falls back to Application.productName.</summary>
        public string AppName => _config != null && !string.IsNullOrEmpty(_config.appName)
            ? _config.appName
            : Application.productName;

        /// <summary>Internal UDP client. Used by HapbeatAudioBridge for real-time streaming.</summary>
        internal HapbeatClient Client => _client;

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

            // Periodic ping + connect status for keep-alive and device display
            if (IsConnected && _config != null && _config.pingInterval > 0)
            {
                if (Time.realtimeSinceStartup - _lastPingTime >= _config.pingInterval)
                {
                    Ping();
                    _client.SendConnectStatus(true, DefaultGroup, AppName, SystemInfo.deviceName);
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
        /// Uses the default group from config if not specified.
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="gain">Gain multiplier (0.0 to 1.0+). Default is 1.0.</param>
        /// <param name="group">Target group ID. -1 = use config default, 0 = all devices. Ignored when target is set.</param>
        /// <param name="displayName">Optional display name for logging (e.g. "Grab"). Not sent to devices.</param>
        /// <param name="target">Path-based target filter (e.g. "player_1/pos_neck"). Empty = broadcast.</param>
        public void Play(string eventId, float gain = 1.0f, int group = -1,
            string displayName = null, string target = null)
        {
            if (!EnsureConnected())
                return;

            byte g = ResolveGroup(group);
            long targetTimeUs = 0;
            _client.SendPlay(eventId, targetTimeUs, g, gain, target);

            string label = string.IsNullOrEmpty(displayName) ? eventId : $"{displayName} ({eventId})";
            string targetInfo = string.IsNullOrEmpty(target) ? $"group={g}" : $"target={target}";
            Log($"\u25b6 Play \"{label}\" gain={gain:F1} {targetInfo}");
        }

        /// <summary>
        /// Schedule a haptic event to play at a specific target time.
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="targetTimeUs">Target time in microseconds (remote clock).</param>
        /// <param name="gain">Gain multiplier (0.0 to 1.0+). Default is 1.0.</param>
        /// <param name="group">Target group ID. -1 = use config default, 0 = all devices.</param>
        public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, int group = -1)
        {
            if (!EnsureConnected())
                return;

            byte g = ResolveGroup(group);
            _client.SendPlay(eventId, targetTimeUs, g, gain);
            Log($"\u25b6 PlayScheduled \"{eventId}\" (target={targetTimeUs}us, gain={gain:F1}, group={g})");
        }

        /// <summary>
        /// Stop a specific haptic event.
        /// </summary>
        /// <param name="eventId">Event identifier to stop.</param>
        /// <param name="group">Target group ID. -1 = use config default, 0 = all devices.</param>
        /// <param name="displayName">Optional display name for logging. Not sent to devices.</param>
        public void Stop(string eventId, int group = -1, string displayName = null)
        {
            if (!EnsureConnected())
                return;

            byte g = ResolveGroup(group);
            _client.SendStop(eventId, g);

            string label = string.IsNullOrEmpty(displayName) ? eventId : $"{displayName} ({eventId})";
            Log($"\u25a0 Stop \"{label}\" group={g}");
        }

        /// <summary>
        /// Stop all haptic events.
        /// </summary>
        /// <param name="group">Target group ID. -1 = use config default, 0 = all devices.</param>
        public void StopAll(int group = -1)
        {
            if (!EnsureConnected())
                return;

            byte g = ResolveGroup(group);
            _client.SendStopAll(g);
            Log($"\u25a0 StopAll (group={g})");
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
        /// Standard mode (Wi-Fi UDP): opens broadcast sending immediately.
        /// Bridge mode (ESP-NOW): connects to the configured Bridge host.
        /// </summary>
        public void Connect()
        {
            if (IsConnected)
            {
                Log("Already connected.");
                return;
            }

            if (_client == null)
            {
                _client = CreateClient();
            }

            int port = _config != null ? _config.port : 7700;

            if (_config != null && _config.useBridge)
            {
                ConnectToBridge();
            }
            else
            {
                try
                {
                    _client.OpenBroadcast(port);
                    Log($"Broadcast mode opened on port {port} (group={DefaultGroup})");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Hapbeat] Failed to open broadcast: {ex.Message}");
                    OnError?.Invoke(ex.Message);
                }
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
                Debug.LogError($"[Hapbeat] Bridge connection failed: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }
        }

        /// <summary>
        /// Disconnect from the current session.
        /// </summary>
        public void Disconnect()
        {
            if (_client == null)
                return;

            // Notify devices that app is disconnecting
            if (_client.IsConnected)
                _client.SendConnectStatus(false, DefaultGroup, AppName, SystemInfo.deviceName);

            _client.Disconnect();
            Log("Disconnected.");
        }

        /// <summary>
        /// Discover Hapbeat devices on the local network via UDP broadcast PING.
        /// This is for UI display / diagnostics — not required for sending commands.
        /// Results are available via DiscoveredDevices property.
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
        /// Stream a Unity AudioClip to Hapbeat devices as PCM16 audio via UDP.
        /// The audio is sent in chunks that fit within MTU limits.
        /// Non-blocking: runs as a coroutine.
        /// </summary>
        /// <param name="clip">AudioClip to stream (will be read as PCM16).</param>
        /// <param name="gain">Gain multiplier for playback on device (0.0 - 2.0).</param>
        /// <param name="target">Optional target filter (e.g. "player_1/pos_neck"). Null = broadcast.</param>
        /// <param name="loop">If true, keep restarting the stream (with a fresh STREAM_BEGIN each
        /// iteration) until <see cref="StopStream"/> is called. Useful for the hold phase of
        /// grab/hold/release sequences.</param>
        public void StreamAudioClip(AudioClip clip, float gain = 1.0f, string target = null, bool loop = false)
        {
            if (!EnsureConnected()) return;
            if (clip == null)
            {
                Debug.LogWarning("[Hapbeat] StreamAudioClip: clip is null.");
                return;
            }
            if (_streamCoroutine != null)
                StopStream();
            string targetInfo = string.IsNullOrEmpty(target) ? "broadcast" : $"target={target}";
            string loopInfo = loop ? " (loop)" : "";
            Log($"\u266a StreamClip: {clip.name}, freq={clip.frequency}, ch={clip.channels}, gain={gain}, {targetInfo}{loopInfo}");
            _streamCoroutine = StartCoroutine(StreamAudioClipCoroutine(clip, gain, target, loop));
        }

        /// <summary>
        /// Stop the current audio stream (if any).
        /// </summary>
        public void StopStream()
        {
            if (_streamCoroutine != null)
            {
                StopCoroutine(_streamCoroutine);
                _streamCoroutine = null;
            }
            if (_client != null && _client.IsConnected)
            {
                _client.SendStreamEnd();
                Log("Stream stopped.");
            }
        }

        /// <summary>Whether audio streaming is currently in progress.</summary>
        public bool IsStreaming => _streamCoroutine != null;

        private Coroutine _streamCoroutine;

        private IEnumerator StreamAudioClipCoroutine(AudioClip clip, float gain, string target = null, bool loop = false)
        {
            // Extract PCM data once — reused across iterations when looping.
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            // Convert float [-1,1] to PCM16 (little-endian)
            byte[] pcmBytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short pcm16 = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                pcmBytes[i * 2] = (byte)(pcm16 & 0xFF);
                pcmBytes[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
            }

            byte channels = (byte)clip.channels;
            ushort sampleRate = (ushort)clip.frequency;
            uint totalSamples = (uint)clip.samples;
            int maxChunkSize = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
            float bytesPerSecond = sampleRate * channels * 2f;
            const float sendAheadSeconds = 0.1f; // 100ms buffer

            int iteration = 0;
            do
            {
                iteration++;

                // Each loop iteration starts with its own STREAM_BEGIN / STREAM_END pair.
                // This keeps the device-side decoder in a clean state between cycles and
                // avoids buffer-offset wraparound issues on long-running holds.
                _client.SendStreamBegin(sampleRate, channels, HapbeatProtocol.AUDIO_FORMAT_PCM16, totalSamples, gain, target);
                if (iteration == 1)
                    Log($"Stream begin: {sampleRate}Hz, {channels}ch, {totalSamples} samples, gain={gain}");

                uint byteOffset = 0;
                int remaining = pcmBytes.Length;
                float startTime = Time.realtimeSinceStartup;

                while (remaining > 0 && _client != null && _client.IsConnected)
                {
                    int chunkSize = Mathf.Min(remaining, maxChunkSize);
                    _client.SendStreamData(byteOffset, pcmBytes, (int)byteOffset, chunkSize);

                    byteOffset += (uint)chunkSize;
                    remaining -= chunkSize;

                    // Pace: wait if we're sending too far ahead of real-time
                    float elapsedTime = Time.realtimeSinceStartup - startTime;
                    float sentDuration = byteOffset / bytesPerSecond;
                    if (sentDuration > elapsedTime + sendAheadSeconds)
                        yield return null;
                }

                // STREAM_END between iterations (also the terminal end for non-loop case).
                if (_client != null && _client.IsConnected)
                    _client.SendStreamEnd();

                if (!loop) break;
                // Bail out if the user called StopStream() during playback — StopStream
                // invokes StopCoroutine, which normally terminates this IEnumerator, but
                // belt-and-braces: also check _streamCoroutine here.
                if (_streamCoroutine == null) break;
            }
            while (loop);

            if (iteration > 1)
                Log($"Stream loop stopped after {iteration} iterations.");
            else
                Log($"Stream complete.");

            _streamCoroutine = null;
        }

        #endregion

        #region Private Methods

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

        private byte ResolveGroup(int group)
        {
            if (group >= 0)
                return (byte)group;
            return DefaultGroup;
        }

        private HapbeatClient CreateClient()
        {
            var client = new HapbeatClient();

            client.OnConnectionStateChanged += (connected) =>
            {
                if (connected)
                {
                    string mode = client.IsBroadcast ? "broadcast" : "unicast";
                    Log($"Ready ({mode}).");
                    _lastPingTime = Time.realtimeSinceStartup;
                    // Notify devices that app is connected
                    client.SendConnectStatus(true, DefaultGroup, AppName, SystemInfo.deviceName);
                    OnConnected?.Invoke();
                }
                else
                {
                    Log("Disconnected.");
                    OnDisconnected?.Invoke();
                }
            };

            client.OnPong += (rttUs, serverTimeUs) =>
            {
                // Calculate time offset: remoteTime = localTime + offset
                long localTimeUs = _client.GetLocalTimestampUs();
                long halfRtt = rttUs / 2;
                TimeOffsetUs = serverTimeUs - (localTimeUs - halfRtt);

                LogVerbose($"PONG received: RTT={rttUs}us, offset={TimeOffsetUs}us");
                OnPong?.Invoke(rttUs);
            };

            client.OnError += (errorCode, message) =>
            {
                string errorMsg = $"Error (code={errorCode}): {message}";
                Debug.LogWarning($"[Hapbeat] {errorMsg}");
                OnError?.Invoke(errorMsg);
            };

            return client;
        }

        private bool EnsureConnected()
        {
            if (IsConnected)
                return true;

            Debug.LogWarning("[Hapbeat] Not ready. Call Connect() first.");
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

        private void LogVerbose(string message)
        {
            if (_config != null && _config.verboseLogging)
            {
                Debug.Log($"[Hapbeat] {message}");
            }
        }

        #endregion
    }
}
