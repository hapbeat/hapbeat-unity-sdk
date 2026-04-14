#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Hapbeat;

namespace Hapbeat.Samples.Editor
{
    /// <summary>
    /// PlayerDemo の各ゾーンシーンを自動生成する Editor スクリプト。
    /// ゾーンごとに独立したシーンを生成し、SceneMenu で即座に切り替える構成。
    ///
    /// Menu:
    ///   Hapbeat > Build Samples > 2a. Player Demo - All Scenes（全シーン一括生成）
    ///   Hapbeat > Build Samples > 2b. Player Demo - Hub Only
    ///   Hapbeat > Build Samples > 2c. Player Demo - Zone A Only
    ///   ...
    /// </summary>
    public static class PlayerDemoSceneBuilder
    {
        // ======================================================================
        // Menu Items
        // ======================================================================

        [MenuItem("Hapbeat/Build Samples/2a. Player Demo - All Scenes", false, 101)]
        public static void BuildAll()
        {
            if (!EditorUtility.DisplayDialog("PlayerDemo 全シーン生成",
                "Hub + Zone A/B/C/D の5シーンを生成します。", "生成する", "キャンセル"))
                return;

            var eventMap = CreatePlayerDemoEventMap();
            BuildHub();
            BuildZoneA(eventMap);
            BuildZoneB(eventMap);
            BuildZoneC(eventMap);
            BuildZoneD();

            EditorUtility.DisplayDialog("完了",
                "PlayerDemo の5シーンを生成しました。\n\n" +
                "残りの手動作業:\n" +
                "1. 各シーンの「>>> XR Origin <<<」を XR Origin (XR Rig) プレハブに置換\n" +
                "2. Build Settings に全シーンを登録\n" +
                "3. Drone/Projectile Prefab を作成（Zone B）\n" +
                "4. 見た目を調整", "OK");
        }

        [MenuItem("Hapbeat/Build Samples/2b. Player Demo - Hub", false, 102)]
        public static void BuildHubMenu()
        {
            BuildHub();
            EditorUtility.DisplayDialog("完了", "PlayerDemoHub シーンを生成しました。", "OK");
        }

        [MenuItem("Hapbeat/Build Samples/2c. Player Demo - Zone A", false, 103)]
        public static void BuildZoneAMenu()
        {
            var eventMap = CreatePlayerDemoEventMap();
            BuildZoneA(eventMap);
            EditorUtility.DisplayDialog("完了", "PlayerDemoZoneA シーンを生成しました。", "OK");
        }

        [MenuItem("Hapbeat/Build Samples/2d. Player Demo - Zone B", false, 104)]
        public static void BuildZoneBMenu()
        {
            var eventMap = CreatePlayerDemoEventMap();
            BuildZoneB(eventMap);
            EditorUtility.DisplayDialog("完了", "PlayerDemoZoneB シーンを生成しました。", "OK");
        }

        [MenuItem("Hapbeat/Build Samples/2e. Player Demo - Zone C", false, 105)]
        public static void BuildZoneCMenu()
        {
            var eventMap = CreatePlayerDemoEventMap();
            BuildZoneC(eventMap);
            EditorUtility.DisplayDialog("完了", "PlayerDemoZoneC シーンを生成しました。", "OK");
        }

        [MenuItem("Hapbeat/Build Samples/2f. Player Demo - Zone D", false, 106)]
        public static void BuildZoneDMenu()
        {
            BuildZoneD();
            EditorUtility.DisplayDialog("完了", "PlayerDemoZoneD シーンを生成しました。", "OK");
        }

        // ======================================================================
        // Scene: Hub
        // ======================================================================

        static void BuildHub()
        {
            var scene = NewScene();

            // 環境
            CreateFloor(new Color(0.25f, 0.25f, 0.3f));
            CreateXRPlaceholder(Vector3.zero);

            // Hapbeat Event Router
            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();

            // ゾーン選択メニュー（目の前に大きなパネル）
            var menuPanel = CreateMenuPanel("Zone Select", new Vector3(0, 1.5f, 2f), true);

            // 説明テキスト
            CreateWorldLabel(null, "Welcome",
                new Vector3(0, 2.5f, 2f), 0.008f,
                "Hapbeat Player Demo\n\n各ゾーンを選択して触覚フィードバックを体験");

            SaveScene("PlayerDemoHub");
        }

        // ======================================================================
        // Scene: Zone A (Active Feedback)
        // ======================================================================

        static void BuildZoneA(HapbeatEventMap eventMap)
        {
            var scene = NewScene();
            CreateFloor(new Color(0.35f, 0.2f, 0.2f));
            CreateXRPlaceholder(new Vector3(0, 0, -2));

            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();
            router.AddComponent<DemoManager>();

            // ナビゲーションメニュー（手元サイズ）
            CreateMenuPanel("Navigation", new Vector3(-1.5f, 1f, 0), false);

            // ゾーンラベル
            CreateWorldLabel(null, "Zone Label", new Vector3(0, 3f, 3f), 0.012f,
                "Zone A: Active Feedback\nパンチ・射撃・ドラム");

            // --- パンチングバッグ ---
            var bag = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            bag.name = "PunchingBag";
            bag.transform.position = new Vector3(-2, 1, 2);
            bag.transform.localScale = new Vector3(0.5f, 1f, 0.5f);
            SetColor(bag, new Color(0.7f, 0.3f, 0.2f));

            var bagRb = bag.AddComponent<Rigidbody>();
            bagRb.mass = 5f;
            bagRb.constraints = RigidbodyConstraints.FreezePositionY;

            var punchScript = bag.AddComponent<PunchingBag>();
            var bagAudio = bag.AddComponent<AudioSource>();
            bagAudio.playOnAwake = false;
            AutoConnectAudio(punchScript, "_impactClip", "punch_impact");
            SetObjectReference(punchScript, "_audioSource", bagAudio);

            var bagTrigger = bag.AddComponent<HapbeatCollisionTrigger>();
            SetTriggerBase(bagTrigger, eventMap, 0);
            SetCollisionTriggerVelocityScaled(bagTrigger);

            CreateWorldLabel(null, "Punch Label", new Vector3(-2, 2.8f, 2), 0.006f,
                "パンチングバッグ\n殴ると速度連動で振動");

            // --- シューティングレンジ ---
            var gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gun.name = "Gun";
            gun.transform.position = new Vector3(0, 1, 0);
            gun.transform.localScale = new Vector3(0.05f, 0.12f, 0.3f);
            SetColor(gun, new Color(0.2f, 0.2f, 0.25f));

            var gunRb = gun.AddComponent<Rigidbody>();
            gunRb.mass = 0.5f;
            TryAddXRGrabInteractable(gun);

            var shooter = gun.AddComponent<ShootingRange>();
            var gunAudio = gun.AddComponent<AudioSource>();
            gunAudio.playOnAwake = false;

            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(gun.transform);
            muzzle.transform.localPosition = new Vector3(0, 0, 0.2f);
            var shooterSO = new SerializedObject(shooter);
            shooterSO.FindProperty("_muzzle").objectReferenceValue = muzzle.transform;
            shooterSO.FindProperty("_audioSource").objectReferenceValue = gunAudio;
            shooterSO.ApplyModifiedPropertiesWithoutUndo();
            AutoConnectAudio(shooter, "_shotClip", "gunshot");

            var shootTrigger = router.AddComponent<HapbeatUnityEventTrigger>();
            SetTriggerBase(shootTrigger, eventMap, 1);
            ConnectUnityEvent(shooter, "OnShoot", shootTrigger, "Fire");

            // Targets
            for (int i = 0; i < 3; i++)
            {
                var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
                target.name = $"Target_{i + 1}";
                target.transform.position = new Vector3(-1.5f + i * 1.5f, 1.5f, 5);
                target.transform.localScale = new Vector3(0.6f, 0.8f, 0.1f);
                SetColor(target, new Color(1f, 0.9f, 0.3f));
                var targetRb = target.AddComponent<Rigidbody>();
                targetRb.isKinematic = true;
                var targetTrigger = target.AddComponent<HapbeatCollisionTrigger>();
                SetTriggerBase(targetTrigger, eventMap, 2);
            }

            CreateWorldLabel(null, "Shoot Label", new Vector3(0, 2.8f, 5), 0.006f,
                "シューティングレンジ\n銃を掴んで射撃→反動");

            // --- ドラムステーション ---
            Color[] drumColors = { Color.red, Color.blue, Color.green, Color.yellow };
            for (int i = 0; i < 4; i++)
            {
                var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pad.name = $"DrumPad_{i + 1}";
                pad.transform.position = new Vector3(1.4f + i * 0.4f, 0.9f, 2);
                pad.transform.localScale = new Vector3(0.3f, 0.02f, 0.3f);
                SetColor(pad, drumColors[i]);

                var drumScript = pad.AddComponent<DrumPad>();
                var drumAudio = pad.AddComponent<AudioSource>();
                drumAudio.playOnAwake = false;
                SetObjectReference(drumScript, "_audioSource", drumAudio);
                AutoConnectAudio(drumScript, "_drumClip", $"drum_hit_{i + 1}");

                var drumTrigger = pad.AddComponent<HapbeatCollisionTrigger>();
                SetTriggerBase(drumTrigger, eventMap, 3);
                SetCollisionTriggerVelocityScaled(drumTrigger);
            }

            CreateWorldLabel(null, "Drum Label", new Vector3(2, 2.8f, 2), 0.006f,
                "ドラムステーション\n叩く強さで振動が変化");

            SaveScene("PlayerDemoZoneA");
        }

        // ======================================================================
        // Scene: Zone B (Passive Feedback)
        // ======================================================================

        static void BuildZoneB(HapbeatEventMap eventMap)
        {
            var scene = NewScene();
            CreateFloor(new Color(0.2f, 0.2f, 0.35f));
            CreateXRPlaceholder(Vector3.zero);

            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();
            router.AddComponent<DemoManager>();

            CreateMenuPanel("Navigation", new Vector3(-1.5f, 1f, 0), false);

            CreateWorldLabel(null, "Zone Label", new Vector3(0, 3f, 0), 0.012f,
                "Zone B: Passive Feedback\n雨・爆発・ドローン被弾");

            // --- レインルーム ---
            var rainRoom = new GameObject("Rain Room");
            rainRoom.transform.position = new Vector3(-4, 0, 3);

            var rainCollider = rainRoom.AddComponent<BoxCollider>();
            rainCollider.isTrigger = true;
            rainCollider.size = new Vector3(5, 5, 5);
            rainCollider.center = new Vector3(0, 2.5f, 0);

            var rainZone = rainRoom.AddComponent<RainZone>();
            var rainAudio = rainRoom.AddComponent<AudioSource>();
            rainAudio.loop = true;
            rainAudio.playOnAwake = false;
            AutoConnectAudio(rainZone, "_hapticClip", "rain_loop");
            SetObjectReference(rainZone, "_rainAudio", rainAudio);
            var rainClip = FindAudioClip("rain_loop");
            if (rainClip != null) rainAudio.clip = rainClip;

            // Rain VFX
            var rainVFX = new GameObject("Rain VFX");
            rainVFX.transform.SetParent(rainRoom.transform);
            rainVFX.transform.localPosition = new Vector3(0, 5, 0);
            var ps = rainVFX.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startSpeed = 8f;
            main.startLifetime = 1.5f;
            main.startSize = 0.05f;
            main.startColor = new Color(0.6f, 0.7f, 1f, 0.5f);
            main.maxParticles = 500;
            var emission = ps.emission;
            emission.rateOverTime = 200;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(5, 0.1f, 5);
            ps.Stop();
            SetObjectReference(rainZone, "_rainVFX", ps);

            CreateWorldLabel(null, "Rain Label", new Vector3(-4, 3.5f, 3), 0.006f,
                "レインルーム\n中に入ると雨の振動");

            // --- 爆発フィールド ---
            var explosionField = new GameObject("Explosion Field");
            explosionField.transform.position = new Vector3(0, 0, 6);

            var ef = explosionField.AddComponent<ExplosionField>();
            var efAudio = explosionField.AddComponent<AudioSource>();
            efAudio.playOnAwake = false;
            AutoConnectAudio(ef, "_explosionClip", "explosion");
            AutoConnectAudio(ef, "_hapticClip", "explosion");

            // Explosion VFX
            var explVFX = new GameObject("Explosion VFX");
            explVFX.transform.SetParent(explosionField.transform);
            var explPS = explVFX.AddComponent<ParticleSystem>();
            var explMain = explPS.main;
            explMain.startSpeed = 5f;
            explMain.startLifetime = 0.8f;
            explMain.startSize = 0.3f;
            explMain.startColor = new Color(1f, 0.5f, 0.1f);
            explMain.loop = false;
            explMain.playOnAwake = false;
            var explEmission = explPS.emission;
            explEmission.SetBursts(new[] { new ParticleSystem.Burst(0f, 30) });
            explEmission.rateOverTime = 0;
            explPS.Stop();

            var points = new Transform[3];
            for (int i = 0; i < 3; i++)
            {
                var point = new GameObject($"ExplosionPoint_{i + 1}");
                point.transform.SetParent(explosionField.transform);
                float angle = i * 120f * Mathf.Deg2Rad;
                point.transform.localPosition = new Vector3(Mathf.Cos(angle) * 4, 0, Mathf.Sin(angle) * 4);
                points[i] = point.transform;
            }

            var efSO = new SerializedObject(ef);
            var pointsProp = efSO.FindProperty("_explosionPoints");
            pointsProp.arraySize = 3;
            for (int i = 0; i < 3; i++)
                pointsProp.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
            efSO.FindProperty("_explosionVFX").objectReferenceValue = explPS;
            efSO.FindProperty("_audioSource").objectReferenceValue = efAudio;
            efSO.ApplyModifiedPropertiesWithoutUndo();

            CreateWorldLabel(null, "Explosion Label", new Vector3(0, 3.5f, 6), 0.006f,
                "爆発フィールド\n距離に応じて振動が減衰");

            // --- ドローン防衛 ---
            var droneDefense = new GameObject("Drone Defense");
            droneDefense.transform.position = new Vector3(4, 0, 3);
            droneDefense.AddComponent<DroneDefense>();

            CreateWorldLabel(null, "Drone Label", new Vector3(4, 3.5f, 3), 0.006f,
                "ドローン防衛\nPrefab を手動で設定してください");

            SaveScene("PlayerDemoZoneB");
        }

        // ======================================================================
        // Scene: Zone C (UI Feedback)
        // ======================================================================

        static void BuildZoneC(HapbeatEventMap eventMap)
        {
            var scene = NewScene();
            CreateFloor(new Color(0.2f, 0.35f, 0.2f));
            CreateXRPlaceholder(Vector3.zero);

            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();
            router.AddComponent<DemoManager>();

            CreateMenuPanel("Navigation", new Vector3(-1.5f, 1f, 0), false);

            CreateWorldLabel(null, "Zone Label", new Vector3(0, 3f, 0), 0.012f,
                "Zone C: UI Feedback\nポーク・グラブ操作");

            // --- ポークボタン ---
            Color[] btnColors = { Color.red, Color.cyan, Color.magenta, Color.green };
            for (int i = 0; i < 4; i++)
            {
                var btn = GameObject.CreatePrimitive(PrimitiveType.Cube);
                btn.name = $"PokeButton_{i + 1}";
                btn.transform.position = new Vector3(-0.3f + i * 0.2f, 1.2f, 2);
                btn.transform.localScale = new Vector3(0.15f, 0.15f, 0.05f);
                SetColor(btn, btnColors[i]);

                var poke = btn.AddComponent<PokeButton>();
                var pokeAudio = btn.AddComponent<AudioSource>();
                pokeAudio.playOnAwake = false;

                var pokeSO = new SerializedObject(poke);
                pokeSO.FindProperty("_buttonRenderer").objectReferenceValue = btn.GetComponent<Renderer>();
                pokeSO.FindProperty("_normalColor").colorValue = btnColors[i];
                pokeSO.FindProperty("_pressedColor").colorValue = Color.white;
                pokeSO.FindProperty("_audioSource").objectReferenceValue = pokeAudio;
                pokeSO.ApplyModifiedPropertiesWithoutUndo();
                AutoConnectAudio(poke, "_pressClip", "ui_click");

                var pokeTrigger = router.AddComponent<HapbeatUnityEventTrigger>();
                SetTriggerBase(pokeTrigger, eventMap, 7);
                ConnectUnityEvent(poke, "OnPressed", pokeTrigger, "Fire");
            }

            CreateWorldLabel(null, "Poke Label", new Vector3(0, 2.5f, 2), 0.006f,
                "ポークボタン\n指で押すと振動");

            // --- グラブオブジェクト ---
            var shelfBoard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shelfBoard.name = "Shelf Board";
            shelfBoard.transform.position = new Vector3(0, 0.9f, 4);
            shelfBoard.transform.localScale = new Vector3(1.5f, 0.05f, 0.4f);
            SetColor(shelfBoard, new Color(0.5f, 0.35f, 0.2f));

            PrimitiveType[] shapes = { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Cylinder };
            for (int i = 0; i < 3; i++)
            {
                var obj = GameObject.CreatePrimitive(shapes[i]);
                obj.name = $"GrabObject_{i + 1}";
                obj.transform.position = new Vector3(-0.4f + i * 0.4f, 1.1f, 4);
                obj.transform.localScale = Vector3.one * 0.12f;

                var rb = obj.AddComponent<Rigidbody>();
                rb.mass = 0.3f;
                TryAddXRGrabInteractable(obj);

                var grabFB = obj.AddComponent<GrabFeedback>();
                obj.AddComponent<AudioSource>().playOnAwake = false;
                AutoConnectAudio(grabFB, "_grabClip", "grab");
                AutoConnectAudio(grabFB, "_releaseClip", "release");

                var collTrigger = obj.AddComponent<HapbeatCollisionTrigger>();
                SetTriggerBase(collTrigger, eventMap, 0);
                SetCollisionTriggerVelocityScaled(collTrigger);
            }

            CreateWorldLabel(null, "Grab Label", new Vector3(0, 2.5f, 4), 0.006f,
                "グラブオブジェクト\n掴む/離すで振動、投げて衝突も");

            SaveScene("PlayerDemoZoneC");
        }

        // ======================================================================
        // Scene: Zone D (Spatial Audio)
        // ======================================================================

        static void BuildZoneD()
        {
            var scene = NewScene();
            CreateFloor(new Color(0.35f, 0.35f, 0.2f));
            CreateXRPlaceholder(Vector3.zero);

            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();

            CreateMenuPanel("Navigation", new Vector3(-1.5f, 1f, 0), false);

            CreateWorldLabel(null, "Zone Label", new Vector3(0, 3.5f, 0), 0.012f,
                "Zone D: Spatial Audio\n音源が周囲を周回\nL/R で定位を体験");

            // Activation Zone
            var activationZone = new GameObject("Activation Zone");
            activationZone.transform.position = Vector3.zero;
            var zoneCollider = activationZone.AddComponent<SphereCollider>();
            zoneCollider.isTrigger = true;
            zoneCollider.radius = 5f;
            var activator = activationZone.AddComponent<ZoneActivator>();

            // Orbiting Sound Source
            var orbiter = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orbiter.name = "Orbiting Sound Source";
            orbiter.transform.position = new Vector3(3, 1.5f, 0);
            orbiter.transform.localScale = Vector3.one * 0.3f;
            SetColor(orbiter, Color.cyan);
            Object.DestroyImmediate(orbiter.GetComponent<Collider>());

            var audioSource = orbiter.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f;
            audioSource.loop = true;
            audioSource.playOnAwake = false;
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 8f;
            audioSource.volume = 0.5f;

            var clip = FindAudioClip("sine_100hz_1s");
            if (clip != null) audioSource.clip = clip;

            var bridge = orbiter.AddComponent<HapbeatAudioBridge>();
            var bridgeSO = new SerializedObject(bridge);
            bridgeSO.FindProperty("_gain").floatValue = 0.5f;
            bridgeSO.FindProperty("_autoStart").boolValue = false;
            bridgeSO.ApplyModifiedPropertiesWithoutUndo();

            var demo = orbiter.AddComponent<SpatialAudioDemo>();
            var demoSO = new SerializedObject(demo);
            demoSO.FindProperty("_radius").floatValue = 3f;
            demoSO.FindProperty("_orbitSpeed").floatValue = 0.3f;
            demoSO.FindProperty("_heightAmplitude").floatValue = 0.5f;
            demoSO.FindProperty("_heightOffset").floatValue = 1.5f;
            demoSO.FindProperty("_visualRenderer").objectReferenceValue = orbiter.GetComponent<Renderer>();
            if (clip != null)
                demoSO.FindProperty("_audioClip").objectReferenceValue = clip;
            demoSO.ApplyModifiedPropertiesWithoutUndo();

            ConnectUnityEvent(activator, "OnEnterZone", demo, "Activate");
            ConnectUnityEvent(activator, "OnExitZone", demo, "Deactivate");

            // 中心マーカー
            var centerMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerMarker.name = "Stand Here";
            centerMarker.transform.localPosition = new Vector3(0, 0.01f, 0);
            centerMarker.transform.localScale = new Vector3(1, 0.01f, 1);
            Object.DestroyImmediate(centerMarker.GetComponent<Collider>());
            SetColor(centerMarker, new Color(1f, 1f, 0.3f, 0.5f));

            // 軌道リング
            var orbitRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            orbitRing.name = "Orbit Ring";
            orbitRing.transform.localPosition = new Vector3(0, 0.02f, 0);
            orbitRing.transform.localScale = new Vector3(6f, 0.005f, 6f);
            Object.DestroyImmediate(orbitRing.GetComponent<Collider>());
            orbitRing.GetComponent<Renderer>().sharedMaterial = CreateTransparentMaterial(new Color(0f, 1f, 1f, 0.15f));

            SaveScene("PlayerDemoZoneD");
        }

        // ======================================================================
        // EventMap
        // ======================================================================

        static HapbeatEventMap CreatePlayerDemoEventMap()
        {
            var map = ScriptableObject.CreateInstance<HapbeatEventMap>();
            map.entries = new List<HapbeatEventEntry>
            {
                new() { displayName = "パンチ",       eventId = "impact.punch",        gain = 1.0f, group = 0, notes = "VelocityScaled" },
                new() { displayName = "射撃反動",     eventId = "action.shoot",         gain = 0.7f, group = 0 },
                new() { displayName = "ターゲット命中", eventId = "impact.target-hit",   gain = 0.5f, group = 0 },
                new() { displayName = "ドラム",       eventId = "impact.drum",          gain = 1.0f, group = 0, notes = "VelocityScaled" },
                new() { displayName = "雨",           eventId = "ambient.rain",         gain = 0.2f, group = 0, notes = "Loop" },
                new() { displayName = "爆発",         eventId = "impact.explosion",     gain = 1.0f, group = 0, notes = "距離減衰" },
                new() { displayName = "被弾",         eventId = "impact.hit-received",  gain = 0.8f, group = 0 },
                new() { displayName = "ボタン押下",   eventId = "ui.button-press",      gain = 0.3f, group = 0 },
                new() { displayName = "グラブ",       eventId = "ui.grab",              gain = 0.4f, group = 0 },
                new() { displayName = "リリース",     eventId = "ui.release",           gain = 0.2f, group = 0 },
            };

            string dir = FindSamplesPath("PlayerDemo");
            string assetDir = dir != null ? dir + "/EventMaps" : "Assets";
            if (!System.IO.Directory.Exists(assetDir))
                System.IO.Directory.CreateDirectory(assetDir);
            string path = assetDir + "/PlayerDemoEventMap.asset";

            // 既存があれば再利用
            var existing = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
            if (existing != null) return existing;

            AssetDatabase.CreateAsset(map, path);
            AssetDatabase.SaveAssets();
            return map;
        }

        // ======================================================================
        // Shared: Scene Setup
        // ======================================================================

        static UnityEngine.SceneManagement.Scene NewScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            // デフォルト Camera を削除（XR Origin が Camera を持つ）
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                Object.DestroyImmediate(c.gameObject);

            // EventSystem (InputSystem 対応)
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

            return scene;
        }

        static void CreateFloor(Color color)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.localScale = new Vector3(3, 1, 3);
            SetColor(floor, color);
        }

        static void CreateXRPlaceholder(Vector3 position)
        {
            var go = new GameObject(">>> XR Origin をここに配置 <<<");
            go.transform.position = position;
        }

        static void SaveScene(string sceneName)
        {
            string dir = FindSamplesPath("PlayerDemo");

            // フォールバック: サンプルパスが見つからない場合は Assets/PlayerDemo/ に保存
            if (dir == null)
            {
                dir = "Assets/PlayerDemo";
                Debug.LogWarning($"[Hapbeat] サンプルパスが見つかりません。{dir}/ に保存します。");
            }

            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            string path = $"{dir}/{sceneName}.unity";
            EditorSceneManager.SaveScene(
                UnityEngine.SceneManagement.SceneManager.GetActiveScene(), path);
            Debug.Log($"[Hapbeat] シーンを保存しました: {path}");
        }

        // ======================================================================
        // Shared: Menu Panel
        // ======================================================================

        /// <summary>ゾーン選択メニューパネルを作成。isHub=true なら大きなメニュー。</summary>
        static GameObject CreateMenuPanel(string name, Vector3 position, bool isHub)
        {
            var panelGO = new GameObject(name);
            panelGO.transform.position = position;

            var canvas = panelGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = panelGO.GetComponent<RectTransform>();
            rect.sizeDelta = isHub ? new Vector2(500, 500) : new Vector2(250, 400);
            panelGO.transform.localScale = Vector3.one * (isHub ? 0.003f : 0.0015f);

            panelGO.AddComponent<CanvasScaler>();
            // VR コントローラーのレイで UI を操作するには TrackedDeviceGraphicRaycaster が必要
            var trackedRaycasterType = System.Type.GetType(
                "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
            if (trackedRaycasterType != null)
                panelGO.AddComponent(trackedRaycasterType);
            else
                panelGO.AddComponent<GraphicRaycaster>(); // XRI がない場合のフォールバック

            // 背景
            var bg = new GameObject("Background");
            bg.transform.SetParent(panelGO.transform, false);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.1f, 0.1f, 0.15f, 0.9f);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;

            // SceneMenu コンポーネント
            var menu = panelGO.AddComponent<SceneMenu>();

            // ボタン
            float y = isHub ? -20 : -10;
            float btnHeight = isHub ? 70 : 50;
            float fontSize = isHub ? 24 : 16;

            var hubBtn = CreateMenuButton(panelGO.transform, "Hub", "Hub", y, btnHeight, fontSize);
            y -= btnHeight + 10;
            var aBtn = CreateMenuButton(panelGO.transform, "Zone A", "Zone A: Active", y, btnHeight, fontSize);
            y -= btnHeight + 10;
            var bBtn = CreateMenuButton(panelGO.transform, "Zone B", "Zone B: Passive", y, btnHeight, fontSize);
            y -= btnHeight + 10;
            var cBtn = CreateMenuButton(panelGO.transform, "Zone C", "Zone C: UI", y, btnHeight, fontSize);
            y -= btnHeight + 10;
            var dBtn = CreateMenuButton(panelGO.transform, "Zone D", "Zone D: Spatial", y, btnHeight, fontSize);

            // SceneMenu にボタンを接続
            var menuSO = new SerializedObject(menu);
            menuSO.FindProperty("_hubButton").objectReferenceValue = hubBtn;
            menuSO.FindProperty("_zoneAButton").objectReferenceValue = aBtn;
            menuSO.FindProperty("_zoneBButton").objectReferenceValue = bBtn;
            menuSO.FindProperty("_zoneCButton").objectReferenceValue = cBtn;
            menuSO.FindProperty("_zoneDButton").objectReferenceValue = dBtn;
            menuSO.ApplyModifiedPropertiesWithoutUndo();

            return panelGO;
        }

        static Button CreateMenuButton(Transform parent, string name, string label, float yOffset, float height, float fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.1f, 1);
            rect.anchorMax = new Vector2(0.9f, 1);
            rect.anchoredPosition = new Vector2(0, yOffset - height / 2);
            rect.sizeDelta = new Vector2(0, height);

            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.3f, 0.5f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var text = textGO.AddComponent<Text>();
            text.text = label;
            text.fontSize = (int)fontSize;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            return btn;
        }

        // ======================================================================
        // Shared: Helpers
        // ======================================================================

        static AudioClip FindAudioClip(string name)
        {
            var guids = AssetDatabase.FindAssets($"{name} t:AudioClip");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null) return clip;
            }
            return null;
        }

        static void AutoConnectAudio(Component component, string fieldName, string clipName)
        {
            var clip = FindAudioClip(clipName);
            if (clip == null) return;
            SetObjectReference(component, fieldName, clip);
        }

        static void SetObjectReference(Component component, string fieldName, Object value)
        {
            var so = new SerializedObject(component);
            var prop = so.FindProperty(fieldName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void SetTriggerBase(HapbeatTriggerBase trigger, HapbeatEventMap eventMap, int entryIndex)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_eventMap").objectReferenceValue = eventMap;
            so.FindProperty("_entryIndex").intValue = entryIndex;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetCollisionTriggerVelocityScaled(HapbeatCollisionTrigger trigger)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_gainMode").enumValueIndex = 1;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetColor(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            renderer.sharedMaterial = CreateMaterial(color);
        }

        static Material CreateMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            return mat;
        }

        static Material CreateTransparentMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            var mat = new Material(shader);

            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", color);
                mat.SetFloat("_Surface", 1);
                mat.SetFloat("_Blend", 0);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = 3000;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            else
            {
                mat.color = color;
                mat.SetFloat("_Mode", 3);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.renderQueue = 3000;
            }
            return mat;
        }

        static void CreateWorldLabel(Transform parent, string name, Vector3 position, float scale, string text)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * scale;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(500, 200);

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var t = textGO.AddComponent<Text>();
            t.text = text;
            t.fontSize = 32;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Color.white;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        static void TryAddXRGrabInteractable(GameObject go)
        {
            var type = System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable, Unity.XR.Interaction.Toolkit")
                    ?? System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable, Unity.XR.Interaction.Toolkit");
            if (type != null)
                go.AddComponent(type);
        }

        static void ConnectUnityEvent(Component source, string eventFieldName, Component target, string methodName)
        {
            var so = new SerializedObject(source);
            var eventProp = so.FindProperty(eventFieldName);
            if (eventProp == null) return;

            var callsProp = eventProp.FindPropertyRelative("m_PersistentCalls.m_Calls");
            int idx = callsProp.arraySize;
            callsProp.InsertArrayElementAtIndex(idx);
            var call = callsProp.GetArrayElementAtIndex(idx);

            call.FindPropertyRelative("m_Target").objectReferenceValue = target;
            call.FindPropertyRelative("m_MethodName").stringValue = methodName;
            call.FindPropertyRelative("m_Mode").intValue = 1;
            call.FindPropertyRelative("m_CallState").intValue = 2;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static string FindSamplesPath(string sampleName)
        {
            // UPM の Import 先は package.json の displayName に基づくため
            // "PlayerDemo" → "Player Demo" のようにスペース入りになる場合がある。
            // DemoManager スクリプトの実際のパスから逆算する。
            var guids = AssetDatabase.FindAssets("t:Script DemoManager");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                // パスから Scripts/ 以下を除去してサンプルルートを取得
                // 例: "Assets/Samples/Hapbeat SDK/0.1.0/Player Demo/Scripts/DemoManager.cs"
                //   → "Assets/Samples/Hapbeat SDK/0.1.0/Player Demo"
                int scriptsIdx = p.IndexOf("/Scripts/");
                if (scriptsIdx >= 0)
                    return p.Substring(0, scriptsIdx);
            }
            return null;
        }
    }
}
#endif
