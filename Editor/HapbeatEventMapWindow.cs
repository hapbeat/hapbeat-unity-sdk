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
        private Vector2 _detailScrollPos;
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

        [MenuItem("Hapbeat/Event Map")]
        [MenuItem("Window/Hapbeat/Event Map")]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatEventMapWindow>("Hapbeat Event Map");
            window.minSize = new Vector2(500, 300);
        }

        [MenuItem("Hapbeat/Create Event Router", false, 50)]
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

            // Two-column layout: entry list (left) + detail (right)
            EditorGUILayout.BeginHorizontal();

            // Left panel: entry list
            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.45f));
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawEntryTable();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Right panel: detail editor
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);
            DrawSelectedEntryDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
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

            if (GUILayout.Button("Batch Setup", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                HapbeatBatchSetupWindow.ShowWindow();
            }

            if (GUILayout.Button("Scan Scene", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ScanScene();
            }

            if (_selectedMap != null && GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                Undo.RecordObject(_selectedMap, "Add Hapbeat Event Entry");
                _selectedMap.entries.Add(new HapbeatEventEntry
                {
                    displayName = "",
                    category = "",
                    eventName = ""
                });
                _selectedEntryIndex = _selectedMap.entries.Count - 1;
                EditorUtility.SetDirty(_selectedMap);
            }

            bool canDelete = _selectedMap != null && _selectedEntryIndex >= 0
                && _selectedEntryIndex < _selectedMap.entries.Count;
            EditorGUI.BeginDisabledGroup(!canDelete);
            if (GUILayout.Button("\u2212", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                Undo.RecordObject(_selectedMap, "Remove Hapbeat Event Entry");
                _selectedMap.entries.RemoveAt(_selectedEntryIndex);
                _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
                EditorUtility.SetDirty(_selectedMap);
                ScanScene();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawEntryTable()
        {
            if (_selectedMap == null) return;

            for (int i = 0; i < _selectedMap.entries.Count; i++)
            {
                var entry = _selectedMap.entries[i];
                bool hasTriggers = _triggersByEntry.ContainsKey(i) && _triggersByEntry[i].Count > 0;
                bool isSelected = _selectedEntryIndex == i;

                var bgColor = GUI.backgroundColor;
                if (isSelected)
                    GUI.backgroundColor = new Color(0.3f, 0.5f, 0.8f, 0.5f);
                else if (!hasTriggers)
                    GUI.backgroundColor = new Color(1f, 0.9f, 0.7f, 0.15f);

                EditorGUILayout.BeginHorizontal("box");
                GUI.backgroundColor = bgColor;

                // Compact: index + name (or eventId fallback)
                string label = !string.IsNullOrEmpty(entry.displayName) ? entry.displayName
                    : !string.IsNullOrEmpty(entry.eventId) ? entry.eventId
                    : "(new)";

                if (GUILayout.Button($"[{i}] {label}", isSelected ? EditorStyles.boldLabel : EditorStyles.label))
                    _selectedEntryIndex = i;

                // Trigger count badge
                if (hasTriggers)
                {
                    var triggers = _triggersByEntry[i];
                    if (GUILayout.Button($"{triggers.Count}\u25cf", EditorStyles.miniLabel, GUILayout.Width(24)))
                    {
                        Selection.activeGameObject = triggers[0].trigger.gameObject;
                        EditorGUIUtility.PingObject(triggers[0].trigger.gameObject);
                    }
                }

                EditorGUILayout.EndHorizontal();
            }

            if (_selectedMap.entries.Count == 0)
            {
                EditorGUILayout.LabelField("(empty \u2014 click + to add)", EditorStyles.centeredGreyMiniLabel);
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

            // Name
            var nameProp = entryProp.FindPropertyRelative("displayName");
            nameProp.stringValue = DrawPlaceholderField("Name", nameProp.stringValue, "e.g. Grab");

            // Category + event name on one line — both editable text with dropdown assist
            var categoryProp = entryProp.FindPropertyRelative("category");
            var eventNameProp = entryProp.FindPropertyRelative("eventName");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Event ID");

            // Category: editable text + dropdown button
            categoryProp.stringValue = DrawPlaceholderFieldInline(categoryProp.stringValue, "clip", 80);
            if (EditorGUILayout.DropdownButton(GUIContent.none, FocusType.Passive, GUILayout.Width(16)))
            {
                var menu = new GenericMenu();
                foreach (var cat in HapbeatEventEntry.StandardCategories)
                {
                    string c = cat;
                    menu.AddItem(new GUIContent(c), categoryProp.stringValue == c,
                        () => { categoryProp.stringValue = c; so.ApplyModifiedProperties(); });
                }
                menu.ShowAsContext();
            }

            EditorGUILayout.LabelField(".", GUILayout.Width(8));
            eventNameProp.stringValue = DrawPlaceholderFieldInline(eventNameProp.stringValue, "hit");
            EditorGUILayout.EndHorizontal();

            // eventId preview
            var entry = _selectedMap.entries[_selectedEntryIndex];
            string previewId = entry.eventId;
            if (string.IsNullOrEmpty(previewId)) previewId = "clip.hit";
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("  \u2192 eventId", previewId);
            EditorGUI.EndDisabledGroup();

            // Validation
            if (!string.IsNullOrEmpty(categoryProp.stringValue) && !HapbeatEventEntry.IsValidSegment(categoryProp.stringValue))
                EditorGUILayout.HelpBox($"category \"{categoryProp.stringValue}\": lowercase a-z, 0-9, -, _ only", MessageType.Warning);
            if (!string.IsNullOrEmpty(eventNameProp.stringValue) && !HapbeatEventEntry.IsValidSegment(eventNameProp.stringValue))
                EditorGUILayout.HelpBox($"name \"{eventNameProp.stringValue}\": lowercase a-z, 0-9, -, _ only", MessageType.Warning);

            // Gain
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"), new GUIContent("Gain"));

            // Target (device addressing) — prefix / player / position fields
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Targeting", EditorStyles.miniBoldLabel);
            var targetProp = entryProp.FindPropertyRelative("target");

            // Parse current target into parts
            ParseTarget(targetProp.stringValue, out string curPrefix, out int curPlayer, out string curPos);

            // Prefix (optional, for large setups)
            string newPrefix = DrawPlaceholderField("Prefix", curPrefix, "(optional, e.g. team_red)");

            // Player + Position on one line
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("Player / Pos");
            int newPlayer = EditorGUILayout.IntField(curPlayer, GUILayout.Width(40));

            // Position dropdown: (none) + 12 positions
            var posOptions = new List<string> { "(none)" };
            var posValues = new List<string> { "" };
            for (int p = 0; p < HapbeatEventEntry.StandardPositions.Length; p++)
            {
                posOptions.Add($"{HapbeatEventEntry.PositionLabels[p]} ({HapbeatEventEntry.StandardPositions[p]})");
                posValues.Add(HapbeatEventEntry.StandardPositions[p]);
            }
            int posIdx = posValues.IndexOf(curPos);
            if (posIdx < 0) posIdx = 0;
            int newPosIdx = EditorGUILayout.Popup(posIdx, posOptions.ToArray());
            string newPos = posValues[newPosIdx];
            EditorGUILayout.EndHorizontal();

            // Build target from parts
            string builtTarget = BuildTargetFromParts(newPrefix, newPlayer, newPos);
            if (builtTarget != targetProp.stringValue)
                targetProp.stringValue = builtTarget;

            // Preview
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("  \u2192 target",
                string.IsNullOrEmpty(builtTarget) ? "(broadcast \u2014 all devices)" : builtTarget);
            EditorGUI.EndDisabledGroup();

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

        // --- Placeholder text field helpers ---
        // Draw real TextField always, overlay placeholder only when unfocused + empty.
        // This ensures correct focus ring behavior.

        private static GUIStyle _phStyle;
        private static GUIStyle PhStyle => _phStyle ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Italic,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 0.5f) },
            padding = new RectOffset(3, 0, 0, 0)
        };

        private static string DrawPlaceholderField(string label, string value, string placeholder)
        {
            string result = EditorGUILayout.TextField(label, value);
            if (string.IsNullOrEmpty(result) && !IsFieldFocused())
            {
                var rect = GUILayoutUtility.GetLastRect();
                rect.x += EditorGUIUtility.labelWidth + 2;
                rect.width -= EditorGUIUtility.labelWidth + 2;
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            }
            return result;
        }

        private static string DrawPlaceholderFieldInline(string value, string placeholder, float width = 0)
        {
            var opts = width > 0 ? new[] { GUILayout.Width(width) } : new GUILayoutOption[0];
            string result = EditorGUILayout.TextField(value, opts);
            if (string.IsNullOrEmpty(result) && !IsFieldFocused())
            {
                var rect = GUILayoutUtility.GetLastRect();
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            }
            return result;
        }

        private static bool IsFieldFocused()
        {
            return GUIUtility.keyboardControl != 0
                && EditorGUIUtility.editingTextField;
        }

        // --- Target address helpers ---

        /// <summary>
        /// Parse a target string back into prefix, player, position parts.
        /// Handles: "", "player_1", "*/pos_neck", "player_1/pos_neck", "red/player_1/pos_neck"
        /// </summary>
        private static void ParseTarget(string target, out string prefix, out int player, out string position)
        {
            prefix = "";
            player = -1;
            position = "";

            if (string.IsNullOrEmpty(target)) return;

            var parts = target.Split('/');
            var prefixParts = new List<string>();

            foreach (var part in parts)
            {
                if (part.StartsWith("player_") && int.TryParse(part.Substring(7), out int p))
                    player = p;
                else if (part.StartsWith("pos_"))
                    position = part;
                else if (part != "*")
                    prefixParts.Add(part);
            }

            prefix = string.Join("/", prefixParts);
        }

        /// <summary>
        /// Build a target string from separate parts (matching manager's _build_address format).
        /// player=-1 → wildcard or omit. position="" → omit.
        /// </summary>
        private static string BuildTargetFromParts(string prefix, int player, string position)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(prefix))
                parts.Add(prefix.Trim());

            if (player >= 1)
                parts.Add($"player_{player}");
            else if (!string.IsNullOrEmpty(position))
                parts.Add("*"); // wildcard player when only position is set

            if (!string.IsNullOrEmpty(position))
                parts.Add(position);

            return string.Join("/", parts);
        }
    }
}
#endif
