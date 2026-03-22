#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatManager.
    /// Shows connection status and provides quick testing controls.
    /// </summary>
    [CustomEditor(typeof(HapbeatManager))]
    public class HapbeatManagerEditor : UnityEditor.Editor
    {
        private string _testEventId = "impact.hit";
        private float _testGain = 1.0f;
        private byte _testGroup = 0;
        private bool _showTestSection = true;

        public override void OnInspectorGUI()
        {
            // Draw the default inspector
            DrawDefaultInspector();

            HapbeatManager manager = (HapbeatManager)target;

            EditorGUILayout.Space(10);
            DrawConnectionStatus(manager);
            EditorGUILayout.Space(10);
            DrawTestControls(manager);
        }

        private void DrawConnectionStatus(HapbeatManager manager)
        {
            EditorGUILayout.LabelField("接続状態", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            bool isConnected = isPlaying && manager.IsConnected;

            // Connection status indicator
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("ステータス");

            Color originalColor = GUI.backgroundColor;
            GUI.backgroundColor = isConnected ? Color.green : Color.red;
            string statusText = isConnected ? "接続中" : "未接続";
            GUILayout.Button(statusText, GUILayout.Width(80));
            GUI.backgroundColor = originalColor;

            EditorGUILayout.EndHorizontal();

            if (isPlaying && isConnected)
            {
                EditorGUILayout.LabelField(
                    "時刻オフセット",
                    $"{manager.BridgeTimeOffsetUs} μs");
            }

            // Connect / Disconnect buttons
            if (isPlaying)
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginDisabledGroup(isConnected);
                if (GUILayout.Button("接続"))
                {
                    manager.Connect();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUI.BeginDisabledGroup(!isConnected);
                if (GUILayout.Button("切断"))
                {
                    manager.Disconnect();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "プレイモードで実行すると接続操作が可能です。",
                    MessageType.Info);
            }
        }

        private void DrawTestControls(HapbeatManager manager)
        {
            _showTestSection = EditorGUILayout.Foldout(_showTestSection, "テスト操作", true);

            if (!_showTestSection)
                return;

            bool canTest = Application.isPlaying && manager.IsConnected;

            EditorGUI.indentLevel++;

            _testEventId = EditorGUILayout.TextField(
                new GUIContent("Event ID", "テストするイベント ID"),
                _testEventId);

            _testGain = EditorGUILayout.Slider(
                new GUIContent("ゲイン", "再生ゲイン"),
                _testGain, 0f, 2f);

            _testGroup = (byte)EditorGUILayout.IntSlider(
                new GUIContent("グループ", "ターゲットグループ ID"),
                _testGroup, 0, 254);

            EditorGUI.BeginDisabledGroup(!canTest);

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Play"))
            {
                manager.Play(_testEventId, _testGain, _testGroup);
            }

            if (GUILayout.Button("Stop"))
            {
                manager.Stop(_testEventId, _testGroup);
            }

            if (GUILayout.Button("Stop All"))
            {
                manager.StopAll(_testGroup);
            }

            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Ping"))
            {
                manager.Ping();
            }

            EditorGUI.EndDisabledGroup();

            if (!canTest)
            {
                string reason = !Application.isPlaying
                    ? "プレイモードで実行してください。"
                    : "Bridge に接続してください。";
                EditorGUILayout.HelpBox(reason, MessageType.Info);
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Force repaint during play mode to keep status updated.
        /// </summary>
        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }
    }
}
#endif
