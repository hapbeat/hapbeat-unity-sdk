#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatManager.
    /// Shows connection status, discovered devices, and provides testing controls.
    /// Supports Edit-mode discovery and ping via standalone HapbeatClient.
    /// </summary>
    [CustomEditor(typeof(HapbeatManager))]
    public class HapbeatManagerEditor : UnityEditor.Editor
    {
        private const string PREF_EVENT_ID = "Hapbeat_TestEventId";
        private const string PREF_GAIN = "Hapbeat_TestGain";
        private const string PREF_TARGET = "Hapbeat_TestTarget";

        private string _testEventId;
        private float _testGain;
        private string _testTarget;
        private bool _showTestSection = true;
        private bool _showDeviceSection = true;

        // Edit-mode standalone client for discovery/ping
        private static HapbeatClient _editorClient;
        private static HapbeatDiscovery _editorDiscovery;
        private static List<HapbeatDevice> _editorDevices = new List<HapbeatDevice>();
        private static bool _editorConnected;
        private static string _editorPingResult = "";

        private void OnEnable()
        {
            _testEventId = SessionState.GetString(PREF_EVENT_ID, "weapon.gunshot");
            _testGain = SessionState.GetFloat(PREF_GAIN, 0.3f);
            _testTarget = SessionState.GetString(PREF_TARGET, "");
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

            // Connect / Disconnect / Discovery buttons
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
                // Edit-mode controls
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

        private void DrawDeviceList(HapbeatManager manager)
        {
            _showDeviceSection = EditorGUILayout.Foldout(_showDeviceSection, "検出デバイス", true);
            if (!_showDeviceSection) return;

            EditorGUI.indentLevel++;

            IReadOnlyList<HapbeatDevice> devices;
            if (Application.isPlaying)
                devices = manager.DiscoveredDevices;
            else
                devices = _editorDevices.AsReadOnly();

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

        private void DrawTestControls(HapbeatManager manager)
        {
            _showTestSection = EditorGUILayout.Foldout(_showTestSection, "テスト操作", true);
            if (!_showTestSection) return;

            bool canTestRuntime = Application.isPlaying && manager.IsConnected;
            bool canTestEditor = !Application.isPlaying && _editorConnected && _editorClient != null;
            bool canTest = canTestRuntime || canTestEditor;

            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();

            _testEventId = EditorGUILayout.TextField(
                new GUIContent("Event ID", "テストするイベント ID"),
                _testEventId);

            _testGain = EditorGUILayout.Slider(
                new GUIContent("ゲイン", "再生ゲイン"),
                _testGain, 0f, 2f);

            _testTarget = EditorGUILayout.TextField(
                new GUIContent("ターゲット", "device-addressing target string. 空 = ブロードキャスト。\n例: player_1, */pos_neck, player_1/pos_chest"),
                _testTarget ?? "");

            if (EditorGUI.EndChangeCheck())
            {
                SessionState.SetString(PREF_EVENT_ID, _testEventId);
                SessionState.SetFloat(PREF_GAIN, _testGain);
                SessionState.SetString(PREF_TARGET, _testTarget ?? "");
            }

            EditorGUI.BeginDisabledGroup(!canTest);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Play"))
            {
                if (canTestRuntime)
                    manager.Play(_testEventId, _testGain, _testGroup);
                else
                    EditorSendPlay();
            }

            if (GUILayout.Button("Stop"))
            {
                if (canTestRuntime)
                    manager.Stop(_testEventId, _testGroup);
                else
                    EditorSendStop();
            }

            if (GUILayout.Button("Stop All"))
            {
                if (canTestRuntime)
                    manager.StopAll(_testGroup);
                else
                    EditorSendStopAll();
            }

            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Ping"))
            {
                if (canTestRuntime)
                    manager.Ping();
                else
                    EditorPing();
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

            // Start editor update loop
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

        private void EditorSendPlay()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            _editorClient.SendPlay(_testEventId, 0, _testGain, _testTarget);
            Debug.Log($"[Hapbeat Edit] Play: eventId={_testEventId}, gain={_testGain}, target={_testTarget ?? "(broadcast)"}");
        }

        private void EditorSendStop()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            _editorClient.SendStop(_testEventId, _testTarget);
            Debug.Log($"[Hapbeat Edit] Stop: eventId={_testEventId}, target={_testTarget ?? "(broadcast)"}");
        }

        private void EditorSendStopAll()
        {
            if (_editorClient == null || !_editorClient.IsConnected) return;
            _editorClient.SendStopAll(_testTarget);
            Debug.Log($"[Hapbeat Edit] StopAll: target={_testTarget ?? "(broadcast)"}");
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

            // Ensure callbacks are dispatched
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
