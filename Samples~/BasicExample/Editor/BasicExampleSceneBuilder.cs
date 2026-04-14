#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Hapbeat;

namespace Hapbeat.Samples.Editor
{
    /// <summary>
    /// BasicExample シーンを自動生成する Editor スクリプト。
    /// Menu: Hapbeat > Build Samples > Basic Example
    /// </summary>
    public static class BasicExampleSceneBuilder
    {
        [MenuItem("Hapbeat/Build Samples/1. Basic Example", false, 100)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog(
                "BasicExample シーン生成",
                "新しいシーンを作成して BasicExample を構築します。\n現在のシーンの未保存の変更は失われます。",
                "生成する", "キャンセル"))
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // --- Hapbeat Event Router ---
            var router = CreateRouter();
            var demo = router.AddComponent<HapbeatDemo>();
            var demoUI = router.AddComponent<HapbeatDemoUI>();

            // --- AudioClip を自動接続 ---
            var clipGuids = AssetDatabase.FindAssets("sine_100hz_1s t:AudioClip");
            if (clipGuids.Length > 0)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(clipGuids[0]));
                var demoSO = new SerializedObject(demo);
                demoSO.FindProperty("_audioClip").objectReferenceValue = clip;
                demoSO.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[Hapbeat] AudioClip を自動接続しました: {clip.name}");
            }
            else
            {
                Debug.LogWarning("[Hapbeat] sine_100hz_1s.wav が見つかりません。HapbeatDemo の AudioClip を手動で設定してください。");
            }

            // --- Canvas + UI ---
            var canvas = CreateScreenCanvas("Canvas");

            var title = CreateText(canvas.transform, "Title", "Hapbeat Basic Demo",
                TextAnchor.MiddleCenter, 28, new Vector2(0.5f, 1f), new Vector2(0, -40));

            var status = CreateText(canvas.transform, "Status", "",
                TextAnchor.MiddleCenter, 20, new Vector2(0.5f, 1f), new Vector2(0, -80));

            var instructions = CreateText(canvas.transform, "Instructions",
                "Space: Stream AudioClip  /  E: Event Command  /  S: Stop  /  P: Ping",
                TextAnchor.MiddleCenter, 16, new Vector2(0.5f, 1f), new Vector2(0, -120));

            var log = CreateText(canvas.transform, "Log", "",
                TextAnchor.UpperLeft, 14, new Vector2(0f, 0f), new Vector2(20, 20));
            var logRect = log.GetComponent<RectTransform>();
            logRect.anchorMin = new Vector2(0, 0);
            logRect.anchorMax = new Vector2(1, 0.5f);
            logRect.offsetMin = new Vector2(20, 20);
            logRect.offsetMax = new Vector2(-20, -20);

            // --- HapbeatDemoUI の参照を接続 ---
            var demoUISO = new SerializedObject(demoUI);
            demoUISO.FindProperty("_statusText").objectReferenceValue = status.GetComponent<Text>();
            demoUISO.FindProperty("_logText").objectReferenceValue = log.GetComponent<Text>();
            demoUISO.ApplyModifiedPropertiesWithoutUndo();

            // --- 保存 ---
            string path = FindSamplesPath("BasicExample");
            if (path == null)
            {
                path = "Assets/BasicExample";
                Debug.LogWarning("[Hapbeat] サンプルパスが見つかりません。Assets/BasicExample/ に保存します。");
            }
            if (!System.IO.Directory.Exists(path))
                System.IO.Directory.CreateDirectory(path);
            string scenePath = path + "/BasicExample.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Hapbeat] BasicExample シーンを保存しました: {scenePath}");

            EditorUtility.DisplayDialog("完了", "BasicExample シーンを生成しました。\nPlay ボタンで動作確認できます。", "OK");
        }

        // ========== Helpers ==========

        static GameObject CreateRouter()
        {
            var existing = Object.FindObjectsByType<HapbeatManager>(FindObjectsSortMode.None);
            var router = new GameObject("[Hapbeat Event Router]");
            if (existing.Length == 0)
                router.AddComponent<HapbeatManager>();
            return router;
        }

        static Canvas CreateScreenCanvas(string name)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            // EventSystem
            if (Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 0)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                // Input System パッケージ対応: InputSystemUIInputModule を優先、なければ旧式
                var inputModuleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputModuleType != null)
                    es.AddComponent(inputModuleType);
                else
                    es.AddComponent<StandaloneInputModule>();
            }

            return canvas;
        }

        static GameObject CreateText(Transform parent, string name, string text,
            TextAnchor alignment, int fontSize, Vector2 anchorPos, Vector2 offset)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(anchorPos.x - 0.4f, anchorPos.y);
            rect.anchorMax = new Vector2(anchorPos.x + 0.4f, anchorPos.y);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(0, 40);

            var t = go.AddComponent<Text>();
            t.text = text;
            t.alignment = alignment;
            t.fontSize = fontSize;
            t.color = Color.white;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;

            return go;
        }

        static string FindSamplesPath(string sampleName)
        {
            // HapbeatDemo スクリプトの実際のパスからサンプルルートを逆算
            var guids = AssetDatabase.FindAssets("t:Script HapbeatDemo");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                // 例: "Assets/Samples/Hapbeat SDK/0.1.0/Basic Example/HapbeatDemo.cs"
                //   → "Assets/Samples/Hapbeat SDK/0.1.0/Basic Example"
                int lastSlash = p.LastIndexOf("/");
                if (lastSlash >= 0 && p.EndsWith(".cs"))
                    return p.Substring(0, lastSlash);
            }
            return null;
        }
    }
}
#endif
