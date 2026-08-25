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
        private HapbeatEndpointStreamMixer _endpointStreamMixer;
        private float _nextStreamEndpointReconcileTime;
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

            // PONG callbacks reconcile immediately. This low-frequency pass exists
            // for the opposite transition: an endpoint that stopped answering has
            // no callback, so its TTL must be observed by polling. Keep this out of
            // the per-frame hot path because resolving allocates per source.
            if (_endpointStreamMixer != null && _endpointStreamMixer.IsStreaming &&
                Time.realtimeSinceStartup >= _nextStreamEndpointReconcileTime)
            {
                _endpointStreamMixer.ReconcileEndpoints();
                _nextStreamEndpointReconcileTime = Time.realtimeSinceStartup + 1f;
            }

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
        /// <param name="pan">Stereo balance applied on the device: -1 = left only, 0 = center, +1 = right only.
        /// Requires firmware with DEC-055 PLAY pan support; older firmware ignores the field and plays centered.</param>
        public void Play(string eventId, float gain = 1.0f, string displayName = null, string target = null,
            float pan = 0f)
        {
            if (!EnsureConnected())
                return;

            long targetTimeUs = 0;
            _client.SendPlay(eventId, targetTimeUs, gain, target, pan);

            string label = string.IsNullOrEmpty(displayName) ? eventId : $"{displayName} ({eventId})";
            string targetInfo = string.IsNullOrEmpty(target) ? "(broadcast)" : $"target={target}";
            Log($"\u25b6 Play \"{label}\" gain={gain:F1} pan={pan:F2} {targetInfo}");
        }

        /// <summary>
        /// Schedule a haptic event to play at a specific target time.
        /// </summary>
        /// <param name="pan">Stereo balance; see <see cref="Play"/>.</param>
        public void PlayScheduled(string eventId, long targetTimeUs, float gain = 1.0f, string target = null,
            float pan = 0f)
        {
            if (!EnsureConnected())
                return;
            _client.SendPlay(eventId, targetTimeUs, gain, target, pan);
            Log($"\u25b6 PlayScheduled \"{eventId}\" (target_time={targetTimeUs}us, gain={gain:F1}, pan={pan:F2}, target={target ?? "(broadcast)"})");
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
        /// The audio is sent in chunks that fit within MTU limits from a dedicated
        /// scheduler thread (non-blocking for Unity's main thread).
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
        /// Sources are normalized to the SDK's stream format and mixed only at
        /// matching device endpoints; differing clip formats and targets are valid.
        /// </para>
        /// </summary>
        /// <param name="clip">AudioClip to stream (will be read as PCM16).</param>
        /// <param name="gain">Initial gain multiplier (0.0 - 2.0). Binding can
        /// override this per frame via the returned handle.</param>
        /// <param name="target">Optional target filter (e.g. "player_1/pos_neck").
        /// Null selects every resolved device endpoint without broadcasting STREAM_DATA.</param>
        /// <param name="loop">If true, the source loops until
        /// <see cref="HapbeatStreamPlayback.Stop"/> is called.</param>
        /// <returns>Per-source handle for runtime control, or <c>null</c> when
        /// the client is disconnected or <paramref name="clip"/> is null. A
        /// non-null handle may report <see cref="HapbeatStreamPlaybackStatus.Deferred"/>
        /// until a matching PONG-backed endpoint is known.</returns>
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

            // Direct streaming intentionally waits for a PONG-backed addressed
            // endpoint instead of broadcasting target-less STREAM_DATA. The returned
            // handle exposes Deferred/Active/Stopped through Status.
            string resolvedTarget = HapbeatClient.ResolveTarget(target, _overridePlayer, _overrideGroup);
            HapbeatStreamPlayback playback = GetEndpointStreamMixer().Add(
                clip, baselineGain, initialGain, resolvedTarget, loop);
            if (playback.Status == HapbeatStreamPlaybackStatus.Deferred)
            {
                if (playback.DeferReason == HapbeatStreamPlaybackDeferReason.TransportTargetConflict)
                {
                    Debug.LogWarning($"[Hapbeat] StreamAudioClip deferred: the active transport stream is already bound to a different target than '{resolvedTarget}'.");
                }
                else
                {
                    Debug.LogWarning($"[Hapbeat] StreamAudioClip deferred: no addressed transport endpoint matches target '{resolvedTarget}'. STREAM_DATA was not broadcast.");
                }
            }
            return playback;
        }


        /// <summary>Stops every logical stream source and ends each active endpoint session.</summary>
        public void StopStream()
        {
            _endpointStreamMixer?.StopAll();
        }

        /// <summary>
        /// Stream flushing is endpoint-scoped: Direct multi-stream sessions never
        /// emit a broadcast BEGIN/END pair because it could stop an unrelated
        /// endpoint's stream.
        /// </summary>
        public void StopStreamWithFlush(string target = null)
        {
            if (string.IsNullOrEmpty(target)) _endpointStreamMixer?.StopAll(flush: true);
            else _endpointStreamMixer?.StopTarget(
                HapbeatClient.ResolveTarget(target, _overridePlayer, _overrideGroup), flush: true);
        }

        /// <summary>True while any logical stream source is registered.</summary>
        public bool IsStreaming => _endpointStreamMixer != null && _endpointStreamMixer.IsStreaming;

        /// <summary>First active logical stream source, or null.</summary>
        public HapbeatStreamPlayback ActivePlayback => _endpointStreamMixer?.ActivePlayback;

        #endregion

        #region Private Methods

        private HapbeatEndpointStreamMixer GetEndpointStreamMixer()
        {
            if (_endpointStreamMixer != null) return _endpointStreamMixer;
            _endpointStreamMixer = new HapbeatEndpointStreamMixer(
                () => _client,
                target => _client != null
                    ? _client.GetResolvedStreamEndpoints(target)
                    : new List<HapbeatClient.StreamEndpoint>(),
                () => _config != null ? _config.streamSendAheadSeconds : 0.05f,
                Log);
            return _endpointStreamMixer;
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
                    // to addressed stream endpoint resolution, and to command unicast
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
                // The client has already recorded endpoint + address before this
                // main-thread callback. Join newly discovered devices and replace
                // sessions whose PONG address changed immediately.
                _endpointStreamMixer?.ReconcileEndpoints();
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
            _endpointStreamMixer?.Dispose();
            _endpointStreamMixer = null;

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
            _endpointStreamMixer?.Dispose();
            _endpointStreamMixer = null;

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
