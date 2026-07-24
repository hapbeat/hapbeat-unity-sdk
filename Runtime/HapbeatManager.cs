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
        /// <summary>Sentinel value for a disabled address-override axis (player or group).
        /// See <see cref="SetAddressOverride"/> / <see cref="OverridePlayer"/> / <see cref="OverrideGroup"/>.</summary>
        public const int AddressOverrideDisabled = -1;

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

        /// <summary>UDP socket is open and ready to send. UDP has no real
        /// connection, so this stays true even if the device is powered off.</summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>
        /// Number of devices that returned a PONG within the last pingInterval × 3 seconds.
        /// 0 = no device has responded (powered off / unreachable). Recommended HUD
        /// label: "Hapbeat: N connected".
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

        /// <summary>True if at least one device is responsive.</summary>
        public bool IsAlive => AliveDeviceCount > 0;

        private float AliveTimeoutSeconds =>
            Mathf.Max(5f, (_config != null ? _config.pingInterval : 5f) * 3f);

        // device 単位の直近 pong time (key = IPAddress)。複数 device broadcast 想定。
        private readonly System.Collections.Generic.Dictionary<System.Net.IPAddress, float>
            _devicePongTimes = new System.Collections.Generic.Dictionary<System.Net.IPAddress, float>();

        // Device TCP config port (serial-config.md §4 / ports.md) — fixed, not
        // user-configurable (matches TCP_CONFIG_PORT in device firmware).
        private const int DeviceTcpConfigPort = 7701;

        // IPs the device has *confirmed* received the current _config.streamBufferMs
        // value this app session (only added on a successful send — see
        // OnStreamBufferConfigResult). Cleared whenever the configured value
        // changes so every device is brought up to date exactly once per value.
        private readonly System.Collections.Generic.HashSet<System.Net.IPAddress>
            _streamBufferConfiguredIps = new System.Collections.Generic.HashSet<System.Net.IPAddress>();
        private int _streamBufferConfiguredForMs = int.MinValue; // sentinel: always "changed" on first call

        // Per-IP retry cooldown after a failed send (e.g. device's single TCP
        // config slot held by Studio/Helper). Value = Time.realtimeSinceStartup
        // after which MaybeConfigureStreamBufferFor will attempt this ip again.
        // Cleared alongside _streamBufferConfiguredIps on a streamBufferMs change.
        private readonly System.Collections.Generic.Dictionary<System.Net.IPAddress, float>
            _streamBufferRetryAfter = new System.Collections.Generic.Dictionary<System.Net.IPAddress, float>();
        private const float StreamBufferRetryCooldownSeconds = 10f;

        // alive count の前回値 (state 変化検出用)
        private int _prevAliveCount = -1;

        /// <summary>Whether the client is in broadcast mode.</summary>
        public bool IsBroadcast => _client != null && _client.IsBroadcast;

        /// <summary>
        /// Estimated time offset between local clock and remote clock in microseconds.
        /// Calculated from PONG responses: remoteTime = localTime + TimeOffsetUs.
        /// </summary>
        public long TimeOffsetUs { get; private set; }

        /// <summary>Currently effective forced player number, or <see cref="AddressOverrideDisabled"/>
        /// (-1) if this axis doesn't override the EventMap target's player. See <see cref="SetAddressOverride"/>.</summary>
        public int OverridePlayer => _overridePlayer;

        /// <summary>Currently effective forced group number, or <see cref="AddressOverrideDisabled"/>
        /// (-1) if this axis doesn't override the EventMap target's group. See <see cref="SetAddressOverride"/>.</summary>
        public int OverrideGroup => _overrideGroup;

        /// <summary>App name shown on device OLED. Uses config value (with &lt;p&gt;/&lt;g&gt;
        /// address-override placeholders applied) or falls back to Application.productName.</summary>
        public string AppName => _config != null && !string.IsNullOrEmpty(_config.appName)
            ? ApplyAddressPlaceholders(_config.appName, _overridePlayer, _overrideGroup)
            : Application.productName;

        /// <summary>
        /// Group byte sent in CONNECT_STATUS (OLED display). This is a legacy
        /// wire field the device firmware stores but never reads back — it has
        /// no effect on Play/Stop/Stream routing (that's controlled entirely by
        /// each EventMap entry's target string, optionally forced by
        /// <see cref="OverridePlayer"/> / <see cref="OverrideGroup"/>). We send
        /// the active override group when set, or 0 otherwise.
        /// </summary>
        private byte ConnectStatusGroupByte => _overrideGroup >= 1 ? (byte)_overrideGroup : (byte)0;

        /// <summary>
        /// Replaces the <c>&lt;p&gt;</c> / <c>&lt;g&gt;</c> placeholders in <paramref name="appName"/>
        /// with the current address-override player/group number, or <c>"-"</c> when that axis
        /// is disabled (-1). Pure, null-safe, UnityEngine-independent — safe to unit test directly.
        /// </summary>
        /// <param name="appName">Raw app name string, e.g. "Booth &lt;p&gt;/&lt;g&gt;". May be null or empty.</param>
        /// <param name="overridePlayer">Current override player number, or a disabled value (&lt; 1).</param>
        /// <param name="overrideGroup">Current override group number, or a disabled value (&lt; 1).</param>
        /// <returns><paramref name="appName"/> with placeholders substituted (unchanged if null/empty
        /// or if it contains no placeholders). Callers should cap the result at
        /// <see cref="HapbeatConfig.MaxAppNameLength"/> for wire transmission.</returns>
        public static string ApplyAddressPlaceholders(string appName, int overridePlayer, int overrideGroup)
        {
            if (string.IsNullOrEmpty(appName)) return appName;

            string p = overridePlayer >= 1 ? overridePlayer.ToString() : "-";
            string g = overrideGroup >= 1 ? overrideGroup.ToString() : "-";
            return appName.Replace("<p>", p).Replace("<g>", g);
        }

        /// <summary>
        /// Global haptic-side latency compensation (seconds). All Trigger-based
        /// Fire / Stop calls add this to the per-entry <c>delayOffsetSeconds</c>
        /// and clamp at zero to compute the effective deferral. Direct callers
        /// of <c>Play / StreamAudioClip / Stop</c> bypass this — Triggers are
        /// the canonical integration point.
        /// </summary>
        public float HapticDelaySeconds => _config != null ? _config.hapticDelaySeconds : 0f;

        /// <summary>
        /// Raised when <see cref="HapbeatConfig.hapticDelaySeconds"/> changes
        /// during Play mode. Subscribers (Trigger / Bridge / Event instances)
        /// should flush their pending delay coroutines so the new latency value
        /// takes effect immediately on subsequent Fire / Stop calls.
        /// <para>
        /// Active StreamClip playbacks, mixer session state, and SequenceTrigger
        /// internal coroutines (start-shot → loop start delay) are <b>not</b>
        /// affected. Only the per-call Fire/Stop deferral that captured the
        /// old <c>hapticDelaySeconds</c> value gets cancelled.
        /// </para>
        /// </summary>
        public static event System.Action OnHapticDelayChanged;

        /// <summary>
        /// Invoked by <see cref="HapbeatConfig.OnValidate"/> when the user edits
        /// <c>hapticDelaySeconds</c> in the Inspector during Play. Public so
        /// custom UIs that drive the field via code (instead of SerializedObject)
        /// can also fan out the notification.
        /// </summary>
        public static void NotifyHapticDelayChanged()
        {
            // Flush our own tracked coroutines first (e.g. StateMachineBehaviour
            // delay routines that can't host their own coroutines), then notify
            // all event subscribers (Trigger / Bridge instances).
            if (Instance != null) Instance.FlushTrackedHapticDelays();
            OnHapticDelayChanged?.Invoke();
        }

        // Coroutines owned by the HapbeatManager singleton on behalf of callers
        // that can't host their own (e.g. StateMachineBehaviour subclasses).
        // Tracked so they can be flushed alongside Trigger / Bridge coroutines
        // when latency settings change during Play.
        private readonly List<Coroutine> _trackedHapticDelays = new List<Coroutine>(4);

        /// <summary>
        /// Start a coroutine on the HapbeatManager and register it as a
        /// haptic-delay deferred routine. Returns the started Coroutine so the
        /// caller can keep a reference (e.g. for its own cancellation).
        /// <para>
        /// Intended for <see cref="HapbeatStateBehaviour"/> and other callers
        /// that can't host coroutines themselves. MonoBehaviour-based triggers
        /// should use their own <c>StartHapticDelayCoroutine</c> helper instead
        /// so the coroutine is bound to the trigger's lifetime.
        /// </para>
        /// </summary>
        public Coroutine StartTrackedHapticDelay(IEnumerator routine)
        {
            var holder = new Coroutine[1];
            holder[0] = StartCoroutine(WrapTrackedHapticDelay(routine, holder));
            _trackedHapticDelays.Add(holder[0]);
            return holder[0];
        }

        private IEnumerator WrapTrackedHapticDelay(IEnumerator inner, Coroutine[] selfHolder)
        {
            yield return StartCoroutine(inner);
            _trackedHapticDelays.Remove(selfHolder[0]);
        }

        private void FlushTrackedHapticDelays()
        {
            for (int i = 0; i < _trackedHapticDelays.Count; i++)
            {
                var c = _trackedHapticDelays[i];
                if (c != null) StopCoroutine(c);
            }
            _trackedHapticDelays.Clear();
        }

        /// <summary>Internal UDP client.</summary>
        internal HapbeatClient Client => _client;

        private HapbeatClient _client;
        private HapbeatDiscovery _discovery;
        private float _lastPingTime;
        private bool _isInitialized;

        // Effective (already-normalized) forced player/group. -1 = disabled.
        // Populated from PlayerPrefs (if present) or HapbeatConfig at Initialize(),
        // and kept in sync with the HapbeatClient the SDK actually sends through.
        private int _overridePlayer = -1;
        private int _overrideGroup = -1;

        /// <summary>PlayerPrefs key for the persisted address-override player number.
        /// Public so Editor UI (<see cref="TryGetPersistedAddressOverride"/> callers,
        /// settings windows, …) can read/clear the same key without duplicating the
        /// string literal.</summary>
        public const string PlayerPrefsKeyOverridePlayer = "Hapbeat.OverridePlayer";

        /// <summary>PlayerPrefs key for the persisted address-override group number.
        /// See <see cref="PlayerPrefsKeyOverridePlayer"/>.</summary>
        public const string PlayerPrefsKeyOverrideGroup = "Hapbeat.OverrideGroup";

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

            // Dispatch set_stream_buffer send-result callbacks (HapbeatDeviceTcpConfig
            // runs its TCP I/O on a thread-pool thread; results are queued there and
            // drained here so OnStreamBufferConfigResult only ever runs on the main
            // thread). Independent of _client, so keep this above the null check.
            HapbeatDeviceTcpConfig.DispatchMainThreadCallbacks();

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
                    _client.SendConnectStatus(true, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);
                    _lastPingTime = Time.realtimeSinceStartup;
                }
            }

            // Detect alive-count changes → fire OnConnected / OnDisconnected
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
        /// Override the player / group applied to every outgoing command at runtime.
        /// Pass <see cref="AddressOverrideDisabled"/> (-1) to disable either axis —
        /// this means "don't override that axis", not "rewrite the target's value to -1";
        /// a disabled axis leaves the EventMap entry's target string exactly as authored.
        /// Values outside 1..99 are normalized to <see cref="AddressOverrideDisabled"/>.
        /// When <paramref name="persist"/> is true the values are stored in PlayerPrefs
        /// and restored on next launch — this is the intended flow for "one identical
        /// build deployed to many HMDs, each bound to its own Hapbeat".
        /// </summary>
        /// <param name="player">Forced player number (1-99), or <see cref="AddressOverrideDisabled"/>
        /// to leave the EventMap target's player as-is.</param>
        /// <param name="group">Forced group number (1-99), or <see cref="AddressOverrideDisabled"/>
        /// to leave the EventMap target's group as-is.</param>
        /// <param name="persist">If true, saves to PlayerPrefs so the override survives an app restart.</param>
        public void SetAddressOverride(int player, int group, bool persist = false)
        {
            _overridePlayer = HapbeatClient.NormalizeOverride(player);
            _overrideGroup = HapbeatClient.NormalizeOverride(group);

            if (_client != null)
                _client.SetAddressOverride(_overridePlayer, _overrideGroup);

            if (persist)
            {
                PlayerPrefs.SetInt(PlayerPrefsKeyOverridePlayer, _overridePlayer);
                PlayerPrefs.SetInt(PlayerPrefsKeyOverrideGroup, _overrideGroup);
                PlayerPrefs.Save();
            }

            Log($"Address override set: player={_overridePlayer}, group={_overrideGroup}, persist={persist}");

            // Keep the device's CONNECT_STATUS (OLED) group display in sync with the
            // new routing immediately, instead of waiting up to pingInterval seconds
            // for the next periodic push.
            if (_client != null && _client.IsConnected)
                _client.SendConnectStatus(true, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);
        }

        /// <summary>
        /// Reads the per-device address override persisted by a prior
        /// <see cref="SetAddressOverride"/> call with <c>persist: true</c>,
        /// without requiring a live <see cref="HapbeatManager"/> instance —
        /// intended for Editor UI that inspects PlayerPrefs outside Play mode.
        /// </summary>
        /// <param name="player">Persisted player number, or -1 if not saved.</param>
        /// <param name="group">Persisted group number, or -1 if not saved.</param>
        /// <returns>True if either the player or the group key is present in PlayerPrefs.</returns>
        public static bool TryGetPersistedAddressOverride(out int player, out int group)
        {
            bool hasPlayer = PlayerPrefs.HasKey(PlayerPrefsKeyOverridePlayer);
            bool hasGroup = PlayerPrefs.HasKey(PlayerPrefsKeyOverrideGroup);
            player = hasPlayer ? PlayerPrefs.GetInt(PlayerPrefsKeyOverridePlayer) : -1;
            group = hasGroup ? PlayerPrefs.GetInt(PlayerPrefsKeyOverrideGroup) : -1;
            return hasPlayer || hasGroup;
        }

        /// <summary>
        /// Clears the per-device address override saved by <see cref="SetAddressOverride"/>
        /// (persist: true) and reverts the runtime override back to disabled
        /// (<see cref="AddressOverrideDisabled"/> on both axes) — there is no config-level
        /// default to fall back to; clearing simply means "stop overriding".
        /// <para>
        /// Reuses <see cref="SetAddressOverride"/> (with <c>persist: false</c>, so the
        /// just-cleared keys aren't immediately re-saved) to push the reverted values
        /// to the client and, if connected, send an immediate CONNECT_STATUS update —
        /// same reflection path as a normal override change.
        /// </para>
        /// </summary>
        public void ClearPersistedAddressOverride()
        {
            PlayerPrefs.DeleteKey(PlayerPrefsKeyOverridePlayer);
            PlayerPrefs.DeleteKey(PlayerPrefsKeyOverrideGroup);
            PlayerPrefs.Save();

            SetAddressOverride(AddressOverrideDisabled, AddressOverrideDisabled, persist: false);

            Log("Persisted address override cleared — reverted to disabled.");
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
                    Log($"Broadcast mode opened on port {port}");
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
                _client.SendConnectStatus(false, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);

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
        /// <b>Multi-source mixing:</b> overlapping <c>StreamAudioClip</c> calls
        /// are float-mixed inside the SDK and sent as a single wire stream. The
        /// existing wire protocol (STREAM_BEGIN/DATA/END) and device firmware
        /// don't change. Each returned <see cref="HapbeatStreamPlayback"/> is
        /// independent; <c>.Stop()</c> ends just that source.
        /// </para>
        /// <para>
        /// Constraint: a new source's <c>clip.frequency</c> / <c>clip.channels</c>
        /// / <c>target</c> must match the active session (mismatch = warning +
        /// null return). The first source decides the session format.
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

                // Send gain=1.0 so the device passes through, and pre-multiply
                // each source's gain in the SDK (sample \u00d7 gain) to avoid the
                // device applying it twice. totalSamples=0 means "unknown".
                _client.SendStreamBegin(_sessionSampleRate, _sessionChannels,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1.0f, target);
                Log($"\u266a Stream session begin: {_sessionSampleRate}Hz, {_sessionChannels}ch, " +
                    $"target={(string.IsNullOrEmpty(target) ? "broadcast" : target)}");

                // Bring every already-known alive device up to the configured
                // stream-buffer mode. Devices that only appear later (PONG
                // after this point) are covered by the OnPongFrom hook above.
                foreach (System.Net.IPAddress ip in _devicePongTimes.Keys)
                    MaybeConfigureStreamBufferFor(ip);

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

        /// <summary>
        /// Stop all active stream sources AND force the device ring buffer to flush.
        /// Normal <see cref="StopStream"/> sends STREAM_END and the device drains
        /// any residual frames naturally (~50–250 ms tail), which feels laggy when
        /// you want immediate silence on release.
        ///
        /// This API runs <see cref="StopStream"/> then sends a STREAM_BEGIN +
        /// STREAM_END pair to trigger the firmware's <c>BEGIN_FLUSH_THRESHOLD</c>
        /// path (ringReset() when residual > 32 ms). The device goes silent within
        /// a few ms. No firmware change needed.
        ///
        /// Note: with no target, this broadcasts and flushes every device — it
        /// will also kill audio from other sessions (e.g. another trigger's
        /// stream). Pass a target for per-target stop.
        /// </summary>
        public void StopStreamWithFlush(string target = null)
        {
            StopStream();
            if (_client == null || !_client.IsConnected) return;
            // Any format works. If residual > 32 ms, ringReset() runs.
            // sample_rate=16000, channels=1, PCM16, total_samples=0, gain=1.0
            _client.SendStreamBegin(16000, 1, HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1.0f, target);
            _client.SendStreamEnd();
            Log("Stream flushed (BEGIN+END pair sent to trigger device ring buffer reset).");
        }

        /// <summary>True while any stream source is active.</summary>
        public bool IsStreaming => _sessionActive;

        /// <summary>
        /// Handle to the FIRST active stream source, or null if nothing is
        /// streaming. Kept for compatibility with the single-stream API. For
        /// multi-source use, hold the per-source handle returned by
        /// <see cref="StreamAudioClip"/>.
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
            public int Cursor; // Next sample index to read (flat index across all channels).

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
        /// Multi-source mixer. Mixes one chunk's worth of samples from every
        /// active source as float, converts to PCM16, and sends as a single
        /// wire stream. When all sources finish, sends STREAM_END to close
        /// the session.
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

                // 5. Pace — pin lead at sendAheadSeconds regardless of frame rate / chunk size.
                //
                // 旧実装は `yield return null` 1 回だけで再開し、即次 chunk を送っていた。
                // chunk duration (mono=43.75ms / stereo=21.875ms @16kHz) が Unity frame interval
                // (~16.67ms @60fps) を上回るケースでは、yield 1 回では追いつかず lead が
                // 線形に成長 → device ring buffer (256ms) を即飽和 → 新規 chunk が drop され、
                // Stop / parameter 変更が device に届かなくなる。
                //
                // overshoot 分だけ WaitForSecondsRealtime で待つことで、frame rate / chunk size
                // に依存せず lead を sendAheadSeconds 付近に張り付かせる。
                float elapsedTime = Time.realtimeSinceStartup - startTime;
                float sentDuration = globalByteOffset / bytesPerSecond;
                float overshoot = sentDuration - elapsedTime - sendAheadSeconds;
                if (overshoot > 0f)
                    yield return new WaitForSecondsRealtime(overshoot);
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

            // PlayerPrefs (if present) restores a per-device address override saved
            // by a prior SetAddressOverride(..., persist: true) call. There is no
            // config-level default to fall back to — an override is either
            // persisted from a previous run, or starts disabled.
            int player = PlayerPrefs.HasKey(PlayerPrefsKeyOverridePlayer)
                ? PlayerPrefs.GetInt(PlayerPrefsKeyOverridePlayer)
                : AddressOverrideDisabled;
            int group = PlayerPrefs.HasKey(PlayerPrefsKeyOverrideGroup)
                ? PlayerPrefs.GetInt(PlayerPrefsKeyOverrideGroup)
                : AddressOverrideDisabled;
            _overridePlayer = HapbeatClient.NormalizeOverride(player);
            _overrideGroup = HapbeatClient.NormalizeOverride(group);

            _client = CreateClient();
            _client.SetAddressOverride(_overridePlayer, _overrideGroup);
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
                    client.SendConnectStatus(true, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);
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
                // Newly-seen (or reconfigured) device — make sure it has the
                // configured stream-buffer mode for CLIP playback.
                MaybeConfigureStreamBufferFor(sender.Address);
            };

            client.OnError += (errorCode, message) =>
            {
                string errorMsg = $"Error (code={errorCode}): {message}";
                Debug.LogWarning($"[Hapbeat] {errorMsg}");
                OnError?.Invoke(errorMsg);
            };

            return client;
        }

        /// <summary>
        /// Pushes <c>set_stream_buffer</c> (contracts §4.20-pre) to <paramref name="ip"/>
        /// over TCP 7701. Only counted as done once the send actually succeeds
        /// (see <see cref="OnStreamBufferConfigResult"/>) — a failed attempt
        /// (device's single TCP config slot held by Studio/Helper, connect
        /// timeout, etc.) is retried after <see cref="StreamBufferRetryCooldownSeconds"/>
        /// on the next call, instead of being silently treated as configured
        /// forever. Fire-and-forget from this method's own point of view — see
        /// <see cref="HapbeatDeviceTcpConfig"/> for why the send itself never
        /// blocks or throws.
        ///
        /// Called from two places (both main-thread, so <see cref="_streamBufferConfiguredIps"/>
        /// / <see cref="_streamBufferRetryAfter"/> need no locking): (a) when a
        /// StreamAudioClip session begins, for every already-known alive device
        /// IP; (b) every time a PONG reveals a device IP, so devices that connect
        /// mid-session (or after the first stream started), or that failed and
        /// are now past their cooldown, still get configured.
        /// </summary>
        private void MaybeConfigureStreamBufferFor(System.Net.IPAddress ip)
        {
            if (_config == null || ip == null) return;
            // Bridge/ESP-NOW-fed devices aren't reachable over direct Wi-Fi TCP —
            // this feature only applies to the standard Wi-Fi UDP CLIP path.
            if (_config.useBridge) return;

            int bufferMs = _config.streamBufferMs;
            if (bufferMs != _streamBufferConfiguredForMs)
            {
                // Value changed since we last sent (including the very first
                // call, via the sentinel) — re-arm so every known device gets
                // the new value once, dropping any stale confirmed/cooldown state.
                _streamBufferConfiguredIps.Clear();
                _streamBufferRetryAfter.Clear();
                _streamBufferConfiguredForMs = bufferMs;
            }
            if (bufferMs <= 0) return; // 0 = don't send (device already defaults to low-latency)
            if (_streamBufferConfiguredIps.Contains(ip)) return; // already confirmed configured for this value

            if (_streamBufferRetryAfter.TryGetValue(ip, out float retryAfter) &&
                Time.realtimeSinceStartup < retryAfter)
            {
                return; // still cooling down from a recent failed attempt
            }

            int forMs = bufferMs; // capture so a late callback can't misattribute a since-changed value
            HapbeatDeviceTcpConfig.SendSetStreamBufferAsync(
                ip.ToString(), DeviceTcpConfigPort, bufferMs, _config.verboseLogging,
                (_, success) => OnStreamBufferConfigResult(ip, forMs, success));
        }

        /// <summary>
        /// Main-thread callback for <see cref="HapbeatDeviceTcpConfig.SendSetStreamBufferAsync"/>
        /// (invoked via <see cref="HapbeatDeviceTcpConfig.DispatchMainThreadCallbacks"/>
        /// from <see cref="Update"/>). On success, marks <paramref name="ip"/> as
        /// configured so it isn't re-sent for this <c>streamBufferMs</c> value. On
        /// failure, arms a short retry cooldown so the next PONG (or the next
        /// StreamAudioClip session start) will try again rather than the device
        /// silently never getting the setting because Studio/Helper happened to
        /// be holding the TCP 7701 slot at the moment we tried.
        /// </summary>
        private void OnStreamBufferConfigResult(System.Net.IPAddress ip, int forMs, bool success)
        {
            // Stale result for a streamBufferMs value we've since moved on from
            // (the confirmed/cooldown maps were already cleared for the new
            // value in MaybeConfigureStreamBufferFor) — ignore.
            if (forMs != _streamBufferConfiguredForMs) return;

            if (success)
            {
                _streamBufferConfiguredIps.Add(ip);
                _streamBufferRetryAfter.Remove(ip);
            }
            else
            {
                _streamBufferRetryAfter[ip] = Time.realtimeSinceStartup + StreamBufferRetryCooldownSeconds;
            }
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
                    _client.SendConnectStatus(false, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);
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
