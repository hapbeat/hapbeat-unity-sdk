#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Hapbeat;
using Hapbeat.Samples.Tutorial;

namespace Hapbeat.Samples.Tutorial.EditorTools
{
    /// <summary>
    /// Scene generator + Strip utility for the Tutorial sample.
    ///
    /// Menus:
    ///   Hapbeat / Build Samples / 2. Tutorial (full scene)
    ///     → Creates Tutorial.unity with 5 zones, [Hapbeat Event Router],
    ///       Picker UI, hotkeys, and a primitive-based world. Run this first.
    ///   Hapbeat / Tutorial / Strip Hapbeat (Tutorial.unity → Tutorial_Plain.unity)
    ///     → Loads Tutorial.unity, removes all Hapbeat components / GameObject,
    ///       saves the result as Tutorial_Plain.unity. Re-run after edits to
    ///       refresh the Without version.
    /// </summary>
    public static class TutorialSceneBuilder
    {
        private const string SCENE_FILE = "Tutorial.unity";
        private const string PLAIN_FILE = "Tutorial_Plain.unity";

        // ----------------------------------------------------------------
        // Build menu
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Build Samples/2. Tutorial (full scene)", false, 110)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog(
                "Tutorial シーン生成",
                "新しい Tutorial.unity シーンと TutorialEventMap.asset を作成します。\n" +
                "現在のシーンの未保存の変更は失われます。",
                "生成する", "キャンセル"))
                return;

            string root = FindTutorialRoot();
            if (root == null)
            {
                EditorUtility.DisplayDialog("エラー",
                    "Tutorial サンプルのフォルダが見つかりません。\nPackage Manager から Tutorial サンプルを Import してください。",
                    "OK");
                return;
            }

            // Build EventMap first so the bridge / triggers can link to it.
            var eventMap = BuildOrLoadEventMap(root);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // Ground floor — a single shared plane.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(8f, 1f, 8f);

            // Per-zone empty parents to keep the hierarchy tidy.
            var z1 = new GameObject("Z1_Bowling").transform;
            var z2 = new GameObject("Z2_Door").transform;
            var z3 = new GameObject("Z3_Pickup").transform;
            var z4 = new GameObject("Z4_Stream").transform;
            var z5 = new GameObject("Z5_Target").transform;

            BuildBowlingLane(z1);
            BuildDoor(z2);
            BuildPickupBox(z3);
            BuildStreamConsole(z4);
            BuildTargetRange(z5);

            // Player + camera + FPS controller.
            var player = BuildPlayer();

            // [Hapbeat Event Router] (this is the part removed by Strip).
            var router = BuildRouter(eventMap);

            // World-space HUD and Picker UI.
            BuildHud(router, player);

            // Save scene.
            string scenePath = $"{root}/Scenes/{SCENE_FILE}";
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();

            string mapPath = $"{root}/EventMap/TutorialEventMap.asset";
            EditorUtility.DisplayDialog("完了",
                $"Tutorial シーンを生成しました:\n  {scenePath}\n\n" +
                $"EventMap も生成しました:\n  {mapPath}\n  (12 entry が StreamClip モードで配線済み)\n\n" +
                "次にメニュー Hapbeat / Tutorial / Strip Hapbeat を実行すると、\n" +
                "Without 版 Tutorial_Plain.unity を生成できます。",
                "OK");
        }

        // ----------------------------------------------------------------
        // Strip menu
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Tutorial/Strip Hapbeat (Tutorial.unity → Tutorial_Plain.unity)", false, 200)]
        public static void StripHapbeat()
        {
            string root = FindTutorialRoot();
            if (root == null)
            {
                EditorUtility.DisplayDialog("エラー", "Tutorial サンプルのフォルダが見つかりません。", "OK");
                return;
            }
            string srcPath = $"{root}/Scenes/{SCENE_FILE}";
            string dstPath = $"{root}/Scenes/{PLAIN_FILE}";
            if (!File.Exists(srcPath))
            {
                EditorUtility.DisplayDialog("エラー",
                    $"{srcPath} が見つかりません。先に Build メニューで Tutorial.unity を生成してください。",
                    "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                "Strip Hapbeat",
                $"Tutorial.unity から Hapbeat 関連コンポーネントを除去し、\n{PLAIN_FILE} として保存します。\n\n" +
                "既存の Tutorial_Plain.unity は上書きされます。",
                "実行する", "キャンセル"))
                return;

            var scene = EditorSceneManager.OpenScene(srcPath, OpenSceneMode.Single);

            int removedComponents = 0;
            int removedGameObjects = 0;

            // Remove HapbeatTriggerBase / HapbeatParameterBinding / TutorialBridge / HapbeatBridge components.
            // We collect first then destroy to avoid mutating during iteration.
            var triggers = Object.FindObjectsByType<HapbeatTriggerBase>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in triggers)
            {
                Object.DestroyImmediate(t);
                removedComponents++;
            }
            var bindings = Object.FindObjectsByType<HapbeatParameterBinding>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in bindings)
            {
                Object.DestroyImmediate(b);
                removedComponents++;
            }
            var bridges = Object.FindObjectsByType<HapbeatBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var br in bridges)
            {
                Object.DestroyImmediate(br);
                removedComponents++;
            }

            // Remove [Hapbeat Event Router] GameObject(s) (Manager + bridge holder).
            var managers = Object.FindObjectsByType<HapbeatManager>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var m in managers)
            {
                Object.DestroyImmediate(m.gameObject);
                removedGameObjects++;
            }

            // Save as Plain.
            EditorSceneManager.SaveScene(scene, dstPath);

            // Reload the master scene so the user doesn't accidentally edit the Plain copy.
            EditorSceneManager.OpenScene(srcPath, OpenSceneMode.Single);

            EditorUtility.DisplayDialog("Strip 完了",
                $"Tutorial_Plain.unity を生成しました。\n\n" +
                $"除去コンポーネント: {removedComponents}\n" +
                $"除去 GameObject: {removedGameObjects}",
                "OK");
        }

        // ----------------------------------------------------------------
        // Zone builders (primitive geometry; swap with CC0 models manually)
        // ----------------------------------------------------------------

        private static void BuildBowlingLane(Transform parent)
        {
            parent.position = new Vector3(-12f, 0f, 0f);

            // Lane surface (a long thin box for visual cue).
            var lane = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lane.name = "Lane";
            lane.transform.SetParent(parent, false);
            lane.transform.localPosition = new Vector3(0f, 0.05f, 3f);
            lane.transform.localScale = new Vector3(2f, 0.1f, 8f);
            DestroyCollider(lane);

            // Spawn point for the ball.
            var spawn = new GameObject("BallSpawn").transform;
            spawn.SetParent(parent, false);
            spawn.localPosition = new Vector3(0f, 0.5f, -1f);

            // Ball.
            var ballGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ballGo.name = "Ball";
            ballGo.transform.SetParent(parent, false);
            ballGo.transform.localPosition = spawn.localPosition;
            ballGo.transform.localScale = Vector3.one * 0.4f;
            var ballRb = ballGo.AddComponent<Rigidbody>();
            ballRb.mass = 4f;

            // 6 pins arranged in a triangle.
            var pinPositions = new[]
            {
                new Vector3(0f, 0.5f, 6f),
                new Vector3(-0.4f, 0.5f, 6.5f),
                new Vector3(0.4f, 0.5f, 6.5f),
                new Vector3(-0.8f, 0.5f, 7f),
                new Vector3(0f, 0.5f, 7f),
                new Vector3(0.8f, 0.5f, 7f),
            };
            var pins = new List<Rigidbody>();
            for (int i = 0; i < pinPositions.Length; i++)
            {
                var pin = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pin.name = $"Pin_{i + 1}";
                pin.transform.SetParent(parent, false);
                pin.transform.localPosition = pinPositions[i];
                pin.transform.localScale = new Vector3(0.18f, 0.4f, 0.18f);
                var rb = pin.AddComponent<Rigidbody>();
                rb.mass = 0.5f;
                pins.Add(rb);
            }

            // Launcher controller on the parent (player wires _aimReference to camera at runtime).
            var launcher = parent.gameObject.AddComponent<BallLauncher>();
            var launcherSO = new SerializedObject(launcher);
            launcherSO.FindProperty("_ball").objectReferenceValue = ballRb;
            launcherSO.FindProperty("_spawnPose").objectReferenceValue = spawn;
            var pinList = launcherSO.FindProperty("_pins");
            pinList.arraySize = pins.Count;
            for (int i = 0; i < pins.Count; i++)
                pinList.GetArrayElementAtIndex(i).objectReferenceValue = pins[i];
            launcherSO.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildDoor(Transform parent)
        {
            parent.position = new Vector3(-4f, 0f, 6f);

            // A simple door: hinged cube. Animator wiring is left to user / SceneBuilder
            // since creating an AnimatorController in code requires AnimationClip authoring.
            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.SetParent(parent, false);
            door.transform.localPosition = new Vector3(0f, 1f, 0f);
            door.transform.localScale = new Vector3(1.5f, 2f, 0.1f);
            var rb = door.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            door.AddComponent<Animator>();
            door.AddComponent<DoorController>();
        }

        private static void BuildPickupBox(Transform parent)
        {
            parent.position = new Vector3(0f, 0f, 6f);

            // Rest pad.
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = "Pad";
            pad.transform.SetParent(parent, false);
            pad.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            pad.transform.localScale = new Vector3(1.2f, 0.1f, 1.2f);

            var rest = new GameObject("RestPose").transform;
            rest.SetParent(parent, false);
            rest.localPosition = new Vector3(0f, 0.4f, 0f);

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "PickupBox";
            box.transform.SetParent(parent, false);
            box.transform.localPosition = rest.localPosition;
            box.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            var boxRb = box.AddComponent<Rigidbody>();
            boxRb.mass = 1f;
            boxRb.linearDamping = 0f;

            var ctl = box.AddComponent<PickupBoxController>();
            var ctlSO = new SerializedObject(ctl);
            ctlSO.FindProperty("_rigidbody").objectReferenceValue = boxRb;
            ctlSO.FindProperty("_restPose").objectReferenceValue = rest;
            ctlSO.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildStreamConsole(Transform parent)
        {
            parent.position = new Vector3(4f, 0f, 6f);

            // Desk
            var desk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            desk.name = "Desk";
            desk.transform.SetParent(parent, false);
            desk.transform.localPosition = new Vector3(0f, 0.5f, 0f);
            desk.transform.localScale = new Vector3(1.5f, 1f, 0.8f);

            // World-space canvas mounted on the desk for sliders.
            var canvasGo = new GameObject("StreamPanel");
            canvasGo.transform.SetParent(parent, false);
            canvasGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            canvasGo.transform.localScale = Vector3.one * 0.005f;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            var rt = canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(400f, 220f);

            var bg = AddImage(canvasGo.transform, "BG", new Vector2(0, 0), new Vector2(400, 220), new Color(0.1f, 0.1f, 0.1f, 0.9f));
            var gainLabel = AddText(canvasGo.transform, "GainLabel", "Gain", new Vector2(-150, 80), 18);
            var gainSlider = AddSlider(canvasGo.transform, "GainSlider", new Vector2(40, 80), new Vector2(280, 30));
            gainSlider.value = 1f;

            var panLabel = AddText(canvasGo.transform, "PanLabel", "Pan", new Vector2(-150, 30), 18);
            var panSlider = AddSlider(canvasGo.transform, "PanSlider", new Vector2(40, 30), new Vector2(280, 30));
            panSlider.minValue = -1f; panSlider.maxValue = 1f; panSlider.value = 0f;

            var clipLabel = AddText(canvasGo.transform, "ClipLabel", "Clip", new Vector2(-150, -20), 18);
            var dropdown = AddDropdown(canvasGo.transform, "ClipDropdown", new Vector2(40, -20), new Vector2(280, 30));

            var status = AddText(canvasGo.transform, "Status", "Stopped", new Vector2(0, -80), 22);

            var ctl = canvasGo.AddComponent<StreamDemoController>();
            var ctlSO = new SerializedObject(ctl);
            ctlSO.FindProperty("_gainSlider").objectReferenceValue = gainSlider;
            ctlSO.FindProperty("_panSlider").objectReferenceValue = panSlider;
            ctlSO.FindProperty("_clipDropdown").objectReferenceValue = dropdown;
            ctlSO.FindProperty("_statusText").objectReferenceValue = status.GetComponent<Text>();
            ctlSO.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void BuildTargetRange(Transform parent)
        {
            parent.position = new Vector3(10f, 0f, 6f);

            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "TargetBoard";
            board.transform.SetParent(parent, false);
            board.transform.localPosition = new Vector3(0f, 1.5f, 4f);
            board.transform.localScale = new Vector3(1.5f, 1.5f, 0.1f);
            board.tag = "Untagged";

            var receiver = board.AddComponent<TargetReceiver>();
            var recSO = new SerializedObject(receiver);
            recSO.FindProperty("_flashRenderer").objectReferenceValue = board.GetComponent<MeshRenderer>();
            recSO.ApplyModifiedPropertiesWithoutUndo();

            // Charge bar (world-space canvas above the player muzzle — simplified to scene-fixed here).
            var canvasGo = new GameObject("ChargeBar");
            canvasGo.transform.SetParent(parent, false);
            canvasGo.transform.localPosition = new Vector3(0f, 0.3f, -2f);
            canvasGo.transform.localScale = Vector3.one * 0.005f;
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            var rt = canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(300f, 30f);

            var bg = AddImage(canvasGo.transform, "BG", new Vector2(0, 0), new Vector2(300f, 30f), new Color(0.05f, 0.05f, 0.05f, 0.7f));
            var bar = AddSlider(canvasGo.transform, "Bar", new Vector2(0, 0), new Vector2(280f, 20f));
            bar.minValue = 0f; bar.maxValue = 1f; bar.value = 0f;

            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(parent, false);
            muzzle.localPosition = new Vector3(0f, 1f, 0f);

            var shooter = parent.gameObject.AddComponent<ChargeShooter>();
            var sSO = new SerializedObject(shooter);
            sSO.FindProperty("_muzzle").objectReferenceValue = muzzle;
            sSO.FindProperty("_chargeBar").objectReferenceValue = bar;
            sSO.ApplyModifiedPropertiesWithoutUndo();

            // Note: _projectilePrefab needs to be assigned by the user (a Sphere prefab with
            // Rigidbody and "Projectile" tag). This sample scene leaves it null on purpose
            // so users can experience populating prefab references during the walkthrough.
        }

        private static GameObject BuildPlayer()
        {
            var player = new GameObject("Player");
            player.transform.position = new Vector3(0f, 1f, -4f);
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(player.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();

            var fps = player.AddComponent<SimpleFPSController>();
            var fpsSO = new SerializedObject(fps);
            fpsSO.FindProperty("_cameraPivot").objectReferenceValue = camGo.transform;
            fpsSO.ApplyModifiedPropertiesWithoutUndo();

            // Default scene camera removed (replaced by player camera).
            var defaults = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (var c in defaults)
            {
                if (c != cam && c.gameObject.name == "Main Camera")
                    Object.DestroyImmediate(c.gameObject);
            }
            cam.tag = "MainCamera";
            return player;
        }

        private static GameObject BuildRouter(HapbeatEventMap eventMap)
        {
            var go = new GameObject("[Hapbeat Event Router]");
            go.AddComponent<HapbeatManager>();
            var bridge = go.AddComponent<TutorialBridge>();
            if (eventMap != null)
            {
                var so = new SerializedObject(bridge);
                so.FindProperty("_eventMap").objectReferenceValue = eventMap;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return go;
        }

        // ----------------------------------------------------------------
        // EventMap auto-generation
        // ----------------------------------------------------------------

        /// <summary>
        /// Create or refresh <c>TutorialEventMap.asset</c> with the 12 entries
        /// described in the Tutorial walkthrough. All entries default to
        /// StreamClip mode using the WAV files shipped in <c>Audio/</c>, so the
        /// sample works without a Hapbeat Studio Kit on the device.
        /// </summary>
        private static HapbeatEventMap BuildOrLoadEventMap(string root)
        {
            string mapPath = $"{root}/EventMap/TutorialEventMap.asset";
            Directory.CreateDirectory(Path.GetDirectoryName(mapPath));

            var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(mapPath);
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<HapbeatEventMap>();
                AssetDatabase.CreateAsset(map, mapPath);
            }

            // Reset entries to a deterministic baseline. Users can edit afterwards.
            map.entries.Clear();

            string audioDir = $"{root}/Audio";

            map.entries.Add(MakeStreamEntry("pin_hit", "physics", "pin_hit", $"{audioDir}/drum_hit_1.wav", "*/pos_r_arm", loop: false));
            map.entries.Add(MakeStreamEntry("door_open", "door", "open", $"{audioDir}/ui_click.wav", "*/pos_neck", loop: false));
            map.entries.Add(MakeStreamEntry("door_close", "door", "close", $"{audioDir}/ui_click.wav", "*/pos_neck", loop: false));
            map.entries.Add(MakeStreamEntry("grab_start", "grab", "start", $"{audioDir}/grab.wav", "*/pos_r_arm", loop: false));

            // grab_loop has a parameter binding for runtime gain modulation by box motion.
            var grabLoop = MakeStreamEntry("grab_loop", "grab", "loop", $"{audioDir}/rain_loop.mp3", "*/pos_r_arm", loop: true);
            grabLoop.bindings.Add(new HapbeatBindingPreset
            {
                ownerObjectName = "PickupBox",
                sourceTransformPath = "",
                sourceProperty = BindingSourceProperty.PositionDeltaMagnitude,
                inputMin = 0f,
                inputMax = 0.5f,
                curveType = BindingCurveType.EaseOut,
                outputParameter = BindingOutputParameter.StreamGain,
                outputMin = 0.2f,
                outputMax = 1.5f,
            });
            map.entries.Add(grabLoop);

            map.entries.Add(MakeStreamEntry("grab_release", "grab", "release", $"{audioDir}/release.wav", "*/pos_r_arm", loop: false));
            map.entries.Add(MakeStreamEntry("stream_demo", "stream", "audio", $"{audioDir}/rain_loop.mp3", target: "", loop: true));
            map.entries.Add(MakeStreamEntry("slider_tick", "ui", "slider_tick", $"{audioDir}/ui_click.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("charge_release", "combat", "shot", $"{audioDir}/explosion.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("target_hit", "combat", "target_hit", $"{audioDir}/target_hit.mp3", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("manual_fire", "misc", "beep", $"{audioDir}/punch_impact.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("burst", "combat", "burst", $"{audioDir}/gunshot.wav", target: "", loop: false));

            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return map;
        }

        private static HapbeatEventEntry MakeStreamEntry(string displayName, string category, string eventName,
            string clipPath, string target, bool loop)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip == null)
                Debug.LogWarning($"[Tutorial] AudioClip not found: {clipPath}. Entry '{displayName}' will need a clip assigned manually.");

            return new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip,
                displayName = displayName,
                category = category,
                eventName = eventName,
                streamClip = clip,
                loop = loop,
                gain = 1.0f,
                target = target,
                group = -1,
            };
        }

        private static void BuildHud(GameObject router, GameObject player)
        {
            // Screen-space canvas for static HUD.
            var canvasGo = new GameObject("HUD");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();

            if (Object.FindFirstObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                var inputModuleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
                if (inputModuleType != null) es.AddComponent(inputModuleType);
                else es.AddComponent<StandaloneInputModule>();
            }

            var guide = AddText(canvasGo.transform, "Guide", "", new Vector2(20, -20), 14, anchor: new Vector2(0, 1), pivot: new Vector2(0, 1), size: new Vector2(360, 220), align: TextAnchor.UpperLeft);
            var conn = AddText(canvasGo.transform, "Connection", "Hapbeat: ?", new Vector2(-20, -20), 14, anchor: new Vector2(1, 1), pivot: new Vector2(1, 1), size: new Vector2(280, 28), align: TextAnchor.UpperRight);

            var pickerLabel = AddText(canvasGo.transform, "PickerLabel", "Target Picker", new Vector2(-20, -60), 14, anchor: new Vector2(1, 1), pivot: new Vector2(1, 1), size: new Vector2(280, 24), align: TextAnchor.UpperRight);

            // Toggle group for Both / Neck / Arm.
            var groupGo = new GameObject("PickerGroup");
            groupGo.transform.SetParent(canvasGo.transform, false);
            var grp = groupGo.AddComponent<ToggleGroup>();
            var groupRect = groupGo.AddComponent<RectTransform>();
            groupRect.anchorMin = new Vector2(1, 1); groupRect.anchorMax = new Vector2(1, 1);
            groupRect.pivot = new Vector2(1, 1);
            groupRect.anchoredPosition = new Vector2(-20, -90);
            groupRect.sizeDelta = new Vector2(280, 36);

            var bothToggle = AddToggle(groupGo.transform, "Both", new Vector2(-200, 0), grp, isOn: true);
            var neckToggle = AddToggle(groupGo.transform, "Neck", new Vector2(-100, 0), grp);
            var armToggle = AddToggle(groupGo.transform, "Arm", new Vector2(0, 0), grp);

            var pickerStatus = AddText(canvasGo.transform, "PickerStatus", "", new Vector2(-20, -130), 12, anchor: new Vector2(1, 1), pivot: new Vector2(1, 1), size: new Vector2(280, 24), align: TextAnchor.UpperRight);

            var pingResult = AddText(canvasGo.transform, "PingResult", "Ping: --", new Vector2(20, 20), 14, anchor: new Vector2(0, 0), pivot: new Vector2(0, 0), size: new Vector2(220, 24), align: TextAnchor.LowerLeft);

            // HudGuide on the canvas.
            var hud = canvasGo.AddComponent<HudGuide>();
            var hudSO = new SerializedObject(hud);
            hudSO.FindProperty("_guideText").objectReferenceValue = guide.GetComponent<Text>();
            hudSO.FindProperty("_connectionStatusText").objectReferenceValue = conn.GetComponent<Text>();
            hudSO.ApplyModifiedPropertiesWithoutUndo();

            // GlobalHotkeys + TargetPickerUI on the router.
            var hotkeys = router.AddComponent<GlobalHotkeys>();
            var hSO = new SerializedObject(hotkeys);
            hSO.FindProperty("_pingResultText").objectReferenceValue = pingResult.GetComponent<Text>();
            hSO.ApplyModifiedPropertiesWithoutUndo();

            var picker = router.AddComponent<TargetPickerUI>();
            var pSO = new SerializedObject(picker);
            pSO.FindProperty("_bothToggle").objectReferenceValue = bothToggle;
            pSO.FindProperty("_neckToggle").objectReferenceValue = neckToggle;
            pSO.FindProperty("_armToggle").objectReferenceValue = armToggle;
            pSO.FindProperty("_statusText").objectReferenceValue = pickerStatus.GetComponent<Text>();
            pSO.ApplyModifiedPropertiesWithoutUndo();

            // Wire bridge fields on the script-driven controllers (find by type to avoid hardcoded names).
            var bridge = router.GetComponent<TutorialBridge>();
            WireBridgeReferences(bridge, hotkeys, picker);
            WireScriptDrivenControllers(bridge);
        }

        private static void WireBridgeReferences(TutorialBridge bridge, GlobalHotkeys hotkeys, TargetPickerUI picker)
        {
            var hSO = new SerializedObject(hotkeys);
            hSO.FindProperty("_bridge").objectReferenceValue = bridge;
            hSO.ApplyModifiedPropertiesWithoutUndo();
            var pSO = new SerializedObject(picker);
            pSO.FindProperty("_bridge").objectReferenceValue = bridge;
            pSO.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void WireScriptDrivenControllers(TutorialBridge bridge)
        {
            foreach (var s in Object.FindObjectsByType<StreamDemoController>(FindObjectsSortMode.None))
            {
                var so = new SerializedObject(s);
                so.FindProperty("_bridge").objectReferenceValue = bridge;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var s in Object.FindObjectsByType<ChargeShooter>(FindObjectsSortMode.None))
            {
                var so = new SerializedObject(s);
                so.FindProperty("_bridge").objectReferenceValue = bridge;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ----------------------------------------------------------------
        // UI helpers
        // ----------------------------------------------------------------

        private static GameObject AddImage(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return go;
        }

        private static GameObject AddText(Transform parent, string name, string text, Vector2 pos, int fontSize,
            Vector2 anchor = default, Vector2 pivot = default, Vector2 size = default, TextAnchor align = TextAnchor.MiddleCenter)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            if (anchor == default) anchor = new Vector2(0.5f, 0.5f);
            if (pivot == default) pivot = new Vector2(0.5f, 0.5f);
            if (size == default) size = new Vector2(280f, 30f);
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var t = go.AddComponent<Text>();
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = align;
            t.color = Color.white;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return go;
        }

        private static Slider AddSlider(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var bgImg = go.AddComponent<Image>();
            bgImg.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
            var slider = go.AddComponent<Slider>();
            slider.minValue = 0f; slider.maxValue = 1f; slider.value = 1f;
            slider.targetGraphic = bgImg;

            // Fill area
            var fill = new GameObject("Fill");
            fill.transform.SetParent(go.transform, false);
            var fillRt = fill.AddComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0, 0); fillRt.anchorMax = new Vector2(1, 1);
            fillRt.offsetMin = Vector2.zero; fillRt.offsetMax = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = new Color(0.4f, 0.7f, 1f, 1f);
            slider.fillRect = fillRt;

            return slider;
        }

        private static Dropdown AddDropdown(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.color = new Color(0.3f, 0.3f, 0.3f, 1f);
            var dd = go.AddComponent<Dropdown>();
            dd.targetGraphic = img;

            // Caption text
            var capt = AddText(go.transform, "Label", "(no clips)", new Vector2(-90, 0), 14, align: TextAnchor.MiddleLeft);
            dd.captionText = capt.GetComponent<Text>();
            return dd;
        }

        private static Toggle AddToggle(Transform parent, string label, Vector2 pos, ToggleGroup group, bool isOn = false)
        {
            var go = new GameObject($"Toggle_{label}");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1, 0.5f);
            rt.anchorMax = new Vector2(1, 0.5f);
            rt.pivot = new Vector2(1, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(80, 28);

            var bgImg = go.AddComponent<Image>();
            bgImg.color = new Color(0.25f, 0.25f, 0.25f, 0.8f);

            var toggle = go.AddComponent<Toggle>();
            toggle.group = group;
            toggle.isOn = isOn;
            toggle.targetGraphic = bgImg;

            var checkmark = new GameObject("Checkmark");
            checkmark.transform.SetParent(go.transform, false);
            var cmRt = checkmark.AddComponent<RectTransform>();
            cmRt.anchorMin = Vector2.zero; cmRt.anchorMax = Vector2.one;
            cmRt.offsetMin = Vector2.zero; cmRt.offsetMax = Vector2.zero;
            var cmImg = checkmark.AddComponent<Image>();
            cmImg.color = new Color(0.4f, 0.7f, 1f, 0.6f);
            toggle.graphic = cmImg;

            AddText(go.transform, "Label", label, Vector2.zero, 14);
            return toggle;
        }

        private static void DestroyCollider(GameObject go)
        {
            // Lane is decorative; keep its collider so the ball rolls.
        }

        // ----------------------------------------------------------------
        // Path resolution
        // ----------------------------------------------------------------

        private static string FindTutorialRoot()
        {
            // Locate the Tutorial sample folder by finding TutorialBridge.cs.
            var guids = AssetDatabase.FindAssets("t:Script TutorialBridge");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                // Expected: ".../Tutorial/Scripts/TutorialBridge.cs"
                int scriptsIdx = p.LastIndexOf("/Scripts/");
                if (scriptsIdx >= 0)
                    return p.Substring(0, scriptsIdx);
            }
            return null;
        }
    }
}
#endif
