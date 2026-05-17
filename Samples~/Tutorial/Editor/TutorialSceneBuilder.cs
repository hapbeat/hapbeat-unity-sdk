#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Hapbeat;
using Hapbeat.Editor;
using Hapbeat.Samples.Tutorial;

namespace Hapbeat.Samples.Tutorial.EditorTools
{
    /// <summary>
    /// Scene generator for the Tutorial sample.
    ///
    /// Menu:
    ///   Hapbeat / Build Samples / 2. Tutorial (full scene)
    ///     → Creates two scenes: Tutorial.unity (触覚適用済み, "With" 版) and
    ///       Tutorial_Plain.unity (触覚なし, "Without" 版). The Plain version
    ///       is the starting point for the self-learning walkthrough; the
    ///       With version is the reference completed form.
    /// </summary>
    public static class TutorialSceneBuilder
    {
        private const string SCENE_FILE = "Tutorial.unity";
        private const string PLAIN_FILE = "Tutorial_Plain.unity";
        private const string EVENT_MAP_FILE = "TutorialEventMap.asset";
        private const string DOOR_AC_FILE = "DoorAnimator.controller";
        private const string KIT_NAME = "tutorial-kit";

        // Subfolders inside the imported sample root and inside the
        // per-sample HapbeatSDK destination — kept symmetric so the
        // Maintainer Sync menu can mirror them 1:1.
        private const string SCENES_SUBDIR    = "Scenes";
        private const string EVENT_MAP_SUBDIR = "EventMaps";
        private const string ANIMATION_SUBDIR = "Animation";
        private const string KIT_SUBDIR       = "Kit"; // Samples~ side ships a single Kit/ folder

        // Per-sample user-area root: Assets/HapbeatSDK/SDK_Samples/Tutorial/
        // (Scenes / EventMaps / Animation live here).
        // The SDK_Samples/ umbrella keeps SDK-shipped assets visually
        // separated from Studio-managed user content (HapbeatSDK/Kits,
        // HapbeatSDK/Scenes, HapbeatSDK/EventMaps).
        // Kits ALWAYS live at the shared Assets/HapbeatSDK/Kits/ root
        // (Studio convention) so HapbeatManifestIntensity finds them via
        // HapbeatKitsReadme.FindKitsRootPath().
        private const string HAPBEATSDK_SAMPLE_ROOT = "Assets/HapbeatSDK/SDK_Samples/Tutorial";

        // ----------------------------------------------------------------
        // Build menu
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Build Samples/2. Tutorial (full scene)", false, 61)]
        public static void Build()
        {
            string sampleRoot = FindTutorialRoot();
            if (sampleRoot == null)
            {
                EditorUtility.DisplayDialog("エラー",
                    "Tutorial サンプルのフォルダが見つかりません。\nPackage Manager から Tutorial サンプルを Import してください。",
                    "OK");
                return;
            }

            // Dual-mode:
            //   - Deploy mode: when the sample ships authored Scene + EventMap
            //     (committed in repo), the Build menu COPIES them into the
            //     user-editable Assets/HapbeatSDK/ area and rebakes references.
            //   - Scaffold mode: when those files don't exist (e.g. an SDK
            //     dev is bootstrapping the very first commit of the authored
            //     scene), the Build menu generates everything from primitives
            //     and writes it into the sample folder so it can be committed.
            string srcScene = $"{sampleRoot}/{SCENES_SUBDIR}/{SCENE_FILE}";
            bool hasAuthoredScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(srcScene) != null;
            if (hasAuthoredScene)
                DeployFromSampleToHapbeatSDK(sampleRoot);
            else
                ScaffoldIntoSample(sampleRoot);
        }

        // ----------------------------------------------------------------
        // Deploy mode — Copy authored assets from imported sample to
        // Assets/HapbeatSDK/{Scenes,EventMaps,Animation}/.
        // ----------------------------------------------------------------

        private static void DeployFromSampleToHapbeatSDK(string sampleRoot)
        {
            // Per-sample Scenes / EventMaps / Animation under HapbeatSDK/SDK_Samples/Tutorial/.
            string dstScenesDir = $"{HAPBEATSDK_SAMPLE_ROOT}/{SCENES_SUBDIR}";
            string dstMapDir    = $"{HAPBEATSDK_SAMPLE_ROOT}/{EVENT_MAP_SUBDIR}";
            string dstAnimDir   = $"{HAPBEATSDK_SAMPLE_ROOT}/{ANIMATION_SUBDIR}";
            HapbeatSampleDeployment.EnsureAssetFolder(dstScenesDir);
            HapbeatSampleDeployment.EnsureAssetFolder(dstMapDir);
            HapbeatSampleDeployment.EnsureAssetFolder(dstAnimDir);

            // Kit goes to the shared HapbeatSDK/Kits/ root (Studio convention).
            string kitsRoot = HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
            string dstKitDir = $"{kitsRoot}/{KIT_NAME}";

            string srcScene = $"{sampleRoot}/{SCENES_SUBDIR}/{SCENE_FILE}";
            string srcPlain = $"{sampleRoot}/{SCENES_SUBDIR}/{PLAIN_FILE}";
            string srcMap   = $"{sampleRoot}/{EVENT_MAP_SUBDIR}/{EVENT_MAP_FILE}";
            string srcAC    = $"{sampleRoot}/{ANIMATION_SUBDIR}/{DOOR_AC_FILE}";
            string srcKit   = $"{sampleRoot}/{KIT_SUBDIR}";

            string dstScene = $"{dstScenesDir}/{SCENE_FILE}";
            string dstPlain = $"{dstScenesDir}/{PLAIN_FILE}";
            string dstMap   = $"{dstMapDir}/{EVENT_MAP_FILE}";
            string dstAC    = $"{dstAnimDir}/{DOOR_AC_FILE}";

            if (!EditorUtility.DisplayDialog(
                "Tutorial を展開",
                "サンプル同梱の Tutorial 資産を Assets/HapbeatSDK/ 配下にコピーします。\n" +
                $"  - {dstScene} (With 版)\n" +
                $"  - {dstPlain} (Without 版)\n" +
                $"  - {dstMap}\n" +
                $"  - {dstAC}\n" +
                $"  - {dstKitDir}/ (manifest + clips)\n\n" +
                "既存のコピーは上書きされます。",
                "展開する", "キャンセル"))
                return;

            // Kit: raw file copy (fresh GUIDs for the copy under HapbeatSDK/Kits/).
            HapbeatSampleDeployment.CopyKitFolder(srcKit, dstKitDir);

            var scenes    = new List<HapbeatSampleDeployment.AssetCopy>
            {
                new HapbeatSampleDeployment.AssetCopy { sourcePath = srcScene, destPath = dstScene },
                new HapbeatSampleDeployment.AssetCopy { sourcePath = srcPlain, destPath = dstPlain },
            };
            var eventMaps = new List<HapbeatSampleDeployment.AssetCopy>
            {
                new HapbeatSampleDeployment.AssetCopy { sourcePath = srcMap, destPath = dstMap },
            };
            var acs       = new List<HapbeatSampleDeployment.AssetCopy>
            {
                new HapbeatSampleDeployment.AssetCopy { sourcePath = srcAC, destPath = dstAC },
            };

            var result = HapbeatSampleDeployment.DeployScene(scenes, eventMaps, acs);
            if (!string.IsNullOrEmpty(result.primaryScenePath))
                EditorSceneManager.OpenScene(result.primaryScenePath, OpenSceneMode.Single);

            EditorUtility.DisplayDialog("完了",
                "Tutorial を展開しました:\n" +
                $"  Scene (With) : {dstScene}\n" +
                $"  Scene (Plain): {dstPlain}\n" +
                $"  EventMap     : {dstMap}\n" +
                $"  AnimatorCtrl : {dstAC}\n\n" +
                "AudioClip はサンプルフォルダのまま参照されます (コピーしません)。\n" +
                "Play で WASD 移動・各ゾーンを試してみてください。",
                "OK");
        }

        // ----------------------------------------------------------------
        // Scaffold mode — Generate Scene + EventMap + AnimatorController from
        // primitives, writing into the sample folder so the SDK developer
        // can commit them as the authored shipped version.
        // ----------------------------------------------------------------

        private static void ScaffoldIntoSample(string sampleRoot)
        {
            // Scaffold and Deploy must produce the same end state so the
            // EventMap's intensity lookup works either way:
            //   - Kit goes to HapbeatSDK/Kits/<kit-name>/ (Studio convention,
            //     discovered by HapbeatManifestIntensity)
            //   - Scenes / EventMaps / Animation go under HapbeatSDK/SDK_Samples/Tutorial/
            //
            // Unity's AssetDatabase cannot write into Samples~/ (tilde marks
            // it non-Asset), so the SDK developer verifies in Play mode here,
            // then runs
            //   Hapbeat → Maintainers → Sync HapbeatSDK → Samples~ (Tutorial)
            // to publish the result into Samples~/Tutorial/ for commit.
            string kitsRoot    = HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
            string dstKitDir   = $"{kitsRoot}/{KIT_NAME}";
            string scenesDir   = $"{HAPBEATSDK_SAMPLE_ROOT}/{SCENES_SUBDIR}";
            string eventMapDir = $"{HAPBEATSDK_SAMPLE_ROOT}/{EVENT_MAP_SUBDIR}";
            string animDir     = $"{HAPBEATSDK_SAMPLE_ROOT}/{ANIMATION_SUBDIR}";

            if (!EditorUtility.DisplayDialog(
                "Tutorial シーンを scaffold",
                "サンプルに同梱されている Tutorial シーンが見つからないため、Assets/HapbeatSDK/ 配下に初期生成します。\n" +
                "(通常は SDK 開発者がコミット前の bootstrap として 1 回だけ実行する操作)\n\n" +
                $"  - {dstKitDir}/ (Kit, manifest + clips)\n" +
                $"  - {scenesDir}/{SCENE_FILE} (With 版・触覚適用済み)\n" +
                $"  - {scenesDir}/{PLAIN_FILE} (Without 版・walkthrough 起点)\n" +
                $"  - {eventMapDir}/{EVENT_MAP_FILE}\n" +
                $"  - {animDir}/{DOOR_AC_FILE}\n\n" +
                "現在のシーンの未保存の変更は失われます。\n" +
                "Play で動作確認した後、Hapbeat → Maintainers → Sync HapbeatSDK → Samples~ (Tutorial) を実行してください。",
                "生成する", "キャンセル"))
                return;

            HapbeatSampleDeployment.EnsureAssetFolder(scenesDir);
            HapbeatSampleDeployment.EnsureAssetFolder(eventMapDir);
            HapbeatSampleDeployment.EnsureAssetFolder(animDir);

            // Kit (raw file copy, fresh GUIDs). Tutorial Audio/ stays
            // referenced from the imported sample folder — only the Kit
            // (manifest.json + empty install-clips/ + stream-clips/) gets
            // copied so HapbeatManifestIntensity can resolve intensity.
            HapbeatSampleDeployment.CopyKitFolder($"{sampleRoot}/{KIT_SUBDIR}", dstKitDir);

            // Scene first. EditorSceneManager.NewScene triggers
            // Resources.UnloadUnusedAssets, which would invalidate any
            // ScriptableObject reference held only by a local C# variable —
            // so we must NOT build the EventMap before this point.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // EventMap (built after NewScene so the reference stays alive).
            string mapPath = $"{eventMapDir}/{EVENT_MAP_FILE}";
            var eventMap = BuildOrLoadEventMap(mapPath, sampleRoot);

            // Auxiliary assets that the scene references.
            string acPath = $"{animDir}/{DOOR_AC_FILE}";
            var doorAnimator = BuildOrLoadDoorAnimatorController(acPath);

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
            BuildDoor(z2, doorAnimator);
            BuildPickupBox(z3);
            BuildStreamConsole(z4);
            BuildTargetRange(z5);

            // Player + camera + FPS controller (also wires camera-dependent
            // gameplay refs like BallLauncher._aimReference and a HoldAnchor
            // child for PickupBox to follow).
            var player = BuildPlayer();
            WireCameraDependentReferences(player);

            // [Hapbeat Event Router] (this is the part removed by Strip).
            var router = BuildRouter(eventMap);

            // World-space HUD and Picker UI.
            BuildHud(router, player);

            // Attach Hapbeat triggers / bindings to the scene objects so the
            // With version is the "completed form" (Plain is generated by
            // stripping these below).
            AttachTriggersToScene(eventMap);

            // Save the With version first.
            string scenePath = $"{scenesDir}/{SCENE_FILE}";
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();

            // Generate the Without (Plain) version by stripping all Hapbeat
            // components & router GameObject from the in-memory scene, then
            // saving as Tutorial_Plain.unity. The walkthrough starts from
            // this Plain copy and rebuilds toward the With version.
            int strippedComponents = 0;
            int strippedGameObjects = 0;

            // First, remove UnityEvent persistent listeners that reference
            // Hapbeat triggers — otherwise Plain's Slider.onValueChanged /
            // TargetReceiver.OnHit would carry a "Missing" entry after the
            // trigger components are destroyed below.
            foreach (var slider in Object.FindObjectsByType<Slider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                for (int i = slider.onValueChanged.GetPersistentEventCount() - 1; i >= 0; i--)
                {
                    var tgt = slider.onValueChanged.GetPersistentTarget(i);
                    if (tgt is HapbeatTriggerBase)
                        UnityEventTools.RemovePersistentListener(slider.onValueChanged, i);
                }
            }
            foreach (var receiver in Object.FindObjectsByType<TargetReceiver>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (receiver.OnHit == null) continue;
                for (int i = receiver.OnHit.GetPersistentEventCount() - 1; i >= 0; i--)
                {
                    var tgt = receiver.OnHit.GetPersistentTarget(i);
                    if (tgt is HapbeatTriggerBase)
                        UnityEventTools.RemovePersistentListener(receiver.OnHit, i);
                }
            }

            foreach (var t in Object.FindObjectsByType<HapbeatTriggerBase>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(t);
                strippedComponents++;
            }
            foreach (var b in Object.FindObjectsByType<HapbeatParameterBinding>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(b);
                strippedComponents++;
            }
            foreach (var br in Object.FindObjectsByType<HapbeatBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(br);
                strippedComponents++;
            }
            foreach (var m in Object.FindObjectsByType<HapbeatManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(m.gameObject);
                strippedGameObjects++;
            }
            string plainPath = $"{scenesDir}/{PLAIN_FILE}";
            EditorSceneManager.SaveScene(scene, plainPath);

            // Reload the With version so the user lands on the master scene
            // (not the Plain copy) after the build finishes.
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            EditorUtility.DisplayDialog("完了",
                "Tutorial を Assets/HapbeatSDK/ に scaffold しました:\n" +
                $"  Kit          : {dstKitDir}/\n" +
                $"  Scene (With) : {scenePath}\n" +
                $"  Scene (Plain): {plainPath}\n" +
                $"  EventMap     : {mapPath}\n" +
                $"  AnimatorCtrl : {acPath}\n\n" +
                "Play で動作確認した後、Hapbeat → Maintainers → Sync HapbeatSDK → Samples~ (Tutorial) を実行して repo にコミットしてください。",
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

        private static void BuildDoor(Transform parent, RuntimeAnimatorController controller)
        {
            parent.position = new Vector3(-4f, 0f, 6f);

            // A simple door: hinged cube. The AnimatorController is created
            // by BuildOrLoadDoorAnimatorController so DoorController.SetBool
            // actually drives a parameter (HapbeatAnimatorTrigger needs the
            // bool parameter to exist for edge detection to fire).
            var door = GameObject.CreatePrimitive(PrimitiveType.Cube);
            door.name = "Door";
            door.transform.SetParent(parent, false);
            door.transform.localPosition = new Vector3(0f, 1f, 0f);
            door.transform.localScale = new Vector3(1.5f, 2f, 0.1f);
            var rb = door.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = true;
            var animator = door.AddComponent<Animator>();
            if (controller != null) animator.runtimeAnimatorController = controller;
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
                // Use the dedicated setup method — SerializedObject doesn't
                // reliably traverse to parent-class protected fields in
                // Unity 6 (same root cause as BasicExample fix#1).
                bridge.EditorSetupEventMap(eventMap);
                EditorUtility.SetDirty(bridge);
            }
            return go;
        }

        // ----------------------------------------------------------------
        // Door AnimatorController auto-generation
        // ----------------------------------------------------------------

        /// <summary>
        /// Create (or load) a minimal AnimatorController at
        /// <c>{sampleRoot}/Animation/DoorAnimator.controller</c> that exposes
        /// a bool parameter "IsOpen". DoorController.SetBool drives the
        /// parameter; HapbeatAnimatorTrigger watches the parameter for
        /// BoolBecameTrue / BoolBecameFalse edges. No state machine is
        /// required for haptic edge detection.
        /// </summary>
        private static AnimatorController BuildOrLoadDoorAnimatorController(string path)
        {
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (ac == null)
                ac = AnimatorController.CreateAnimatorControllerAtPath(path);

            bool hasIsOpen = false;
            foreach (var p in ac.parameters)
            {
                if (p.name == "IsOpen") { hasIsOpen = true; break; }
            }
            if (!hasIsOpen)
                ac.AddParameter("IsOpen", AnimatorControllerParameterType.Bool);

            EditorUtility.SetDirty(ac);
            return ac;
        }

        // ----------------------------------------------------------------
        // Camera-dependent gameplay refs (BallLauncher aim / Pickup hold)
        // ----------------------------------------------------------------

        /// <summary>
        /// Wires the player Camera into gameplay components that need it:
        ///   - BallLauncher._aimReference (so the ball launches in the
        ///     direction the player is looking).
        ///   - A new HoldAnchor (Camera child) consumed by
        ///     PickupBoxController._holdAnchor (so the picked-up box
        ///     follows the camera at a fixed offset).
        /// Called after BuildPlayer because both endpoints are camera
        /// children that only exist once the player is in the scene.
        /// </summary>
        private static void WireCameraDependentReferences(GameObject player)
        {
            var cam = player != null ? player.GetComponentInChildren<Camera>(true) : null;
            if (cam == null) return;

            // HoldAnchor — child of camera, ~0.7m in front of the eye line.
            var anchor = new GameObject("HoldAnchor").transform;
            anchor.SetParent(cam.transform, false);
            anchor.localPosition = new Vector3(0f, -0.2f, 0.7f);

            foreach (var launcher in Object.FindObjectsByType<BallLauncher>(FindObjectsSortMode.None))
            {
                var so = new SerializedObject(launcher);
                so.FindProperty("_aimReference").objectReferenceValue = cam.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            foreach (var box in Object.FindObjectsByType<PickupBoxController>(FindObjectsSortMode.None))
            {
                var so = new SerializedObject(box);
                so.FindProperty("_holdAnchor").objectReferenceValue = anchor;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        // ----------------------------------------------------------------
        // Hapbeat trigger attachment (With version only — stripped from Plain)
        // ----------------------------------------------------------------

        /// <summary>
        /// Walk the freshly-built scene and attach Hapbeat triggers / bindings
        /// so Tutorial.unity is a playable "completed form". Tutorial_Plain.unity
        /// is generated by stripping every Hapbeat component back out.
        /// </summary>
        private static void AttachTriggersToScene(HapbeatEventMap map)
        {
            if (map == null)
            {
                Debug.LogError("[Tutorial] AttachTriggersToScene called with null map.");
                return;
            }

            // Z1: HapbeatCollisionTrigger on each Pin (velocity-scaled).
            foreach (var pin in FindGameObjectsWithNamePrefix("Pin_"))
                AttachCollisionTrigger(pin, map, "pin_hit");

            // Z2: HapbeatAnimatorTrigger ×2 on the Door.
            var door = FindGameObjectByName("Door");
            if (door != null)
            {
                AttachAnimatorTrigger(door, map, "door_open",
                    HapbeatAnimatorTrigger.Condition.BoolBecameTrue);
                AttachAnimatorTrigger(door, map, "door_close",
                    HapbeatAnimatorTrigger.Condition.BoolBecameFalse);
            }

            // Z3: HapbeatSequenceTrigger + HapbeatParameterBinding on PickupBox.
            var pickup = FindGameObjectByName("PickupBox");
            if (pickup != null)
            {
                var seq = AttachSequenceTrigger(pickup, map,
                    loopEntryName: "grab_loop",
                    onStartEntryName: "grab_start",
                    onStopEntryName: "grab_release");
                // Wire PickupBoxController._sequence to the trigger we just added.
                var ctrl = pickup.GetComponent<PickupBoxController>();
                if (ctrl != null && seq != null)
                {
                    var so = new SerializedObject(ctrl);
                    so.FindProperty("_sequence").objectReferenceValue = seq;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                AttachLoopBinding(pickup, map, "grab_loop");
            }

            // Z4: HapbeatTickEmitter on Gain/Pan sliders, wire onValueChanged.
            var gainSlider = FindComponentByGameObjectName<Slider>("GainSlider");
            var panSlider  = FindComponentByGameObjectName<Slider>("PanSlider");
            if (gainSlider != null) AttachSliderTickEmitter(gainSlider, map, "slider_tick");
            if (panSlider  != null) AttachSliderTickEmitter(panSlider,  map, "slider_tick");

            // Z5: HapbeatUnityEventTrigger on TargetBoard, wire TargetReceiver.OnHit.
            var board = FindGameObjectByName("TargetBoard");
            if (board != null)
            {
                var trig = AttachUnityEventTrigger(board, map, "target_hit");
                var receiver = board.GetComponent<TargetReceiver>();
                if (receiver != null && trig != null)
                {
                    UnityEventTools.AddPersistentListener(receiver.OnHit, trig.Fire);
                    EditorUtility.SetDirty(receiver);
                }
            }
        }

        // -- Attach helpers ------------------------------------------------

        private static (HapbeatEventEntry entry, int index) FindEntry(HapbeatEventMap map, string displayName)
        {
            for (int i = 0; i < map.entries.Count; i++)
            {
                if (map.entries[i].displayName == displayName)
                    return (map.entries[i], i);
            }
            Debug.LogWarning($"[Tutorial] EventMap entry '{displayName}' not found.");
            return (null, -1);
        }

        private static void AttachCollisionTrigger(GameObject host, HapbeatEventMap map, string entryName)
        {
            var (entry, idx) = FindEntry(map, entryName);
            if (entry == null) return;
            var trig = host.AddComponent<HapbeatCollisionTrigger>();
            trig.EditorSetupEntry(map, entry.id, idx);
            var so = new SerializedObject(trig);
            so.FindProperty("_triggerEvent").enumValueIndex = (int)HapbeatCollisionTrigger.TriggerEvent.CollisionEnter;
            so.FindProperty("_gainMode").enumValueIndex = (int)HapbeatCollisionTrigger.GainMode.VelocityScaled;
            so.FindProperty("_velocityThreshold").floatValue = 0.5f;
            so.FindProperty("_maxVelocity").floatValue = 5f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(trig);
        }

        private static void AttachAnimatorTrigger(GameObject host, HapbeatEventMap map, string entryName,
            HapbeatAnimatorTrigger.Condition condition)
        {
            var (entry, idx) = FindEntry(map, entryName);
            if (entry == null) return;
            var trig = host.AddComponent<HapbeatAnimatorTrigger>();
            trig.EditorSetupEntry(map, entry.id, idx);
            var so = new SerializedObject(trig);
            so.FindProperty("_parameterName").stringValue = "IsOpen";
            so.FindProperty("_condition").enumValueIndex = (int)condition;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(trig);
        }

        private static HapbeatSequenceTrigger AttachSequenceTrigger(GameObject host, HapbeatEventMap map,
            string loopEntryName, string onStartEntryName, string onStopEntryName)
        {
            var (loopEntry,  loopIdx)  = FindEntry(map, loopEntryName);
            var (startEntry, startIdx) = FindEntry(map, onStartEntryName);
            var (stopEntry,  stopIdx)  = FindEntry(map, onStopEntryName);
            if (loopEntry == null) return null;

            var trig = host.AddComponent<HapbeatSequenceTrigger>();
            trig.EditorSetupEntry(map, loopEntry.id, loopIdx);

            var so = new SerializedObject(trig);
            if (startEntry != null)
            {
                so.FindProperty("_onStartEntryId").stringValue = startEntry.id;
                so.FindProperty("_onStartEntryIndex").intValue = startIdx;
            }
            if (stopEntry != null)
            {
                so.FindProperty("_onStopEntryId").stringValue = stopEntry.id;
                so.FindProperty("_onStopEntryIndex").intValue = stopIdx;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(trig);
            return trig;
        }

        private static void AttachLoopBinding(GameObject host, HapbeatEventMap map, string entryName)
        {
            var (entry, _) = FindEntry(map, entryName);
            if (entry == null || entry.bindings == null || entry.bindings.Count == 0) return;

            var preset = entry.bindings[0]; // grab_loop ships exactly one preset.
            var binding = host.AddComponent<HapbeatParameterBinding>();
            var so = new SerializedObject(binding);
            // Link mode (live read-through from the preset).
            so.FindProperty("_linkedEventMap").objectReferenceValue = map;
            so.FindProperty("_linkedBindingId").stringValue = preset.id;
            // Mirror the preset values to the local fields too so the
            // standalone-mode fallback still behaves sensibly if the link
            // is ever broken.
            so.FindProperty("_sourceProperty").enumValueIndex = (int)preset.sourceProperty;
            so.FindProperty("_inputMin").floatValue = preset.inputMin;
            so.FindProperty("_inputMax").floatValue = preset.inputMax;
            so.FindProperty("_curveType").enumValueIndex = (int)preset.curveType;
            so.FindProperty("_outputParameter").enumValueIndex = (int)preset.outputParameter;
            so.FindProperty("_outputMin").floatValue = preset.outputMin;
            so.FindProperty("_outputMax").floatValue = preset.outputMax;
            // _sourceTransform stays null → falls back to host transform,
            // which is the PickupBox itself (PositionDeltaMagnitude reads
            // host world-position delta).
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(binding);
        }

        private static void AttachSliderTickEmitter(Slider slider, HapbeatEventMap map, string entryName)
        {
            var (entry, idx) = FindEntry(map, entryName);
            if (entry == null) return;
            var trig = slider.gameObject.AddComponent<HapbeatTickEmitter>();
            trig.EditorSetupEntry(map, entry.id, idx);
            var so = new SerializedObject(trig);
            so.FindProperty("_tickThreshold").floatValue = 0.05f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(trig);
            // Slider.onValueChanged(float) → HapbeatTickEmitter.Fire(float)
            UnityAction<float> call = trig.Fire;
            UnityEventTools.AddPersistentListener(slider.onValueChanged, call);
            EditorUtility.SetDirty(slider);
        }

        private static HapbeatUnityEventTrigger AttachUnityEventTrigger(GameObject host, HapbeatEventMap map, string entryName)
        {
            var (entry, idx) = FindEntry(map, entryName);
            if (entry == null) return null;
            var trig = host.AddComponent<HapbeatUnityEventTrigger>();
            trig.EditorSetupEntry(map, entry.id, idx);
            EditorUtility.SetDirty(trig);
            return trig;
        }

        // -- Scene-walk helpers --------------------------------------------

        private static IEnumerable<GameObject> FindGameObjectsWithNamePrefix(string prefix)
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in all)
            {
                if (t != null && t.name.StartsWith(prefix))
                    yield return t.gameObject;
            }
        }

        private static GameObject FindGameObjectByName(string name)
        {
            var all = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in all)
            {
                if (t != null && t.name == name) return t.gameObject;
            }
            return null;
        }

        private static T FindComponentByGameObjectName<T>(string name) where T : Component
        {
            var go = FindGameObjectByName(name);
            return go != null ? go.GetComponent<T>() : null;
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
        private static HapbeatEventMap BuildOrLoadEventMap(string mapPath, string sampleRoot)
        {
            var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(mapPath);
            if (map == null)
            {
                map = ScriptableObject.CreateInstance<HapbeatEventMap>();
                AssetDatabase.CreateAsset(map, mapPath);
            }

            // Reset entries to a deterministic baseline. Users can edit afterwards.
            map.entries.Clear();

            // Audio WAVs ship inside the same sample folder.
            string audioDir = $"{sampleRoot}/Audio";

            // All entries use category = KIT_NAME ("tutorial-kit") so the
            // composed eventId becomes "tutorial-kit.<displayName>", matching
            // the manifest.json shipped in Samples~/Tutorial/Kit/.
            map.entries.Add(MakeStreamEntry("pin_hit", KIT_NAME, "pin_hit", $"{audioDir}/drum_hit_1.wav", "*/pos_r_arm", loop: false));
            map.entries.Add(MakeStreamEntry("door_open", KIT_NAME, "door_open", $"{audioDir}/ui_click.wav", "*/pos_neck", loop: false));
            map.entries.Add(MakeStreamEntry("door_close", KIT_NAME, "door_close", $"{audioDir}/ui_click.wav", "*/pos_neck", loop: false));
            map.entries.Add(MakeStreamEntry("grab_start", KIT_NAME, "grab_start", $"{audioDir}/grab.wav", "*/pos_r_arm", loop: false));

            // grab_loop has a parameter binding for runtime gain modulation by box motion.
            var grabLoop = MakeStreamEntry("grab_loop", KIT_NAME, "grab_loop", $"{audioDir}/rain_loop.mp3", "*/pos_r_arm", loop: true);
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

            map.entries.Add(MakeStreamEntry("grab_release", KIT_NAME, "grab_release", $"{audioDir}/release.wav", "*/pos_r_arm", loop: false));
            map.entries.Add(MakeStreamEntry("stream_demo", KIT_NAME, "stream_demo", $"{audioDir}/rain_loop.mp3", target: "", loop: true));
            map.entries.Add(MakeStreamEntry("slider_tick", KIT_NAME, "slider_tick", $"{audioDir}/ui_click.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("charge_release", KIT_NAME, "charge_release", $"{audioDir}/explosion.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("target_hit", KIT_NAME, "target_hit", $"{audioDir}/target_hit.mp3", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("manual_fire", KIT_NAME, "manual_fire", $"{audioDir}/punch_impact.wav", target: "", loop: false));
            map.entries.Add(MakeStreamEntry("burst", KIT_NAME, "burst", $"{audioDir}/gunshot.wav", target: "", loop: false));

            // Materialize stable ids before AttachTriggersToScene reads them.
            foreach (var e in map.entries) { var _ = e.id; }
            foreach (var e in map.entries)
            {
                if (e.bindings == null) continue;
                foreach (var b in e.bindings) { if (b != null) { var __ = b.id; } }
            }

            // Manifest intensity cache populate. Tutorial ships no Kit, so
            // TryGetIntensity typically fails — fall back to 1.0 so the
            // runtime "no cached manifest intensity" warning doesn't fire
            // and gain semantics stay equivalent to plain entry.gain.
            HapbeatManifestIntensity.Invalidate();
            foreach (var e in map.entries)
            {
                if (HapbeatManifestIntensity.TryGetIntensity(e, out float intensity))
                    e.SetCachedManifestIntensity(intensity);
                else
                    e.SetCachedManifestIntensity(1.0f);
            }

            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssets();
            // Do NOT call AssetDatabase.Refresh() here — it can invalidate
            // in-memory references to the just-created map (see BasicExample
            // fix history c16cb8f / 1df0820).
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
