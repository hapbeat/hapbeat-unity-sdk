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
        /// <summary>UDP socket 接続状態 (= 送信可能か)。device が落ちていても UDP は無接続性のため true のまま。</summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>
        /// 直近 pingInterval × 3 秒以内に PONG を返した device の台数。
        /// 0 ならどの device からも反応なし (電源 OFF / 通信不通)。
        /// HUD には「Hapbeat: N connected」のように出すのが推奨。
        /// </summary>
        public int AliveDeviceCount
        {
            get
            {
                if (_client == null || !_client.IsConnected) return 0;
                float timeout = AliveTimeoutSeconds;
                float now = Time.realtimeSinceStartup;
                int count = 0;
                foreach (var kv in _devicePongTimes)
                {
                    if (now - kv.Value <= timeout) count++;
                }
                return count;
            }
        }

        /// <summary>少なくとも 1 台の device が responsive か。</summary>
        public bool IsAlive => AliveDeviceCount > 0;

        private float AliveTimeoutSeconds =>
            Mathf.Max(5f, (_config != null ? _config.pingInterval : 5f) * 3f);

        // device 単位の直近 pong time (key = IPAddress)。複数 device broadcast 想定。
        private readonly System.Collections.Generic.Dictionary<System.Net.IPAddress, float>
            _devicePongTimes = new System.Collections.Generic.Dictionary<System.Net.IPAddress, float>();
        // alive count の前回値 (state 変化検出用)
        private int _prevAliveCount = -1;

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

            // Alive 台数の変化検出 → OnConnected / OnDisconnected 発火
            int cur = AliveDeviceCount;
            if (cur != _prevAliveCount)
            {
                if (_prevAliveCount <= 0 && cur > 0) OnConnected?.Invoke();
                else if (_prevAliveCount > 0 && cur == 0) OnDisconnected?.Invoke();
                _prevAliveCount = cur;
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
        /// <b>Multi-source mixing (2026-05-18 \u4ee5\u964d):</b> \u8907\u6570\u306e StreamAudioClip
        /// \u547c\u51fa\u306f SDK \u5185\u3067 float \u5408\u6210\u3055\u308c\u3001\u5358\u4e00\u306e wire stream \u3068\u3057\u3066\u9001\u51fa\u3055\u308c\u308b\u3002
        /// \u65e2\u5b58\u306e wire protocol (STREAM_BEGIN/DATA/END) \u3068 device firmware \u306f
        /// \u5909\u66f4\u4e0d\u8981\u3002\u8fd4\u5374\u3055\u308c\u305f <see cref="HapbeatStreamPlayback"/> \u306f source
        /// \u3054\u3068\u306b\u72ec\u7acb\u3067\u3001<c>.Stop()</c> \u3067\u305d\u306e source \u3060\u3051\u505c\u6b62\u3059\u308b\u3002
        /// </para>
        /// <para>
        /// \u5236\u7d04: \u65b0\u898f source \u306e <c>clip.frequency</c> / <c>clip.channels</c> /
        /// <c>target</c> \u306f active session \u3068\u4e00\u81f4\u3059\u308b\u5fc5\u8981\u304c\u3042\u308b (mismatch \u306f
        /// warning + null \u8fd4\u5374)\u3002\u6700\u521d\u306e source \u306e\u5024\u304c session \u3092\u6c7a\u5b9a\u3059\u308b\u3002
        /// </para>
        /// </summary>
        /// <param name="clip">AudioClip to stream (will be read as PCM16).</param>
        /// <param name="gain">Initial gain multiplier (0.0 - 2.0). Binding can
        /// override this per frame via the returned handle.</param>
        /// <param name="target">Optional target filter (e.g. "player_1/pos_neck"). Null = broadcast.</param>
        /// <param name="loop">If true, the source loops until
        /// <see cref="HapbeatStreamPlayback.Stop"/> is called.</param>
        /// <returns>Per-source handle for runtime control, or <c>null</c> if
        /// the source couldn't start (not connected, null clip, session
        /// rate/channel/target mismatch).</returns>
        public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float gain = 1.0f, string target = null, bool loop = false)
        {
            return StreamAudioClip(clip, gain, gain, target, loop);
        }

        /// <summary>
        /// Same as the 4-arg overload, but with separate baseline / initial gain
        /// (used by <see cref="HapbeatTriggerBase"/> to pre-seed binding-modulated gain).
        /// </summary>
        public HapbeatStreamPlayback StreamAudioClip(AudioClip clip, float baselineGain, float initialGain, string target, bool loop)
        {
            if (!EnsureConnected()) return null;
            if (clip == null)
            {
                Debug.LogWarning("[Hapbeat] StreamAudioClip: clip is null.");
                return null;
            }

            // Session compatibility check (sample rate / channels / target must match active session)
            if (_sessionActive)
            {
                if (clip.frequency != _sessionSampleRate || clip.channels != _sessionChannels)
                {
                    Debug.LogWarning($"[Hapbeat] StreamAudioClip: rate/channel mismatch with active session " +
                        $"(session={_sessionSampleRate}Hz/{_sessionChannels}ch, new={clip.frequency}Hz/{clip.channels}ch). Rejecting new source.");
                    return null;
                }
                bool sameTarget = string.IsNullOrEmpty(target)
                    ? string.IsNullOrEmpty(_sessionTarget)
                    : (target == _sessionTarget);
                if (!sameTarget)
                {
                    Debug.LogWarning($"[Hapbeat] StreamAudioClip: target mismatch with active session " +
                        $"(session='{_sessionTarget}', new='{target}'). Rejecting new source.");
                    return null;
                }
            }

            var playback = new HapbeatStreamPlayback(baselineGain, initialGain);
            var source = new StreamSource(clip, playback, loop);

            if (!_sessionActive)
            {
                _sessionSampleRate = (ushort)clip.frequency;
                _sessionChannels = (byte)clip.channels;
                _sessionTarget = target;
                _sessionActive = true;

                // gain=1.0 \u3092\u9001\u3063\u3066 device \u306f passthrough \u306b\u3057\u3001\u5404 source \u306e
                // gain \u306f SDK \u5074\u3067 sample \u00d7 gain \u3068\u3057\u3066 pre-multiply \u3059\u308b
                // (device \u306e\u4e8c\u91cd\u9069\u7528\u3092\u907f\u3051\u308b)\u3002totalSamples=0 \u306f "unknown" \u901a\u77e5\u3002
                _client.SendStreamBegin(_sessionSampleRate, _sessionChannels,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1.0f, target);
                Log($"\u266a Stream session begin: {_sessionSampleRate}Hz, {_sessionChannels}ch, " +
                    $"target={(string.IsNullOrEmpty(target) ? "broadcast" : target)}");

                _sources.Add(source);
                _mixerCoroutine = StartCoroutine(MixerCoroutine());
            }
            else
            {
                _sources.Add(source);
                Log($"\u266a Stream source added: {clip.name} (active sources={_sources.Count}, loop={loop})");
            }

            return playback;
        }

        /// <summary>
        /// Stop ALL active stream sources and end the session.
        /// Use <see cref="HapbeatStreamPlayback.Stop"/> on a specific handle
        /// to stop just one source while others continue.
        /// </summary>
        public void StopStream()
        {
            if (_mixerCoroutine != null)
            {
                StopCoroutine(_mixerCoroutine);
                _mixerCoroutine = null;
            }
            for (int i = 0; i < _sources.Count; i++)
                _sources[i].Playback?.MarkStopped();
            _sources.Clear();
            if (_sessionActive && _client != null && _client.IsConnected)
            {
                _client.SendStreamEnd();
                Log("Stream session stopped (all sources).");
            }
            _sessionActive = false;
        }

        /// <summary>True while any stream source is active.</summary>
        public bool IsStreaming => _sessionActive;

        /// <summary>
        /// Handle to the FIRST active stream source, or null if nothing is
        /// streaming. \u5358\u4e00 stream \u6642\u4ee3\u306e API \u4e92\u63db\u306e\u305f\u3081\u6b8b\u7f6e\u3002\u30de\u30eb\u30c1\u30bd\u30fc\u30b9\u6642\u306f
        /// \u5404 source \u306e handle \u3092 <see cref="StreamAudioClip"/> \u623b\u308a\u5024\u304b\u3089\u4fdd\u6301\u3059\u308b\u3002
        /// </summary>
        public HapbeatStreamPlayback ActivePlayback =>
            (_sources.Count > 0 && !_sources[0].Playback.IsStopped) ? _sources[0].Playback : null;

        // ----- Multi-source mixing internals -----

        private sealed class StreamSource
        {
            public readonly float[] Samples;
            public readonly byte Channels;
            public readonly ushort SampleRate;
            public readonly bool Loop;
            public readonly HapbeatStreamPlayback Playback;
            public readonly string Name;
            public int Cursor; // \u6b21\u306b\u8aad\u3080 sample index (channel \u6df7\u5728\u306e\u30d5\u30e9\u30c3\u30c8\u30a4\u30f3\u30c7\u30c3\u30af\u30b9)

            public StreamSource(AudioClip clip, HapbeatStreamPlayback pb, bool loop)
            {
                Samples = new float[clip.samples * clip.channels];
                clip.GetData(Samples, 0);
                Channels = (byte)clip.channels;
                SampleRate = (ushort)clip.frequency;
                Loop = loop;
                Playback = pb;
                Name = clip.name;
                Cursor = 0;
            }

            public bool IsDone => Playback == null || Playback.IsStopped;
        }

        private readonly System.Collections.Generic.List<StreamSource> _sources
            = new System.Collections.Generic.List<StreamSource>(4);
        private Coroutine _mixerCoroutine;
        private ushort _sessionSampleRate;
        private byte _sessionChannels;
        private string _sessionTarget;
        private bool _sessionActive;

        /// <summary>
        /// Multi-source mixer. 全 active source から chunk 分のサンプルを
        /// float で合成し、PCM16 化して 1 本の wire stream として送出する。
        /// 全 source が終了したら STREAM_END を送って session を終える。
        /// </summary>
        private IEnumerator MixerCoroutine()
        {
            byte channels = _sessionChannels;
            ushort sampleRate = _sessionSampleRate;
            int maxChunkBytes = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
            // Chunk size in SAMPLES (not bytes). Must be a whole number of
            // sample frames so stereo L/R stays aligned at chunk boundaries.
            int bytesPerFrame = channels * 2;
            int framesPerChunk = maxChunkBytes / bytesPerFrame;
            if (framesPerChunk <= 0) framesPerChunk = 1;
            int samplesPerChunk = framesPerChunk * channels;
            byte[] pcmChunk = new byte[samplesPerChunk * 2];
            float[] mixBuffer = new float[samplesPerChunk];

            float bytesPerSecond = sampleRate * channels * 2f;
            float sendAheadSeconds = _config != null ? _config.streamSendAheadSeconds : 0.05f;
            if (sendAheadSeconds < 0.01f) sendAheadSeconds = 0.05f;

            uint globalByteOffset = 0;
            float startTime = Time.realtimeSinceStartup;

            while (_sources.Count > 0 && _client != null && _client.IsConnected)
            {
                // 1. Clear mix buffer
                System.Array.Clear(mixBuffer, 0, mixBuffer.Length);

                // 2. Mix each source into mixBuffer (also remove stopped/finished sources)
                for (int s = _sources.Count - 1; s >= 0; s--)
                {
                    var src = _sources[s];
                    if (src.IsDone)
                    {
                        _sources.RemoveAt(s);
                        continue;
                    }

                    float g = src.Playback.Gain;
                    float gainL, gainR;
                    src.Playback.GetStereoChannelGains(out gainL, out gainR);

                    int framesRemaining = framesPerChunk;
                    int outIdx = 0;
                    while (framesRemaining > 0)
                    {
                        int srcSamplesLeft = src.Samples.Length - src.Cursor;
                        int srcFramesAvail = srcSamplesLeft / channels;
                        if (srcFramesAvail <= 0)
                        {
                            if (src.Loop)
                            {
                                src.Cursor = 0;
                                continue;
                            }
                            else
                            {
                                src.Playback.MarkStopped();
                                _sources.RemoveAt(s);
                                break;
                            }
                        }
                        int framesToCopy = Mathf.Min(framesRemaining, srcFramesAvail);
                        if (channels == 2)
                        {
                            for (int f = 0; f < framesToCopy; f++)
                            {
                                mixBuffer[outIdx++] += src.Samples[src.Cursor++] * g * gainL;
                                mixBuffer[outIdx++] += src.Samples[src.Cursor++] * g * gainR;
                            }
                        }
                        else
                        {
                            for (int f = 0; f < framesToCopy; f++)
                                mixBuffer[outIdx++] += src.Samples[src.Cursor++] * g;
                        }
                        framesRemaining -= framesToCopy;
                    }
                }

                // 3. Convert mixed float → PCM16 (clamp to int16 range)
                for (int i = 0; i < samplesPerChunk; i++)
                {
                    short pcm = (short)Mathf.Clamp(mixBuffer[i] * 32767f, -32768f, 32767f);
                    pcmChunk[i * 2    ] = (byte)(pcm & 0xFF);
                    pcmChunk[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                }

                // 4. Send chunk
                int chunkBytes = samplesPerChunk * 2;
                _client.SendStreamData(globalByteOffset, pcmChunk, 0, chunkBytes);
                globalByteOffset += (uint)chunkBytes;

                // 5. Pace
                float elapsedTime = Time.realtimeSinceStartup - startTime;
                float sentDuration = globalByteOffset / bytesPerSecond;
                if (sentDuration > elapsedTime + sendAheadSeconds)
                    yield return null;
            }

            // All sources done → end session
            if (_client != null && _client.IsConnected)
                _client.SendStreamEnd();
            _sessionActive = false;
            _mixerCoroutine = null;
            Log($"Stream session ended ({globalByteOffset} bytes sent).");
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

            // Per-device liveness 集計 (broadcast 経路で複数 device 想定)
            client.OnPongFrom += (sender, rttUs) =>
            {
                _devicePongTimes[sender.Address] = Time.realtimeSinceStartup;
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
