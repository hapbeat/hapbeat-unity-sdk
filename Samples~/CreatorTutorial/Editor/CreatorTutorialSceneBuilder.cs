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
    /// Creator Tutorial の Before / After シーンを自動生成する Editor スクリプト。
    /// Menu: Hapbeat > Build Samples > Creator Tutorial
    /// </summary>
    public static class CreatorTutorialSceneBuilder
    {
        [MenuItem("Hapbeat/Build Samples/3. Creator Tutorial (Before + After)", false, 102)]
        public static void Build()
        {
            if (!EditorUtility.DisplayDialog(
                "CreatorTutorial シーン生成",
                "Before（触覚なし）と After（Hapbeat 統合済み）の2シーンを生成します。",
                "生成する", "キャンセル"))
                return;

            string basePath = FindSamplesPath();

            // Before シーン
            BuildBeforeScene(basePath);

            // After シーン
            BuildAfterScene(basePath);

            EditorUtility.DisplayDialog("完了",
                "CreatorTutorial の Before / After シーンを生成しました。\n\n" +
                "残りの手動作業:\n" +
                "1. XR Origin を各シーンに配置\n" +
                "2. 各 AudioSource に Audio Clip を設定\n" +
                "3. Before シーンで触覚なし動作を確認\n" +
                "4. After シーンで触覚あり動作を確認",
                "OK");
        }

        // ======================================================================
        // Before シーン（触覚なし）
        // ======================================================================

        static void BuildBeforeScene(string basePath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // デフォルト Camera を削除
            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                Object.DestroyImmediate(c.gameObject);

            // XR Origin プレースホルダー
            var xrPlaceholder = new GameObject(">>> XR Origin をここに配置 <<<");
            xrPlaceholder.transform.position = new Vector3(0, 0, -3);

            // 環境
            BuildShootingRangeEnvironment();

            // Gun
            var gun = BuildGun();

            // Targets
            BuildTargets(5);

            // Obstacle
            BuildObstacle();

            // Score UI
            BuildScoreUI();

            // 保存
            if (basePath != null)
            {
                string path = basePath + "/CreatorTutorial_Before.unity";
                EditorSceneManager.SaveScene(scene, path);
                Debug.Log($"[Hapbeat] Before シーンを保存しました: {path}");
            }
            else
            {
                EditorSceneManager.SaveScene(scene, "Assets/CreatorTutorial_Before.unity");
                Debug.LogWarning("[Hapbeat] サンプルパスが見つかりません。Assets/ に Before シーンを保存しました。");
            }
        }

        // ======================================================================
        // After シーン（Hapbeat 統合済み）
        // ======================================================================

        static void BuildAfterScene(string basePath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                Object.DestroyImmediate(c.gameObject);

            var xrPlaceholder = new GameObject(">>> XR Origin をここに配置 <<<");
            xrPlaceholder.transform.position = new Vector3(0, 0, -3);

            BuildShootingRangeEnvironment();
            var gun = BuildGun();
            var targets = BuildTargets(5);
            var obstacle = BuildObstacle();
            BuildScoreUI();

            // === Hapbeat 統合部分 ===

            // EventMap
            var eventMap = CreateTutorialEventMap(basePath);

            // Hapbeat Event Router
            var router = new GameObject("[Hapbeat Event Router]");
            router.AddComponent<HapbeatManager>();

            // 射撃反動トリガー
            var shootTrigger = router.AddComponent<HapbeatUnityEventTrigger>();
            SetTriggerBase(shootTrigger, eventMap, 0); // 射撃反動

            // Gun の OnShoot → shootTrigger.Fire()
            var shooter = gun.GetComponent<SimpleShooter>();
            ConnectUnityEvent(shooter, "OnShoot", shootTrigger, "Fire");

            // 各 Target に CollisionTrigger
            foreach (var target in targets)
            {
                var ct = target.AddComponent<HapbeatCollisionTrigger>();
                SetTriggerBase(ct, eventMap, 1); // ターゲット命中
            }

            // Obstacle に CollisionTrigger
            var obstacleTrigger = obstacle.AddComponent<HapbeatCollisionTrigger>();
            SetTriggerBase(obstacleTrigger, eventMap, 2); // 被弾
            var obsSO = new SerializedObject(obstacleTrigger);
            obsSO.FindProperty("_triggerEvent").enumValueIndex = 2; // TriggerEnter
            obsSO.ApplyModifiedPropertiesWithoutUndo();

            // 保存
            if (basePath != null)
            {
                string path = basePath + "/CreatorTutorial_After.unity";
                EditorSceneManager.SaveScene(scene, path);
                Debug.Log($"[Hapbeat] After シーンを保存しました: {path}");
            }
            else
            {
                EditorSceneManager.SaveScene(scene, "Assets/CreatorTutorial_After.unity");
                Debug.LogWarning("[Hapbeat] サンプルパスが見つかりません。Assets/ に After シーンを保存しました。");
            }
        }

        // ======================================================================
        // 共通のシーン要素
        // ======================================================================

        static void BuildShootingRangeEnvironment()
        {
            var env = new GameObject("Environment");

            // Floor
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(env.transform);
            floor.transform.localScale = new Vector3(3, 1, 3);
            SetColor(floor, new Color(0.35f, 0.35f, 0.4f));

            // 壁（奥）
            var wallBack = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wallBack.name = "Wall Back";
            wallBack.transform.SetParent(env.transform);
            wallBack.transform.position = new Vector3(0, 2, 8);
            wallBack.transform.localScale = new Vector3(10, 4, 0.2f);
            SetColor(wallBack, new Color(0.5f, 0.5f, 0.55f));

            // 壁（左右）
            var wallLeft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wallLeft.name = "Wall Left";
            wallLeft.transform.SetParent(env.transform);
            wallLeft.transform.position = new Vector3(-5, 2, 4);
            wallLeft.transform.localScale = new Vector3(0.2f, 4, 8);
            SetColor(wallLeft, new Color(0.5f, 0.5f, 0.55f));

            var wallRight = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wallRight.name = "Wall Right";
            wallRight.transform.SetParent(env.transform);
            wallRight.transform.position = new Vector3(5, 2, 4);
            wallRight.transform.localScale = new Vector3(0.2f, 4, 8);
            SetColor(wallRight, new Color(0.5f, 0.5f, 0.55f));
        }

        static GameObject BuildGun()
        {
            var gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gun.name = "Gun";
            gun.transform.position = new Vector3(0, 1, -1);
            gun.transform.localScale = new Vector3(0.05f, 0.12f, 0.3f);
            SetColor(gun, new Color(0.15f, 0.15f, 0.2f));

            var rb = gun.AddComponent<Rigidbody>();
            rb.mass = 0.5f;
            TryAddXRGrabInteractable(gun);

            var shooter = gun.AddComponent<SimpleShooter>();
            var audio = gun.AddComponent<AudioSource>();
            audio.playOnAwake = false;

            // Muzzle
            var muzzle = new GameObject("Muzzle");
            muzzle.transform.SetParent(gun.transform);
            muzzle.transform.localPosition = new Vector3(0, 0, 0.2f);

            var so = new SerializedObject(shooter);
            so.FindProperty("_muzzle").objectReferenceValue = muzzle.transform;
            so.FindProperty("_audioSource").objectReferenceValue = audio;
            so.ApplyModifiedPropertiesWithoutUndo();

            return gun;
        }

        static List<GameObject> BuildTargets(int count)
        {
            var targets = new List<GameObject>();
            var parent = new GameObject("Targets");

            for (int i = 0; i < count; i++)
            {
                var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
                target.name = $"Target_{i + 1}";
                target.transform.SetParent(parent.transform);
                target.transform.position = new Vector3(-2f + i * 1f, 1.5f, 7);
                target.transform.localScale = new Vector3(0.5f, 0.8f, 0.1f);
                SetColor(target, new Color(1f, 0.85f, 0.2f));

                var rb = target.AddComponent<Rigidbody>();
                rb.isKinematic = true;

                var targetScript = target.AddComponent<Target>();
                var audio = target.AddComponent<AudioSource>();
                audio.playOnAwake = false;

                var tso = new SerializedObject(targetScript);
                tso.FindProperty("_audioSource").objectReferenceValue = audio;
                tso.ApplyModifiedPropertiesWithoutUndo();

                targets.Add(target);
            }

            return targets;
        }

        static GameObject BuildObstacle()
        {
            var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            obstacle.name = "Obstacle";
            obstacle.transform.position = new Vector3(-3, 0.5f, 3);
            obstacle.transform.localScale = new Vector3(0.4f, 1f, 0.4f);
            SetColor(obstacle, new Color(0.8f, 0.2f, 0.2f));

            var rb = obstacle.AddComponent<Rigidbody>();
            rb.isKinematic = true;

            // 簡単な往復移動（左右）
            // Note: 実際の往復移動はスクリプトが必要だが、ここではコンポーネントだけ設定
            // Obstacle の Collider を Trigger にする（被弾判定用）
            var col = obstacle.GetComponent<BoxCollider>();
            // Trigger 版の外側 Collider を追加
            var triggerCol = obstacle.AddComponent<BoxCollider>();
            triggerCol.isTrigger = true;
            triggerCol.size = new Vector3(1.5f, 1.5f, 1.5f); // 少し大きめ

            return obstacle;
        }

        static void BuildScoreUI()
        {
            // Canvas
            var canvasGO = new GameObject("Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            // Score Text
            var scoreGO = CreateUIText(canvasGO.transform, "ScoreText", "Score: 0",
                new Vector2(0, 1), new Vector2(0.3f, 1), new Vector2(10, -10), new Vector2(0, -50));

            // Timer Text
            var timerGO = CreateUIText(canvasGO.transform, "TimerText", "Time: 60s",
                new Vector2(0.7f, 1), new Vector2(1, 1), new Vector2(0, -10), new Vector2(-10, -50));

            // ScoreUI component
            var scoreUI = canvasGO.AddComponent<ScoreUI>();
            var so = new SerializedObject(scoreUI);
            so.FindProperty("_scoreText").objectReferenceValue = scoreGO.GetComponent<Text>();
            so.FindProperty("_timerText").objectReferenceValue = timerGO.GetComponent<Text>();
            so.ApplyModifiedPropertiesWithoutUndo();

            // EventSystem
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
        }

        // ======================================================================
        // EventMap
        // ======================================================================

        static HapbeatEventMap CreateTutorialEventMap(string basePath)
        {
            var map = ScriptableObject.CreateInstance<HapbeatEventMap>();
            map.entries = new List<HapbeatEventEntry>
            {
                new() { displayName = "射撃反動",       eventId = "action.shoot",        gain = 0.7f, group = 0 },
                new() { displayName = "ターゲット命中", eventId = "impact.target-hit",    gain = 0.5f, group = 0 },
                new() { displayName = "被弾",           eventId = "impact.hit-received",  gain = 0.8f, group = 0 },
            };

            if (basePath != null)
            {
                string dir = basePath + "/EventMaps";
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                string path = dir + "/TutorialEventMap.asset";
                AssetDatabase.CreateAsset(map, path);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Hapbeat] TutorialEventMap を保存しました: {path}");
            }
            else
            {
                AssetDatabase.CreateAsset(map, "Assets/TutorialEventMap.asset");
                AssetDatabase.SaveAssets();
            }

            return map;
        }

        // ======================================================================
        // Helpers
        // ======================================================================

        static void SetTriggerBase(HapbeatTriggerBase trigger, HapbeatEventMap eventMap, int entryIndex)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_eventMap").objectReferenceValue = eventMap;
            so.FindProperty("_entryIndex").intValue = entryIndex;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetColor(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            var shader = Shader.Find("Universal Render Pipeline/Lit")
                      ?? Shader.Find("Standard");
            var mat = new Material(shader);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            else
                mat.color = color;
            renderer.sharedMaterial = mat;
        }

        static GameObject CreateUIText(Transform parent, string name, string text,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var t = go.AddComponent<Text>();
            t.text = text;
            t.fontSize = 24;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            return go;
        }

        static void TryAddXRGrabInteractable(GameObject go)
        {
            var type = System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable, Unity.XR.Interaction.Toolkit");
            if (type == null)
                type = System.Type.GetType("UnityEngine.XR.Interaction.Toolkit.XRGrabInteractable, Unity.XR.Interaction.Toolkit");
            if (type != null)
                go.AddComponent(type);
            else
                Debug.LogWarning($"[Hapbeat] XRGrabInteractable が見つかりません。{go.name} に手動で追加してください。");
        }

        static void ConnectUnityEvent(Component source, string eventFieldName, Component target, string methodName)
        {
            var so = new SerializedObject(source);
            var eventProp = so.FindProperty(eventFieldName);
            if (eventProp == null)
            {
                Debug.LogWarning($"[Hapbeat] {source.GetType().Name}.{eventFieldName} が見つかりません。");
                return;
            }

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

        static string FindSamplesPath()
        {
            var guids = AssetDatabase.FindAssets("t:Script SimpleShooter");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                // 例: "Assets/Samples/Hapbeat SDK/0.1.0/Creator Tutorial/Scripts/SimpleShooter.cs"
                //   → "Assets/Samples/Hapbeat SDK/0.1.0/Creator Tutorial"
                int scriptsIdx = p.IndexOf("/Scripts/");
                if (scriptsIdx >= 0)
                    return p.Substring(0, scriptsIdx);
            }
            return null;
        }
    }
}
#endif
