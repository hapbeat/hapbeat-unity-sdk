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
        private const string PREF_MODE         = "Hapbeat_TestMode";          // 0=Command, 1=StreamClip
        private const string PREF_EVENT_ID     = "Hapbeat_TestEventId";
        private const string PREF_KIT_NAME     = "Hapbeat_TestKitName";
        private const string PREF_STREAM_GUID  = "Hapbeat_TestStreamClipGuid";
        private const string PREF_LOOP         = "Hapbeat_TestLoop";
        private const string PREF_GAIN         = "Hapbeat_TestGain";
        private const string PREF_TARGET       = "Hapbeat_TestTarget";

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
            _testGain      = SessionState.GetFloat(PREF_GAIN, 0.5f);
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

        // ── 接続状態セクション ────────────────────────────────────────────────

        private void DrawConnectionStatus(HapbeatManager manager)
        {
            EditorGUILayout.LabelField("接続状態", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            bool isConnected = isPlaying ? manager.IsConnected : _editorConnected;

            // Status indicator
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("ステータス");

            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = isConnected ? Color.green : Color.red;

            string statusText;
            if (isPlaying && isConnected)
            {
                string mode = manager.IsBroadcast ? "broadcast" : "unicast";
                statusText = $"送信可 ({mode})";
            }
            else if (!isPlaying && isConnected)
            {
                statusText = "Edit モード接続中";
            }
            else
            {
                statusText = "未接続";
            }
            GUILayout.Button(statusText, GUILayout.Width(160));
            GUI.backgroundColor = originalColor;

            EditorGUILayout.EndHorizontal();

            if (isPlaying && isConnected)
            {
                EditorGUILayout.LabelField("送信モード", manager.IsBroadcast ? "ブロードキャスト" : "ユニキャスト (Bridge)");
                EditorGUILayout.LabelField("デフォルトグループ", manager.DefaultGroup.ToString());
                EditorGUILayout.LabelField("時刻オフセット", $"{manager.TimeOffsetUs} μs");
            }

            if (isPlaying)
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginDisabledGroup(isConnected);
                if (GUILayout.Button("接続"))
                    manager.Connect();
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!isConnected);
                if (GUILayout.Button("切断"))
                    manager.Disconnect();
                EditorGUI.EndDisabledGroup();

                if (GUILayout.Button("検出"))
                    manager.Discover();

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();

                if (GUILayout.Button(_editorConnected ? "切断" : "接続 (Edit)"))
                {
                    if (_editorConnected)
                        EditorDisconnect();
                    else
                        EditorConnect(manager);
                }

                if (GUILayout.Button("検出 (Edit)"))
                    EditorDiscover(manager);

                if (_editorConnected && GUILayout.Button("Ping"))
                    EditorPing();

                EditorGUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_editorPingResult))
                    EditorGUILayout.HelpBox(_editorPingResult, MessageType.None);
            }
        }

        // ── デバイス一覧セクション ─────────────────────────────────────────

        private void DrawDeviceList(HapbeatManager manager)
        {
            _showDeviceSection = EditorGUILayout.Foldout(_showDeviceSection, "検出デバイス", true);
            if (!_showDeviceSection) return;

            EditorGUI.indentLevel++;

            IReadOnlyList<HapbeatDevice> devices = Application.isPlaying
                ? manager.DiscoveredDevices
                : (IReadOnlyList<HapbeatDevice>)_editorDevices.AsReadOnly();

            if (devices.Count == 0)
            {
                EditorGUILayout.HelpBox("デバイス未検出。「検出」ボタンを押してください。", MessageType.Info);
            }
            else
            {
                foreach (var device in devices)
                {
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField(device.name, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField("IP", device.ipAddress);
                    EditorGUILayout.LabelField("グループ", device.group.ToString());
                    if (!string.IsNullOrEmpty(device.firmwareVersion))
                        EditorGUILayout.LabelField("FW", device.firmwareVersion);
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUI.indentLevel--;
        }

        // ── テスト操作セクション (EventMap-style) ─────────────────────────

        private void DrawTestControls(HapbeatManager manager)
        {
            _showTestSection = EditorGUILayout.Foldout(_showTestSection, "テスト操作", true);
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
                    "テスト操作には接続が必要です。上部の「接続 (Edit)」または プレイモードで接続してください。",
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
                "Kit manifest の events bucket から Command イベントを選択。\n" +
                "選択すると Event ID と Gain (manifest intensity) が自動で埋まる。"));

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
                buttonLabel = "(pick event...)";
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
                        // Auto-derive gain from manifest intensity so the test play
                        // matches authored intent without manual slider adjustment.
                        _testGain = Mathf.Clamp(captured.intensity, 0f, 2f);
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
                // Auto-pull gain from manifest intensity for consistency with EventMap test play.
                if (!float.IsNaN(_resolvedIntensity))
                    _testGain = Mathf.Clamp(_resolvedIntensity, 0f, 2f);
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

        // ── Gain slider with manifest-intensity hint ───────────────────────

        private void DrawGainSlider()
        {
            string suffix = "";
            if (!float.IsNaN(_resolvedIntensity))
            {
                string kitLabel = string.IsNullOrEmpty(_resolvedKitPath)
                    ? ""
                    : $" — {System.IO.Path.GetFileName(_resolvedKitPath)}";
                suffix = $"  (manifest intensity = {_resolvedIntensity:F2}{kitLabel})";
            }
            else if (_testMode == 0 && !string.IsNullOrEmpty(_testEventId))
                suffix = "  (no manifest match — set gain manually)";
            else if (_testMode == 1 && _testStreamClip != null)
                suffix = "  (clip not in a Kit — set gain manually)";

            _testGain = EditorGUILayout.Slider(
                new GUIContent("Gain" + suffix,
                    "再生 gain。EventMap 同様、Kit から選択した直後は manifest intensity が自動で\n" +
                    "セットされる (上書き可)。"),
                _testGain, 0f, 2f);
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
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                {
                    Debug.LogWarning("[Hapbeat] Test play: Event ID is empty.");
                    return;
                }
                manager.Play(_testEventId, _testGain, target: target);
            }
            else
            {
                if (_testStreamClip == null)
                {
                    Debug.LogWarning("[Hapbeat] Test play: Stream Clip is empty.");
                    return;
                }
                manager.StreamAudioClip(_testStreamClip, _testGain, target, _testLoop);
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
            if (_testMode == 0)
            {
                if (string.IsNullOrEmpty(_testEventId))
                {
                    Debug.LogWarning("[Hapbeat Edit] Test play: Event ID is empty.");
                    return;
                }
                HapbeatEditorTransport.Play(_testEventId, _testGain, target);
                Debug.Log($"[Hapbeat Edit] Play: eventId={_testEventId} gain={_testGain:F2} target={target ?? "(broadcast)"}");
            }
            else
            {
                if (_testStreamClip == null)
                {
                    Debug.LogWarning("[Hapbeat Edit] Test play: Stream Clip is empty.");
                    return;
                }
                HapbeatEditorTransport.StartStream(_testStreamClip, _testGain, target, _testLoop);
                Debug.Log($"[Hapbeat Edit] StartStream: clip={_testStreamClip.name} gain={_testGain:F2} loop={_testLoop} target={target ?? "(broadcast)"}");
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
            if (_editorClient == null || !_editorClient.IsConnected) return;
            string target = NullIfEmpty(_testTarget);
            HapbeatEditorTransport.StopAll(target);
            // Stream session も同時に閉じる (mixer は StopAll を見ないので個別に)
            if (HapbeatEditorTransport.IsStreaming)
                HapbeatEditorTransport.StopStream();
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
                _editorPingResult = $"Ping 成功: RTT = {rttUs} μs ({rttUs / 1000.0:F1} ms)";
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
            _editorPingResult = "Ping 送信中...";
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
