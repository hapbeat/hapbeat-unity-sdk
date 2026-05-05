#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Hapbeat;
using Hapbeat.Editor;

namespace Hapbeat.Samples.Editor
{
    /// <summary>
    /// BasicExample シーンを自動生成する Editor スクリプト。
    /// Menu: Hapbeat > Build Samples > 1. Basic Example
    ///
    /// 生成物の配置 (ユーザー領域):
    ///   Assets/HapbeatKits/BasicExampleKit/  (manifest.json + sine_100hz_1s.wav)
    ///   Assets/HapbeatSDK/EventMaps/BasicExampleEventMap.asset
    ///   Assets/HapbeatSDK/Scenes/BasicExample.unity
    ///
    /// `Samples~/BasicExample/` は Package Manager から import される読み取り専用
    /// 雛形として残す。Build を再実行すると Kit / EventMap / Scene が再生成される。
    /// </summary>
    public static class BasicExampleSceneBuilder
    {
        private const string kKitName = "BasicExampleKit";
        private const string kEventMapName = "BasicExampleEventMap";
        private const string kSceneName = "BasicExample";

        private const string kSdkRoot = "Assets/HapbeatSDK";
        private const string kScenesDir = kSdkRoot + "/Scenes";
        private const string kEventMapsDir = kSdkRoot + "/EventMaps";

        [MenuItem("Hapbeat/Build Samples/1. Basic Example", false, 100)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog(
                "BasicExample 生成",
                "BasicExample 用 Kit / EventMap / Scene を生成します。\n" +
                $"  - Assets/HapbeatKits/{kKitName}/ (manifest + wav)\n" +
                $"  - {kEventMapsDir}/{kEventMapName}.asset\n" +
                $"  - {kScenesDir}/{kSceneName}.unity\n\n" +
                "現在のシーンの未保存の変更は失われます。",
                "生成する", "キャンセル"))
                return;

            string sampleRoot = FindSampleRoot();
            if (sampleRoot == null)
            {
                EditorUtility.DisplayDialog("エラー",
                    "Basic Example サンプルのフォルダが見つかりません。\nPackage Manager から Basic Example を Import してください。",
                    "OK");
                return;
            }

            // 1. HapbeatKits root + this kit folder.
            HapbeatKitsFolderCreator.EnsureFolderAndReadme(openReadme: false);
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath() ?? HapbeatKitsReadme.DefaultKitsRootPath;
            string kitDir = $"{kitsRoot}/{kKitName}";
            CopyKit(sampleRoot + "/Kit", kitDir);

            // 2. HapbeatSDK/{Scenes, EventMaps} layout.
            EnsureFolder(kSdkRoot);
            EnsureFolder(kScenesDir);
            EnsureFolder(kEventMapsDir);

            // 3. EventMap referencing the kit's wav.
            string mapPath = $"{kEventMapsDir}/{kEventMapName}.asset";
            var eventMap = BuildOrLoadEventMap(mapPath, kitDir);

            // 4. Scene generation.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var router = CreateRouter();
            var demo = router.AddComponent<HapbeatDemo>();
            var demoUI = router.AddComponent<HapbeatDemoUI>();

            // Wire AudioClip from the kit (so the user sees the kit-routed flow).
            var clipPath = $"{kitDir}/sine_100hz_1s.wav";
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip != null)
            {
                var demoSO = new SerializedObject(demo);
                demoSO.FindProperty("_audioClip").objectReferenceValue = clip;
                demoSO.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"[Hapbeat] AudioClip wired from kit: {clipPath}");
            }
            else
            {
                Debug.LogWarning($"[Hapbeat] AudioClip not found at {clipPath}. The Space-key streaming demo will need a clip.");
            }

            // Canvas + UI (unchanged from original layout).
            var canvas = CreateScreenCanvas("Canvas");

            CreateText(canvas.transform, "Title", "Hapbeat Basic Demo",
                TextAnchor.MiddleCenter, 28, new Vector2(0.5f, 1f), new Vector2(0, -40));
            var status = CreateText(canvas.transform, "Status", "",
                TextAnchor.MiddleCenter, 20, new Vector2(0.5f, 1f), new Vector2(0, -80));
            CreateText(canvas.transform, "Instructions",
                "Space: Stream AudioClip  /  E: Event Command (demo.sine)  /  S: Stop  /  P: Ping",
                TextAnchor.MiddleCenter, 16, new Vector2(0.5f, 1f), new Vector2(0, -120));
            var log = CreateText(canvas.transform, "Log", "",
                TextAnchor.UpperLeft, 14, new Vector2(0f, 0f), new Vector2(20, 20));
            var logRect = log.GetComponent<RectTransform>();
            logRect.anchorMin = new Vector2(0, 0);
            logRect.anchorMax = new Vector2(1, 0.5f);
            logRect.offsetMin = new Vector2(20, 20);
            logRect.offsetMax = new Vector2(-20, -20);

            var demoUISO = new SerializedObject(demoUI);
            demoUISO.FindProperty("_statusText").objectReferenceValue = status.GetComponent<Text>();
            demoUISO.FindProperty("_logText").objectReferenceValue = log.GetComponent<Text>();
            demoUISO.ApplyModifiedPropertiesWithoutUndo();

            // Use the EventMap's first entry as the default Event ID for the E key.
            if (eventMap != null && eventMap.entries.Count > 0)
            {
                var firstEntry = eventMap.entries[0];
                if (!string.IsNullOrEmpty(firstEntry.eventId))
                {
                    var demoSO = new SerializedObject(demo);
                    demoSO.FindProperty("_eventId").stringValue = firstEntry.eventId;
                    demoSO.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // 5. Save scene.
            string scenePath = $"{kScenesDir}/{kSceneName}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Hapbeat] BasicExample scene saved: {scenePath}");

            EditorUtility.DisplayDialog("完了",
                "BasicExample を生成しました:\n" +
                $"  Kit       : {kitDir}/\n" +
                $"  EventMap  : {mapPath}\n" +
                $"  Scene     : {scenePath}\n\n" +
                "Play で動作確認できます。",
                "OK");
        }

        // ----------------------------------------------------------------
        // Kit copy
        // ----------------------------------------------------------------

        private static void CopyKit(string srcAssetPath, string dstAssetPath)
        {
            string srcAbs = ToAbsolute(srcAssetPath);
            string dstAbs = ToAbsolute(dstAssetPath);
            if (!Directory.Exists(srcAbs))
            {
                Debug.LogWarning($"[Hapbeat] Kit source not found: {srcAssetPath}");
                return;
            }
            Directory.CreateDirectory(dstAbs);
            foreach (var file in Directory.GetFiles(srcAbs))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".meta")) continue;
                string dst = Path.Combine(dstAbs, name);
                if (!File.Exists(dst))
                    File.Copy(file, dst);
            }
            AssetDatabase.Refresh();
        }

        // ----------------------------------------------------------------
        // EventMap
        // ----------------------------------------------------------------

        private static HapbeatEventMap BuildOrLoadEventMap(string assetPath, string kitDir)
        {
            var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(assetPath);
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<HapbeatEventMap>();
                AssetDatabase.CreateAsset(map, assetPath);
            }
            map.entries.Clear();

            string clipAssetPath = $"{kitDir}/sine_100hz_1s.wav";
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipAssetPath);

            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = "demo_sine",
                category = "demo",
                eventName = "sine",
                streamClip = clip,
                loop = false,
                gain = 1.0f,
                target = "",
                group = -1,
            });

            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssets();
            return map;
        }

        // ----------------------------------------------------------------
        // Helpers (Canvas / Text / Router)
        // ----------------------------------------------------------------

        private static GameObject CreateRouter()
        {
            var existing = Object.FindObjectsByType<HapbeatManager>(FindObjectsSortMode.None);
            var router = new GameObject("[Hapbeat Event Router]");
            if (existing.Length == 0)
                router.AddComponent<HapbeatManager>();
            return router;
        }

        private static Canvas CreateScreenCanvas(string name)
        {
            var go = new GameObject(name);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            if (Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length == 0)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                var inputModuleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputModuleType != null)
                    es.AddComponent(inputModuleType);
                else
                    es.AddComponent<StandaloneInputModule>();
            }

            return canvas;
        }

        private static GameObject CreateText(Transform parent, string name, string text,
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

        // ----------------------------------------------------------------
        // Path resolution
        // ----------------------------------------------------------------

        private static string FindSampleRoot()
        {
            // BasicExampleSceneBuilder.cs 自身の場所からサンプルルートを逆引き。
            var guids = AssetDatabase.FindAssets("t:Script BasicExampleSceneBuilder");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                int editorIdx = p.LastIndexOf("/Editor/");
                if (editorIdx >= 0) return p.Substring(0, editorIdx);
            }
            // フォールバック: HapbeatDemo.cs を探す。
            guids = AssetDatabase.FindAssets("t:Script HapbeatDemo");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                int slash = p.LastIndexOf("/");
                if (slash >= 0) return p.Substring(0, slash);
            }
            return null;
        }

        private static void EnsureFolder(string assetPath)
        {
            string abs = ToAbsolute(assetPath);
            if (!Directory.Exists(abs))
                Directory.CreateDirectory(abs);
            AssetDatabase.Refresh();
        }

        private static string ToAbsolute(string assetPath)
        {
            if (assetPath.StartsWith("Assets/"))
                return Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length))
                    .Replace('\\', '/');
            return Path.GetFullPath(assetPath);
        }
    }
}
#endif
