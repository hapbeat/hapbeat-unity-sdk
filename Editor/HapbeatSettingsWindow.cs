#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor window for configuring Hapbeat SDK connection settings.
    /// Accessible via Window > Hapbeat > Settings.
    /// </summary>
    public class HapbeatSettingsWindow : EditorWindow
    {
        private HapbeatConfig _config;
        private SerializedObject _serializedConfig;
        private string _pingResult = "";
        private bool _isPinging;
        private Vector2 _scrollPosition;
        private bool _bridgeFoldout;

        [MenuItem("Hapbeat/Open Settings", false, 12)]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatSettingsWindow>("Hapbeat Settings");
            window.minSize = new Vector2(350, 400);
        }

        private void OnEnable()
        {
            FindOrCreateConfig();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(10);
            DrawConfigSection();
            EditorGUILayout.Space(10);
            DrawConnectionSection();
            EditorGUILayout.Space(10);
            DrawTestSection();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Hapbeat SDK Settings", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Configure the Hapbeat device connection.\n" +
                "Default: Wi-Fi UDP with device auto-discovery.",
                MessageType.Info);
        }

        private void DrawConfigSection()
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);

            if (_config == null)
            {
                EditorGUILayout.HelpBox(
                    "HapbeatConfig asset not found. Create one to continue.",
                    MessageType.Warning);

                if (GUILayout.Button("Create HapbeatConfig"))
                {
                    CreateConfigAsset();
                }
                return;
            }

            if (_serializedConfig == null || _serializedConfig.targetObject == null)
            {
                _serializedConfig = new SerializedObject(_config);
            }

            _serializedConfig.Update();

            // ── Network ──────────────────────────────────────────────
            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("port"),
                new GUIContent("UDP Port", "Port for UDP packets."));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("group"),
                new GUIContent("Group ID (display only)",
                    "Shown in the device's CONNECT_STATUS (OLED display) only. Does NOT " +
                    "affect Play/Stop/Stream routing — see Addressing below for that."));

            // ── Addressing ────────────────────────────────────────────
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Addressing", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "-1 = disabled (the EventMap entry's target string is sent as-is).\n" +
                "1-99 = force every outgoing command to that player / group, " +
                "regardless of what each EventMap entry's target says. Use this to " +
                "deploy one identical build to many HMDs, each bound to its own Hapbeat " +
                "(HapbeatManager.SetAddressOverride can also change this at runtime).",
                MessageType.None);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("overridePlayer"),
                new GUIContent("Override Player", "Forced player number applied to every outgoing command. -1 = disabled."));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("overrideGroup"),
                new GUIContent("Override Group", "Forced group number applied to every outgoing command. -1 = disabled."));

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("App Info", EditorStyles.boldLabel);

            var appNameProp = _serializedConfig.FindProperty("appName");
            EditorGUILayout.PropertyField(appNameProp,
                new GUIContent("App Name",
                    "Shown on the Hapbeat device display. " +
                    $"Max {HapbeatConfig.MaxAppNameLength} chars; the default app_name " +
                    "element (8x1) shows the first 8. Empty = use Application.productName."));
            // Char-count indicator (right-aligned, mini style).
            int len = appNameProp.stringValue?.Length ?? 0;
            EditorGUILayout.LabelField(
                $"   {len} / {HapbeatConfig.MaxAppNameLength} chars" +
                (len == 0 ? "  (empty: uses Application.productName)" : ""),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Discovery", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("discoveryTimeoutMs"),
                new GUIContent("Discovery Timeout (ms)", "Device discovery timeout."));

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Behavior", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("pingInterval"),
                new GUIContent("Ping Interval (s)", "Keep-alive ping interval."));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("streamSendAheadSeconds"),
                new GUIContent(
                    "Stream Buffer (s)",
                    "Host-side send-ahead buffer for StreamClip.\n" +
                    "Short: faster Stop response, more jitter risk.\n" +
                    "Long: stable, small tail after Stop.\n" +
                    "Range 10–200 ms, default 50 ms."));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("enableLogging"),
                new GUIContent("Verbose Log", "Print verbose log to the console."));

            // ── Latency compensation ─────────────────────────────────
            // Global offset to align haptic with the (often slower) audio output
            // path (Bluetooth, etc.). Per-entry fine tuning lives on each
            // EventMap entry's Delay Offset.
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Latency Compensation", EditorStyles.boldLabel);

            // Stored in seconds on the asset (consistent with delayOffsetSeconds /
            // streamSendAheadSeconds / the wire protocol), but shown / edited in
            // milliseconds here since that's the unit users think in for latency.
            // Convert ms <-> s at the UI boundary only; no data migration.
            var delayProp = _serializedConfig.FindProperty("hapticDelaySeconds");
            EditorGUI.BeginChangeCheck();
            float delayMs = Mathf.Round(delayProp.floatValue * 1000f);
            delayMs = EditorGUILayout.Slider(
                new GUIContent(
                    "Haptic Delay (ms)",
                    "Global delay applied to every Play / StreamClip to align with the audio\n" +
                    "output device. Hapbeat is UDP-direct (~10 ms), so when speakers /\n" +
                    "headphones are slower the haptic arrives first — this offset compensates.\n\n" +
                    "Typical values:\n" +
                    "  Wired / USB DAC      → 0\n" +
                    "  Built-in speakers    → 20–50\n" +
                    "  Bluetooth (aptX LL)  → 30–50\n" +
                    "  Bluetooth (SBC/AAC)  → 150–200\n\n" +
                    "Per-entry Delay Offset can add ±200 ms on top."),
                delayMs, 0f, 500f);
            if (EditorGUI.EndChangeCheck())
                delayProp.floatValue = delayMs / 1000f;

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField("Config Asset", _config, typeof(HapbeatConfig), false);
            EditorGUILayout.EndHorizontal();

            // ── Advanced: Bridge (ESP-NOW) ───────────────────────────
            // Most users won't touch ESP-NOW — keep it folded by default at the
            // bottom of the window so the common settings stay above the fold.
            EditorGUILayout.Space(10);
            _bridgeFoldout = EditorGUILayout.Foldout(_bridgeFoldout,
                "Advanced: Bridge (ESP-NOW)", toggleOnLabelClick: true);
            if (_bridgeFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox(
                        "For ESP-NOW multicast to many devices. Skip this when plain\n" +
                        "Wi-Fi UDP is enough.",
                        MessageType.None);
                    EditorGUILayout.PropertyField(
                        _serializedConfig.FindProperty("useBridge"),
                        new GUIContent("Use Bridge", "Enable for ESP-NOW multi-device send."));

                    EditorGUI.BeginDisabledGroup(!_config.useBridge);
                    EditorGUILayout.PropertyField(
                        _serializedConfig.FindProperty("bridgeHost"),
                        new GUIContent("Bridge Host", "Bridge hostname or IP address."));
                    EditorGUI.EndDisabledGroup();
                }
            }

            _serializedConfig.ApplyModifiedProperties();
        }

        private void DrawConnectionSection()
        {
            EditorGUILayout.LabelField("Connection Status", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            bool isConnected = isPlaying && HapbeatManager.Instance != null &&
                               HapbeatManager.Instance.IsConnected;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Toggle("Play Mode", isPlaying);
            EditorGUILayout.Toggle("Connected", isConnected);
            EditorGUI.EndDisabledGroup();

            if (!isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Enter Play mode to test the connection.",
                    MessageType.Info);
            }
        }

        private void DrawTestSection()
        {
            EditorGUILayout.LabelField("Test", EditorStyles.boldLabel);

            bool canTest = Application.isPlaying && HapbeatManager.Instance != null;

            EditorGUI.BeginDisabledGroup(!canTest || _isPinging);

            if (GUILayout.Button("Ping"))
            {
                PerformPingTest();
            }

            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_pingResult))
            {
                EditorGUILayout.HelpBox(_pingResult, MessageType.None);
            }
        }

        private void PerformPingTest()
        {
            if (HapbeatManager.Instance == null)
                return;

            _isPinging = true;
            _pingResult = "Pinging...";

            if (!HapbeatManager.Instance.IsConnected)
            {
                HapbeatManager.Instance.Connect();
            }

            HapbeatManager.Instance.OnPong += OnPingResponse;
            HapbeatManager.Instance.Ping();

            // Timeout after 3 seconds
            EditorApplication.delayCall += () =>
            {
                if (_isPinging)
                {
                    _isPinging = false;
                    _pingResult = "Ping timeout — no response from device.";
                    HapbeatManager.Instance.OnPong -= OnPingResponse;
                    Repaint();
                }
            };
        }

        private void OnPingResponse(long rttUs)
        {
            _isPinging = false;
            _pingResult = $"Ping OK: RTT = {rttUs} μs ({rttUs / 1000.0:F1} ms)";

            if (HapbeatManager.Instance != null)
            {
                HapbeatManager.Instance.OnPong -= OnPingResponse;
            }

            Repaint();
        }

        private void FindOrCreateConfig()
        {
            // Search for existing config asset
            string[] guids = AssetDatabase.FindAssets("t:HapbeatConfig");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _config = AssetDatabase.LoadAssetAtPath<HapbeatConfig>(path);
            }

            if (_config != null)
            {
                _serializedConfig = new SerializedObject(_config);
            }
        }

        private void CreateConfigAsset()
        {
            _config = ScriptableObject.CreateInstance<HapbeatConfig>();

            // Ensure the Resources directory exists
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            string path = "Assets/Resources/HapbeatConfig.asset";
            AssetDatabase.CreateAsset(_config, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            _serializedConfig = new SerializedObject(_config);
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = _config;

            Debug.Log($"[Hapbeat] Created HapbeatConfig at {path}");
        }
    }
}
#endif
