#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
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
    /// 構成:
    ///   - HapbeatManager           (singleton)
    ///   - HapbeatActionHelper      (Stop / StopStream / Ping を UnityEvent から呼ぶための wrapper)
    ///   - HapbeatUnityEventTrigger × 3 (oneshot stream / loop stream / command の各 entry に bind)
    ///   - HapbeatKeyDispatcher     (Space / L / E / S / P を上記 Trigger / Helper に UnityEvent wiring)
    ///   - HapbeatDemoUI            (Status と Log の表示専用)
    ///
    /// 生成物の配置 (ユーザー領域):
    ///   Assets/HapbeatSDK/Kits/basic-exam-kit/{install-clips, stream-clips}/
    ///   Assets/HapbeatSDK/EventMaps/BasicExampleEventMap.asset
    ///   Assets/HapbeatSDK/Scenes/BasicExample.unity
    /// </summary>
    public static class BasicExampleSceneBuilder
    {
        private const string kKitName = "basic-exam-kit";
        private const string kEventMapName = "BasicExampleEventMap";
        private const string kSceneName = "BasicExample";

        private const string kEntryStreamOneshot = "demo_stream_sine_100hz";
        private const string kEntryStreamLoop    = "demo_stream_loop_100hz";
        private const string kEntryCommand       = "demo_command_sine_200hz";

        private static string kScenesDir => HapbeatSDKFolderCreator.kScenesDir;
        private static string kEventMapsDir => HapbeatSDKFolderCreator.kEventMapsDir;

        [MenuItem("Hapbeat/Build Samples/1. Basic Example", false, 100)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog(
                "BasicExample 生成",
                "BasicExample 用 Kit / EventMap / Scene を生成します。\n" +
                $"  - Assets/HapbeatSDK/Kits/{kKitName}/ (manifest + wav)\n" +
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

            // 1. HapbeatSDK layout (Kits/Scenes/EventMaps).
            string kitsRoot = HapbeatSDKFolderCreator.EnsureLayout(verbose: false);

            // 2. Kit copy.
            string kitDir = $"{kitsRoot}/{kKitName}";
            CopyKit(sampleRoot + "/Kit", kitDir);

            // 3. EventMap.
            string mapPath = $"{kEventMapsDir}/{kEventMapName}.asset";
            var eventMap = BuildOrLoadEventMap(mapPath, kitDir);

            // 4. Scene.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Make the scene render as a black 2D UI: clear camera to solid black
            // and remove the default skybox / ambient light so the Scene view also
            // matches the Game view (no procedural sky in either edit or play mode).
            var mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = Color.black;
            }
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.fog = false;

            // Router with Manager + Helper + 3 Triggers + Dispatcher + UI.
            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();
            var helper = router.AddComponent<HapbeatActionHelper>();

            var trigOneshot = AddTrigger(router, eventMap, kEntryStreamOneshot);
            var trigLoop    = AddTrigger(router, eventMap, kEntryStreamLoop);
            var trigCommand = AddTrigger(router, eventMap, kEntryCommand);

            // UI canvas + status / instructions / log.
            var canvas = CreateScreenCanvas("Canvas");
            CreateText(canvas.transform, "Title", "Hapbeat Basic Demo",
                TextAnchor.MiddleCenter, 28, new Vector2(0.5f, 1f), new Vector2(0, -40));
            var status = CreateText(canvas.transform, "Status", "",
                TextAnchor.MiddleCenter, 20, new Vector2(0.5f, 1f), new Vector2(0, -80));
            CreateText(canvas.transform, "Instructions",
                "Space: Stream 1-shot (100Hz)   L: Stream loop (100Hz)   E: Command (200Hz)   S: Stop all   P: Ping",
                TextAnchor.MiddleCenter, 16, new Vector2(0.5f, 1f), new Vector2(0, -120));
            var log = CreateText(canvas.transform, "Log", "",
                TextAnchor.MiddleCenter, 16, new Vector2(0.5f, 0.5f), new Vector2(0, 0));
            var logRect = log.GetComponent<RectTransform>();
            logRect.anchorMin = new Vector2(0, 0);
            logRect.anchorMax = new Vector2(1, 0.5f);
            logRect.offsetMin = new Vector2(20, 20);
            logRect.offsetMax = new Vector2(-20, -20);

            var demoUI = router.AddComponent<HapbeatDemoUI>();
            var demoUISO = new SerializedObject(demoUI);
            demoUISO.FindProperty("_statusText").objectReferenceValue = status.GetComponent<Text>();
            demoUISO.FindProperty("_logText").objectReferenceValue = log.GetComponent<Text>();
            demoUISO.ApplyModifiedPropertiesWithoutUndo();

            // Key dispatcher with persistent UnityEvent listeners.
            var dispatcher = router.AddComponent<HapbeatKeyDispatcher>();
            BindKey(dispatcher, "Stream 1-shot", KeyCode.Space, trigOneshot, demoUI, "Stream 1-shot");
            BindKey(dispatcher, "Stream loop",   KeyCode.L,     trigLoop,    demoUI, "Stream loop");
            BindKey(dispatcher, "Command",       KeyCode.E,     trigCommand, demoUI, "Fire command");
            BindStop(dispatcher, helper, demoUI);
            BindPing(dispatcher, helper, demoUI);

            EditorUtility.SetDirty(dispatcher);

            // Save scene.
            string scenePath = $"{kScenesDir}/{kSceneName}.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Hapbeat] BasicExample scene saved: {scenePath}");

            EditorUtility.DisplayDialog("完了",
                "BasicExample を生成しました:\n" +
                $"  Kit       : {kitDir}/\n" +
                $"  EventMap  : {mapPath}\n" +
                $"  Scene     : {scenePath}\n\n" +
                "Play で Space / L / E / S / P を試してみてください。",
                "OK");
        }

        // ----------------------------------------------------------------
        // Trigger setup
        // ----------------------------------------------------------------

        private static HapbeatUnityEventTrigger AddTrigger(GameObject host, HapbeatEventMap map, string displayName)
        {
            var trig = host.AddComponent<HapbeatUnityEventTrigger>();
            int idx = -1;
            HapbeatEventEntry entry = null;
            for (int i = 0; i < map.entries.Count; i++)
            {
                if (map.entries[i].displayName == displayName)
                {
                    entry = map.entries[i];
                    idx = i;
                    break;
                }
            }
            if (entry == null)
            {
                Debug.LogWarning($"[Hapbeat] EventMap entry '{displayName}' not found.");
                return trig;
            }

            // Materialize the stable id (lazy-assign on first read).
            string id = entry.id;

            var so = new SerializedObject(trig);
            so.FindProperty("_eventMap").objectReferenceValue = map;
            so.FindProperty("_entryId").stringValue = id;
            so.FindProperty("_entryIndex").intValue = idx;
            so.ApplyModifiedPropertiesWithoutUndo();
            return trig;
        }

        // ----------------------------------------------------------------
        // Key binding helpers
        // ----------------------------------------------------------------

        private static void BindKey(HapbeatKeyDispatcher dispatcher, string label, KeyCode key,
            HapbeatUnityEventTrigger trigger, HapbeatDemoUI ui, string logMessage)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = label, key = key };
            UnityEventTools.AddPersistentListener(b.onPressed, trigger.Fire);
            if (ui != null && !string.IsNullOrEmpty(logMessage))
                UnityEventTools.AddStringPersistentListener(b.onPressed, ui.Log, logMessage);
            dispatcher.Bindings.Add(b);
        }

        private static void BindStop(HapbeatKeyDispatcher dispatcher, HapbeatActionHelper helper, HapbeatDemoUI ui)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = "Stop all", key = KeyCode.S };
            UnityEventTools.AddPersistentListener(b.onPressed, helper.StopEverything);
            if (ui != null)
                UnityEventTools.AddStringPersistentListener(b.onPressed, ui.Log, "Stop all");
            dispatcher.Bindings.Add(b);
        }

        private static void BindPing(HapbeatKeyDispatcher dispatcher, HapbeatActionHelper helper, HapbeatDemoUI ui)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = "Ping", key = KeyCode.P };
            UnityEventTools.AddPersistentListener(b.onPressed, helper.Ping);
            if (ui != null)
                UnityEventTools.AddStringPersistentListener(b.onPressed, ui.Log, "Ping sent");
            dispatcher.Bindings.Add(b);
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
            CopyDirectoryRecursive(srcAbs, dstAbs);
            AssetDatabase.Refresh();
        }

        private static void CopyDirectoryRecursive(string srcAbs, string dstAbs)
        {
            Directory.CreateDirectory(dstAbs);
            foreach (var file in Directory.GetFiles(srcAbs))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".meta")) continue;
                string dst = Path.Combine(dstAbs, name);
                if (!File.Exists(dst))
                    File.Copy(file, dst);
            }
            foreach (var dir in Directory.GetDirectories(srcAbs))
            {
                string name = Path.GetFileName(dir);
                CopyDirectoryRecursive(dir, Path.Combine(dstAbs, name));
            }
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

            string streamClipPath = $"{kitDir}/stream-clips/sine_100hz_1s.wav";
            var streamClip = AssetDatabase.LoadAssetAtPath<AudioClip>(streamClipPath);

            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = kEntryStreamOneshot,
                category = "demo",
                eventName = "stream_sine",
                streamClip = streamClip,
                loop = false,
                gain = 1.0f,
                target = "",
                group = -1,
            });
            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = kEntryStreamLoop,
                category = "demo",
                eventName = "stream_loop",
                streamClip = streamClip,
                loop = true,
                gain = 1.0f,
                target = "",
                group = -1,
            });
            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.Command,
                displayName = kEntryCommand,
                category = "demo",
                eventName = "command_sine",
                streamClip = null,
                loop = false,
                gain = 1.0f,
                target = "",
                group = -1,
            });

            // Force id materialization so Trigger _entryId references stay stable.
            foreach (var e in map.entries) { var _ = e.id; }

            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssets();
            return map;
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

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
            var guids = AssetDatabase.FindAssets("t:Script BasicExampleSceneBuilder");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                int editorIdx = p.LastIndexOf("/Editor/");
                if (editorIdx >= 0) return p.Substring(0, editorIdx);
            }
            // Fallback: locate via the dispatcher script.
            guids = AssetDatabase.FindAssets("t:Script HapbeatKeyDispatcher");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                int slash = p.LastIndexOf("/");
                if (slash >= 0) return p.Substring(0, slash);
            }
            return null;
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
