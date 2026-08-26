#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatManager.
    /// Shows connection status, discovered devices, and provides test play controls.
    /// Supports Edit-mode discovery and ping via standalone HapbeatClient.
    /// <para>
    /// The Test section mirrors the EventMap Window so users don't have to
    /// hand-type event IDs or target strings:
    /// <list type="bullet">
    ///   <item>Mode toggle (Command / StreamClip)</item>
    ///   <item>"From Kit" event picker (Command) — populated by HapbeatManifestIntensity</item>
    ///   <item>AudioClip drag-drop (StreamClip) + loop toggle</item>
    ///   <item>Targeting fields (Prefix / Player / Position / Group) — same builder as EventMap</item>
    /// </list>
    /// </para>
    /// </summary>
    [CustomEditor(typeof(HapbeatManager))]
    public class HapbeatManagerEditor : UnityEditor.Editor
    {
        // SessionState keys (persist between domain reloads while editor open).
        // `_v2` suffix: schema 変更 (gain semantics + None default) で legacy session を捨てる
        // — 旧キーには "weapon.gunshot" や intensity と取り違えた 0.3 等が残っている可能性がある。
        private const string PREF_MODE         = "Hapbeat_TestMode_v2";       // 0=Command, 1=StreamClip
        private const string PREF_EVENT_ID     = "Hapbeat_TestEventId_v2";
        private const string PREF_KIT_NAME     = "Hapbeat_TestKitName_v2";
        private const string PREF_STREAM_GUID  = "Hapbeat_TestStreamClipGuid_v2";
        private const string PREF_LOOP         = "Hapbeat_TestLoop_v2";
        private const string PREF_GAIN         = "Hapbeat_TestGain_v2";
        private const string PREF_TARGET       = "Hapbeat_TestTarget_v2";

        // --- Test play state ---
        private int _testMode;          // 0 = Command, 1 = StreamClip
        private string _testEventId;
        private string _testKitName;    // last-picked kit (for the "from kit" dropdown to remember its category)
        private AudioClip _testStreamClip;
        private bool _testLoop;
        private float _testGain;
        private string _testTarget;

        // Cached resolved manifest intensity for the active selection (display-only hint).
        // float.NaN = not resolved (no match in any kit manifest).
        private float _resolvedIntensity = float.NaN;
        private string _resolvedKitPath = "";

        private bool _showTestSection = true;
        private bool _showDeviceSection = true;

        // Edit-mode standalone client for discovery/ping (independent of Play-mode singleton)
        private static HapbeatClient _editorClient;
        private static HapbeatDiscovery _editorDiscovery;
        private static List<HapbeatDevice> _editorDevices = new List<HapbeatDevice>();
        private static bool _editorConnected;
        private static string _editorPingResult = "";

        private void OnEnable()
        {
            _testMode      = SessionState.GetInt(PREF_MODE, 0);
            _testEventId   = SessionState.GetString(PREF_EVENT_ID, "");
            _testKitName   = SessionState.GetString(PREF_KIT_NAME, "");
            string clipGuid = SessionState.GetString(PREF_STREAM_GUID, "");
            if (!string.IsNullOrEmpty(clipGuid))
            {
                string path = AssetDatabase.GUIDToAssetPath(clipGuid);
                if (!string.IsNullOrEmpty(path))
                    _testStreamClip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            _testLoop      = SessionState.GetBool(PREF_LOOP, false);
            // _testGain は EventMap の entry.gain と同じ意味で「authored intensity の倍率」。
            // 既定 1.0 = manifest intensity をそのまま使う。送信 gain = _testGain × intensity。
            _testGain      = SessionState.GetFloat(PREF_GAIN, 1.0f);
            _testTarget    = SessionState.GetString(PREF_TARGET, "");

            ResolveIntensity();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            HapbeatManager manager = (HapbeatManager)target;

            EditorGUILayout.Space(10);
            DrawConnectionStatus(manager);
            EditorGUILayout.Space(10);
            DrawDeviceList(manager);
            EditorGUILayout.Space(10);
            DrawTestControls(manager);

            // Dispatch edit-mode callbacks
            if (!Application.isPlaying)
            {
                _editorDiscovery?.DispatchCallbacks();
                _editorClient?.DispatchMainThreadCallbacks();
            }
        }

        // ── Connection status section ─────────────────────────────────────

        private void DrawConnectionStatus(HapbeatManager manager)
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            bool isConnected = isPlaying ? manager.IsConnected : _editorConnected;

            // Status indicator
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Status");

            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = isConnected ? Color.green : Color.red;

            string statusText;
            if (isPlaying && isConnected)
            {
                statusText = "Ready (WifiUdp)";
            }
            else if (!isPlaying && isConnected)
            {
                statusText = "Connected (Edit)";
            }
            else
            {
                statusText = "Not connected";
            }
            GUILayout.Button(statusText, GUILayout.Width(160));
            GUI.backgroundColor = originalColor;

            EditorGUILayout.EndHorizontal();

            if (isPlaying && isConnected)
            {
                EditorGUILayout.LabelField("Transport", "WifiUdp");
                EditorGUILayout.LabelField("Time offset", $"{manager.TimeOffsetUs} μs");
            }

            EditorGUILayout.Space(6);
            DrawAddressOverrideBox(manager);

            if (isPlaying)
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginDisabledGroup(isConnected);
                if (GUILayout.Button("Connect"))
                    manager.Connect();
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!isConnected);
                if (GUILayout.Button("Disconnect"))
                    manager.Disconnect();
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("Discover"))
                    manager.Discover();

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(_editorConnected ? "Disconnect" : "Connect (Edit)"))
                {
                    if (_editorConnected)
                        EditorDisconnect();
                    else
                        EditorConnect(manager);
                }

                if (GUILayout.Button("Discover (Edit)"))
                    EditorDiscover(manager);

                if (_editorConnected && GUILayout.Button("Ping"))
                    EditorPing();

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_editorPingResult))
                    EditorGUILayout.HelpBox(_editorPingResult, MessageType.None);
            }
        }

        /// <summary>
        /// Compact "Override Addressing (this device)" box: the two always-on status
        /// rows (saved / active), shared with <see cref="HapbeatRuntimeStatusWindow"/>
        /// via <see cref="HapbeatAddressOverrideStatusGUI"/>, plus a button to open
        /// that window for the full view (including "Clear Saved Override", now
        /// consolidated there instead of duplicated inline).
        /// <para>
        /// Available in both Edit and Play mode (unlike the rest of this section's
        /// runtime-only rows) since the saved override can be inspected without
        /// entering Play. Rows are drawn unconditionally (never inserted/removed) so
        /// this box never shifts the rest of the inspector's layout — only the text changes.
        /// </para>
        /// </summary>
        private void DrawAddressOverrideBox(HapbeatManager manager)
        {
            HapbeatAddressOverrideStatusGUI.DrawCompact(manager);

            if (GUILayout.Button("Open Runtime Status"))
                HapbeatRuntimeStatusWindow.ShowWindow();
        }

        // ── Device list section ─────────────────────────────────────────

        private void DrawDeviceList(HapbeatManager manager)
        {
            _showDeviceSection = EditorGUILayout.Foldout(_showDeviceSection, "Discovered devices", true);
            if (!_showDeviceSection) return;

            EditorGUI.indentLevel++;

            IReadOnlyList<HapbeatDevice> devices = Application.isPlaying
                ? manager.DiscoveredDevices
                : (IReadOnlyList<HapbeatDevice>)_editorDevices.AsReadOnly();

            if (devices.Count == 0)
            {
                EditorGUILayout.HelpBox("No devices found. Press \"Discover\" to scan.", MessageType.Info);
            }
            else
            {
                foreach (var device in devices)
                {
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField(device.name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("IP", device.ipAddress);
                    EditorGUILayout.LabelField("Group", device.group.ToString());
                    if (!string.IsNullOrEmpty(device.firmwareVersion))
                        EditorGUILayout.LabelField("FW", device.firmwareVersion);
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUI.indentLevel--;
        }

        // ── Test controls section (EventMap-style) ────────────────────────

        private void DrawTestControls(HapbeatManager manager)
        {
            _showTestSection = EditorGUILayout.Foldout(_showTestSection, "Test", true);
            if (!_showTestSection) return;

            bool canTestRuntime = Application.isPlaying && manager.IsConnected;
            bool canTestEditor  = !Application.isPlaying && _editorConnected && _editorClient != null;
            bool canTest        = canTestRuntime || canTestEditor;

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            // --- Mode toggle ---
            int newMode = GUILayout.Toolbar(_testMode, new[] { "Command", "StreamClip" });
            if (newMode != _testMode)
            {
                _testMode = newMode;
                ResolveIntensity();
            }

            // --- Mode-specific source picker ---
            if (_testMode == 0)
                DrawCommandPicker();
            else
                DrawStreamClipPicker();

            // --- Targeting (EventMap-style) ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Targeting", EditorStyles.miniBoldLabel);
            DrawTargetingFields();

            // --- Gain ---
            EditorGUILayout.Space(4);
            DrawGainSlider();

            if (EditorGUI.EndChangeCheck())
                PersistSession();

            // --- Action buttons ---
            EditorGUILayout.Space(4);
            EditorGUI.BeginDisabledGroup(!canTest);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Play"))
            {
                if (canTestRuntime) RuntimePlay(manager);
                else                EditorSendPlay();
            }

            if (GUILayout.Button("Stop"))
            {
                if (canTestRuntime) RuntimeStop(manager);
                else                EditorSendStop();
            }

            if (GUILayout.Button("Stop All"))
            {
                if (canTestRuntime)
                {
                    // STOP_ALL only covers Command playback on device. Edit-mode's
                    // EditorSendStopAll also kills the active stream session — do
                    // the same here so "Stop All" really means everything-from-this-panel.
                    manager.StopAll(NullIfEmpty(_testTarget));
                    manager.StopStream();
                }
                else
                {
                    EditorSendStopAll();
                }
            }

            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Ping"))
            {
                if (canTestRuntime) manager.Ping();
                else                EditorPing();
            }

            EditorGUI.EndDisabledGroup();

            if (!canTest)
            {
                EditorGUILayout.HelpBox(
                    "Test requires a connection. Use \"Connect (Edit)\" above or enter Play mode.",
                    MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        // ── Command mode picker ───────────────────────────────────────────

        private void DrawCommandPicker()
        {
            // "From Kit" dropdown — scan HapbeatSDK/Kits/ for manifests' events bucket.
            var events = HapbeatManifestIntensity.LoadAllEvents()
                .Where(e => e.mode == "command")
                .ToList();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("From Kit",
                "Pick a Command event from a deployed kit manifest.\n" +
                "Selecting fills Event ID and shows the manifest intensity."));

            string buttonLabel;
            if (events.Count == 0)
            {
                buttonLabel = "(no kits — Deploy from Studio)";
            }
            else if (!string.IsNullOrEmpty(_testEventId))
            {
                // Display short form when the kit name is implied by the eventId prefix
                buttonLabel = _testEventId;
            }
            else
            {
                // Default state — no event selected. Match EventMap "(None)" convention.
                buttonLabel = "(None)";
            }

            if (GUILayout.Button(buttonLabel, EditorStyles.popup))
                ShowKitEventMenu(events);

            EditorGUILayout.EndHorizontal();

            // Manual override (still allow hand-typing for niche cases)
            string newId = EditorGUILayout.TextField(
                new GUIContent("Event ID",
                    "Direct event ID. Picker auto-fills this; you can also type it manually."),
                _testEventId ?? "");
            if (newId != _testEventId)
            {
                _testEventId = newId;
                ResolveIntensity();
            }
        }

        private void ShowKitEventMenu(List<HapbeatManifestIntensity.KitManifestEvent> events)
        {
            var menu = new GenericMenu();

            // Top: (None) to clear the selection back to default.
            menu.AddItem(new GUIContent("(None)"), string.IsNullOrEmpty(_testEventId), () =>
            {
                _testEventId = "";
                _testKitName = "";
                _resolvedIntensity = float.NaN;
                _resolvedKitPath = "";
                PersistSession();
                Repaint();
            });
            menu.AddSeparator("");

            // Group events under their kit name for readability.
            var byKit = events.GroupBy(e => e.kitName).OrderBy(g => g.Key);
            foreach (var grp in byKit)
            {
                foreach (var ev in grp.OrderBy(e => e.eventId))
                {
                    string menuPath = $"{grp.Key}/{ev.eventId}";
                    bool isCurrent = ev.eventId == _testEventId;
                    var captured = ev;
                    menu.AddItem(new GUIContent(menuPath), isCurrent, () =>
                    {
                        _testEventId = captured.eventId;
                        _testKitName = captured.kitName;
                        // _testGain は触らない (EventMap entry.gain と同じ semantics で
                        // 「authored intensity の倍率」)。送信時に intensity と掛け算するので
                        // gain = 1.0 のままで「manifest 通り」になる。
                        _resolvedIntensity = captured.intensity;
                        _resolvedKitPath = captured.kitDir;
                        PersistSession();
                        Repaint();
                    });
                }
            }
            if (events.Count == 0)
                menu.AddDisabledItem(new GUIContent("(no command-mode events in deployed kits)"));
            menu.ShowAsContext();
        }

        // ── StreamClip mode picker ─────────────────────────────────────────

        private void DrawStreamClipPicker()
        {
            EditorGUI.BeginChangeCheck();
            var newClip = (AudioClip)EditorGUILayout.ObjectField(
                new GUIContent("Stream Clip",
                    "Drag-drop an AudioClip (WAV / .audioclip asset). The clip will\n" +
                    "be streamed as PCM16 over UDP. If the clip lives inside a Kit\n" +
                    "folder (stream-clips/), the manifest intensity is used for gain."),
                _testStreamClip, typeof(AudioClip), allowSceneObjects: false);
            if (EditorGUI.EndChangeCheck())
            {
                _testStreamClip = newClip;
                ResolveIntensity();
                // _testGain は触らない (gain × intensity が送信値になる semantics を維持)。
            }

            _testLoop = EditorGUILayout.Toggle(
                new GUIContent("Loop",
                    "Loop the clip until Stop is pressed (or Stop All / disconnect)."),
                _testLoop);
        }

        // ── Targeting fields (same encoding as EventMap Window) ────────────

        private void DrawTargetingFields()
        {
            HapbeatTargetEditorUtil.ParseTarget(_testTarget ?? "",
                out string curPrefix, out int curPlayer, out string curPos, out int curGroup);

            string newPrefix = EditorGUILayout.TextField(
                new GUIContent("Prefix",
                    "Optional team prefix for large multi-team setups.\n" +
                    "Example: team_red, booth_a.\n" +
                    "Leave empty for most projects."),
                curPrefix);

            int newPlayer = EditorGUILayout.IntField(
                new GUIContent("Player",
                    "Player number (1-99). -1 = all players (wildcard).\n" +
                    "For broadcast to every device: Player = -1, Position = (none), Group = -1."),
                curPlayer);

            // Position popup — populated from HapbeatEventEntry.StandardPositions.
            var posOptions = new string[HapbeatEventEntry.StandardPositions.Length + 1];
            var posValues  = new string[posOptions.Length];
            posOptions[0] = "(none — all positions)";
            posValues[0]  = "";
            for (int p = 0; p < HapbeatEventEntry.StandardPositions.Length; p++)
            {
                posOptions[p + 1] = HapbeatEventEntry.PositionLabels[p];
                posValues[p + 1]  = HapbeatEventEntry.StandardPositions[p];
            }
            int posIdx = System.Array.IndexOf(posValues, curPos);
            if (posIdx < 0) posIdx = 0;
            int newPosIdx = EditorGUILayout.Popup(
                new GUIContent("Position",
                    "Body position of the target device.\n" +
                    "Select (none) to target all positions for the selected player."),
                posIdx, posOptions);
            string newPos = posValues[newPosIdx];

            int newGroup = EditorGUILayout.IntField(
                new GUIContent("Group",
                    "Group number (1-99). -1 = all groups (wildcard).\n" +
                    "Encoded as 'group_<N>' segment after position."),
                curGroup);

            string built = HapbeatTargetEditorUtil.BuildTargetFromParts(
                newPrefix, newPlayer, newPos, newGroup);
            if (built != _testTarget)
                _testTarget = built;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" → target"),
                string.IsNullOrEmpty(_testTarget) ? "(broadcast — all devices)" : _testTarget);
            EditorGUI.EndDisabledGroup();
        }

        // ── Gain slider — EventMap entry.gain と同じ semantics ──────────────
        // _testGain は「authored intensity の倍率」(EventMap.entry.gain と同義)。
        // 送信値 = _testGain × manifest.intensity (resolved 時) または _testGain (未解決時)。
        // 例) manifest.intensity = 0.5 で _testGain = 1.0 → 送信 0.5。
        //     gain を 2.0 にすれば boost (送信 1.0)。

        private void DrawGainSlider()
        {
            float effective = ComputeEffectiveGain();
            string suffix;
            if (!float.IsNaN(_resolvedIntensity))
            {
                string kitLabel = string.IsNullOrEmpty(_resolvedKitPath)
                    ? ""
                    : $", {System.IO.Path.GetFileName(_resolvedKitPath)}";
                // gain × intensity の式と結果を見せる。EventMap と同じ思想。
                suffix = $"  ({_testGain:F2} × intensity {_resolvedIntensity:F2}{kitLabel} = {effective:F2} sent)";
            }
            else if ((_testMode == 0 && !string.IsNullOrEmpty(_testEventId)) ||
                     (_testMode == 1 && _testStreamClip != null))
            {
                // No manifest match — send raw _testGain.
                suffix = $"  (no manifest match → sending {effective:F2})";
            }
            else
            {
                suffix = "";
            }

            _testGain = EditorGUILayout.Slider(
                new GUIContent("Gain" + suffix,
                    "Multiplier on the authored intensity (same as EventMap entry.gain).\n" +
                    "1.0 = use manifest intensity as-is. 0.5 = half. 2.0 = boost.\n" +
                    "Sent value = Gain × manifest.intensity (or just Gain if no manifest match)."),
                _testGain, 0f, 2f);
        }

        /// <summary>
        /// _testGain (entry.gain 相当) × manifest.intensity (resolved 時)。
        /// EventMap の <c>entry.GetEffectiveGain()</c> と同じ計算。
        /// </summary>
        private float ComputeEffectiveGain()
        {
            float intensity = float.IsNaN(_resolvedIntensity) ? 1.0f : _resolvedIntensity;
            return _testGain * intensity;
        }

        // ── Manifest intensity resolution ──────────────────────────────────

        /// <summary>
        /// Recompute the manifest intensity hint for the current selection.
        /// Updates <c>_resolvedIntensity</c> (NaN if no match) and
        /// <c>_resolvedKitPath</c>. Does NOT touch <c>_testGain</c> — gain is
        /// only overwritten at explicit pick events to respect user intent.
        /// </summary>
        private void ResolveIntensity()
        {
            _resolvedIntensity = float.NaN;
            _resolvedKitPath = "";

            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId)) return;
                foreach (var ev in HapbeatManifestIntensity.LoadAllEvents())
                {
                    if (ev.mode != "command") continue;
                    if (ev.eventId != _testEventId) continue;
                    _resolvedIntensity = ev.intensity;
                    _resolvedKitPath = ev.kitDir;
                    return;
                }
            }
            else
            {
                if (_testStreamClip == null) return;
                string clipPath = AssetDatabase.GetAssetPath(_testStreamClip)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(clipPath)) return;
                foreach (var ev in HapbeatManifestIntensity.LoadAllEvents())
                {
                    if (ev.mode != "stream_clip") continue;
                    string expected = $"{ev.kitDir}/{ev.clipRelPath}".Replace('\\', '/');
                    if (clipPath == expected)
                    {
                        _resolvedIntensity = ev.intensity;
                        _resolvedKitPath = ev.kitDir;
                        return;
                    }
                }
            }
        }

        // ── Test play dispatchers (Runtime / Edit-mode parity) ─────────────

        private void RuntimePlay(HapbeatManager manager)
        {
            string target = NullIfEmpty(_testTarget);
            float effective = ComputeEffectiveGain();
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                {
                    Debug.LogWarning("[Hapbeat] Test play: Event ID is empty.");
                    return;
                }
                manager.Play(_testEventId, effective, target: target);
            }
            else
            {
                if (_testStreamClip == null)
                {
                    Debug.LogWarning("[Hapbeat] Test play: Stream Clip is empty.");
                    return;
                }
                manager.StreamAudioClip(_testStreamClip, effective, target, _testLoop);
            }
        }

        private void RuntimeStop(HapbeatManager manager)
        {
            string target = NullIfEmpty(_testTarget);
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                    manager.StopAll(target);
                else
                    manager.Stop(_testEventId, target: target);
            }
            else
            {
                manager.StopStream();
            }
        }

        private void EditorSendPlay()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            string target = NullIfEmpty(_testTarget);
            float effective = ComputeEffectiveGain();
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                {
                    Debug.LogWarning("[Hapbeat Edit] Test play: Event ID is empty.");
                    return;
                }
                HapbeatEditorTransport.Play(_testEventId, effective, target);
                Debug.Log($"[Hapbeat Edit] Play: eventId={_testEventId} gain={_testGain:F2} × intensity={(float.IsNaN(_resolvedIntensity) ? 1.0f : _resolvedIntensity):F2} = {effective:F2} target={target ?? "(broadcast)"}");
            }
            else
            {
                if (_testStreamClip == null)
                {
                    Debug.LogWarning("[Hapbeat Edit] Test play: Stream Clip is empty.");
                    return;
                }
                HapbeatEditorTransport.StartStream(_testStreamClip, effective, target, _testLoop);
                Debug.Log($"[Hapbeat Edit] StartStream: clip={_testStreamClip.name} gain={_testGain:F2} × intensity={(float.IsNaN(_resolvedIntensity) ? 1.0f : _resolvedIntensity):F2} = {effective:F2} loop={_testLoop} target={target ?? "(broadcast)"}");
            }
        }

        private void EditorSendStop()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            string target = NullIfEmpty(_testTarget);
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                    HapbeatEditorTransport.StopAll(target);
                else
                    HapbeatEditorTransport.Stop(_testEventId, target);
                Debug.Log($"[Hapbeat Edit] Stop: eventId={_testEventId ?? "(all)"} target={target ?? "(broadcast)"}");
            }
            else
            {
                HapbeatEditorTransport.StopStream();
                Debug.Log("[Hapbeat Edit] StopStream");
            }
        }

        private void EditorSendStopAll()
        {
            // Stop deferred sources too: they have no active endpoint session, so
            // HapbeatEditorTransport.IsStreaming is false even though they can join
            // on a later PONG.
            HapbeatEditorTransport.StopStream();

            if (_editorClient == null || !_editorClient.IsConnected) return;
            string target = NullIfEmpty(_testTarget);
            HapbeatEditorTransport.StopAll(target);
            Debug.Log($"[Hapbeat Edit] StopAll: target={target ?? "(broadcast)"}");
        }

        // ── SessionState persistence ──────────────────────────────────────

        private void PersistSession()
        {
            SessionState.SetInt(PREF_MODE, _testMode);
            SessionState.SetString(PREF_EVENT_ID, _testEventId ?? "");
            SessionState.SetString(PREF_KIT_NAME, _testKitName ?? "");
            string clipGuid = "";
            if (_testStreamClip != null)
            {
                string path = AssetDatabase.GetAssetPath(_testStreamClip);
                if (!string.IsNullOrEmpty(path)) clipGuid = AssetDatabase.AssetPathToGUID(path);
            }
            SessionState.SetString(PREF_STREAM_GUID, clipGuid);
            SessionState.SetBool(PREF_LOOP, _testLoop);
            SessionState.SetFloat(PREF_GAIN, _testGain);
            SessionState.SetString(PREF_TARGET, _testTarget ?? "");
        }

        private static string NullIfEmpty(string s)
            => string.IsNullOrEmpty(s) ? null : s;

        // ── Edit-mode client lifecycle (unchanged from previous version) ──

        #region Edit-Mode Operations

        private void EditorConnect(HapbeatManager manager)
        {
            if (_editorClient != null)
                _editorClient.Dispose();

            _editorClient = new HapbeatClient();
            _editorClient.OnPong += (rttUs, serverTimeUs) =>
            {
                _editorPingResult = $"Ping ok: RTT = {rttUs} μs ({rttUs / 1000.0:F1} ms)";
                Repaint();
            };
            _editorClient.OnConnectionStateChanged += (connected) =>
            {
                _editorConnected = connected;
                Repaint();
            };

            // Get port from the serialized config
            var configProp = serializedObject.FindProperty("_config");
            int port = 7700;
            if (configProp.objectReferenceValue is HapbeatConfig config)
                port = config.port;

            _editorClient.OpenBroadcast(port);
            _editorPingResult = "";

            EditorApplication.update -= EditorUpdate;
            EditorApplication.update += EditorUpdate;
        }

        private void EditorDisconnect()
        {
            _editorClient?.Dispose();
            _editorClient = null;
            _editorConnected = false;
            _editorPingResult = "";
            EditorApplication.update -= EditorUpdate;
            Repaint();
        }

        private void EditorPing()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            _editorClient.SendPing();
            _editorPingResult = "Pinging...";
        }

        private void EditorDiscover(HapbeatManager manager)
        {
            if (_editorDiscovery == null)
            {
                _editorDiscovery = new HapbeatDiscovery();
                _editorDiscovery.OnDeviceFound += (device) =>
                {
                    _editorDevices.Add(device);
                    Debug.Log($"[Hapbeat Edit] Device found: {device.name} at {device.ipAddress} (group={device.group}, fw={device.firmwareVersion})");
                    Repaint();
                };
                _editorDiscovery.OnDiscoveryComplete += (devices) =>
                {
                    Debug.Log($"[Hapbeat Edit] Discovery complete: {devices.Count} device(s)");
                    Repaint();
                };
            }

            _editorDevices.Clear();

            var configProp = serializedObject.FindProperty("_config");
            int port = 7700;
            if (configProp.objectReferenceValue is HapbeatConfig config)
                port = config.port;

            _editorDiscovery.Discover(3000, port);

            EditorApplication.update -= EditorUpdate;
            EditorApplication.update += EditorUpdate;
        }

        private static void EditorUpdate()
        {
            _editorDiscovery?.DispatchCallbacks();
            _editorClient?.DispatchMainThreadCallbacks();
        }

        #endregion

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }
    }
}
#endif
