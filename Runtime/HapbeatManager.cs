using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

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

        /// <summary>
        /// Snapshot of currently-alive device IPs (same liveness window as
        /// <see cref="AliveDeviceCount"/>). Used to seed <see cref="HapbeatClient.SetStreamUnicastTargets"/>
        /// once per stream session — see <see cref="StreamAudioClip(AudioClip, float, float, string, bool)"/>.
        /// </summary>
        private List<System.Net.IPAddress> GetAliveDeviceIPs()
        {
            float timeout = AliveTimeoutSeconds;
            float now = Time.realtimeSinceStartup;
            var result = new List<System.Net.IPAddress>(_devicePongTimes.Count);
            foreach (var kv in _devicePongTimes)
            {
                if (now - kv.Value <= timeout) result.Add(kv.Key);
            }
            return result;
        }

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

        /// <summary>Currently effective forced player number, or <see cref="AddressOverrideDisabled"/>
        /// (-1) if this axis doesn't override the EventMap target's player. See <see cref="SetAddressOverride"/>.</summary>
        public int OverridePlayer => _overridePlayer;

        /// <summary>Currently effective forced group number, or <see cref="AddressOverrideDisabled"/>
        /// (-1) if this axis doesn't override the EventMap target's group. See <see cref="SetAddressOverride"/>.</summary>
        public int OverrideGroup => _overrideGroup;

        /// <summary>Build-wide forced player number from <see cref="HapbeatConfig.buildOverridePlayer"/>,
        /// or <see cref="AddressOverrideDisabled"/> (-1) when this axis is left to the device.
        /// See <see cref="ResolveEffectiveOverride"/>.</summary>
        public int BuildOverridePlayer =>
            _config != null ? HapbeatClient.NormalizeOverride(_config.buildOverridePlayer) : AddressOverrideDisabled;

        /// <summary>Build-wide forced group number from <see cref="HapbeatConfig.buildOverrideGroup"/>,
        /// or <see cref="AddressOverrideDisabled"/> (-1). See <see cref="BuildOverridePlayer"/>.</summary>
        public int BuildOverrideGroup =>
            _config != null ? HapbeatClient.NormalizeOverride(_config.buildOverrideGroup) : AddressOverrideDisabled;

        /// <summary>True when the player axis is pinned by the build's config and therefore
        /// cannot be changed on this device (<see cref="SetAddressOverride"/> ignores it,
        /// and UI should present it as read-only).</summary>
        public bool IsPlayerForcedByBuild => BuildOverridePlayer >= 1;

        /// <summary>True when the group axis is pinned by the build's config. See <see cref="IsPlayerForcedByBuild"/>.</summary>
        public bool IsGroupForcedByBuild => BuildOverrideGroup >= 1;

        /// <summary>
        /// Per-axis address-override resolution, shared by <see cref="Initialize"/> and
        /// <see cref="SetAddressOverride"/>. Pure and UnityEngine-independent so it can be
        /// unit tested directly (see Tests/Runtime/AddressOverrideResolutionTests.cs).
        /// <para>
        /// A config value in 1..99 wins unconditionally — that's the "this whole build is
        /// pinned to this player/group" case, which no per-device state may undo. Otherwise
        /// the per-device value (a PlayerPrefs-restored number, or whatever the runtime panel
        /// asked for) applies, normalized the usual way so anything outside 1..99 means
        /// "don't override this axis".
        /// </para>
        /// </summary>
        /// <param name="configValue">Raw <see cref="HapbeatConfig.buildOverridePlayer"/> /
        /// <see cref="HapbeatConfig.buildOverrideGroup"/> value. -1 (or anything outside 1..99) = not forced.</param>
        /// <param name="perDeviceValue">Per-device value (PlayerPrefs / runtime request). Only used
        /// when the build doesn't force this axis.</param>
        /// <returns>The effective, already-normalized override for this axis.</returns>
        public static int ResolveEffectiveOverride(int configValue, int perDeviceValue)
        {
            int forced = HapbeatClient.NormalizeOverride(configValue);
            return forced >= 1 ? forced : HapbeatClient.NormalizeOverride(perDeviceValue);
        }

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

        // Auto-reconnect state. _shouldStayConnected records *intent*: set by
        // Connect()/ConnectToBridge(), cleared by Disconnect()/Cleanup(), so a
        // deliberate disconnect is never undone by the retry loop.
        private bool _shouldStayConnected;
        private int _reconnectAttempts;
        private float _reconnectNextAttemptTime;
        private float _connectedSinceTime;
        private const float ReconnectBaseDelaySeconds = 2f;
        private const float ReconnectMaxDelaySeconds = 30f;
        // A session must last at least this long before it counts as "good" and
        // clears the backoff. Resetting on socket-open alone would let an adapter
        // that accepts a bind but drops immediately churn at the base delay
        // forever, rebuilding a socket and a thread every couple of seconds.
        private const float ReconnectStableSeconds = 10f;
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

            // Reopen the socket if anything dropped it (see TryAutoReconnect).
            // Deliberately after the keep-alive block: reconnecting sets
            // IsConnected synchronously but defers its own PING/CONNECT_STATUS to
            // the queued state-changed handler, so running the block first on the
            // reconnect frame would send a duplicate pair.
            TryAutoReconnect();

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
        /// <para>
        /// An axis pinned by the build (<see cref="IsPlayerForcedByBuild"/> /
        /// <see cref="IsGroupForcedByBuild"/>) is <b>not</b> changed here: the requested
        /// value for that axis is ignored (verbose-logged), the forced value is kept, and
        /// nothing is written to PlayerPrefs for it. Only the non-forced axes are
        /// per-device state at all — see <see cref="ResolveEffectiveOverride"/>.
        /// </para>
        /// </summary>
        /// <param name="player">Forced player number (1-99), or <see cref="AddressOverrideDisabled"/>
        /// to leave the EventMap target's player as-is.</param>
        /// <param name="group">Forced group number (1-99), or <see cref="AddressOverrideDisabled"/>
        /// to leave the EventMap target's group as-is.</param>
        /// <param name="persist">If true, saves to PlayerPrefs so the override survives an app restart.</param>
        public void SetAddressOverride(int player, int group, bool persist = false)
        {
            int requestedPlayer = HapbeatClient.NormalizeOverride(player);
            int requestedGroup = HapbeatClient.NormalizeOverride(group);

            bool playerForced = IsPlayerForcedByBuild;
            bool groupForced = IsGroupForcedByBuild;

            _overridePlayer = ResolveEffectiveOverride(BuildOverridePlayer, requestedPlayer);
            _overrideGroup = ResolveEffectiveOverride(BuildOverrideGroup, requestedGroup);

            if (playerForced && requestedPlayer != _overridePlayer)
                LogVerbose($"Address override: player={requestedPlayer} ignored — forced to {_overridePlayer} by HapbeatConfig.buildOverridePlayer.");
            if (groupForced && requestedGroup != _overrideGroup)
                LogVerbose($"Address override: group={requestedGroup} ignored — forced to {_overrideGroup} by HapbeatConfig.buildOverrideGroup.");

            if (_client != null)
                _client.SetAddressOverride(_overridePlayer, _overrideGroup);

            // Persist only the axes the build leaves to this device. Writing a
            // forced axis would bake the build's value into PlayerPrefs, so a
            // later build that un-forces that axis would silently inherit it.
            if (persist && (!playerForced || !groupForced))
            {
                if (!playerForced)
                    PlayerPrefs.SetInt(PlayerPrefsKeyOverridePlayer, _overridePlayer);
                if (!groupForced)
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
        /// (<see cref="AddressOverrideDisabled"/>) on every axis the build leaves to this
        /// device. An axis pinned by the build (<see cref="IsPlayerForcedByBuild"/> /
        /// <see cref="IsGroupForcedByBuild"/>) stays on its config value — it was never
        /// per-device state, so there is nothing to clear for it.
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

            // Build-forced axes are restored to their config value by
            // SetAddressOverride itself (it ignores the -1 for those axes).
            SetAddressOverride(AddressOverrideDisabled, AddressOverrideDisabled, persist: false);

            Log($"Persisted address override cleared — player={_overridePlayer}, group={_overrideGroup} " +
                "(build-forced axes keep their config value).");
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
            // Record the intent before the early return: calling Connect() while
            // already connected still means "stay connected" to the retry loop.
            _shouldStayConnected = true;

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
            _shouldStayConnected = true;

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
            // Deliberate disconnect — stand the retry loop down so it doesn't
            // immediately undo this.
            _shouldStayConnected = false;

            // Join the background mixer thread (if any) before tearing down the
            // socket it sends through. No-op if nothing is streaming.
            StopStream();

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

            var playback = new HapbeatStreamPlayback(baselineGain, initialGain);
            var source = new StreamSource(clip, playback, loop);

            // The whole "read _sessionActive \u2192 validate compatibility \u2192 mutate
            // _sources/_sessionActive" sequence must be atomic: the background
            // mixer thread can flip _sessionActive false (natural completion \u2014
            // all sources finished on their own) at any time now, not just on a
            // main-thread StopStream() call as before. Without one lock spanning
            // the read and the mutation, a session could finish between this
            // method's compatibility check and its _sources.Add(), silently
            // dropping the new source into an abandoned list nobody is mixing
            // anymore.
            bool startNewSession = false;
            int activeSourceCount = 0;
            lock (_streamLock)
            {
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

                    _sources.Add(source);
                    activeSourceCount = _sources.Count;
                }
                else
                {
                    _sessionSampleRate = (ushort)clip.frequency;
                    _sessionChannels = (byte)clip.channels;
                    _sessionTarget = target;
                    _sessionActive = true;
                    _streamStopRequested = false;
                    Interlocked.Exchange(ref _streamEndSentFlag, 0);
                    // Reset diagnostics before the mixer thread starts recording.
                    _diagSendCount = 0;
                    _diagBeginCount = 0;
                    Interlocked.Exchange(ref _diagEndCount, 0);
                    _sources.Add(source);
                    startNewSession = true;
                }
            }

            if (startNewSession)
            {
                // Unicast STREAM_BEGIN/DATA/END to already-known devices instead of
                // broadcast, when enabled \u2014 broadcast frames can sit in the AP's
                // DTIM power-save queue for a whole beacon interval, which shows up
                // as periodic ~100-200ms stutter in streamed haptics. Snapshotted
                // once here (session start), not re-evaluated mid-session, so the
                // background mixer thread's SendStreamData stays lock-free; a
                // device whose first PONG arrives after this point is picked up
                // starting with the next session instead. Falls back to broadcast
                // when nobody has PONGed yet or we're not in plain broadcast mode
                // (e.g. Bridge/ESP-NOW already unicasts to the bridge host).
                if (_client.IsBroadcast && _config != null && _config.streamUnicast)
                {
                    var aliveIps = GetAliveDeviceIPs();
                    // Filter aliveIps down to devices whose known address (from PONG)
                    // matches the *resolved* target \u2014 the same string SendStreamBegin
                    // below will actually put on the wire (address-override player/
                    // group applied), not the raw EventMap target \u2014 so filtering
                    // agrees with what firmware will match on its end. Unknown-
                    // address devices are kept (fail open); this is what keeps a
                    // 1-person-multiple-device / many-simultaneous-pairs LAN from
                    // fanning every stream chunk out to everyone else's devices too.
                    string resolvedTarget = HapbeatClient.ResolveTarget(target, _overridePlayer, _overrideGroup);
                    int matchedCount = _client.SetStreamUnicastTargets(aliveIps, resolvedTarget);
                    // Log what actually happened, not what we hoped for: matchedCount
                    // reflects post-filter reality (see SetStreamUnicastTargets), so a
                    // 0 here after known devices existed means the send is skipped
                    // this session, not silently broadcast.
                    if (matchedCount > 0)
                        Log($"\u266a Stream unicast: targeting {matchedCount} known device(s).");
                    else if (aliveIps.Count > 0)
                        Log($"\u266a Stream unicast: target '{resolvedTarget}' matched 0 of {aliveIps.Count} known device(s); skipping stream send this session.");
                    else
                        Log("\u266a Stream unicast: no known devices yet; broadcasting this session.");
                }
                else
                {
                    _client.SetStreamUnicastTargets(null);
                }

                // Send gain=1.0 so the device passes through, and pre-multiply
                // each source's gain in the SDK (sample \u00d7 gain) to avoid the
                // device applying it twice. totalSamples=0 means "unknown".
                _client.SendStreamBegin(_sessionSampleRate, _sessionChannels,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1.0f, target);
                _diagBeginCount++;
                Log($"\u266a Stream session begin: {_sessionSampleRate}Hz, {_sessionChannels}ch, " +
                    $"target={(string.IsNullOrEmpty(target) ? "broadcast" : target)}");

                // Background thread instead of a MonoBehaviour coroutine: the mixer
                // previously ran inside MixerCoroutine (Update-driven), so a frame
                // hitch (GC / rendering spike / physics) delayed chunk sends and
                // starved the device ring buffer, producing audible dropouts even
                // though Wi-Fi and the firmware mixer were healthy. A dedicated
                // Thread paces sends off a Stopwatch instead of Unity's frame clock,
                // so it keeps sending on schedule regardless of what the main thread
                // is doing. See subrepo-devs/unity-sdk/instructions/
                // instructions-streaming-background-thread-202605210100.md.
                //
                // _streamThread itself is only ever otherwise touched under
                // _streamLock (by StopStream and the mixer thread's own trailer);
                // assigning it under the same lock here — even though this whole
                // method is main-thread-only in practice — keeps lock discipline
                // uniform for that field rather than relying on call-order
                // reasoning. Starting the thread while holding the lock is safe:
                // its first lock acquisition happens inside StreamThreadLoop's
                // while-loop, so at worst it blocks briefly until this scope exits.
                lock (_streamLock)
                {
                    _streamThread = new Thread(StreamThreadLoop)
                    {
                        Name = "HapbeatStreamMixer",
                        IsBackground = true
                    };
                    _streamThread.Start();
                }
            }
            else
            {
                Log($"\u266a Stream source added: {clip.name} (active sources={activeSourceCount}, loop={loop})");
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
            Thread threadToJoin;
            bool wasActive;
            lock (_streamLock)
            {
                wasActive = _sessionActive;
                for (int i = 0; i < _sources.Count; i++)
                    _sources[i].Playback?.MarkStopped();
                _sources.Clear();
                // Tell the thread to exit without sending its own STREAM_END —
                // this call owns that (via TrySendStreamEnd below), so whichever
                // of the two ends up running first the device only sees one.
                _streamStopRequested = true;
                threadToJoin = _streamThread;
            }

            if (threadToJoin != null)
            {
                // Chunks are ~10ms and the pacing sleep polls _streamStopRequested
                // every <2ms, so the thread should wake and exit within a few ms.
                // 500ms is a generous ceiling, not the expected case — if it's ever
                // hit something else is wrong (e.g. a blocked UDP send), and we log
                // rather than hang the caller (main thread) forever.
                if (!threadToJoin.Join(500))
                    Debug.LogWarning("[Hapbeat] StopStream: mixer thread did not exit within 500ms.");
            }

            lock (_streamLock)
            {
                _sessionActive = false;
                _streamThread = null;
                _streamStopRequested = false;
            }

            if (wasActive && TrySendStreamEnd("Stream session stopped (all sources)."))
                LogStreamDiagSummary("stopped");

            // Clear the per-session unicast snapshot *after* the closing STREAM_END
            // above (which must still reach this session's unicast targets) so
            // anything sent afterward — including StopStreamWithFlush's own
            // BEGIN+END pair, documented to broadcast — doesn't reuse a now-stale
            // device list.
            _client?.SetStreamUnicastTargets(null);
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
        public HapbeatStreamPlayback ActivePlayback
        {
            get
            {
                // _sources is mutated by the background mixer thread (adds happen
                // on the main thread too — see StreamAudioClip); guard every access
                // with the same lock so this never observes a torn/mid-mutation list.
                lock (_streamLock)
                {
                    return (_sources.Count > 0 && !_sources[0].Playback.IsStopped) ? _sources[0].Playback : null;
                }
            }
        }

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
                // AudioClip.GetData is a Unity API and main-thread-only, so we read
                // the whole clip into a plain managed float[] here, on the main
                // thread, at StreamAudioClip() call time — before the source is ever
                // handed to the background mixer thread. The mixer thread only ever
                // touches this plain array + Cursor afterwards, so it needs no Unity
                // API access at all.
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

        // Guards all structural access to _sources (Add from the main thread in
        // StreamAudioClip / StopStream; Add+Remove+iterate from the mixer thread
        // in StreamThreadLoop) plus the _sessionActive / _streamThread /
        // _streamStopRequested trio below. Per-chunk hold time is a few float
        // multiplies over a few hundred samples — sub-microsecond — so contention
        // is never a concern even though the mixer thread takes it every ~10ms.
        private readonly object _streamLock = new object();
        private readonly System.Collections.Generic.List<StreamSource> _sources
            = new System.Collections.Generic.List<StreamSource>(4);
        private Thread _streamThread;
        // Set by StopStream() to tell the mixer thread to exit immediately without
        // sending its own STREAM_END (StopStream sends it instead — see
        // TrySendStreamEnd). Polled frequently (sub-2ms) by both the loop condition
        // and PreciseSleep so Stop-triggered teardown is fast, not just eventual.
        private volatile bool _streamStopRequested;
        // Interlocked-guarded flip so exactly one of {StopStream (forced stop),
        // StreamThreadLoop (natural completion — all sources finished on their
        // own)} sends the closing STREAM_END, even in the rare race where both
        // happen within the same few microseconds.
        private int _streamEndSentFlag;
        private ushort _sessionSampleRate;
        private byte _sessionChannels;
        private string _sessionTarget;
        private volatile bool _sessionActive;

        // ---- Lightweight stream-send diagnostics (see LogStreamDiagSummary) ----
        // Pre-allocated so recording adds zero per-chunk GC pressure (recording is
        // the thing we're measuring, so it must not perturb the allocation profile).
        // _diagSendTicks is written only by the mixer thread (single writer) and read
        // once at session end — either on the mixer thread itself (natural completion)
        // or on the main thread after Join() (forced stop) — so it needs no lock.
        private const int StreamDiagCapacity = 8192; // ~80s at 10ms chunks; overflow just stops recording
        private readonly long[] _diagSendTicks = new long[StreamDiagCapacity];
        private int _diagSendCount;   // mixer-thread writer
        private int _diagBeginCount;  // main-thread writer (StreamAudioClip)
        private int _diagEndCount;    // Interlocked (TrySendStreamEnd, either thread)

        /// <summary>
        /// Sends STREAM_END at most once per session — see <see cref="_streamEndSentFlag"/>.
        /// Callable from either the main thread (StopStream) or the mixer thread
        /// (StreamThreadLoop's natural-completion path).
        /// </summary>
        /// <returns>True if THIS call owns end-of-session (won the flag flip) — the
        /// caller should emit the one-shot diagnostics summary. False if the other
        /// path already closed the session (it owns the summary).</returns>
        private bool TrySendStreamEnd(string logMessage)
        {
            if (Interlocked.Exchange(ref _streamEndSentFlag, 1) != 0)
                return false; // the other path already sent it (and owns the diag summary)
            if (_client != null && _client.IsConnected)
            {
                _client.SendStreamEnd();
                Interlocked.Increment(ref _diagEndCount);
                Log(logMessage);
            }
            return true;
        }

        /// <summary>
        /// One-shot per-session summary of the streaming send cadence. Called
        /// exactly once per session by whichever path won <see cref="TrySendStreamEnd"/>,
        /// after the mixer thread has stopped writing <see cref="_diagSendTicks"/>
        /// (so no lock is needed). Gated by <c>enableLogging</c> like <see cref="Log"/>,
        /// so it is inert in normal operation. Lets a single playback reproduce +
        /// paste the log to pinpoint intra-session send gaps (e.g. GC stop-the-world
        /// suspending the mixer thread) vs. device-visible re-triggering (BEGIN/END &gt; 1).
        /// </summary>
        private void LogStreamDiagSummary(string reason)
        {
            if (_config == null || !_config.enableLogging) return;

            int n = _diagSendCount;
            int begins = _diagBeginCount;
            int ends = _diagEndCount;
            if (n <= 0)
            {
                Log($"Stream diag ({reason}): sends=0 BEGIN={begins} END={ends}");
                return;
            }

            double freq = Stopwatch.Frequency;
            long t0 = _diagSendTicks[0];
            double maxGapMs = 0.0;
            int bigGaps = 0;
            var gapTimes = new System.Text.StringBuilder(160);
            for (int i = 1; i < n; i++)
            {
                double gapMs = (_diagSendTicks[i] - _diagSendTicks[i - 1]) * 1000.0 / freq;
                if (gapMs > maxGapMs) maxGapMs = gapMs;
                if (gapMs > 20.0)
                {
                    bigGaps++;
                    if (bigGaps <= 40)
                    {
                        double atMs = (_diagSendTicks[i] - t0) * 1000.0 / freq;
                        if (gapTimes.Length > 0) gapTimes.Append(", ");
                        gapTimes.Append(atMs.ToString("F0")).Append("ms(+").Append(gapMs.ToString("F0")).Append(')');
                    }
                }
            }
            double spanMs = (_diagSendTicks[n - 1] - t0) * 1000.0 / freq;

            Log($"Stream diag ({reason}): sends={n} span={spanMs:F0}ms " +
                $"maxGap={maxGapMs:F1}ms gaps>20ms={bigGaps} BEGIN={begins} END={ends}" +
                (bigGaps > 0 ? $" @[{gapTimes}{(bigGaps > 40 ? ", …" : "")}]" : ""));
        }

        /// <summary>
        /// Target chunk duration for the background mixer thread. Deliberately
        /// small (~10ms, vs. the old MTU-max chunks of ~22–44ms) so the thread
        /// sends on a fine, even cadence instead of infrequent bursts — this is
        /// the actual smoothness fix; running off a Stopwatch instead of Unity's
        /// frame clock only removes the *source* of the jitter (main-thread frame
        /// hitches), while smaller chunks reduce how much any single missed send
        /// can starve the device ring buffer.
        /// </summary>
        private const float StreamChunkTargetSeconds = 0.01f;

        /// <summary>
        /// Multi-source mixer, running on a dedicated background thread (started
        /// by <see cref="StreamAudioClip"/>, joined by <see cref="StopStream"/> /
        /// <see cref="Disconnect"/> / <see cref="Cleanup"/>). Mixes one chunk's
        /// worth of samples from every active source as float, converts to
        /// PCM16, and sends as a single wire stream, paced off a
        /// <see cref="Stopwatch"/> so it's unaffected by Unity main-thread frame
        /// jitter (GC / rendering spikes). When all sources finish on their own
        /// (nobody called <see cref="HapbeatStreamPlayback.Stop"/> /
        /// <see cref="StopStream"/>), this thread sends STREAM_END itself and
        /// exits — the session isn't kept idle/resident between streams (see
        /// instruction doc §"idle 常駐" tradeoff): sessions are episodic bursts of
        /// haptic feedback, not a continuous background service, and thread
        /// start/stop cost (sub-ms) is negligible next to a ~10ms chunk cadence,
        /// so tearing down between sessions keeps the lifetime trivially easy to
        /// reason about (no idle-thread wake/sleep coordination to get wrong).
        /// <para>
        /// Loop-seam crossfade (instruction doc §D) is intentionally out of scope
        /// here — it's an independent audio-quality improvement, not part of the
        /// threading fix, and is left for a follow-up.
        /// </para>
        /// </summary>
        private void StreamThreadLoop()
        {
            byte channels = _sessionChannels;
            ushort sampleRate = _sessionSampleRate;
            int maxChunkBytes = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
            // Chunk size in SAMPLES (not bytes). Must be a whole number of
            // sample frames so stereo L/R stays aligned at chunk boundaries.
            int bytesPerFrame = channels * 2;
            int mtuFramesPerChunk = Math.Max(1, maxChunkBytes / bytesPerFrame);
            int targetFramesPerChunk = Math.Max(1, Mathf.RoundToInt(sampleRate * StreamChunkTargetSeconds));
            int framesPerChunk = Math.Min(mtuFramesPerChunk, targetFramesPerChunk);
            int samplesPerChunk = framesPerChunk * channels;
            byte[] pcmChunk = new byte[samplesPerChunk * 2];
            float[] mixBuffer = new float[samplesPerChunk];

            float bytesPerSecond = sampleRate * channels * 2f;
            float sendAheadSeconds = _config != null ? _config.streamSendAheadSeconds : 0.05f;
            if (sendAheadSeconds < 0.01f) sendAheadSeconds = 0.05f;

            var stopwatch = Stopwatch.StartNew();
            uint globalByteOffset = 0;
            // False only when StopStream() forced this iteration to end — in that
            // case StopStream (not us) sends the closing STREAM_END.
            bool naturalCompletion = true;

            try
            {
                while (true)
                {
                    if (_streamStopRequested) { naturalCompletion = false; break; }

                    bool anySources;
                    lock (_streamLock)
                    {
                        if (_streamStopRequested) { naturalCompletion = false; break; }

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

                        anySources = _sources.Count > 0;
                    }

                    // 3. Convert mixed float → PCM16 (clamp to int16 range) — outside
                    // the lock; only touches this thread's own local buffers.
                    for (int i = 0; i < samplesPerChunk; i++)
                    {
                        short pcm = (short)Mathf.Clamp(mixBuffer[i] * 32767f, -32768f, 32767f);
                        pcmChunk[i * 2    ] = (byte)(pcm & 0xFF);
                        pcmChunk[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                    }

                    // 4. Send chunk — also outside the lock (UDP send shouldn't block
                    // main-thread Add/StopStream calls waiting on _streamLock). Sent
                    // unconditionally, even when this mix just drained the last active
                    // source (anySources below is now false): step 2 above still wrote
                    // that source's real trailing samples into mixBuffer before removing
                    // it, so checking "no sources left" *before* sending (as before) threw
                    // away up to one whole chunk (~10ms) of real audio off the end of
                    // every one-shot clip instead of delivering its tail.
                    int chunkBytes = samplesPerChunk * 2;
                    var client = _client;
                    if (client == null || !client.IsConnected) { naturalCompletion = false; break; }
                    client.SendStreamData(globalByteOffset, pcmChunk, 0, chunkBytes);
                    globalByteOffset += (uint)chunkBytes;
                    // Diagnostics: stamp each actual wire send (single-writer, pre-allocated).
                    if (_diagSendCount < StreamDiagCapacity)
                        _diagSendTicks[_diagSendCount++] = Stopwatch.GetTimestamp();

                    if (!anySources) break; // natural completion — no more sources; last chunk already sent above

                    // 5. Pace — pin lead at sendAheadSeconds regardless of chunk size,
                    // using a Stopwatch instead of Unity's Time.realtimeSinceStartup
                    // (UnityEngine.Time is main-thread-only and would throw here).
                    //
                    // overshoot is computed from *cumulative* bytes sent vs. elapsed
                    // wall time (not a fixed per-chunk sleep), so any single chunk that
                    // sends late is auto-corrected on the next iteration — this feedback
                    // property carries over unchanged from the original coroutine.
                    double elapsedTime = stopwatch.Elapsed.TotalSeconds;
                    double sentDuration = globalByteOffset / (double)bytesPerSecond;
                    double overshoot = sentDuration - elapsedTime - sendAheadSeconds;
                    if (overshoot > 0.0)
                        PreciseSleep(overshoot);
                }
            }
            finally
            {
                // Trailer (sessionActive clear + natural-completion STREAM_END) runs
                // as ONE atomic critical section, not two. Previously the STREAM_END
                // send happened after this lock was released, which raced the main
                // thread: StreamAudioClip() only needs _sessionActive == false (set
                // below) to start a brand-new session — it sends its own STREAM_BEGIN
                // and resets _streamEndSentFlag to 0 while holding this same lock. If
                // that happened in the gap between "unlock" and "send old END" here,
                // the stale END would land *after* the new BEGIN (killing the new
                // session on the device) and would also consume the flag the new
                // session's own eventual END depends on (leaving the new session's
                // real END permanently suppressed). Sending inside this lock closes
                // that window: StreamAudioClip's new-session branch can't proceed
                // until it acquires the same lock, by which point this trailer —
                // flag flip + END — has already fully run.
                //
                // Wrapped in try/finally (rather than falling off the end of the
                // while-loop) so an unexpected exception mid-loop still clears
                // _sessionActive and attempts the closing STREAM_END instead of
                // leaving the session stuck "active" forever with no thread left
                // to service it.
                lock (_streamLock)
                {
                    _sessionActive = false;
                    _streamThread = null;
                    _streamStopRequested = false;

                    if (naturalCompletion && TrySendStreamEnd($"Stream session ended ({globalByteOffset} bytes sent)."))
                        LogStreamDiagSummary("natural");
                }
            }
        }

        /// <summary>
        /// Sleeps for approximately <paramref name="seconds"/>, exiting early if
        /// <see cref="_streamStopRequested"/> becomes true so <see cref="StopStream"/>
        /// gets a responsive join instead of waiting out a full pacing sleep.
        /// <para>
        /// Every call from <see cref="StreamThreadLoop"/> requests a duration no
        /// larger than one ~10ms chunk (the pacing check runs once per chunk, and
        /// only sleeps the small excess once the send-ahead lead is reached) — but
        /// Windows' default timer resolution makes a single <c>Thread.Sleep(1)</c>
        /// call accurate only to ~15.6ms, i.e. coarser than every request this
        /// method actually receives in practice. Calling <c>Thread.Sleep(1)</c> at
        /// all therefore always overshoots by ~5–13ms, and because a correction is
        /// due on nearly every chunk boundary, that overshoot got paid on nearly
        /// every chunk — bleeding the lead below <c>sendAheadSeconds</c> on a
        /// recurring cadence instead of an occasional one. <c>spinThresholdSeconds</c>
        /// is set above that ~15.6ms floor so requests in this method's actual
        /// range always take the accurate <c>SpinWait</c> path; <c>Thread.Sleep(1)</c>
        /// is kept only for the (here, unreached in normal operation) case of a
        /// genuinely large request, where its floor cost is a small fraction of
        /// the total and busy-waiting the whole thing would waste real CPU.
        /// </para>
        /// </summary>
        private void PreciseSleep(double seconds)
        {
            const double spinThresholdSeconds = 0.016; // >= Windows' ~15.6ms Sleep(1) floor: spin instead of Sleep
            var sw = Stopwatch.StartNew();
            while (!_streamStopRequested)
            {
                double remaining = seconds - sw.Elapsed.TotalSeconds;
                if (remaining <= 0.0) return;
                if (remaining > spinThresholdSeconds)
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(200);
            }
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

            // Per axis: a build-wide value in HapbeatConfig (1..99) wins and is not
            // changeable on this device; otherwise PlayerPrefs (if present) restores a
            // per-device override saved by a prior SetAddressOverride(..., persist: true)
            // call, and failing that the axis starts disabled.
            // See ResolveEffectiveOverride.
            int player = PlayerPrefs.HasKey(PlayerPrefsKeyOverridePlayer)
                ? PlayerPrefs.GetInt(PlayerPrefsKeyOverridePlayer)
                : AddressOverrideDisabled;
            int group = PlayerPrefs.HasKey(PlayerPrefsKeyOverrideGroup)
                ? PlayerPrefs.GetInt(PlayerPrefsKeyOverrideGroup)
                : AddressOverrideDisabled;
            _overridePlayer = ResolveEffectiveOverride(BuildOverridePlayer, player);
            _overrideGroup = ResolveEffectiveOverride(BuildOverrideGroup, group);

            _client = CreateClient();
            _isInitialized = true;

            // Auto-connect on startup
            Connect();
        }

        private HapbeatClient CreateClient()
        {
            var client = new HapbeatClient();

            // Push the routing config here rather than at the Initialize() call site:
            // Connect() / ConnectToBridge() also construct a client when none exists
            // yet, and those paths used to leave it on library defaults — silently
            // dropping the address override for anyone who calls Connect() directly.
            client.SetAddressOverride(_overridePlayer, _overrideGroup);
            client.SetCommandUnicast(
                _config == null || _config.commandUnicast, AliveTimeoutSeconds);

            client.OnConnectionStateChanged += (connected) =>
            {
                if (connected)
                {
                    string mode = client.IsBroadcast ? "broadcast" : "unicast";
                    Log($"Ready ({mode}).");
                    _lastPingTime = Time.realtimeSinceStartup;
                    // Note when this session started; the backoff is only cleared
                    // once it has proven itself (see ReconnectStableSeconds).
                    _connectedSinceTime = Time.realtimeSinceStartup;
                    // Notify devices that app is connected
                    client.SendConnectStatus(true, ConnectStatusGroupByte, AppName, SystemInfo.deviceName);
                    // Discover devices immediately instead of waiting a full
                    // pingInterval (5 s by default) for the first periodic PING.
                    // Until a device PONGs, it is invisible to AliveDeviceCount,
                    // to the stream unicast snapshot, and to command unicast
                    // routing -- so anything fired in that window broadcasts while
                    // later commands unicast. Mixing the two transports is what
                    // lets a PLAY and its matching STOP take different paths (and
                    // arrive out of order, since broadcast is the one the AP delays
                    // for DTIM). Pinging on connect shrinks that window from
                    // seconds to about one RTT.
                    client.SendPing();
                    OnConnected?.Invoke();
                }
                else
                {
                    Log("Disconnected.");

                    // A session that held up long enough earns a fresh backoff;
                    // one that dropped straight away keeps escalating.
                    if (_connectedSinceTime > 0f
                        && Time.realtimeSinceStartup - _connectedSinceTime >= ReconnectStableSeconds)
                    {
                        _reconnectAttempts = 0;
                    }
                    _connectedSinceTime = 0f;

                    // Liveness is per-connection: these IPs were learned on the
                    // network we just lost. Keeping them would leave
                    // AliveDeviceCount reporting phantom devices and would seed
                    // the next stream's unicast targets with unreachable hosts —
                    // non-empty, so it would not fall back to broadcast either.
                    _devicePongTimes.Clear();

                    // Arm the retry loop; TryAutoReconnect applies the backoff and
                    // checks whether staying connected was the intent.
                    _reconnectNextAttemptTime =
                        Time.realtimeSinceStartup + ReconnectBaseDelaySeconds;
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

        /// <summary>
        /// Reopen the connection after something dropped it. Driven from Update()
        /// on an exponential backoff (2 s, doubling, capped at 30 s).
        ///
        /// Without this a single transient failure was terminal for the whole
        /// process: HapbeatClient flagged the connection down, the periodic
        /// PING / CONNECT_STATUS in Update() stopped (both are gated on
        /// IsConnected), and each device fell back to "app not connected" once its
        /// 15 s CONNECT_STATUS timeout expired — with nothing able to restore it
        /// short of restarting the application. Unattended installations (kiosks,
        /// exhibitions) have no operator to notice and do that.
        /// </summary>
        private void TryAutoReconnect()
        {
            if (!_shouldStayConnected || IsConnected)
                return;
            if (_config != null && !_config.autoReconnect)
                return;
            if (Time.realtimeSinceStartup < _reconnectNextAttemptTime)
                return;

            _reconnectAttempts++;
            float nextDelay = Mathf.Min(
                ReconnectBaseDelaySeconds * Mathf.Pow(2f, _reconnectAttempts),
                ReconnectMaxDelaySeconds);
            _reconnectNextAttemptTime = Time.realtimeSinceStartup + nextDelay;

            Debug.LogWarning(
                $"[Hapbeat] Connection is down — reconnect attempt {_reconnectAttempts}. " +
                $"Next retry in {nextDelay:0.#}s if this one fails.");

            // Join the stream mixer thread before the socket underneath it is
            // replaced. Its unicast targets were resolved against the connection
            // we just lost, so the stream is over either way — and letting it keep
            // sending while Connect() closes and reopens the socket is exactly the
            // race the send paths' snapshots guard against. Stop it properly
            // instead of relying on that guard. No-op if nothing is streaming.
            StopStream();

            // Connect() reopens through HapbeatClient.OpenBroadcast/Connect, which
            // now tear down unconditionally — so a half-dead session (socket still
            // open after a socket error flagged the connection down) is cleaned up
            // there rather than being leaked or duplicated here.
            Connect();
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
            // Shutting down for good — never let the retry loop resurrect the
            // client while the app is tearing down.
            _shouldStayConnected = false;

            // Join the background mixer thread (if any) before disposing the client
            // it sends through — must happen first so StopStream's own final
            // STREAM_END still has a live socket to go out on. Domain-reload /
            // Play-mode-stop safety: without this, the thread would keep running
            // (raw System.Threading.Thread isn't swept by Unity like a Coroutine is)
            // and reference a disposed HapbeatClient on its next iteration.
            StopStream();

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
