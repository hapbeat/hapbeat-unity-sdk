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
            EditorGUILayout.Space(3);

            if (_selectedMap == null)
            {
                EditorGUILayout.HelpBox(
                    "Event Map が見つかりません。\nAssets > Create > Hapbeat > Event Map で作成してください。",
                    MessageType.Info);
                return;
            }

            // Keyboard navigation
            HandleKeyboard();

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.42f));
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawEntryTable();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);
            DrawSelectedEntryDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void HandleKeyboard()
        {
            if (_selectedMap == null || _selectedMap.entries.Count == 0) return;
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.UpArrow)
            {
                _selectedEntryIndex = Mathf.Max(0, _selectedEntryIndex - 1);
                e.Use();
                Repaint();
            }
            else if (e.keyCode == KeyCode.DownArrow)
            {
                _selectedEntryIndex = Mathf.Min(_selectedMap.entries.Count - 1, _selectedEntryIndex + 1);
                e.Use();
                Repaint();
            }
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

        private static readonly Color SelectedBg = new Color(0.24f, 0.48f, 0.90f, 0.6f);
        private static readonly Color SelectedText = Color.white;

        private void DrawEntryTable()
        {
            if (_selectedMap == null) return;

            for (int i = 0; i < _selectedMap.entries.Count; i++)
            {
                var entry = _selectedMap.entries[i];
                bool hasTriggers = _triggersByEntry.ContainsKey(i) && _triggersByEntry[i].Count > 0;
                bool isSelected = _selectedEntryIndex == i;

                // Single-line card using manual Rect layout for clipping control
                float rowHeight = EditorGUIUtility.singleLineHeight + 4;
                var cardRect = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));

                // Selection highlight
                if (Event.current.type == EventType.Repaint)
                {
                    if (isSelected)
                        EditorGUI.DrawRect(cardRect, SelectedBg);

                    // --- Build 3 segments: name (never clip) | eventId (clip first) | target (high priority) ---
                    string name = !string.IsNullOrEmpty(entry.displayName) ? entry.displayName : "(new)";
                    string nameText = $"[{i}] {name}";

                    ParseTarget(entry.target, out _, out int pl, out string pos);
                    string tgt = "";
                    if (pl >= 1) tgt += $"P{pl}";
                    if (!string.IsNullOrEmpty(pos))
                    {
                        if (tgt.Length > 0) tgt += "/";
                        tgt += pos.Replace("pos_", "");
                    }
                    if (hasTriggers) tgt += $" {_triggersByEntry[i].Count}\u25cf";

                    string eid = !string.IsNullOrEmpty(entry.eventId) ? entry.eventId : "";

                    // Styles
                    Color normalDim = Color.gray;
                    var nameStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal
                    };
                    var dimStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        clipping = TextClipping.Clip
                    };
                    var rightStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight
                    };
                    if (isSelected)
                    {
                        nameStyle.normal.textColor = SelectedText;
                        dimStyle.normal.textColor = new Color(0.75f, 0.85f, 1f);
                        rightStyle.normal.textColor = new Color(0.9f, 0.95f, 1f);
                    }
                    else
                    {
                        dimStyle.normal.textColor = normalDim;
                        rightStyle.normal.textColor = normalDim;
                    }

                    // Measure fixed-width segments
                    float pad = 4;
                    float nameW = nameStyle.CalcSize(new GUIContent(nameText)).x + pad;
                    float tgtW = !string.IsNullOrEmpty(tgt) ? rightStyle.CalcSize(new GUIContent(tgt)).x + pad : 0;
                    float totalW = cardRect.width - 4;

                    // Layout: [name] ... [eventId clipped] [target]
                    // Name takes what it needs, target takes what it needs, eventId gets the rest
                    float eidW = Mathf.Max(0, totalW - nameW - tgtW);

                    var nameRect = new Rect(cardRect.x + 2, cardRect.y, nameW, cardRect.height);
                    GUI.Label(nameRect, nameText, nameStyle);

                    if (eidW > 20 && !string.IsNullOrEmpty(eid))
                    {
                        var eidRect = new Rect(nameRect.xMax, cardRect.y, eidW, cardRect.height);
                        GUI.Label(eidRect, eid, dimStyle);
                    }

                    if (tgtW > 0)
                    {
                        var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                        GUI.Label(tgtRect, tgt, rightStyle);
                    }
                }

                GUIUtility.GetControlID(FocusType.Passive, cardRect);

                // Click anywhere to select
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    Event.current.Use();
                    Repaint();
                }

                // Right-click
                if (Event.current.type == EventType.ContextClick
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    var ctx = new GenericMenu();
                    int idx = i;
                    ctx.AddItem(new GUIContent("Add Entry Above"), false, () => InsertEntry(idx));
                    ctx.AddItem(new GUIContent("Add Entry Below"), false, () => InsertEntry(idx + 1));
                    ctx.AddSeparator("");
                    ctx.AddItem(new GUIContent("Delete Entry"), false, () => DeleteEntry(idx));
                    ctx.ShowAsContext();
                    Event.current.Use();
                }
            }

            if (_selectedMap.entries.Count == 0)
                EditorGUILayout.LabelField("(empty \u2014 click + to add)", EditorStyles.centeredGreyMiniLabel);
        }

        private void InsertEntry(int index)
        {
            if (_selectedMap == null) return;
            Undo.RecordObject(_selectedMap, "Insert Hapbeat Event Entry");
            index = Mathf.Clamp(index, 0, _selectedMap.entries.Count);
            _selectedMap.entries.Insert(index, new HapbeatEventEntry());
            _selectedEntryIndex = index;
            EditorUtility.SetDirty(_selectedMap);
        }

        private void DeleteEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Remove Hapbeat Event Entry");
            _selectedMap.entries.RemoveAt(index);
            _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
            EditorUtility.SetDirty(_selectedMap);
            ScanScene();
        }

        private void DrawSelectedEntryDetail()
        {
            if (_selectedMap == null || _selectedEntryIndex < 0 || _selectedEntryIndex >= _selectedMap.entries.Count)
                return;

            EditorGUILayout.LabelField("Entry Detail", EditorStyles.boldLabel);

            var so = new SerializedObject(_selectedMap);
            var entriesProp = so.FindProperty("entries");
            var entryProp = entriesProp.GetArrayElementAtIndex(_selectedEntryIndex);

            // Use consistent label width for alignment
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70;

            EditorGUI.BeginChangeCheck();

            // Name
            var nameProp = entryProp.FindPropertyRelative("displayName");
            nameProp.stringValue = EditorGUILayout.TextField(
                new GUIContent("Name", "Human-readable label for this event (e.g. Grab, Click)."),
                nameProp.stringValue);

            // Category + event name — horizontal
            var categoryProp = entryProp.FindPropertyRelative("category");
            var eventNameProp = entryProp.FindPropertyRelative("eventName");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Event ID",
                "Composed from category.name. Sent to devices to identify which clip to play.\n" +
                "Standard categories: clip, impact, vibration, texture, ambient, ui, custom."));
            categoryProp.stringValue = DrawPlaceholderFieldInline(categoryProp.stringValue, "clip", 70);
            if (EditorGUILayout.DropdownButton(GUIContent.none, FocusType.Passive, GUILayout.Width(14)))
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
            EditorGUILayout.LabelField(".", GUILayout.Width(6));
            eventNameProp.stringValue = DrawPlaceholderFieldInline(eventNameProp.stringValue, "hit");
            EditorGUILayout.EndHorizontal();

            // eventId preview (read-only)
            var entry = _selectedMap.entries[_selectedEntryIndex];
            string previewId = !string.IsNullOrEmpty(entry.eventId) ? entry.eventId : "clip.hit";
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 eventId"), previewId);
            EditorGUI.EndDisabledGroup();

            // Validation
            if (!string.IsNullOrEmpty(categoryProp.stringValue) && !HapbeatEventEntry.IsValidSegment(categoryProp.stringValue))
                EditorGUILayout.HelpBox($"category: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
            if (!string.IsNullOrEmpty(eventNameProp.stringValue) && !HapbeatEventEntry.IsValidSegment(eventNameProp.stringValue))
                EditorGUILayout.HelpBox($"name: lowercase a-z, 0-9, -, _ only", MessageType.Warning);

            // Gain
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"),
                new GUIContent("Gain", "Output gain multiplier. 0.0 = silent, 1.0 = normal, 2.0 = maximum."));

            // --- Targeting ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Targeting", EditorStyles.miniBoldLabel);
            var targetProp = entryProp.FindPropertyRelative("target");
            ParseTarget(targetProp.stringValue, out string curPrefix, out int curPlayer, out string curPos);

            // Prefix
            string newPrefix = EditorGUILayout.TextField(
                new GUIContent("Prefix",
                    "Optional team/group prefix for large multi-team setups.\n" +
                    "Example: team_red, booth_a.\n" +
                    "Leave empty for most projects."),
                curPrefix);

            // Player
            int newPlayer = EditorGUILayout.IntField(
                new GUIContent("Player",
                    "Player number (1-99). Set -1 to target all players.\n" +
                    "For broadcast to all devices, set Player = -1 and Position = (none)."),
                curPlayer);

            // Position
            var posOptions = new string[HapbeatEventEntry.StandardPositions.Length + 1];
            var posValues = new string[posOptions.Length];
            posOptions[0] = "(none \u2014 all positions)";
            posValues[0] = "";
            for (int p = 0; p < HapbeatEventEntry.StandardPositions.Length; p++)
            {
                posOptions[p + 1] = $"{HapbeatEventEntry.PositionLabels[p]}";
                posValues[p + 1] = HapbeatEventEntry.StandardPositions[p];
            }
            int posIdx = System.Array.IndexOf(posValues, curPos);
            if (posIdx < 0) posIdx = 0;
            int newPosIdx = EditorGUILayout.Popup(
                new GUIContent("Position",
                    "Body position of the target device.\n" +
                    "Select (none) to target all positions for the selected player.\n" +
                    "For broadcast, set both Player = -1 and Position = (none)."),
                posIdx, posOptions);
            string newPos = posValues[newPosIdx];

            // Build and preview
            string builtTarget = BuildTargetFromParts(newPrefix, newPlayer, newPos);
            if (builtTarget != targetProp.stringValue)
                targetProp.stringValue = builtTarget;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 target"),
                string.IsNullOrEmpty(builtTarget) ? "(broadcast \u2014 all devices)" : builtTarget);
            EditorGUI.EndDisabledGroup();

            // Triggers in scene
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Triggers in Scene:", EditorStyles.miniBoldLabel);
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"  {info.typeName}", GUILayout.Width(70));
                    if (GUILayout.Button(info.gameObjectName, EditorStyles.linkLabel))
                    {
                        Selection.activeGameObject = info.trigger.gameObject;
                        EditorGUIUtility.PingObject(info.trigger.gameObject);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Notes (last)
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("notes"),
                new GUIContent("Notes", "Designer notes. Not sent to devices."));

            EditorGUIUtility.labelWidth = prevLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
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
