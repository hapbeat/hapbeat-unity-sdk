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

        [MenuItem("Hapbeat/Settings", false, 11)]
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
            EditorGUILayout.LabelField("Hapbeat SDK 設定", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Hapbeat デバイスへの接続設定を行います。\n" +
                "標準は Wi-Fi UDP でデバイスを自動検出して接続します。",
                MessageType.Info);
        }

        private void DrawConfigSection()
        {
            EditorGUILayout.LabelField("接続設定", EditorStyles.boldLabel);

            if (_config == null)
            {
                EditorGUILayout.HelpBox(
                    "HapbeatConfig アセットが見つかりません。新規作成してください。",
                    MessageType.Warning);

                if (GUILayout.Button("HapbeatConfig を作成"))
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

            // ── 基本設定 ──────────────────────────────────────────────
            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("port"),
                new GUIContent("UDP ポート", "通信用 UDP ポート番号"));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("group"),
                new GUIContent("グループ ID", "送信先グループ。0 = 全デバイス、1-254 = 特定グループ（マルチプレイヤー時）"));

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("アプリ情報", EditorStyles.boldLabel);

            var appNameProp = _serializedConfig.FindProperty("appName");
            EditorGUILayout.PropertyField(appNameProp,
                new GUIContent("アプリ名",
                    "Hapbeat デバイスのディスプレイに表示されるクライアントアプリ名。\n" +
                    $"Max {HapbeatConfig.MaxAppNameLength} 文字 (display grid 幅)。デフォルトの app_name 要素 (8x1) では先頭 8 文字のみ表示。\n" +
                    "空欄の場合は Application.productName が自動使用される。"));
            // Char-count indicator (right-aligned, mini style).
            int len = appNameProp.stringValue?.Length ?? 0;
            EditorGUILayout.LabelField(
                $"   {len} / {HapbeatConfig.MaxAppNameLength} 文字" +
                (len == 0 ? "  (空欄: Application.productName を使用)" : ""),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("検出設定", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("discoveryTimeoutMs"),
                new GUIContent("検出タイムアウト (ms)", "デバイス検出のタイムアウト"));

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("動作設定", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("pingInterval"),
                new GUIContent("Ping 間隔 (秒)", "キープアライブ Ping の送信間隔"));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("streamSendAheadSeconds"),
                new GUIContent(
                    "Stream バッファ (秒)",
                    "StreamClip 送信時にホスト側で先送りするバッファ長。\n" +
                    "短い: Stop の応答が速い、ジッタで途切れやすい\n" +
                    "長い: 安定、Stop 後に短い残響あり\n" +
                    "Range 10–200ms / Default 50ms。詳しくは docs/streaming"));

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("enableLogging"),
                new GUIContent("ログ出力", "詳細ログをコンソールに出力"));

            // ── レイテンシ補正 ────────────────────────────────────────
            // Audio 出力デバイス (Bluetooth 等) との遅延差を吸収する全体オフセット。
            // 個別 entry 単位の追加調整は EventMap Window 側 (delayOffsetSeconds)。
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("レイテンシ補正", EditorStyles.boldLabel);

            EditorGUILayout.PropertyField(
                _serializedConfig.FindProperty("hapticDelaySeconds"),
                new GUIContent(
                    "触覚遅延 (秒)",
                    "Audio 出力デバイス (Bluetooth 等) の latency に合わせて、全 Play / StreamClip\n" +
                    "に加算するグローバル遅延。Hapbeat 触覚は UDP 直送で非常に低遅延 (~10ms) なので、\n" +
                    "speakers / headphones が遅い環境では触覚が先に来てしまう。これを補正する offset。\n\n" +
                    "目安:\n" +
                    "  ・有線 / USB DAC      → 0.00\n" +
                    "  ・内蔵スピーカー       → 0.02〜0.05\n" +
                    "  ・Bluetooth (aptX LL) → 0.03〜0.05\n" +
                    "  ・Bluetooth (SBC/AAC) → 0.15〜0.20\n\n" +
                    "各 EventMap entry の Delay Offset でさらに ±0.2 秒の個別調整も可能。"));

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField("Config アセット", _config, typeof(HapbeatConfig), false);
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
                        "ESP-NOW で複数 Hapbeat に同報する用途向け。通常の Wi-Fi UDP で\n" +
                        "事足りる場合は触る必要ありません。",
                        MessageType.None);
                    EditorGUILayout.PropertyField(
                        _serializedConfig.FindProperty("useBridge"),
                        new GUIContent("Bridge を使用", "ESP-NOW 経由の多デバイス送信時に有効化"));

                    EditorGUI.BeginDisabledGroup(!_config.useBridge);
                    EditorGUILayout.PropertyField(
                        _serializedConfig.FindProperty("bridgeHost"),
                        new GUIContent("Bridge ホスト", "Bridge のホスト名または IP アドレス"));
                    EditorGUI.EndDisabledGroup();
                }
            }

            _serializedConfig.ApplyModifiedProperties();
        }

        private void DrawConnectionSection()
        {
            EditorGUILayout.LabelField("接続状態", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            bool isConnected = isPlaying && HapbeatManager.Instance != null &&
                               HapbeatManager.Instance.IsConnected;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Toggle("プレイモード", isPlaying);
            EditorGUILayout.Toggle("接続中", isConnected);
            EditorGUI.EndDisabledGroup();

            if (!isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "接続テストを行うには、プレイモードで実行してください。",
                    MessageType.Info);
            }
        }

        private void DrawTestSection()
        {
            EditorGUILayout.LabelField("接続テスト", EditorStyles.boldLabel);

            bool canTest = Application.isPlaying && HapbeatManager.Instance != null;

            EditorGUI.BeginDisabledGroup(!canTest || _isPinging);

            if (GUILayout.Button("Ping テスト"))
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
            _pingResult = "Ping 送信中...";

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
                    _pingResult = "Ping タイムアウト - デバイスが応答しません。";
                    HapbeatManager.Instance.OnPong -= OnPingResponse;
                    Repaint();
                }
            };
        }

        private void OnPingResponse(long rttUs)
        {
            _isPinging = false;
            _pingResult = $"Ping 成功: RTT = {rttUs} μs ({rttUs / 1000.0:F1} ms)";

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

            Debug.Log($"[Hapbeat] HapbeatConfig を作成しました: {path}");
        }
    }
}
#endif
