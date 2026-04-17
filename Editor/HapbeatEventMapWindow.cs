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

        // Split ratio between list (left) and detail (right) panels
        private const string kSplitRatioKey = "HapbeatEventMap_SplitRatio";
        private float _splitRatio = 0.42f;
        private bool _isDraggingSplit;
        private const float SplitterWidth = 4f;

        // Clipboard for copy/paste
        private static HapbeatEventEntry _clipboardEntry;

        // Cached scene scan results
        private Dictionary<int, List<TriggerInfo>> _triggersByEntry = new Dictionary<int, List<TriggerInfo>>();
        private List<TriggerInfo> _orphanedTriggers = new List<TriggerInfo>();

        private struct TriggerInfo
        {
            public HapbeatTriggerBase trigger;
            public string gameObjectName;
            public string typeName;
            public List<string> wiredEvents; // e.g. "XRGrabInteractable.selectEntered"
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
            _splitRatio = EditorPrefs.GetFloat(kSplitRatioKey, 0.42f);
            _splitRatio = Mathf.Clamp(_splitRatio, 0.2f, 0.8f);
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

            float leftWidth = position.width * _splitRatio;

            EditorGUILayout.BeginHorizontal();

            // Left panel
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawEntryTable();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Splitter bar
            DrawSplitter();

            // Right panel
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);
            DrawSelectedEntryDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSplitter()
        {
            var rect = GUILayoutUtility.GetRect(SplitterWidth, SplitterWidth,
                GUILayout.ExpandHeight(true), GUILayout.Width(SplitterWidth));

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                _isDraggingSplit = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isDraggingSplit)
            {
                _splitRatio = Mathf.Clamp(e.mousePosition.x / position.width, 0.2f, 0.8f);
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _isDraggingSplit)
            {
                _isDraggingSplit = false;
                EditorPrefs.SetFloat(kSplitRatioKey, _splitRatio);
                e.Use();
            }
        }

        private void HandleKeyboard()
        {
            if (_selectedMap == null || _selectedMap.entries.Count == 0) return;
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.UpArrow)
            {
                _selectedEntryIndex = Mathf.Max(0, _selectedEntryIndex - 1);
                GUIUtility.keyboardControl = 0;
                e.Use();
                Repaint();
            }
            else if (e.keyCode == KeyCode.DownArrow)
            {
                _selectedEntryIndex = Mathf.Min(_selectedMap.entries.Count - 1, _selectedEntryIndex + 1);
                GUIUtility.keyboardControl = 0;
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
                    string icon = entry.GetModeIcon();
                    string nameText = string.IsNullOrEmpty(icon) ? $"[{i}] {name}" : $"[{i}] {name} {icon}";

                    ParseTarget(entry.target, out _, out int pl, out string pos);
                    string tgt = "all";
                    if (pl >= 1 || !string.IsNullOrEmpty(pos))
                    {
                        tgt = "";
                        if (pl >= 1) tgt += $"P{pl}";
                        if (!string.IsNullOrEmpty(pos))
                        {
                            if (tgt.Length > 0) tgt += "/";
                            tgt += pos.Replace("pos_", "");
                        }
                    }
                    if (hasTriggers) tgt += $" {_triggersByEntry[i].Count}\u25cf";

                    string eid = entry.GetSummary();

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

                    // Layout: [name (never clip)] [eventId (clip first)] [target (hide if no room)]
                    float pad = 4;
                    float nameW = nameStyle.CalcSize(new GUIContent(nameText)).x + pad;
                    float tgtW = !string.IsNullOrEmpty(tgt) ? rightStyle.CalcSize(new GUIContent(tgt)).x + pad : 0;
                    float totalW = cardRect.width - 4;
                    float remaining = totalW - nameW;

                    // Name always drawn
                    var nameRect = new Rect(cardRect.x + 2, cardRect.y, nameW, cardRect.height);
                    GUI.Label(nameRect, nameText, nameStyle);

                    // Only draw extra info if there's room after name
                    if (remaining > 30)
                    {
                        if (remaining >= tgtW + 30 && !string.IsNullOrEmpty(eid))
                        {
                            // Room for both: eventId (clipped) + target
                            float eidW = remaining - tgtW;
                            var eidRect = new Rect(nameRect.xMax, cardRect.y, eidW, cardRect.height);
                            GUI.Label(eidRect, eid, dimStyle);
                            if (tgtW > 0)
                            {
                                var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                                GUI.Label(tgtRect, tgt, rightStyle);
                            }
                        }
                        else if (remaining >= tgtW && tgtW > 0)
                        {
                            // Room for target only, skip eventId
                            var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                            GUI.Label(tgtRect, tgt, rightStyle);
                        }
                        // else: too narrow, show name only
                    }
                }

                GUIUtility.GetControlID(FocusType.Passive, cardRect);

                // Click anywhere to select + defocus text fields
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    GUIUtility.keyboardControl = 0; // defocus text fields
                    Event.current.Use();
                    Repaint();
                }

                // Right-click context menu
                if (Event.current.type == EventType.ContextClick
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    var ctx = new GenericMenu();
                    int idx = i;
                    ctx.AddItem(new GUIContent("Copy Entry Values"), false, () => CopyEntry(idx));
                    bool canPaste = _clipboardEntry != null;
                    if (canPaste)
                        ctx.AddItem(new GUIContent("Paste Entry Values"), false, () => PasteEntry(idx));
                    else
                        ctx.AddDisabledItem(new GUIContent("Paste Entry Values"));
                    ctx.AddSeparator("");
                    ctx.AddItem(new GUIContent("Add Entry Above"), false, () => InsertEntry(idx));
                    ctx.AddItem(new GUIContent("Add Entry Below"), false, () => InsertEntry(idx + 1));
                    ctx.AddItem(new GUIContent("Duplicate Entry"), false, () => DuplicateEntry(idx));
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

        private void CopyEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            var src = _selectedMap.entries[index];
            _clipboardEntry = new HapbeatEventEntry
            {
                mode = src.mode,
                displayName = src.displayName,
                category = src.category,
                eventName = src.eventName,
                streamClip = src.streamClip,
                gain = src.gain,
                target = src.target,
                group = src.group,
                notes = src.notes
            };
        }

        private void PasteEntry(int index)
        {
            if (_selectedMap == null || _clipboardEntry == null) return;
            if (index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Paste Hapbeat Event Entry");
            var dst = _selectedMap.entries[index];
            dst.mode = _clipboardEntry.mode;
            dst.category = _clipboardEntry.category;
            dst.eventName = _clipboardEntry.eventName;
            dst.streamClip = _clipboardEntry.streamClip;
            dst.gain = _clipboardEntry.gain;
            dst.target = _clipboardEntry.target;
            dst.group = _clipboardEntry.group;
            // Don't paste displayName or notes — keep the target entry's identity
            EditorUtility.SetDirty(_selectedMap);
        }

        private void DuplicateEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Duplicate Hapbeat Event Entry");
            var src = _selectedMap.entries[index];
            var dup = new HapbeatEventEntry
            {
                mode = src.mode,
                displayName = src.displayName + " (copy)",
                category = src.category,
                eventName = src.eventName,
                streamClip = src.streamClip,
                gain = src.gain,
                target = src.target,
                group = src.group,
                notes = src.notes
            };
            _selectedMap.entries.Insert(index + 1, dup);
            _selectedEntryIndex = index + 1;
            EditorUtility.SetDirty(_selectedMap);
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

            // Mode
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("mode"),
                new GUIContent("Mode", "Command: send eventId. StreamClip: stream AudioClip. StreamSource: capture AudioSource."));

            var entry = _selectedMap.entries[_selectedEntryIndex];

            // Mode-specific fields
            switch (entry.mode)
            {
                case HapticMode.Command:
                    DrawCommandFields(entryProp, so);
                    break;
                case HapticMode.StreamClip:
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("streamClip"),
                        new GUIContent("Clip", "AudioClip to stream over UDP. Streamed as PCM16."));
                    break;
                case HapticMode.StreamSource:
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("streamClip"),
                        new GUIContent("Default Clip", "Optional. Used when Batch Setup adds a new AudioSource."));
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("silentMode"),
                        new GUIContent("Silent Mode", "Mute speaker output. Audio captured for haptics only."));
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("loop"),
                        new GUIContent("Loop", "Loop the AudioSource playback."));
                    DrawBindingsList(entryProp);
                    break;
            }

            // Gain (all modes)
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"),
                new GUIContent("Gain",
                    "Master gain multiplier for this entry. 0.0 = silent, 1.0 = normal, 2.0 = maximum.\n\n" +
                    "For StreamSource: multiplied on top of Binding outputs.\n" +
                    "Example: Binding Volume output = 1.0, Gain = 0.5 → device receives audio × 0.5"));

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

            // Wiring in scene — grouped by GameObject
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Wiring:", EditorStyles.miniBoldLabel);

                // Group triggers by GameObject (skip destroyed)
                var byObject = new Dictionary<GameObject, List<string>>();
                bool needRescan = false;
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    if (info.trigger == null) { needRescan = true; continue; }
                    var go = info.trigger.gameObject;
                    if (!byObject.ContainsKey(go))
                        byObject[go] = new List<string>();
                    if (info.wiredEvents != null)
                        byObject[go].AddRange(info.wiredEvents);
                    if (byObject[go].Count == 0)
                        byObject[go].Add("(manual)");
                }
                if (needRescan)
                {
                    // Defer ScanScene to next frame to avoid mid-draw layout change
                    EditorApplication.delayCall += ScanScene;
                    // Do NOT early-return here — would cause GUILayout Begin/End mismatch.
                    // Continue drawing with the (possibly stale) list; next frame will refresh.
                }

                float nameW = 80;
                var sorted = byObject.OrderBy(kv => kv.Key.name).ToList();
                foreach (var kv in sorted)
                {
                    for (int w = 0; w < kv.Value.Count; w++)
                    {
                        var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                        float wireW = rect.width - nameW - 4;

                        // Show object name only on first line
                        if (w == 0)
                        {
                            if (GUI.Button(new Rect(rect.x, rect.y, nameW, rect.height),
                                kv.Key.name, EditorStyles.linkLabel))
                            {
                                Selection.activeGameObject = kv.Key;
                                EditorGUIUtility.PingObject(kv.Key);
                            }
                        }

                        GUI.Label(new Rect(rect.x + nameW + 4, rect.y, wireW, rect.height),
                            kv.Value[w], EditorStyles.miniLabel);
                    }
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

        private void DrawBindingsList(SerializedProperty entryProp)
        {
            var bindingsProp = entryProp.FindPropertyRelative("bindings");

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Parameter Bindings", EditorStyles.miniBoldLabel);
            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(22)))
            {
                bindingsProp.arraySize++;
                var newProp = bindingsProp.GetArrayElementAtIndex(bindingsProp.arraySize - 1);
                newProp.FindPropertyRelative("sourceTransformPath").stringValue = "";
                newProp.FindPropertyRelative("sourceProperty").enumValueIndex = (int)BindingSourceProperty.LocalPositionY;
                newProp.FindPropertyRelative("inputMin").floatValue = 0f;
                newProp.FindPropertyRelative("inputMax").floatValue = 1f;
                newProp.FindPropertyRelative("curveType").enumValueIndex = (int)BindingCurveType.Linear;
                newProp.FindPropertyRelative("outputParameter").enumValueIndex = (int)BindingOutputParameter.Volume;
                newProp.FindPropertyRelative("outputMin").floatValue = 0f;
                newProp.FindPropertyRelative("outputMax").floatValue = 1f;
            }
            EditorGUILayout.EndHorizontal();

            if (bindingsProp.arraySize == 0)
            {
                EditorGUILayout.LabelField("  (no bindings \u2014 click + to add)", EditorStyles.miniLabel);
                return;
            }

            // Wider label for binding fields (80 → 95)
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 95;

            int pendingDelete = -1;
            for (int i = 0; i < bindingsProp.arraySize; i++)
            {
                var bp = bindingsProp.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                var srcProp = bp.FindPropertyRelative("sourceProperty");
                var outProp = bp.FindPropertyRelative("outputParameter");
                string summary = $"#{i}  {(BindingSourceProperty)srcProp.enumValueIndex} \u2192 {(BindingOutputParameter)outProp.enumValueIndex}";
                EditorGUILayout.LabelField(summary, EditorStyles.miniBoldLabel);
                if (GUILayout.Button("\u2212", EditorStyles.miniButton, GUILayout.Width(22)))
                    pendingDelete = i;
                EditorGUILayout.EndHorizontal();

                var pathProp = bp.FindPropertyRelative("sourceTransformPath");
                DrawSourcePathWithDragDrop(pathProp);
                DrawSourcePathPingRow(pathProp);

                EditorGUILayout.PropertyField(srcProp,
                    new GUIContent("Property", "Which value to read from the source Transform/Rigidbody."));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Input Range",
                    "Source value at min/max. Input is normalized to 0-1 within this range.\n" +
                    "Example: LocalPositionY 0 (idle) → -0.01 (pressed) \u21d2 Min=0, Max=-0.01"));
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMin"), GUIContent.none);
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMax"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                var curveTypeProp = bp.FindPropertyRelative("curveType");
                EditorGUILayout.PropertyField(curveTypeProp,
                    new GUIContent("Curve", "Shape of input-to-output mapping."));
                if ((BindingCurveType)curveTypeProp.enumValueIndex == BindingCurveType.Custom)
                    EditorGUILayout.PropertyField(bp.FindPropertyRelative("customCurve"), new GUIContent("Custom Curve"));

                EditorGUILayout.PropertyField(outProp,
                    new GUIContent("Output",
                        "Volume: AudioSource.volume (0-1). Applied BEFORE capture.\n" +
                        "Pitch: AudioSource.pitch (-3 to 3). Vibration frequency.\n" +
                        "Pan: AudioSource.panStereo (-1 to 1). L/R balance.\n" +
                        "BridgeGain: HapbeatAudioBridge.Gain (0-2). Applied AFTER capture.\n\n" +
                        "Final device value = audio × Volume × BridgeGain × entry.gain"));
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Output Range",
                    "Target values at input min/max."));
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMin"), GUIContent.none);
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMax"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                // Debug log
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("debugLog"),
                    new GUIContent("Debug Log", "Log input/output values to console."));
                var dbgProp = bp.FindPropertyRelative("debugLog");
                if (dbgProp.boolValue)
                {
                    GUILayout.Space(8);
                    GUILayout.Label("Interval", GUILayout.Width(52));
                    var intervalProp = bp.FindPropertyRelative("debugLogInterval");
                    intervalProp.floatValue = EditorGUILayout.Slider(intervalProp.floatValue, 0.05f, 2f);
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.EndVertical();
            }

            // Deferred deletion after the loop to avoid GUI layout mismatch
            if (pendingDelete >= 0 && pendingDelete < bindingsProp.arraySize)
                bindingsProp.DeleteArrayElementAtIndex(pendingDelete);

            EditorGUIUtility.labelWidth = prevLabel;
        }

        /// <summary>
        /// Source Path text field with drag&drop support.
        /// The entire text area is a drop zone; a small object picker (◎) button
        /// on the right lets the user browse GameObjects as well.
        /// </summary>
        private static void DrawSourcePathWithDragDrop(SerializedProperty pathProp)
        {
            var rect = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            float pickerW = 22;

            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height),
                new GUIContent("Source Path",
                    "Relative path from target to source Transform.\n" +
                    "Empty / '.' = target itself.\n" +
                    "'Visual' = child named Visual.\n" +
                    "'Body/Head' = nested child.\n\n" +
                    "Drag a GameObject here or use the \u25ce picker button.\n" +
                    "If it's a descendant of the Hierarchy-selected object,\n" +
                    "the relative path is computed. Otherwise, the name is used."));

            var fullRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);
            var textRect = new Rect(fullRect.x, fullRect.y, fullRect.width - pickerW, fullRect.height);
            var pickerRect = new Rect(textRect.xMax, fullRect.y, pickerW, fullRect.height);

            // Handle drag&drop on text area only (picker has its own drag handling)
            // IMPORTANT: must run BEFORE TextField to intercept drag events
            HandlePathDragDrop(textRect, pathProp);

            pathProp.stringValue = EditorGUI.TextField(textRect, pathProp.stringValue);

            // Object picker button (adjacent to text field, no gap)
            var picked = EditorGUI.ObjectField(pickerRect, null, typeof(GameObject), true) as GameObject;
            if (picked != null)
            {
                pathProp.stringValue = ComputeRelativePath(picked);
                GUI.FocusControl(null);
            }
        }

        // Debug flag for drag-drop troubleshooting
        private const bool kDragDebug = true;

        private static void HandlePathDragDrop(Rect dropRect, SerializedProperty pathProp)
        {
            var e = Event.current;

            // Only process drag events
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;

            bool inside = dropRect.Contains(e.mousePosition);
            if (kDragDebug)
                Debug.Log($"[DragDrop] event={e.type} pos={e.mousePosition} rect={dropRect} inside={inside} refs={DragAndDrop.objectReferences.Length}");

            if (!inside) return;

            GameObject droppedGo = null;
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj is GameObject go) { droppedGo = go; break; }
            }

            DragAndDrop.visualMode = droppedGo != null
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (e.type == EventType.DragPerform && droppedGo != null)
            {
                DragAndDrop.AcceptDrag();
                pathProp.stringValue = ComputeRelativePath(droppedGo);
                GUI.FocusControl(null);
            }
            e.Use();
        }

        /// <summary>
        /// Draws a small row showing the resolved GameObject for the source path
        /// (based on wiring triggers or Hierarchy selection). Click to ping.
        /// </summary>
        private void DrawSourcePathPingRow(SerializedProperty pathProp)
        {
            string path = pathProp.stringValue;

            // Try resolving via scene triggers bound to this entry, fall back to Selection.
            var resolved = ResolvePathInScene(path);

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(rect.x, rect.y, labelW, rect.height);
            var valueRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(" \u2192 resolves to",
                "Shows which GameObject the path resolves to, using scene triggers or Hierarchy selection as root. Click to ping."));

            if (resolved != null)
            {
                if (GUI.Button(valueRect, resolved.name, EditorStyles.linkLabel))
                {
                    Selection.activeGameObject = resolved;
                    EditorGUIUtility.PingObject(resolved);
                }
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.LabelField(valueRect,
                    string.IsNullOrEmpty(path) ? "(self)" : "(not resolvable \u2014 no matching target in scene)",
                    EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
        }

        /// <summary>
        /// Try to resolve a source path relative to:
        /// 1. GameObjects of triggers bound to the currently selected entry
        /// 2. The currently selected Hierarchy object
        /// Returns the first resolved GameObject or null.
        /// </summary>
        private GameObject ResolvePathInScene(string path)
        {
            var candidates = new List<Transform>();

            // Triggers for the currently selected entry
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    if (info.trigger != null)
                        candidates.Add(info.trigger.transform);
                }
            }

            // Hierarchy selection fallback
            if (Selection.activeGameObject != null)
                candidates.Add(Selection.activeGameObject.transform);

            foreach (var root in candidates)
            {
                if (root == null) continue;
                if (string.IsNullOrEmpty(path) || path == ".")
                    return root.gameObject;
                var child = root.Find(path);
                if (child != null) return child.gameObject;
            }
            return null;
        }

        /// <summary>
        /// Compute a relative path from the currently-selected Hierarchy object to the dropped one.
        /// Falls back to the dropped object's name if no ancestor match is found.
        /// </summary>
        private static string ComputeRelativePath(GameObject dropped)
        {
            // Use the Hierarchy-selected object as the target root
            GameObject selected = Selection.activeGameObject;
            if (selected != null && selected != dropped)
            {
                Transform cursor = dropped.transform;
                var segments = new List<string>();
                while (cursor != null && cursor.gameObject != selected)
                {
                    segments.Insert(0, cursor.name);
                    cursor = cursor.parent;
                }
                if (cursor != null && cursor.gameObject == selected)
                    return string.Join("/", segments);
            }

            // Fallback: use the dropped object's name only
            return dropped.name;
        }

        private void DrawCommandFields(SerializedProperty entryProp, SerializedObject so)
        {
            var categoryProp = entryProp.FindPropertyRelative("category");
            var eventNameProp = entryProp.FindPropertyRelative("eventName");

            // Manual Rect layout for consistent alignment
            var lineRect = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            float dropW = 16;
            float fieldStart = lineRect.x + labelW + 2;
            float totalFieldW = lineRect.width - labelW - 2;
            float catW = totalFieldW * 0.4f - dropW;
            float nameW = totalFieldW * 0.6f;

            // Label
            EditorGUI.LabelField(new Rect(lineRect.x, lineRect.y, labelW, lineRect.height),
                new GUIContent("Event ID", "Composed as category.name. Sent to devices."));

            // Category text field
            categoryProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart, lineRect.y, catW, lineRect.height),
                categoryProp.stringValue, "clip");

            // Category dropdown button
            var dropRect = new Rect(fieldStart + catW, lineRect.y, dropW, lineRect.height);
            if (EditorGUI.DropdownButton(dropRect, GUIContent.none, FocusType.Passive))
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

            // Event name text field
            eventNameProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart + catW + dropW + 2, lineRect.y, nameW - 2, lineRect.height),
                eventNameProp.stringValue, "hit");

            // Preview
            var entry = _selectedMap.entries[_selectedEntryIndex];
            string previewId = !string.IsNullOrEmpty(entry.eventId) ? entry.eventId : "clip.hit";
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 eventId"), previewId);
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(categoryProp.stringValue) && !HapbeatEventEntry.IsValidSegment(categoryProp.stringValue))
                EditorGUILayout.HelpBox("category: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
            if (!string.IsNullOrEmpty(eventNameProp.stringValue) && !HapbeatEventEntry.IsValidSegment(eventNameProp.stringValue))
                EditorGUILayout.HelpBox("name: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
        }

        /// <summary>Draw a text field with placeholder at a specific Rect.</summary>
        private static string DrawPlaceholderRect(Rect rect, string value, string placeholder)
        {
            string result = EditorGUI.TextField(rect, value);
            if (string.IsNullOrEmpty(result) && !EditorGUIUtility.editingTextField)
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            return result;
        }

        private void ScanScene()
        {
            _triggersByEntry.Clear();
            _orphanedTriggers.Clear();

            if (_selectedMap == null) return;

            var allTriggers = FindObjectsByType<HapbeatTriggerBase>(FindObjectsSortMode.None);
            foreach (var trigger in allTriggers)
            {
                if (trigger == null) continue;
                if (trigger.EventMap != _selectedMap) continue;

                var info = new TriggerInfo
                {
                    trigger = trigger,
                    gameObjectName = trigger.gameObject.name,
                    typeName = GetTriggerTypeName(trigger),
                    wiredEvents = FindWiredEvents(trigger)
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

        /// <summary>
        /// Find which UnityEvent fields on sibling components reference this trigger.
        /// Returns list like ["XRGrabInteractable.selectEntered", "Button.onClick"]
        /// </summary>
        private List<string> FindWiredEvents(HapbeatTriggerBase trigger)
        {
            var result = new List<string>();
            var go = trigger.gameObject;

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is HapbeatTriggerBase || comp is HapbeatEvent || comp is HapbeatAudioBridge)
                    continue;

                string compName = comp.GetType().Name;
                var so = new SerializedObject(comp);
                var iter = so.GetIterator();

                while (iter.NextVisible(true))
                {
                    if (iter.propertyType != SerializedPropertyType.Generic || iter.depth > 2)
                        continue;

                    // Check for persistent calls
                    SerializedProperty callsProp = null;
                    var directCalls = iter.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    if (directCalls != null)
                        callsProp = directCalls;
                    else
                    {
                        var inner = iter.FindPropertyRelative("m_Event");
                        if (inner != null)
                            callsProp = inner.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    }

                    if (callsProp == null || !callsProp.isArray) continue;

                    for (int c = 0; c < callsProp.arraySize; c++)
                    {
                        var call = callsProp.GetArrayElementAtIndex(c);
                        var targetRef = call.FindPropertyRelative("m_Target");
                        if (targetRef != null && targetRef.objectReferenceValue == trigger)
                        {
                            string method = call.FindPropertyRelative("m_MethodName")?.stringValue ?? "?";
                            // Clean field name: m_SelectEntered → selectEntered
                            string fieldName = iter.name;
                            if (fieldName.StartsWith("m_"))
                                fieldName = fieldName.Substring(2);
                            if (fieldName.StartsWith("First") || fieldName.StartsWith("Last"))
                                fieldName = fieldName.Substring(5);
                            if (fieldName.Length > 0)
                                fieldName = char.ToLower(fieldName[0]) + fieldName.Substring(1);
                            result.Add($"{compName}.{fieldName} \u2192 {method}");
                        }
                    }
                }
            }

            return result;
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
