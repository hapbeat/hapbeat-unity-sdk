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

        /// <summary>Internal UDP client.</summary>
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
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="gain">Gain multiplier (0.0 to 1.0+). Default is 1.0.</param>
        /// <param name="displayName">Optional display name for logging (e.g. "Grab"). Not sent to devices.</param>
        /// <param name="target">Device-addressing target string. Empty = broadcast. See contracts/specs/device-addressing.md (e.g. "player_1/pos_neck", "*/pos_chest").</param>
        public void Play(string eventId, float gain = 1.0f, string displayName = null, string target = null)
        {
            if (!EnsureConnected())
                return;

            long targetTimeUs = 0;
            _client.SendPlay(eventId, targetTimeUs, gain, target);

            string label = string.IsNullOrEmpty(displayName) ? eventId : $"{displayName} ({eventId})";
            string targetInfo = string.IsNullOrEmpty(target) ? "(broadcast)" : $"target={target}";
            Log($"\u25b6 Play \"{label}\" gain={gain:F1} {targetInfo}");
        }

        /// <summary>
        /// Schedule a haptic event to play at a specific target time.
        /// </summary>
        public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, string target = null)
        {
            if (!EnsureConnected())
                return;
            _client.SendPlay(eventId, targetTimeUs, gain, target);
            Log($"\u25b6 PlayScheduled \"{eventId}\" (target_time={targetTimeUs}us, gain={gain:F1}, target={target ?? "(broadcast)"})");
        }

        /// <summary>
        /// Stop a specific haptic event.
        /// </summary>
        /// <param name="target">Device-addressing target string. Empty = broadcast.</param>
        public void Stop(string eventId, string displayName = null, string target = null)
        {
            if (!EnsureConnected())
                return;
            _client.SendStop(eventId, target);

            string label = string.IsNullOrEmpty(displayName) ? eventId : $"{displayName} ({eventId})";
            string targetInfo = string.IsNullOrEmpty(target) ? "(broadcast)" : $"target={target}";
            Log($"\u25a0 Stop \"{label}\" {targetInfo}");
        }

        /// <summary>
        /// Stop all haptic events.
        /// </summary>
        /// <param name="target">Device-addressing target string. Empty = broadcast (stop all on every matching device).</param>
        public void StopAll(string target = null)
        {
            if (!EnsureConnected())
                return;
            _client.SendStopAll(target);
            Log($"\u25a0 StopAll target={target ?? "(broadcast)"}");
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
        /// The audio is sent in chunks that fit within MTU limits. Runs as a
        /// coroutine (non-blocking).
        ///
        /// <para>
        /// Returns a <see cref="HapbeatStreamPlayback"/> handle whose <c>Gain</c>
        /// and <c>Pan</c> properties can be written each frame to modulate the
        /// stream continuously. This is the mechanism <see cref="HapbeatParameterBinding"/>
        /// uses to map game state (velocity, position, …) to haptic intensity /
        /// stereo balance in real time.
        /// </para>
        ///
        /// <para>
        /// At most one stream is active at a time. Calling this while another
        /// stream is in flight stops the previous one first.
        /// </para>
        /// </summary>
        /// <param name="clip">AudioClip to stream (will be read as PCM16).</param>
        /// <param name="gain">Initial gain multiplier (0.0 - 2.0). Binding can
        /// override this per frame via the returned handle.</param>
        /// <param name="target">Optional target filter (e.g. "player_1/pos_neck"). Null = broadcast.</param>
        /// <param name="loop">If true, keep restarting the stream (with a fresh STREAM_BEGIN each
        /// iteration) until <see cref="HapbeatStreamPlayback.Stop"/> or
        /// <see cref="StopStream"/> is called. Useful for the hold phase of
        /// grab/hold/release sequences and for continuously-modulated effects
        /// like drag / scrape.</param>
        /// <returns>Handle for runtime control, or <c>null</c> if the stream
        /// couldn't start (not connected, null clip).</returns>
        public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float gain = 1.0f, string target = null, bool loop = false)
        {
            return StreamAudioClip(clip, gain, gain, target, loop);
        }

        /// <summary>
        /// Same as the 4-arg overload, but lets the caller specify an initial
        /// <c>Gain</c> value that differs from the baseline. Used by
        /// <see cref="HapbeatTriggerBase"/> to pre-seed the stream with a
        /// binding-modulated gain so the first audio chunks aren't at full
        /// baseline for ~100 ms before the binding's first <c>Update</c>
        /// writes the modulated value (audible burst otherwise).
        /// </summary>
        public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float baselineGain, float initialGain, string target, bool loop)
        {
            if (!EnsureConnected()) return null;
            if (clip == null)
            {
                Debug.LogWarning("[Hapbeat] StreamAudioClip: clip is null.");
                return null;
            }
            if (_streamCoroutine != null)
                StopStream();

            string targetInfo = string.IsNullOrEmpty(target) ? "broadcast" : $"target={target}";
            string loopInfo = loop ? " (loop)" : "";
            string gainInfo = Mathf.Approximately(baselineGain, initialGain)
                ? $"gain={baselineGain}"
                : $"baseline={baselineGain}, initial={initialGain}";
            Log($"\u266a StreamClip: {clip.name}, freq={clip.frequency}, ch={clip.channels}, {gainInfo}, {targetInfo}{loopInfo}");

            _activePlayback = new HapbeatStreamPlayback(baselineGain, initialGain);
            _streamCoroutine = StartCoroutine(StreamAudioClipCoroutine(clip, _activePlayback, target, loop));
            return _activePlayback;
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
            if (_activePlayback != null)
            {
                _activePlayback.MarkStopped();
                _activePlayback = null;
            }
            if (_client != null && _client.IsConnected)
            {
                _client.SendStreamEnd();
                Log("Stream stopped.");
            }
        }

        /// <summary>Whether audio streaming is currently in progress.</summary>
        public bool IsStreaming => _streamCoroutine != null;

        /// <summary>Handle to the current stream, or null if nothing is streaming.</summary>
        public HapbeatStreamPlayback ActivePlayback => _activePlayback;

        private Coroutine _streamCoroutine;
        private HapbeatStreamPlayback _activePlayback;

        private IEnumerator StreamAudioClipCoroutine(AudioClip clip, HapbeatStreamPlayback playback, string target, bool loop)
        {
            // Extract PCM data once (float samples, interleaved). We convert to
            // PCM16 per chunk so each chunk can pick up the latest gain / pan
            // values from the playback handle — this is what lets
            // HapbeatParameterBinding modulate the stream in real time.
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            byte channels = (byte)clip.channels;
            ushort sampleRate = (ushort)clip.frequency;
            int maxChunkBytes = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
            // Chunk size in SAMPLES (not bytes). Must be a whole number of
            // sample frames so stereo L/R stays aligned at chunk boundaries.
            int bytesPerFrame = channels * 2;
            int samplesPerChunk = (maxChunkBytes / bytesPerFrame) * channels;
            if (samplesPerChunk <= 0) samplesPerChunk = channels; // degenerate fallback
            byte[] pcmChunk = new byte[samplesPerChunk * 2];

            float bytesPerSecond = sampleRate * channels * 2f;
            // Smaller send-ahead buffer makes StopStream() audibly stop faster but
            // raises underrun risk on slow links. Configurable via HapbeatConfig.
            // A floor of 10ms protects against legacy assets that don't have the
            // field serialised yet (Unity defaults to 0 when migrating, which would
            // cause constant underrun).
            float sendAheadSeconds = _config != null ? _config.streamSendAheadSeconds : 0.05f;
            if (sendAheadSeconds < 0.01f) sendAheadSeconds = 0.05f;

            // Seamless loop: emit a SINGLE STREAM_BEGIN up front (totalSamples=0
            // for "unknown length"), then keep feeding STREAM_DATA with a
            // monotonically-increasing global byte offset across iterations so
            // the device sees one continuous stream with no reset seam.
            uint reportedTotalSamples = loop ? 0u : (uint)clip.samples;
            // Send gain=1.0 in STREAM_BEGIN because we pre-multiply the samples
            // below. The device applies STREAM_BEGIN.gain exactly once on its
            // side; if we also sent playback.Gain here, the device would
            // double-attenuate (samples × gain on the SDK side + × gain on the
            // device side = gain² → e.g. 0.3 looks like 0.09). Passing 1.0
            // makes the device a passthrough and keeps the SDK as the single
            // source of truth for the gain value — required for dynamic
            // modulation via HapbeatStreamPlayback.Gain / HapbeatParameterBinding.
            _client.SendStreamBegin(sampleRate, channels, HapbeatProtocol.AUDIO_FORMAT_PCM16,
                reportedTotalSamples, 1.0f, target);
            Log(loop
                ? $"Stream begin (loop): {sampleRate}Hz, {channels}ch, initialGain={playback.Gain:F2} (sent as samples×gain; STREAM_BEGIN=1.0)"
                : $"Stream begin: {sampleRate}Hz, {channels}ch, {clip.samples} samples, initialGain={playback.Gain:F2} (sent as samples×gain; STREAM_BEGIN=1.0)");

            uint globalByteOffset = 0;
            float startTime = Time.realtimeSinceStartup;
            int iteration = 0;
            bool stoppedByUser = false;

            do
            {
                iteration++;
                int sampleCursor = 0;

                while (sampleCursor < samples.Length && _client != null && _client.IsConnected)
                {
                    if (playback.IsStopped) { stoppedByUser = true; break; }

                    int frameCount = Mathf.Min((samples.Length - sampleCursor) / channels, samplesPerChunk / channels);
                    int samplesThisChunk = frameCount * channels;

                    // Read latest modulation values for this chunk.
                    float g = playback.Gain;
                    float gainL, gainR;
                    playback.GetStereoChannelGains(out gainL, out gainR);

                    // Interleaved float -> PCM16 with gain (+ per-channel pan for stereo).
                    if (channels == 2)
                    {
                        for (int i = 0; i < samplesThisChunk; i += 2)
                        {
                            float l = samples[sampleCursor + i]     * g * gainL;
                            float r = samples[sampleCursor + i + 1] * g * gainR;
                            short pcmL = (short)Mathf.Clamp(l * 32767f, -32768f, 32767f);
                            short pcmR = (short)Mathf.Clamp(r * 32767f, -32768f, 32767f);
                            int bi = i * 2;
                            pcmChunk[bi    ] = (byte)(pcmL & 0xFF);
                            pcmChunk[bi + 1] = (byte)((pcmL >> 8) & 0xFF);
                            pcmChunk[bi + 2] = (byte)(pcmR & 0xFF);
                            pcmChunk[bi + 3] = (byte)((pcmR >> 8) & 0xFF);
                        }
                    }
                    else // mono (pan ignored)
                    {
                        for (int i = 0; i < samplesThisChunk; i++)
                        {
                            float v = samples[sampleCursor + i] * g;
                            short pcm = (short)Mathf.Clamp(v * 32767f, -32768f, 32767f);
                            pcmChunk[i * 2    ] = (byte)(pcm & 0xFF);
                            pcmChunk[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                        }
                    }

                    int chunkBytes = samplesThisChunk * 2;
                    _client.SendStreamData(globalByteOffset, pcmChunk, 0, chunkBytes);
                    globalByteOffset += (uint)chunkBytes;
                    sampleCursor += samplesThisChunk;

                    // Pace: wait if we're sending too far ahead of real-time.
                    float elapsedTime = Time.realtimeSinceStartup - startTime;
                    float sentDuration = globalByteOffset / bytesPerSecond;
                    if (sentDuration > elapsedTime + sendAheadSeconds)
                        yield return null;
                }

                if (stoppedByUser) break;
                if (!loop) break;
                if (_streamCoroutine == null) break; // StopStream() called
            }
            while (loop);

            // Single STREAM_END at the very end.
            if (_client != null && _client.IsConnected)
                _client.SendStreamEnd();

            Log(iteration > 1
                ? $"Stream loop stopped after {iteration} iterations ({globalByteOffset} bytes sent)."
                : $"Stream complete ({globalByteOffset} bytes sent).");

            playback.MarkStopped();
            if (_activePlayback == playback) _activePlayback = null;
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
            // Send a final CONNECT_STATUS connected=false so the device updates
            // its display immediately instead of waiting the full 15-second
            // CONNECT_TIMEOUT_MS to age out. This is fire-and-forget; UDP
            // teardown can race with app shutdown so we swallow any error.
            if (_client != null && _client.IsConnected)
            {
                try
                {
                    _client.SendConnectStatus(false, DefaultGroup, AppName, SystemInfo.deviceName);
                }
                catch { /* shutdown race — ignore */ }
            }

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
