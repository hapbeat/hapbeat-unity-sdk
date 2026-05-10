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
    ///   - HapbeatKeyDispatcher     (Space / R / F / S / C を上記 Trigger / Helper に UnityEvent wiring)
    ///   - HapbeatStatusOverlay            (Status と Log の表示専用)
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

        [MenuItem("Hapbeat/Build Samples/1. Basic Example", false, 60)]
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
            Debug.Log($"[Hapbeat] EventMap built: name='{(eventMap != null ? eventMap.name : "<null>")}', " +
                      $"entries={(eventMap != null ? eventMap.entries.Count : -1)}");

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
            CreateInstructions(canvas.transform,
                new[] { "Space", "R", "F", "S", "C" },
                new[]
                {
                    "CLIP (Stream) 1-shot (100Hz)",
                    "CLIP (Stream) loop (100Hz)",
                    "FIRE (Command) (200Hz)",
                    "Stop all",
                    "Ping",
                },
                fontSize: 16, yOffset: -120,
                keyColor: new Color(0.43f, 0.78f, 1.0f)); // soft cyan
            var log = CreateText(canvas.transform, "Log", "",
                TextAnchor.MiddleCenter, 16, new Vector2(0.5f, 0.5f), new Vector2(0, 0));
            var logRect = log.GetComponent<RectTransform>();
            logRect.anchorMin = new Vector2(0, 0);
            logRect.anchorMax = new Vector2(1, 0.5f);
            logRect.offsetMin = new Vector2(20, 20);
            logRect.offsetMax = new Vector2(-20, -20);

            var overlay = router.AddComponent<HapbeatStatusOverlay>();
            var overlaySO = new SerializedObject(overlay);
            overlaySO.FindProperty("_statusText").objectReferenceValue = status.GetComponent<Text>();
            overlaySO.FindProperty("_logText").objectReferenceValue = log.GetComponent<Text>();
            overlaySO.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(overlay);

            // Key dispatcher with persistent UnityEvent listeners.
            var dispatcher = router.AddComponent<HapbeatKeyDispatcher>();
            BindKey(dispatcher, "Stream 1-shot", KeyCode.Space, trigOneshot, overlay, "Stream 1-shot");
            BindKey(dispatcher, "Stream loop",   KeyCode.R,     trigLoop,    overlay, "Stream loop");
            BindKey(dispatcher, "Command",       KeyCode.F,     trigCommand, overlay, "Fire command");
            BindStop(dispatcher, helper, overlay);
            BindPing(dispatcher, helper, overlay);

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
                "Play で Space / R / F / S / C を試してみてください。",
                "OK");
        }

        // ----------------------------------------------------------------
        // Trigger setup
        // ----------------------------------------------------------------

        private static HapbeatUnityEventTrigger AddTrigger(GameObject host, HapbeatEventMap map, string displayName)
        {
            var trig = host.AddComponent<HapbeatUnityEventTrigger>();

            if (map == null)
            {
                Debug.LogError($"[Hapbeat] EventMap is null when wiring trigger '{displayName}'.");
                return trig;
            }

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
                Debug.LogWarning($"[Hapbeat] EventMap entry '{displayName}' not found " +
                                 $"(map has {map.entries.Count} entries).");
                return trig;
            }

            // Materialize the stable id (lazy-assign on first read).
            string id = entry.id;

            // Use the dedicated setup method instead of SerializedObject to avoid
            // inheritance-traversal issues with protected fields across Unity versions.
            trig.EditorSetupEntry(map, id, idx);
            EditorUtility.SetDirty(trig);
            return trig;
        }

        // ----------------------------------------------------------------
        // Key binding helpers
        // ----------------------------------------------------------------

        private static void BindKey(HapbeatKeyDispatcher dispatcher, string label, KeyCode key,
            HapbeatUnityEventTrigger trigger, HapbeatStatusOverlay ui, string logMessage)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = label, key = key };
            UnityEventTools.AddPersistentListener(b.onPressed, trigger.Fire);
            if (ui != null && !string.IsNullOrEmpty(logMessage))
                UnityEventTools.AddStringPersistentListener(b.onPressed, ui.Log, logMessage);
            dispatcher.Bindings.Add(b);
        }

        private static void BindStop(HapbeatKeyDispatcher dispatcher, HapbeatActionHelper helper, HapbeatStatusOverlay ui)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = "Stop all", key = KeyCode.S };
            UnityEventTools.AddPersistentListener(b.onPressed, helper.StopEverything);
            if (ui != null)
                UnityEventTools.AddStringPersistentListener(b.onPressed, ui.Log, "Stop all");
            dispatcher.Bindings.Add(b);
        }

        private static void BindPing(HapbeatKeyDispatcher dispatcher, HapbeatActionHelper helper, HapbeatStatusOverlay ui)
        {
            var b = new HapbeatKeyDispatcher.Binding { label = "Ping", key = KeyCode.C };
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

            // Event IDs follow the <kit-id>.<bare-filename> convention so
            // they line up with the manifest.json keys verbatim. The loop
            // variant uses a "_loop" suffix to keep its id distinct (same
            // wav, but different EventMap entry / runtime behaviour).
            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = kEntryStreamOneshot,
                category = kKitName,
                eventName = "sine_100hz_1s",
                streamClip = streamClip,
                loop = false,
                gain = 1.0f,
                target = "",
            });
            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = kEntryStreamLoop,
                category = kKitName,
                eventName = "sine_100hz_1s_loop",
                streamClip = streamClip,
                loop = true,
                gain = 1.0f,
                target = "",
            });
            map.entries.Add(new HapbeatEventEntry
            {
                mode = HapticMode.Command,
                displayName = kEntryCommand,
                category = kKitName,
                eventName = "sine_200hz_1s",
                streamClip = null,
                loop = false,
                gain = 1.0f,
                target = "",
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

        // Two-column instructions block: keys on the left (right-aligned, colored),
        // descriptions on the right (left-aligned, white). The two columns meet at
        // canvas-center horizontally so the colons line up regardless of key length.
        private static void CreateInstructions(Transform parent, string[] keys, string[] descriptions,
            int fontSize, float yOffset, Color keyColor)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Keys column (right-aligned, right edge at canvas horizontal center).
            var keysGo = new GameObject("InstructionsKeys");
            keysGo.transform.SetParent(parent, false);
            var keysRect = keysGo.AddComponent<RectTransform>();
            keysRect.anchorMin = new Vector2(0.5f, 1f);
            keysRect.anchorMax = new Vector2(0.5f, 1f);
            keysRect.pivot = new Vector2(1f, 1f);
            keysRect.sizeDelta = new Vector2(240, 200);
            keysRect.anchoredPosition = new Vector2(0, yOffset);
            var keysText = keysGo.AddComponent<Text>();
            keysText.text = string.Join("\n", keys);
            keysText.alignment = TextAnchor.UpperRight;
            keysText.fontSize = fontSize;
            keysText.color = keyColor;
            keysText.font = font;
            keysText.horizontalOverflow = HorizontalWrapMode.Overflow;
            keysText.verticalOverflow = VerticalWrapMode.Overflow;

            // Descriptions column (left-aligned, left edge at canvas horizontal center,
            // each line prefixed with " : " so the colons share a single column).
            var descGo = new GameObject("InstructionsDesc");
            descGo.transform.SetParent(parent, false);
            var descRect = descGo.AddComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0.5f, 1f);
            descRect.anchorMax = new Vector2(0.5f, 1f);
            descRect.pivot = new Vector2(0f, 1f);
            descRect.sizeDelta = new Vector2(480, 200);
            descRect.anchoredPosition = new Vector2(0, yOffset);
            var descText = descGo.AddComponent<Text>();
            var descLines = new string[descriptions.Length];
            for (int i = 0; i < descriptions.Length; i++)
                descLines[i] = " : " + descriptions[i];
            descText.text = string.Join("\n", descLines);
            descText.alignment = TextAnchor.UpperLeft;
            descText.fontSize = fontSize;
            descText.color = Color.white;
            descText.font = font;
            descText.horizontalOverflow = HorizontalWrapMode.Overflow;
            descText.verticalOverflow = VerticalWrapMode.Overflow;
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
            // BasicExampleSceneBuilder.cs lives in <SampleRoot>/Editor/, so the
            // sample root is always the parent of the /Editor/ segment.
            var guids = AssetDatabase.FindAssets("t:Script BasicExampleSceneBuilder");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                int editorIdx = p.LastIndexOf("/Editor/");
                if (editorIdx >= 0) return p.Substring(0, editorIdx);
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
