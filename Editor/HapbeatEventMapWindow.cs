#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

        /// <summary>
        /// Display labels for <see cref="HapticMode"/>. Order MUST match the enum
        /// declaration (Command, StreamClip, StreamSource) so the popup index
        /// round-trips cleanly. Uses the same FIRE/CLIP/LIVE vocabulary as
        /// hapbeat-studio's mode toggle for authoring continuity.
        /// </summary>
        private static readonly string[] s_ModeLabels =
        {
            "FIRE (Command)",
            "CLIP (Stream Clip)",
            "LIVE (Stream Source)",
        };

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
                    // Format: "[index] <mode-icon> <display-name>"
                    //   e.g. "[0] ▶ Click", "[1] ♪ Landing", "[2] ~ PushFeedback"
                    string name = !string.IsNullOrEmpty(entry.displayName) ? entry.displayName : "(new)";
                    string icon = entry.GetModeIcon();
                    string nameText = string.IsNullOrEmpty(icon)
                        ? $"[{i}] {name}"
                        : $"[{i}] {icon} {name}";

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

                    // Priority for extra info when the card is narrow:
                    //   1. eventId / summary (the "description" of what this entry does) — kept as long as possible
                    //   2. target — hidden first when no room
                    // Rationale: the description tells the designer WHAT the entry is;
                    // target is secondary routing info that can be inferred from context.
                    if (remaining > 30)
                    {
                        bool hasEid = !string.IsNullOrEmpty(eid);
                        bool hasTgt = tgtW > 0;

                        if (hasEid && hasTgt && remaining >= tgtW + 30)
                        {
                            // Plenty of room: eventId (clipped) + target on the right
                            float eidW = remaining - tgtW;
                            var eidRect = new Rect(nameRect.xMax, cardRect.y, eidW, cardRect.height);
                            GUI.Label(eidRect, eid, dimStyle);
                            var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                            GUI.Label(tgtRect, tgt, rightStyle);
                        }
                        else if (hasEid)
                        {
                            // Medium-narrow: keep the description (clipped), drop the target.
                            var eidRect = new Rect(nameRect.xMax, cardRect.y, remaining - 2, cardRect.height);
                            GUI.Label(eidRect, eid, dimStyle);
                        }
                        else if (hasTgt && remaining >= tgtW)
                        {
                            // No description available — fall back to showing target.
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

            // Mode — labelled to match Studio's FIRE / CLIP / LIVE shorthand so
            // authors see the same terminology across both tools. The underlying
            // enum names (Command / StreamClip / StreamSource) stay unchanged,
            // so serialized data is unaffected.
            var modeProp = entryProp.FindPropertyRelative("mode");
            int newModeIdx = EditorGUILayout.Popup(
                new GUIContent("Mode",
                    "FIRE: send eventId, device plays pre-flashed Kit clip.\n" +
                    "CLIP: SDK streams a Kit WAV over UDP as PCM16.\n" +
                    "LIVE: SDK captures an AudioSource and streams it."),
                modeProp.enumValueIndex,
                s_ModeLabels);
            modeProp.enumValueIndex = newModeIdx;

            var entry = _selectedMap.entries[_selectedEntryIndex];

            // Mode-specific fields
            switch (entry.mode)
            {
                case HapticMode.Command:
                    DrawCommandFields(entryProp, so);
                    DrawKitEventIdDropdown(
                        entryProp.FindPropertyRelative("category"),
                        entryProp.FindPropertyRelative("eventName"),
                        so);
                    break;
                case HapticMode.StreamClip:
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("streamClip"),
                        new GUIContent("Clip", "AudioClip to stream over UDP. Streamed as PCM16.\nDrag from your HapbeatKits/<kit>/stream-clips/ folder."));
                    DrawKitFolderHint("stream-clips");
                    break;
                case HapticMode.StreamSource:
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("streamClip"),
                        new GUIContent("Default Clip",
                            "AudioClip reference (optional). Studio exports this WAV to stream-clips/.\n" +
                            "Batch Setup uses it as the default AudioSource clip.\n" +
                            "Drag from your HapbeatKits/<kit>/stream-clips/ folder."));
                    DrawKitFolderHint("stream-clips");
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

        // Cached rects for drag&drop detection (keyed by SerializedProperty.propertyPath).
        // Captured during EventType.Repaint — Unity's drag events can report mousePosition
        // in a different coordinate space than GetControlRect() returns during drag-only
        // passes, so we must use a known-good rect captured from a full paint pass.
        private static Dictionary<string, Rect> _bindingBoxRectCache = new Dictionary<string, Rect>();
        private static Dictionary<string, Rect> _sourcePathRectCache = new Dictionary<string, Rect>();

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
                // Assign a fresh GUID so runtime HapbeatParameterBinding instances can
                // link to this preset by id (stable across list reordering).
                newProp.FindPropertyRelative("_id").stringValue = System.Guid.NewGuid().ToString("N");
                newProp.FindPropertyRelative("sourceTransformPath").stringValue = "";
                newProp.FindPropertyRelative("sourceProperty").enumValueIndex = (int)BindingSourceProperty.LocalPositionY;
                newProp.FindPropertyRelative("inputMin").floatValue = 0f;
                newProp.FindPropertyRelative("inputMax").floatValue = 1f;
                newProp.FindPropertyRelative("curveType").enumValueIndex = (int)BindingCurveType.Linear;
                newProp.FindPropertyRelative("outputParameter").enumValueIndex = (int)BindingOutputParameter.Volume;
                newProp.FindPropertyRelative("outputMin").floatValue = 0f;
                newProp.FindPropertyRelative("outputMax").floatValue = 1f;
                newProp.FindPropertyRelative("debugLog").boolValue = false;
                newProp.FindPropertyRelative("debugLogInterval").floatValue = 0.1f;
                newProp.FindPropertyRelative("debugLogChangeThreshold").floatValue = 0.02f;
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
                string boxKey = bp.propertyPath;

                // Backfill a GUID if missing (migration from pre-id presets, or
                // preset duplicated via Ctrl-D which copies the id from the source).
                var idProp = bp.FindPropertyRelative("_id");
                if (idProp != null && string.IsNullOrEmpty(idProp.stringValue))
                    idProp.stringValue = System.Guid.NewGuid().ToString("N");

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

                // Debug log — only emits when (value changed >= threshold) AND (interval elapsed).
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("debugLog"),
                    new GUIContent("Debug Log",
                        "Log input/output values to console. Only emitted when the " +
                        "normalized value changed by 'Change' or more AND at least " +
                        "'Interval' seconds passed since the last log line."));
                var dbgProp = bp.FindPropertyRelative("debugLog");
                if (dbgProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Interval",
                        "Minimum seconds between log lines (throttle)."));
                    var intervalProp = bp.FindPropertyRelative("debugLogInterval");
                    intervalProp.floatValue = EditorGUILayout.Slider(intervalProp.floatValue, 0.01f, 2f);
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Change",
                        "Minimum normalized-value change (0-1 scale) to emit a line. " +
                        "0 = log every interval regardless of change."));
                    var threshProp = bp.FindPropertyRelative("debugLogChangeThreshold");
                    if (threshProp != null)
                        threshProp.floatValue = EditorGUILayout.Slider(threshProp.floatValue, 0f, 1f);
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();

                // Capture the entire binding box rect during Repaint.
                // Used as a fallback drop zone so dragging anywhere inside the binding
                // box (not just the narrow Source Path line) updates sourceTransformPath.
                if (Event.current.type == EventType.Repaint)
                    _bindingBoxRectCache[boxKey] = GUILayoutUtility.GetLastRect();

                // Fallback drop handler — only activates if the text-field-level drop
                // didn't consume the event (e.g., mouse outside text field but inside box).
                if (_bindingBoxRectCache.TryGetValue(boxKey, out var boxRect))
                {
                    var pathPropForBox = bp.FindPropertyRelative("sourceTransformPath");
                    HandlePathDragDrop(boxRect, pathPropForBox, "box");
                }
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
        ///
        /// Drop detection uses a rect captured during EventType.Repaint and stored in
        /// <see cref="_sourcePathRectCache"/>. GetControlRect() returns different values
        /// during drag-only event passes inside nested scroll views, so the cached
        /// Repaint-era rect is the only reliable source of truth for hit-testing.
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

            // Cache the text-field rect during Repaint so drag events can hit-test against
            // a known-good rect. The rect returned by GetControlRect() during a drag-only
            // pass may be off because the layout cursor inside a scroll view hasn't been
            // advanced the same way as during Layout/Repaint.
            string key = pathProp.propertyPath;
            if (Event.current.type == EventType.Repaint)
                _sourcePathRectCache[key] = textRect;

            // Resolve drop rect: prefer the cached Repaint rect, fall back to live rect.
            Rect dragRect = _sourcePathRectCache.TryGetValue(key, out var cached) ? cached : textRect;

            // Handle drag&drop on text area only (picker has its own drag handling).
            // Runs BEFORE TextField so the drag event is consumed before IMGUI's text
            // selection logic can steal it.
            HandlePathDragDrop(dragRect, pathProp, "text");

            pathProp.stringValue = EditorGUI.TextField(textRect, pathProp.stringValue);

            // Object picker button (adjacent to text field, no gap)
            var picked = EditorGUI.ObjectField(pickerRect, null, typeof(GameObject), true) as GameObject;
            if (picked != null)
            {
                pathProp.stringValue = ComputeRelativePath(picked);
                GUI.FocusControl(null);
            }
        }

        // Toggle with Hapbeat > Debug > Log Drag&Drop menu (off by default).
        private const string kDragDebugPrefKey = "Hapbeat.EventMap.DragDropDebug";
        private static bool DragDebugEnabled => EditorPrefs.GetBool(kDragDebugPrefKey, false);

        [MenuItem("Hapbeat/Debug/Log Drag&Drop Events", false, 500)]
        private static void ToggleDragDebug()
        {
            bool v = !DragDebugEnabled;
            EditorPrefs.SetBool(kDragDebugPrefKey, v);
            Debug.Log($"[Hapbeat] Drag&Drop event logging: {(v ? "ON" : "OFF")}");
        }

        [MenuItem("Hapbeat/Debug/Log Drag&Drop Events", true)]
        private static bool ToggleDragDebugValidate()
        {
            Menu.SetChecked("Hapbeat/Debug/Log Drag&Drop Events", DragDebugEnabled);
            return true;
        }

        /// <summary>
        /// Accepts a drag&drop of a GameObject into the sourceTransformPath field.
        /// <paramref name="zone"/> identifies which hit-test rect matched (for diagnostics).
        /// </summary>
        private static void HandlePathDragDrop(Rect dropRect, SerializedProperty pathProp, string zone)
        {
            var e = Event.current;

            // Only process drag events
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;

            // Guard: if another handler already consumed this drag frame, skip.
            if (e.type == EventType.Used) return;

            bool inside = dropRect.Contains(e.mousePosition);

            if (DragDebugEnabled)
                Debug.Log($"[DragDrop:{zone}] event={e.type} pos={e.mousePosition} rect={dropRect} inside={inside} refs={DragAndDrop.objectReferences.Length}");

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
                pathProp.serializedObject.ApplyModifiedProperties();
                GUI.FocusControl(null);
                if (DragDebugEnabled)
                    Debug.Log($"[DragDrop:{zone}] DROPPED '{droppedGo.name}' → '{pathProp.stringValue}'");
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

        // ── Kit manifest scanner ─────────────────────────────────────────────

        /// <summary>
        /// Cached event metadata parsed from all kits' manifest.json files under
        /// Assets/HapbeatKits/. Invalidated on a short TTL and whenever a user
        /// clicks "Refresh" in the Event ID dropdown.
        /// </summary>
        private struct KitManifestEvent
        {
            public string kitId;       // folder name under HapbeatKits/, e.g. "hand-demo-kit"
            public string eventId;     // full ID, e.g. "impact.hit-soft"
            public string mode;        // "command" (default) / "stream_clip" / "stream_source"
            public string description; // optional description from manifest
        }

        private static List<KitManifestEvent> _cachedKitEvents;
        private static double _cachedKitEventsTime = -1;
        private const double KitEventCacheTtl = 3.0;

        private static List<KitManifestEvent> LoadKitManifestEvents()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cachedKitEvents != null && now - _cachedKitEventsTime < KitEventCacheTtl)
                return _cachedKitEvents;

            var result = new List<KitManifestEvent>();
            // Resolve via marker asset so the user can move/rename the folder freely.
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot) && AssetDatabase.IsValidFolder(kitsRoot))
            {
                foreach (string kitDir in AssetDatabase.GetSubFolders(kitsRoot))
                {
                    string manifestAssetPath = $"{kitDir}/manifest.json";
                    // AssetPath → absolute path ("Assets/..." is relative to project)
                    string absPath = Path.Combine(
                        Application.dataPath,
                        manifestAssetPath.Substring("Assets/".Length));
                    if (!File.Exists(absPath)) continue;
                    try
                    {
                        string json = File.ReadAllText(absPath);
                        string kitId = Path.GetFileName(kitDir);
                        ParseManifestEventsInto(json, kitId, result);
                    }
                    catch (System.Exception e)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[Hapbeat] Failed to parse {manifestAssetPath}: {e.Message}");
                    }
                }
            }

            _cachedKitEvents = result;
            _cachedKitEventsTime = now;
            return result;
        }

        private static void InvalidateKitManifestCache()
        {
            _cachedKitEvents = null;
            _cachedKitEventsTime = -1;
        }

        /// <summary>
        /// Extract the top-level "events" object from a manifest.json and push each
        /// entry's id/mode/description into <paramref name="output"/>. Uses brace-depth
        /// tracking rather than a JSON library so the SDK stays dependency-free.
        /// </summary>
        private static void ParseManifestEventsInto(string json, string kitId, List<KitManifestEvent> output)
        {
            // Locate the "events" : { ... } block at the top level.
            var eventsMatch = Regex.Match(json, "\"events\"\\s*:\\s*\\{");
            if (!eventsMatch.Success) return;

            int blockStart = eventsMatch.Index + eventsMatch.Length;
            int blockEnd = FindMatchingBrace(json, blockStart);
            if (blockEnd < 0) return;

            string block = json.Substring(blockStart, blockEnd - blockStart);

            // Walk top-level keys inside the events block.
            int pos = 0;
            while (pos < block.Length)
            {
                var keyMatch = Regex.Match(
                    block.Substring(pos), "\"([^\"]+)\"\\s*:\\s*\\{");
                if (!keyMatch.Success) break;

                string eventId = keyMatch.Groups[1].Value;
                int entryStart = pos + keyMatch.Index + keyMatch.Length;
                int entryEnd = FindMatchingBrace(block, entryStart);
                if (entryEnd < 0) break;

                string entryBody = block.Substring(entryStart, entryEnd - entryStart);

                string mode = "command"; // default per pack-format spec (absent mode = command)
                var modeMatch = Regex.Match(entryBody, "\"mode\"\\s*:\\s*\"([^\"]+)\"");
                if (modeMatch.Success) mode = modeMatch.Groups[1].Value;

                string description = "";
                var descMatch = Regex.Match(entryBody, "\"description\"\\s*:\\s*\"([^\"]*)\"");
                if (descMatch.Success) description = descMatch.Groups[1].Value;

                output.Add(new KitManifestEvent
                {
                    kitId = kitId,
                    eventId = eventId,
                    mode = mode,
                    description = description,
                });

                pos = entryEnd + 1;
            }
        }

        /// <summary>
        /// Given the index of the character AFTER an opening "{", return the index
        /// of its matching "}" (brace-balanced). Returns -1 if unbalanced.
        /// Naively ignores string-literal escaping; acceptable for well-formed Studio output.
        /// </summary>
        private static int FindMatchingBrace(string s, int openAfterIdx)
        {
            int depth = 1;
            int i = openAfterIdx;
            bool inString = false;
            while (i < s.Length && depth > 0)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
                else if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                if (depth > 0) i++;
            }
            return depth == 0 ? i : -1;
        }

        // ── Event ID dropdown (Command mode) ─────────────────────────────────

        /// <summary>
        /// Adds a "From Kit ▾" dropdown below the Event ID row that lists every
        /// command-mode event found in Assets/HapbeatKits/*/manifest.json. Picking
        /// an entry splits it into category + name and writes both fields.
        /// </summary>
        private static void DrawKitEventIdDropdown(
            SerializedProperty categoryProp,
            SerializedProperty eventNameProp,
            SerializedObject so)
        {
            var events = LoadKitManifestEvents()
                .Where(e => e.mode == "command")
                .ToList();

            string curId = BuildEventId(categoryProp.stringValue, eventNameProp.stringValue);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth);

            GUILayout.Label(
                events.Count == 0
                    ? "No Kit events found. Deploy a Kit from Studio."
                    : $"{events.Count} event(s) from {events.Select(e => e.kitId).Distinct().Count()} kit(s)",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("From Kit \u25be", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                var menu = new GenericMenu();
                if (events.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("No Kit manifests found"));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Open HapbeatKits folder"), false, () =>
                        RevealKitSubfolder(""));
                }
                else
                {
                    // Group by kit id, kits alphabetical, events alphabetical within kit
                    foreach (var group in events.GroupBy(e => e.kitId).OrderBy(g => g.Key))
                    {
                        foreach (var ev in group.OrderBy(e => e.eventId))
                        {
                            string menuPath = $"{group.Key}/{ev.eventId}";
                            bool isCurrent = ev.eventId == curId;
                            // capture-by-value for the closure
                            string capturedId = ev.eventId;
                            menu.AddItem(new GUIContent(menuPath), isCurrent, () =>
                            {
                                SplitEventId(capturedId, out string cat, out string name);
                                categoryProp.stringValue = cat;
                                eventNameProp.stringValue = name;
                                so.ApplyModifiedProperties();
                            });
                        }
                    }
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Refresh"), false, InvalidateKitManifestCache);
                }
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string BuildEventId(string cat, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (string.IsNullOrEmpty(cat)) return name;
            return $"{cat}.{name}";
        }

        private static void SplitEventId(string eventId, out string cat, out string name)
        {
            int dot = eventId.IndexOf('.');
            if (dot > 0)
            {
                cat = eventId.Substring(0, dot);
                name = eventId.Substring(dot + 1);
            }
            else
            {
                cat = "";
                name = eventId;
            }
        }

        // ── Kit folder hint ──────────────────────────────────────────────────

        /// <summary>
        /// Show a small inline hint + "Reveal" button pointing to the kit subfolder
        /// that corresponds to the current mode (clips/ for Command, stream-clips/ for
        /// StreamClip/StreamSource).  Searches Assets/HapbeatKits/ for installed kits
        /// and pings the first matching subfolder in the Project window.
        /// </summary>
        private static void DrawKitFolderHint(string subfolder)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth);
            string label = subfolder == "clips"
                ? "Look in: <kits-root>/<kit>/clips/"
                : "Look in: <kits-root>/<kit>/stream-clips/";
            GUILayout.Label(label, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(54)))
                RevealKitSubfolder(subfolder);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolve the kits root folder, offering to create it if no marker
        /// exists. The folder isn't auto-created on SDK import — so the first
        /// time a user clicks "Reveal" / "From Kit ▾" without having run the
        /// setup menu, they hit a create-or-cancel dialog here.
        /// </summary>
        /// <returns>Asset-relative path of the kits root, or <c>null</c> if the
        /// user cancelled / creation failed.</returns>
        private static string ResolveOrCreateKitsRoot()
        {
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot) && AssetDatabase.IsValidFolder(kitsRoot))
                return kitsRoot;

            bool confirmed = EditorUtility.DisplayDialog(
                "Create HapbeatKits Folder?",
                "HapbeatKits フォルダがまだ見つかりません。\n\n" +
                "これは Hapbeat Studio が Kit (manifest.json + 音源) を書き出す先です。\n" +
                $"作成する場合、既定の場所 {HapbeatKitsReadme.DefaultKitsRootPath}/ に作り、" +
                "マーカーアセット (HapbeatKitsReadme) を中に置きます。\n" +
                "後で好きな場所にフォルダごと移動してかまいません — マーカーで追跡します。\n\n" +
                "いま作成しますか？",
                "作成する", "キャンセル");
            if (!confirmed) return null;

            if (!HapbeatKitsFolderCreator.EnsureFolderAndReadme(openReadme: true))
                return null;

            return HapbeatKitsReadme.FindKitsRootPath();
        }

        private static void RevealKitSubfolder(string subfolder)
        {
            string kitsRoot = ResolveOrCreateKitsRoot();
            if (string.IsNullOrEmpty(kitsRoot)) return;

            // Empty subfolder = caller wants the kits root itself
            if (string.IsNullOrEmpty(subfolder))
            {
                var rootObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
                if (rootObj != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = rootObj;
                    EditorGUIUtility.PingObject(rootObj);
                }
                return;
            }

            // Collect all kit sub-directories under Assets/HapbeatKits/
            string[] kitDirs = AssetDatabase.GetSubFolders(kitsRoot);
            if (kitDirs == null || kitDirs.Length == 0)
            {
                // Folder exists but no kit has been deployed yet — open the root
                // so the user can see the README / drop a kit in.
                var rootObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
                if (rootObj != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = rootObj;
                    EditorGUIUtility.PingObject(rootObj);
                }
                UnityEngine.Debug.LogWarning(
                    $"[Hapbeat] {kitsRoot}/ contains no kits yet. " +
                    "Open Hapbeat Studio, point its working directory at this folder, then Save/Deploy a Kit.");
                return;
            }

            // Find the first kit that has the requested subfolder
            foreach (string kitDir in kitDirs)
            {
                string targetPath = $"{kitDir}/{subfolder}";
                UnityEngine.Object folder =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetPath);
                if (folder != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = folder;
                    EditorGUIUtility.PingObject(folder);
                    return;
                }
            }

            // Subfolder doesn't exist yet — ping the kits root and hint to Save/Deploy
            string hint = subfolder == "clips"
                ? "No clips/ folder found. Deploy a Kit that contains Command events."
                : "No stream-clips/ folder found. Deploy a Kit that contains StreamClip or StreamSource events.";
            UnityEngine.Debug.LogWarning($"[Hapbeat] {hint}");
            UnityEngine.Object kitsFolder =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
            if (kitsFolder != null)
            {
                EditorUtility.FocusProjectWindow();
                Selection.activeObject = kitsFolder;
                EditorGUIUtility.PingObject(kitsFolder);
            }
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
