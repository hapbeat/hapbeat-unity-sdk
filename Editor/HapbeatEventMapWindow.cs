#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor window showing all event map entries and their scene trigger bindings.
    /// Provides a centralized dashboard of "what triggers what" and "where is it attached".
    /// Window > Hapbeat > Event Map
    /// </summary>
    public class HapbeatEventMapWindow : EditorWindow
    {
        private HapbeatEventMap _selectedMap;
        private Vector2 _scrollPosition;
        private int _selectedEntryIndex = -1;

        // Cached scene scan results
        private Dictionary<int, List<TriggerInfo>> _triggersByEntry = new Dictionary<int, List<TriggerInfo>>();
        private List<TriggerInfo> _orphanedTriggers = new List<TriggerInfo>();

        private struct TriggerInfo
        {
            public HapbeatTriggerBase trigger;
            public string gameObjectName;
            public string typeName;
        }

        [MenuItem("Window/Hapbeat/Event Map")]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatEventMapWindow>("Hapbeat Event Map");
            window.minSize = new Vector2(500, 300);
        }

        [MenuItem("GameObject/Hapbeat/Event Router", false, 10)]
        public static void CreateEventRouter()
        {
            // Check if one already exists
            var existing = FindObjectsByType<HapbeatManager>(FindObjectsSortMode.None);
            GameObject router = new GameObject("[Hapbeat Event Router]");
            Undo.RegisterCreatedObjectUndo(router, "Create Hapbeat Event Router");

            // Add HapbeatManager if not in scene
            if (existing.Length == 0)
                router.AddComponent<HapbeatManager>();

            Selection.activeGameObject = router;
            Debug.Log("[Hapbeat] Event Router を作成しました。ここに AnimatorTrigger / UnityEventTrigger を追加してください。");
        }

        private void OnEnable()
        {
            FindEventMap();
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(5);

            if (_selectedMap == null)
            {
                EditorGUILayout.HelpBox(
                    "Event Map が見つかりません。\nAssets > Create > Hapbeat > Event Map で作成してください。",
                    MessageType.Info);
                return;
            }

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawEntryTable();
            EditorGUILayout.Space(10);
            DrawSelectedEntryDetail();
            EditorGUILayout.EndScrollView();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Event Map selector
            EditorGUILayout.LabelField("Event Map:", GUILayout.Width(70));
            var newMap = (HapbeatEventMap)EditorGUILayout.ObjectField(
                _selectedMap, typeof(HapbeatEventMap), false, GUILayout.Width(200));
            if (newMap != _selectedMap)
            {
                _selectedMap = newMap;
                ScanScene();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Scan Scene", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ScanScene();
            }

            if (_selectedMap != null && GUILayout.Button("+ Entry", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                Undo.RecordObject(_selectedMap, "Add Hapbeat Event Entry");
                _selectedMap.entries.Add(new HapbeatEventEntry
                {
                    displayName = $"New Event {_selectedMap.entries.Count}",
                    eventId = "new.event"
                });
                EditorUtility.SetDirty(_selectedMap);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntryTable()
        {
            if (_selectedMap == null) return;

            // Header
            EditorGUILayout.BeginHorizontal("box");
            GUILayout.Label("Name", EditorStyles.boldLabel, GUILayout.Width(120));
            GUILayout.Label("Event ID", EditorStyles.boldLabel, GUILayout.Width(140));
            GUILayout.Label("Gain", EditorStyles.boldLabel, GUILayout.Width(50));
            GUILayout.Label("Group", EditorStyles.boldLabel, GUILayout.Width(50));
            GUILayout.Label("Type", EditorStyles.boldLabel, GUILayout.Width(80));
            GUILayout.Label("Attached To", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            // Rows
            for (int i = 0; i < _selectedMap.entries.Count; i++)
            {
                var entry = _selectedMap.entries[i];
                bool hasTriggers = _triggersByEntry.ContainsKey(i) && _triggersByEntry[i].Count > 0;
                bool isSelected = _selectedEntryIndex == i;

                // Row color
                var bgColor = GUI.backgroundColor;
                if (isSelected)
                    GUI.backgroundColor = new Color(0.3f, 0.5f, 0.8f, 0.5f);
                else if (!hasTriggers)
                    GUI.backgroundColor = new Color(1f, 0.9f, 0.7f, 0.3f);

                EditorGUILayout.BeginHorizontal("box");
                GUI.backgroundColor = bgColor;

                // Name
                string displayName = string.IsNullOrEmpty(entry.displayName) ? "(unnamed)" : entry.displayName;
                if (GUILayout.Button(displayName, EditorStyles.label, GUILayout.Width(120)))
                    _selectedEntryIndex = i;

                // Event ID
                GUILayout.Label(entry.eventId, GUILayout.Width(140));

                // Gain
                GUILayout.Label(entry.gain.ToString("F1"), GUILayout.Width(50));

                // Group
                string groupStr = entry.group < 0 ? "def" : entry.group.ToString();
                GUILayout.Label(groupStr, GUILayout.Width(50));

                // Type + Attached To
                if (hasTriggers)
                {
                    var triggers = _triggersByEntry[i];
                    string typeName = triggers[0].typeName;
                    GUILayout.Label(typeName, GUILayout.Width(80));

                    // Summarize attached GameObjects
                    var goNames = triggers.Select(t => t.gameObjectName).Distinct().ToList();
                    string summary = goNames.Count <= 2
                        ? string.Join(", ", goNames)
                        : $"{goNames[0]} +{goNames.Count - 1}";
                    summary += $" ({triggers.Count})";

                    if (GUILayout.Button(summary, EditorStyles.linkLabel))
                    {
                        // Select first trigger's GameObject in Hierarchy
                        Selection.activeGameObject = triggers[0].trigger.gameObject;
                        EditorGUIUtility.PingObject(triggers[0].trigger.gameObject);
                    }
                }
                else
                {
                    GUILayout.Label("—", GUILayout.Width(80));
                    var warnStyle = new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } };
                    GUILayout.Label("no triggers", warnStyle);
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawSelectedEntryDetail()
        {
            if (_selectedMap == null || _selectedEntryIndex < 0 || _selectedEntryIndex >= _selectedMap.entries.Count)
                return;

            EditorGUILayout.LabelField("Entry Detail", EditorStyles.boldLabel);

            var so = new SerializedObject(_selectedMap);
            var entriesProp = so.FindProperty("entries");
            var entryProp = entriesProp.GetArrayElementAtIndex(_selectedEntryIndex);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("displayName"), new GUIContent("Name"));
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("eventId"), new GUIContent("Event ID"));
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"), new GUIContent("Gain"));
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("group"), new GUIContent("Group"));
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("notes"), new GUIContent("Notes"));
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
            }

            // List triggers for this entry
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Triggers in Scene:", EditorStyles.boldLabel);
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"  {info.typeName}", GUILayout.Width(100));
                    if (GUILayout.Button(info.gameObjectName, EditorStyles.linkLabel))
                    {
                        Selection.activeGameObject = info.trigger.gameObject;
                        EditorGUIUtility.PingObject(info.trigger.gameObject);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        private void ScanScene()
        {
            _triggersByEntry.Clear();
            _orphanedTriggers.Clear();

            if (_selectedMap == null) return;

            var allTriggers = FindObjectsByType<HapbeatTriggerBase>(FindObjectsSortMode.None);
            foreach (var trigger in allTriggers)
            {
                if (trigger.EventMap != _selectedMap) continue;

                var info = new TriggerInfo
                {
                    trigger = trigger,
                    gameObjectName = trigger.gameObject.name,
                    typeName = GetTriggerTypeName(trigger)
                };

                int idx = trigger.EntryIndex;
                if (idx >= 0 && idx < _selectedMap.entries.Count)
                {
                    if (!_triggersByEntry.ContainsKey(idx))
                        _triggersByEntry[idx] = new List<TriggerInfo>();
                    _triggersByEntry[idx].Add(info);
                }
                else
                {
                    _orphanedTriggers.Add(info);
                }
            }

            Repaint();
        }

        private string GetTriggerTypeName(HapbeatTriggerBase trigger)
        {
            if (trigger is HapbeatCollisionTrigger) return "Collision";
            if (trigger is HapbeatAnimatorTrigger) return "Animator";
            if (trigger is HapbeatUnityEventTrigger) return "Event";
            return trigger.GetType().Name;
        }

        private void FindEventMap()
        {
            if (_selectedMap != null) return;

            string[] guids = AssetDatabase.FindAssets("t:HapbeatEventMap");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _selectedMap = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
            }
        }

        private void OnFocus()
        {
            ScanScene();
        }
    }
}
#endif
