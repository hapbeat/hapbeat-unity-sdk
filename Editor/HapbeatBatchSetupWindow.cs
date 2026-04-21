#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Batch setup tool for adding/updating/removing Hapbeat triggers on multiple GameObjects.
    /// Scans target objects to discover available UnityEvent fields on any component.
    /// Supports HapbeatUnityEventTrigger, HapbeatCollisionTrigger and HapbeatSequenceTrigger.
    ///
    /// <para>
    /// Reference workflow: drop a GameObject that already has a Hapbeat trigger
    /// into the <b>Reference</b> field and its settings are imported into the
    /// manual UI below — event map, entry, wiring selections, and all. Edit
    /// what you want to differ (e.g. change the entry from "grab" to "snap")
    /// and press Apply. Clear the reference with the × button next to it.
    /// </para>
    /// </summary>
    public class HapbeatBatchSetupWindow : EditorWindow
    {
        private enum TriggerType { UnityEventTrigger, CollisionTrigger, SequenceTrigger }
        private TriggerType _triggerType = TriggerType.UnityEventTrigger;

        // --- Shared ---
        private HapbeatEventMap _eventMap;
        private int _entryIndex;
        private float _cooldown;

        // --- CollisionTrigger ---
        private HapbeatCollisionTrigger.TriggerEvent _collisionEvent = HapbeatCollisionTrigger.TriggerEvent.TriggerEnter;
        private HapbeatCollisionTrigger.GainMode _gainMode = HapbeatCollisionTrigger.GainMode.Fixed;
        private float _velocityThreshold = 0.5f;
        private float _maxVelocity = 5f;

        // --- SequenceTrigger ---
        // -1 means "(none)" — only the Loop phase uses the shared _entryIndex above.
        private int _onStartEntryIndex = -1;
        private int _onStopEntryIndex = -1;
        // -1 = auto (use On Start clip duration). See HapbeatSequenceTrigger._startShotDelay.
        private float _startShotDelay = -1f;

        // --- Apply policy ---
        // When true, any existing HapbeatTriggerBase on a target GameObject (and
        // UnityEvent wires pointing to them) is removed before Apply adds the new
        // trigger. Avoids the "selectEntered has two Fire calls" duplication that
        // otherwise happens when re-applying with a different entry.
        private bool _replaceExisting = true;

        // --- Targets ---
        private List<GameObject> _targets = new List<GameObject>();
        private Vector2 _targetScrollPos;
        private Vector2 _mainScrollPos;

        // --- Reference (import source) ---
        private GameObject _referenceObject;
        private string _referenceSummary = "";

        // --- Scanned events ---
        private List<DetectedEventGroup> _detectedEvents = new List<DetectedEventGroup>();
        private Vector2 _eventScrollPos;
        // Track target list hash to auto-rescan on change
        private int _lastTargetHash;

        [Serializable]
        private struct DetectedEventGroup
        {
            public string displayName;
            public string componentType;
            public string fieldPath;
            public int objectCount;
            public bool selected;
            public int methodIndex; // 0=Fire, 1=Stop
        }

        private static readonly string[] WireMethods = { "Fire", "Stop" };

        // Persisted selection keys (survives editor restart)
        private const string kSelectedEventsKey = "HapbeatBatchSetup_SelectedEvents";

        [MenuItem("Hapbeat/Batch Setup", false, 20)]
        [MenuItem("Window/Hapbeat/Batch Setup")]
        public static void ShowWindow()
        {
            var w = GetWindow<HapbeatBatchSetupWindow>("Hapbeat Batch Setup");
            w.minSize = new Vector2(430, 400);
        }

        private void OnSelectionChange() => Repaint();

        private void OnGUI()
        {
            _mainScrollPos = EditorGUILayout.BeginScrollView(_mainScrollPos);

            DrawTargetSection();
            EditorGUILayout.Space(6);
            DrawReferenceSection();
            EditorGUILayout.Space(6);
            DrawTriggerTypeSection();
            EditorGUILayout.Space(6);
            DrawTriggerSettings();
            EditorGUILayout.Space(6);

            if (_triggerType == TriggerType.UnityEventTrigger
                || _triggerType == TriggerType.SequenceTrigger)
            {
                AutoScanIfNeeded();
                DrawEventWiringSection();
            }

            EditorGUILayout.Space(10);
            DrawActions();

            EditorGUILayout.EndScrollView();
        }

        // =====================================================================
        // Targets
        // =====================================================================

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("Target GameObjects", EditorStyles.boldLabel);

            var dropRect = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            var dropStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            GUI.Box(dropRect, "Drag & Drop GameObjects here", dropStyle);
            HandleDragDrop(dropRect);

            EditorGUILayout.BeginHorizontal();
            int selCount = Selection.gameObjects?.Length ?? 0;
            if (GUILayout.Button($"Add from Selection ({selCount})", GUILayout.Height(20)))
            {
                if (Selection.gameObjects != null)
                    foreach (var go in Selection.gameObjects)
                        if (go != null && !_targets.Contains(go))
                            _targets.Add(go);
            }
            if (_targets.Count > 0 && GUILayout.Button("Clear", GUILayout.Width(50), GUILayout.Height(20)))
                _targets.Clear();
            EditorGUILayout.EndHorizontal();

            if (_targets.Count > 0)
            {
                _targetScrollPos = EditorGUILayout.BeginScrollView(
                    _targetScrollPos, GUILayout.MaxHeight(100));
                for (int i = 0; i < _targets.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    var next = (GameObject)EditorGUILayout.ObjectField(
                        _targets[i], typeof(GameObject), true, GUILayout.Height(18));
                    if (next != _targets[i])
                    {
                        if (next == null) { _targets.RemoveAt(i); i--; }
                        else if (!_targets.Contains(next)) _targets[i] = next;
                        EditorGUILayout.EndHorizontal();
                        continue;
                    }
                    if (GUILayout.Button("\u00d7", GUILayout.Width(18), GUILayout.Height(18)))
                    { _targets.RemoveAt(i); i--; }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void HandleDragDrop(Rect dropRect)
        {
            var evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition)) return;
            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.Any(o => o is GameObject)
                    ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (var obj in DragAndDrop.objectReferences)
                    if (obj is GameObject go && !_targets.Contains(go))
                        _targets.Add(go);
                evt.Use();
            }
        }

        // =====================================================================
        // Reference (import source)
        //
        // The Reference field lets the user pick a GameObject that already has
        // a Hapbeat trigger set up and IMPORT its configuration into the manual
        // UI below. After import the user can tweak any field (e.g. swap the
        // event) and run Apply — the tweaked copy lands on the targets.
        //
        // This replaces the old "Clone" button that applied the ref's settings
        // verbatim with no chance to edit first — its main pain-point per user
        // feedback.
        // =====================================================================

        private void DrawReferenceSection()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Reference (import source)", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (_referenceObject != null)
                {
                    if (GUILayout.Button("\u00d7 Clear", EditorStyles.miniButton, GUILayout.Width(60)))
                    {
                        _referenceObject = null;
                        _referenceSummary = "";
                    }
                }
            }

            var newRef = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Reference",
                    "Drag an object that already has a Hapbeat trigger to import its full " +
                    "batch-setup configuration (trigger type, event map, entry, cooldown, " +
                    "wire selections, sequence phases) into the fields below.\n\n" +
                    "After import you can tweak anything and run Apply — the modified copy " +
                    "is written to the targets. Use \u00d7 Clear to remove the link."),
                _referenceObject, typeof(GameObject), true);

            if (newRef != _referenceObject)
            {
                _referenceObject = newRef;
                if (newRef != null)
                    ImportFromReference(newRef);
                else
                    _referenceSummary = "";
            }

            if (!string.IsNullOrEmpty(_referenceSummary))
                EditorGUILayout.HelpBox(_referenceSummary, MessageType.Info);
        }

        /// <summary>
        /// Import the first HapbeatTriggerBase on <paramref name="refObj"/> into
        /// the window's manual-entry fields (trigger type, event map, entry,
        /// cooldown, sequence phases, wire selections). Subsequent triggers are
        /// ignored with a warning — batch setup operates on one trigger at a time.
        /// </summary>
        private void ImportFromReference(GameObject refObj)
        {
            var triggers = refObj.GetComponents<HapbeatTriggerBase>();
            if (triggers == null || triggers.Length == 0)
            {
                _referenceSummary = "\u26a0 Reference has no Hapbeat triggers to import.";
                return;
            }

            var first = triggers[0];
            var so = new SerializedObject(first);

            if (first is HapbeatCollisionTrigger)
                _triggerType = TriggerType.CollisionTrigger;
            else if (first is HapbeatSequenceTrigger)
                _triggerType = TriggerType.SequenceTrigger;
            else
                _triggerType = TriggerType.UnityEventTrigger;

            _eventMap = first.EventMap;
            _entryIndex = first.EntryIndex;
            var cdProp = so.FindProperty("_cooldown");
            _cooldown = cdProp != null ? cdProp.floatValue : 0f;

            if (first is HapbeatSequenceTrigger)
            {
                _onStartEntryIndex = so.FindProperty("_onStartEntryIndex")?.intValue ?? -1;
                _onStopEntryIndex = so.FindProperty("_onStopEntryIndex")?.intValue ?? -1;
                _startShotDelay = so.FindProperty("_startShotDelay")?.floatValue ?? -1f;
            }

            if (first is HapbeatCollisionTrigger coll)
            {
                var cso = new SerializedObject(coll);
                _collisionEvent = (HapbeatCollisionTrigger.TriggerEvent)
                    cso.FindProperty("_triggerEvent").enumValueIndex;
                _gainMode = (HapbeatCollisionTrigger.GainMode)
                    cso.FindProperty("_gainMode").enumValueIndex;
                _velocityThreshold = cso.FindProperty("_velocityThreshold")?.floatValue ?? 0.5f;
                _maxVelocity = cso.FindProperty("_maxVelocity")?.floatValue ?? 5f;
            }

            // Import wire selections: for every UnityEvent on refObj that points
            // at `first`, mark the matching DetectedEventGroup as selected with
            // the corresponding method. We can't fully pre-populate
            // _detectedEvents yet (that needs target-side scan), so we stash the
            // ref's wires in EditorPrefs so AutoScanIfNeeded picks them up.
            var refWires = new Dictionary<string, int>(); // "ComponentFullName|fieldName" → method index
            foreach (var comp in refObj.GetComponents<Component>())
            {
                if (comp == null || comp is HapbeatTriggerBase) continue;
                var cso = new SerializedObject(comp);
                var iter = cso.GetIterator();
                while (iter.NextVisible(true))
                {
                    var callsProp = FindCalls(iter);
                    if (callsProp == null) continue;
                    for (int c = 0; c < callsProp.arraySize; c++)
                    {
                        var call = callsProp.GetArrayElementAtIndex(c);
                        if (call.FindPropertyRelative("m_Target").objectReferenceValue == first)
                        {
                            string method = call.FindPropertyRelative("m_MethodName").stringValue;
                            int methodIdx = Array.IndexOf(WireMethods, method);
                            if (methodIdx < 0) methodIdx = 0;
                            refWires[$"{comp.GetType().FullName}|{iter.name}"] = methodIdx;
                        }
                    }
                }
            }

            // Persist so the target-scan can match on next frame.
            SavePrefWires(refWires);
            _lastTargetHash = 0; // force a fresh scan
            _detectedEvents.Clear();

            string skip = triggers.Length > 1
                ? $" (and {triggers.Length - 1} more trigger(s) ignored — import operates on one)"
                : "";
            _referenceSummary =
                $"Imported: {first.GetType().Name}, " +
                $"{(_eventMap != null ? _eventMap.name : "no map")} / entry #{_entryIndex}, " +
                $"{refWires.Count} wire(s){skip}";
        }

        private SerializedProperty FindCalls(SerializedProperty prop)
        {
            var direct = prop.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (direct != null) return direct;
            var inner = prop.FindPropertyRelative("m_Event");
            if (inner != null) return inner.FindPropertyRelative("m_PersistentCalls.m_Calls");
            return null;
        }

        // =====================================================================
        // Trigger Type
        // =====================================================================

        private void DrawTriggerTypeSection()
        {
            _triggerType = (TriggerType)EditorGUILayout.EnumPopup("Trigger Type", _triggerType);

            string desc = _triggerType switch
            {
                TriggerType.UnityEventTrigger =>
                    "UnityEvent に接続して発火。ボタンクリック等の単発イベント向け。",
                TriggerType.CollisionTrigger =>
                    "物理衝突/トリガーで発火。投げた物が当たる等の物理接触向け。",
                TriggerType.SequenceTrigger =>
                    "Fire()/Stop() の 3 フェーズ (On Start / Loop / On Stop) を 1 つにまとめたトリガー。\n" +
                    "XR の掴む/ホールド/離す (firstSelectEntered / lastSelectExited) に配線すると、\n" +
                    "つかむ瞬間 + 保持中のループ + 離す余韻を 1 コンポーネントで扱える。",
                _ => ""
            };
            if (!string.IsNullOrEmpty(desc))
                EditorGUILayout.HelpBox(desc, MessageType.None);
        }

        // =====================================================================
        // Trigger Settings
        // =====================================================================

        private void DrawTriggerSettings()
        {
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

            _eventMap = (HapbeatEventMap)EditorGUILayout.ObjectField(
                "Event Map", _eventMap, typeof(HapbeatEventMap), false);

            if (_eventMap != null && _eventMap.entries.Count > 0)
            {
                string[] names = _eventMap.GetDisplayNames();

                if (_triggerType == TriggerType.SequenceTrigger)
                {
                    _onStartEntryIndex = DrawOptionalEntryPopup(
                        "On Start",
                        "Optional one-shot fired by Fire() — the \"attack\" moment (grab click, enter ping).\n" +
                        "Use Command or StreamClip mode; (none) to skip.",
                        _onStartEntryIndex, names);

                    _entryIndex = Mathf.Clamp(_entryIndex, 0, names.Length - 1);
                    _entryIndex = EditorGUILayout.Popup(
                        new GUIContent("Loop (hold)",
                            "The entry that plays between Fire() and Stop(). Usually a " +
                            "StreamClip with Loop=true and (optionally) parameter bindings " +
                            "for dynamic modulation. Required."),
                        _entryIndex, names);

                    _onStopEntryIndex = DrawOptionalEntryPopup(
                        "On Stop",
                        "Optional one-shot fired by Stop() — the \"release\" moment (release thud, exit ping).\n" +
                        "Use Command or StreamClip mode; (none) to skip.",
                        _onStopEntryIndex, names);

                    DrawStartShotDelayField();

                    var loopEntry = _eventMap.GetEntry(_entryIndex);
                    if (loopEntry != null && loopEntry.mode == HapticMode.StreamClip && !loopEntry.loop)
                    {
                        EditorGUILayout.HelpBox(
                            "Loop entry is a StreamClip without loop=true. The hold phase will end " +
                            "when the clip finishes. Enable Loop on the entry to keep it going.",
                            MessageType.Info);
                    }
                }
                else
                {
                    _entryIndex = Mathf.Clamp(_entryIndex, 0, names.Length - 1);
                    _entryIndex = EditorGUILayout.Popup("Event", _entryIndex, names);

                    var entry = _eventMap.GetEntry(_entryIndex);
                    if (entry != null && !string.IsNullOrEmpty(entry.eventId))
                    {
                        EditorGUI.indentLevel++;
                        EditorGUI.BeginDisabledGroup(true);
                        EditorGUILayout.TextField("Event ID", entry.eventId);
                        EditorGUI.EndDisabledGroup();
                        EditorGUI.indentLevel--;
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    _eventMap == null ? "Event Map を設定してください。"
                                      : "Event Map にエントリがありません。",
                    MessageType.Warning);
            }

            _cooldown = EditorGUILayout.FloatField("Cooldown", _cooldown);

            if (_triggerType == TriggerType.CollisionTrigger)
            {
                _collisionEvent = (HapbeatCollisionTrigger.TriggerEvent)
                    EditorGUILayout.EnumPopup("Physics Event", _collisionEvent);
                _gainMode = (HapbeatCollisionTrigger.GainMode)
                    EditorGUILayout.EnumPopup("Gain Mode", _gainMode);
                if (_gainMode == HapbeatCollisionTrigger.GainMode.VelocityScaled)
                {
                    _velocityThreshold = EditorGUILayout.FloatField("Velocity Threshold", _velocityThreshold);
                    _maxVelocity = EditorGUILayout.FloatField("Max Velocity", _maxVelocity);
                }
            }

            _replaceExisting = EditorGUILayout.ToggleLeft(
                new GUIContent("Replace existing Hapbeat triggers on target",
                    "When Apply runs, remove any existing Hapbeat triggers (and their " +
                    "UnityEvent wires / parameter bindings) on each target GameObject " +
                    "before adding the new one.\n\n" +
                    "Recommended when changing an entry — otherwise the old trigger " +
                    "and its wires stick around and you get duplicate haptics."),
                _replaceExisting);
        }

        /// <summary>
        /// Compact row for the "Start → Loop delay" field. Lets the user switch
        /// between auto (-1, = on-start clip duration), no-delay (0), and an
        /// explicit positive value without juggling a popup + float field.
        /// </summary>
        private void DrawStartShotDelayField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool isAuto = _startShotDelay < 0f;
                var label = new GUIContent("Start → Loop Delay",
                    "Seconds to wait between the On Start one-shot and the Loop phase.\n" +
                    "Auto = match the On Start clip's duration (so the one-shot finishes before " +
                    "the loop stream takes over on the device).\n" +
                    "Workaround for a device-firmware rapid-stream-transition issue.");
                bool newIsAuto = EditorGUILayout.ToggleLeft(label, isAuto, GUILayout.Width(200));
                if (newIsAuto != isAuto)
                {
                    _startShotDelay = newIsAuto ? -1f : 0f;
                    isAuto = newIsAuto;
                }

                using (new EditorGUI.DisabledScope(isAuto))
                {
                    float shown = isAuto ? 0f : Mathf.Max(0f, _startShotDelay);
                    float edited = EditorGUILayout.FloatField(shown);
                    if (!isAuto) _startShotDelay = Mathf.Max(0f, edited);
                    EditorGUILayout.LabelField("s", GUILayout.Width(16));
                }
            }
            if (_startShotDelay < 0f)
                EditorGUILayout.LabelField("  (auto = On Start clip duration)", EditorStyles.miniLabel);
        }

        /// <summary>
        /// Popup that maps -1 to a leading "(none)" option. Returns -1 or the
        /// selected entry index. Used for the SequenceTrigger's optional phases.
        /// </summary>
        private static int DrawOptionalEntryPopup(string label, string tooltip, int current, string[] entryNames)
        {
            string[] options = new string[entryNames.Length + 1];
            options[0] = "(none)";
            System.Array.Copy(entryNames, 0, options, 1, entryNames.Length);

            int popupIdx = Mathf.Clamp(current + 1, 0, options.Length - 1);
            int newIdx = EditorGUILayout.Popup(new GUIContent(label, tooltip), popupIdx, options);
            return newIdx - 1;
        }

        // =====================================================================
        // Event Wiring — auto-scan + persist selections
        // =====================================================================

        private void AutoScanIfNeeded()
        {
            int hash = ComputeTargetHash();
            if (hash != _lastTargetHash && _targets.Any(t => t != null))
            {
                ScanTargetsForEvents(_targets.Where(t => t != null).ToList());
                _lastTargetHash = hash;
            }
        }

        private int ComputeTargetHash()
        {
            int h = _targets.Count;
            foreach (var t in _targets)
                if (t != null) h = h * 31 + t.GetInstanceID();
            return h;
        }

        private void DrawEventWiringSection()
        {
            EditorGUILayout.LabelField("Event Wiring", EditorStyles.boldLabel);

            if (_detectedEvents.Count == 0)
            {
                EditorGUILayout.LabelField(
                    _targets.Any(t => t != null) ? "UnityEvent が見つかりませんでした。"
                                                 : "ターゲットを追加すると自動スキャンします。",
                    EditorStyles.miniLabel);
                return;
            }

            _eventScrollPos = EditorGUILayout.BeginScrollView(
                _eventScrollPos, GUILayout.MaxHeight(160));
            for (int i = 0; i < _detectedEvents.Count; i++)
            {
                var e = _detectedEvents[i];
                EditorGUILayout.BeginHorizontal();
                bool newSel = EditorGUILayout.Toggle(e.selected, GUILayout.Width(16));
                EditorGUILayout.LabelField(e.displayName);
                EditorGUILayout.LabelField($"({e.objectCount})", EditorStyles.miniLabel, GUILayout.Width(28));
                int newMethod = EditorGUILayout.Popup(e.methodIndex, WireMethods, GUILayout.Width(55));
                EditorGUILayout.EndHorizontal();

                if (newSel != e.selected || newMethod != e.methodIndex)
                {
                    e.selected = newSel;
                    e.methodIndex = newMethod;
                    _detectedEvents[i] = e;
                    SaveEventSelections();
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Select All", EditorStyles.miniButton))
            {
                for (int i = 0; i < _detectedEvents.Count; i++)
                { var e = _detectedEvents[i]; e.selected = true; _detectedEvents[i] = e; }
                SaveEventSelections();
            }
            if (GUILayout.Button("Deselect All", EditorStyles.miniButton))
            {
                for (int i = 0; i < _detectedEvents.Count; i++)
                { var e = _detectedEvents[i]; e.selected = false; _detectedEvents[i] = e; }
                SaveEventSelections();
            }
            if (GUILayout.Button("Re-scan", EditorStyles.miniButton))
            {
                ScanTargetsForEvents(_targets.Where(t => t != null).ToList());
            }
            EditorGUILayout.EndHorizontal();
        }

        private void ScanTargetsForEvents(List<GameObject> targets)
        {
            // Prefer reference-imported selections when they exist; fall back to
            // the user's prior persisted picks otherwise.
            var refSelections = LoadPrefWires();
            var savedSelections = LoadEventSelections();
            var groups = new Dictionary<string, DetectedEventGroup>();

            foreach (var go in targets)
            {
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (comp is HapbeatTriggerBase || comp is HapbeatEvent)
                        continue;

                    string typeName = comp.GetType().Name;
                    var so = new SerializedObject(comp);
                    var iter = so.GetIterator();

                    while (iter.NextVisible(true))
                    {
                        if (!IsUnityEventProperty(iter)) continue;

                        string fieldName = iter.name;
                        string key = $"{comp.GetType().FullName}|{fieldName}";

                        string cleanField = fieldName;
                        if (cleanField.StartsWith("m_"))
                            cleanField = cleanField.Substring(2);
                        if (cleanField.Length > 0)
                            cleanField = char.ToLower(cleanField[0]) + cleanField.Substring(1);

                        if (groups.ContainsKey(key))
                        {
                            var g = groups[key];
                            g.objectCount++;
                            groups[key] = g;
                        }
                        else
                        {
                            bool sel;
                            int methodIdx;
                            if (refSelections.TryGetValue(key, out int refMethod))
                            {
                                sel = true;
                                methodIdx = refMethod;
                            }
                            else if (savedSelections.TryGetValue(key, out bool savedSel))
                            {
                                sel = savedSel;
                                methodIdx = fieldName.Contains("Exited") ? 1 : 0;
                            }
                            else
                            {
                                sel = fieldName == "m_SelectEntered"
                                   || fieldName == "m_SelectExited"
                                   || fieldName == "m_OnClick";
                                methodIdx = fieldName.Contains("Exited") ? 1 : 0;
                            }

                            groups[key] = new DetectedEventGroup
                            {
                                displayName = $"{typeName} / {cleanField}",
                                componentType = comp.GetType().FullName,
                                fieldPath = fieldName,
                                objectCount = 1,
                                selected = sel,
                                methodIndex = methodIdx
                            };
                        }
                    }
                }
            }

            _detectedEvents = groups.Values
                .OrderByDescending(g => g.selected)
                .ThenBy(g => g.displayName)
                .ToList();
        }

        private bool IsUnityEventProperty(SerializedProperty prop)
        {
            if (prop.propertyType != SerializedPropertyType.Generic) return false;
            if (prop.depth > 2) return false;

            var copy = prop.Copy();
            if (copy.FindPropertyRelative("m_PersistentCalls") != null) return true;

            var inner = copy.FindPropertyRelative("m_Event");
            if (inner != null && inner.FindPropertyRelative("m_PersistentCalls") != null) return true;

            return false;
        }

        // --- Persist event selections via EditorPrefs ---

        private void SaveEventSelections()
        {
            var dict = new Dictionary<string, bool>();
            foreach (var e in _detectedEvents)
                dict[$"{e.componentType}|{e.fieldPath}"] = e.selected;

            string json = JsonUtility.ToJson(new SerializableDict(dict));
            EditorPrefs.SetString(kSelectedEventsKey, json);
        }

        private Dictionary<string, bool> LoadEventSelections()
        {
            string json = EditorPrefs.GetString(kSelectedEventsKey, "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, bool>();
            try
            {
                var sd = JsonUtility.FromJson<SerializableDict>(json);
                return sd.ToDictionary();
            }
            catch { return new Dictionary<string, bool>(); }
        }

        // --- Transient ref-wire selections (only used until the next import) ---

        private const string kRefWiresKey = "HapbeatBatchSetup_RefWires";

        private void SavePrefWires(Dictionary<string, int> wires)
        {
            var b = new List<bool>();
            var keys = new List<string>();
            var methods = new List<int>();
            foreach (var kv in wires) { keys.Add(kv.Key); methods.Add(kv.Value); }
            var sd = new SerializableIntDict { keys = keys, values = methods };
            EditorPrefs.SetString(kRefWiresKey, JsonUtility.ToJson(sd));
        }

        private Dictionary<string, int> LoadPrefWires()
        {
            string json = EditorPrefs.GetString(kRefWiresKey, "");
            if (string.IsNullOrEmpty(json)) return new Dictionary<string, int>();
            try
            {
                var sd = JsonUtility.FromJson<SerializableIntDict>(json);
                var d = new Dictionary<string, int>();
                for (int i = 0; i < Mathf.Min(sd.keys.Count, sd.values.Count); i++)
                    d[sd.keys[i]] = sd.values[i];
                return d;
            }
            catch { return new Dictionary<string, int>(); }
        }

        [Serializable]
        private class SerializableDict
        {
            public List<string> keys = new List<string>();
            public List<bool> values = new List<bool>();

            public SerializableDict() { }
            public SerializableDict(Dictionary<string, bool> dict)
            {
                foreach (var kv in dict) { keys.Add(kv.Key); values.Add(kv.Value); }
            }
            public Dictionary<string, bool> ToDictionary()
            {
                var d = new Dictionary<string, bool>();
                for (int i = 0; i < Mathf.Min(keys.Count, values.Count); i++)
                    d[keys[i]] = values[i];
                return d;
            }
        }

        [Serializable]
        private class SerializableIntDict
        {
            public List<string> keys = new List<string>();
            public List<int> values = new List<int>();
        }

        // =====================================================================
        // Actions
        // =====================================================================

        private void DrawActions()
        {
            var validTargets = _targets.Where(t => t != null).ToList();
            bool canApply = validTargets.Count > 0
                && _eventMap != null && _eventMap.entries.Count > 0;

            var origColor = GUI.backgroundColor;

            EditorGUI.BeginDisabledGroup(!canApply);
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button($"Apply ({validTargets.Count} objects)", GUILayout.Height(26)))
                ApplyBatch(validTargets);
            GUI.backgroundColor = origColor;
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(3);

            EditorGUI.BeginDisabledGroup(validTargets.Count == 0);
            GUI.backgroundColor = new Color(1f, 0.6f, 0.5f);
            if (GUILayout.Button($"Cleanup Hapbeat ({validTargets.Count} objects)", GUILayout.Height(22)))
                CleanupBatch(validTargets);
            GUI.backgroundColor = origColor;
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.LabelField(
                _replaceExisting
                    ? "Apply: 既存の Hapbeat トリガーを一度削除してから追加 (重複防止)。  Cleanup: Hapbeat 全除去。"
                    : "Apply: 同じ種別+Entry は上書き、違えば並列追加。  Cleanup: Hapbeat 全除去。",
                EditorStyles.miniLabel);
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private void ApplyBatch(List<GameObject> targets)
        {
            Undo.SetCurrentGroupName("Hapbeat Batch Setup");
            int undoGroup = Undo.GetCurrentGroup();
            int added = 0, updated = 0, wired = 0, wiresRemoved = 0, componentsRemoved = 0;

            var entry = _eventMap != null ? _eventMap.GetEntry(_entryIndex) : null;

            foreach (var go in targets)
            {
                // Replace mode: drop existing Hapbeat triggers + their wires +
                // linked parameter bindings before adding the new trigger. This
                // is the fix for "selectEntered now has two Fire calls" after
                // re-running Apply with a different entry.
                if (_replaceExisting)
                {
                    var existingTriggers = new List<HapbeatTriggerBase>(go.GetComponents<HapbeatTriggerBase>());
                    foreach (var tr in existingTriggers)
                        wiresRemoved += RemoveWiring(go, tr);
                    foreach (var binding in go.GetComponents<HapbeatParameterBinding>())
                    {
                        Undo.DestroyObjectImmediate(binding);
                        componentsRemoved++;
                    }
                    foreach (var tr in existingTriggers)
                    {
                        Undo.DestroyObjectImmediate(tr);
                        componentsRemoved++;
                    }
                }

                if (_triggerType == TriggerType.UnityEventTrigger)
                {
                    var trigger = FindOrCreate<HapbeatUnityEventTrigger>(go, out bool isNew);
                    ConfigureBase(trigger);
                    if (isNew) added++; else updated++;
                    wired += WireScannedEvents(go, trigger);
                    if (entry != null && entry.mode == HapticMode.StreamClip)
                        ApplyBindingPresets(go, entry);
                }
                else if (_triggerType == TriggerType.SequenceTrigger)
                {
                    var trigger = FindOrCreate<HapbeatSequenceTrigger>(go, out bool isNew);
                    ConfigureBase(trigger);
                    ConfigureSequence(trigger);
                    if (isNew) added++; else updated++;
                    wired += WireScannedEvents(go, trigger);
                    if (entry != null && entry.mode == HapticMode.StreamClip)
                        ApplyBindingPresets(go, entry);
                }
                else
                {
                    var trigger = FindOrCreate<HapbeatCollisionTrigger>(go, out bool isNew);
                    ConfigureBase(trigger);
                    ConfigureCollision(trigger);
                    if (isNew) added++; else updated++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            string msg = $"{added} added, {updated} updated"
                + (wired > 0 ? $", {wired} events wired" : "")
                + (_replaceExisting && componentsRemoved > 0 ? $", {componentsRemoved} replaced" : "")
                + (_replaceExisting && wiresRemoved > 0 ? $", {wiresRemoved} old wires removed" : "");
            Debug.Log($"[Hapbeat Batch Setup] {msg}");
            EditorUtility.DisplayDialog("Hapbeat Batch Setup", msg, "OK");
        }

        /// <summary>
        /// Apply the entry's binding presets to the target GameObject.
        ///
        /// Matching strategy (3-phase, per preset, in order):
        ///   1. <b>Exact link match</b> — an existing <see cref="HapbeatParameterBinding"/>
        ///      already linked to the same EventMap + preset id. Update in place.
        ///   2. <b>Unlinked legacy match</b> — an existing unlinked binding whose
        ///      sourceProperty and outputParameter match the preset. Upgrade it by
        ///      setting the link.
        ///   3. <b>New</b> — add a new binding.
        /// </summary>
        private void ApplyBindingPresets(GameObject go, HapbeatEventEntry entry)
        {
            if (entry.bindings == null || entry.bindings.Count == 0) return;

            var existing = new List<HapbeatParameterBinding>(go.GetComponents<HapbeatParameterBinding>());
            var consumed = new HashSet<HapbeatParameterBinding>();

            foreach (var preset in entry.bindings)
            {
                string presetId = preset.id; // lazy-assign GUIDs

                Transform srcT = ResolveTransformPath(go.transform, preset.sourceTransformPath);
                if (srcT == null)
                {
                    Debug.LogWarning($"[Hapbeat] Binding: source path '{preset.sourceTransformPath}' not found on {go.name}. Skipping.");
                    continue;
                }

                var binding = FindLinkedBinding(existing, consumed, _eventMap, presetId);
                if (binding == null)
                    binding = FindUpgradeCandidate(existing, consumed, preset);

                if (binding == null)
                    binding = Undo.AddComponent<HapbeatParameterBinding>(go);
                else
                    Undo.RecordObject(binding, "Update Hapbeat Binding");
                consumed.Add(binding);

                var bso = new SerializedObject(binding);
                bso.FindProperty("_linkedEventMap").objectReferenceValue = _eventMap;
                bso.FindProperty("_linkedBindingId").stringValue = presetId;
                bso.FindProperty("_sourceTransform").objectReferenceValue = srcT;
                bso.FindProperty("_sourceProperty").enumValueIndex = (int)preset.sourceProperty;
                bso.FindProperty("_inputMin").floatValue = preset.inputMin;
                bso.FindProperty("_inputMax").floatValue = preset.inputMax;
                bso.FindProperty("_curveType").enumValueIndex = (int)preset.curveType;
                if (preset.customCurve != null)
                {
                    var customCurveProp = bso.FindProperty("_customCurve");
                    if (customCurveProp != null)
                        customCurveProp.animationCurveValue = preset.customCurve;
                }
                bso.FindProperty("_outputParameter").enumValueIndex = (int)preset.outputParameter;
                bso.FindProperty("_outputMin").floatValue = preset.outputMin;
                bso.FindProperty("_outputMax").floatValue = preset.outputMax;
                var dbgProp = bso.FindProperty("_debugLog");
                if (dbgProp != null) dbgProp.boolValue = preset.debugLog;
                var dbgIntProp = bso.FindProperty("_debugLogInterval");
                if (dbgIntProp != null) dbgIntProp.floatValue = preset.debugLogInterval;
                var dbgChangeProp = bso.FindProperty("_debugLogChangeThreshold");
                if (dbgChangeProp != null) dbgChangeProp.floatValue = preset.debugLogChangeThreshold;
                bso.ApplyModifiedProperties();
                EditorUtility.SetDirty(binding);
            }
        }

        private static HapbeatParameterBinding FindLinkedBinding(
            List<HapbeatParameterBinding> candidates,
            HashSet<HapbeatParameterBinding> consumed,
            HapbeatEventMap map,
            string id)
        {
            if (candidates == null) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var b = candidates[i];
                if (b == null || consumed.Contains(b)) continue;
                if (ReferenceEquals(b.LinkedEventMap, map) && b.LinkedBindingId == id)
                    return b;
            }
            return null;
        }

        private static HapbeatParameterBinding FindUpgradeCandidate(
            List<HapbeatParameterBinding> candidates,
            HashSet<HapbeatParameterBinding> consumed,
            HapbeatBindingPreset preset)
        {
            if (candidates == null) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var b = candidates[i];
                if (b == null || consumed.Contains(b)) continue;
                if (b.LinkedEventMap != null) continue;
                var so = new SerializedObject(b);
                var srcPropProp = so.FindProperty("_sourceProperty");
                var outParamProp = so.FindProperty("_outputParameter");
                if (srcPropProp == null || outParamProp == null) continue;
                if (srcPropProp.enumValueIndex == (int)preset.sourceProperty &&
                    outParamProp.enumValueIndex == (int)preset.outputParameter)
                {
                    return b;
                }
            }
            return null;
        }

        private static Transform ResolveTransformPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path) || path == ".") return root;
            return root.Find(path);
        }

        private T FindOrCreate<T>(GameObject go, out bool isNew) where T : HapbeatTriggerBase
        {
            foreach (var existing in go.GetComponents<T>())
            {
                if (existing.EventMap == _eventMap && existing.EntryIndex == _entryIndex)
                {
                    Undo.RecordObject(existing, "Update Hapbeat Trigger");
                    isNew = false;
                    return existing;
                }
            }
            isNew = true;
            return Undo.AddComponent<T>(go);
        }

        private void ConfigureBase(HapbeatTriggerBase trigger)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_eventMap").objectReferenceValue = _eventMap;
            so.FindProperty("_entryIndex").intValue = _entryIndex;
            so.FindProperty("_cooldown").floatValue = _cooldown;
            so.ApplyModifiedProperties();
        }

        private void ConfigureCollision(HapbeatCollisionTrigger trigger)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_triggerEvent").enumValueIndex = (int)_collisionEvent;
            so.FindProperty("_gainMode").enumValueIndex = (int)_gainMode;
            if (_gainMode == HapbeatCollisionTrigger.GainMode.VelocityScaled)
            {
                so.FindProperty("_velocityThreshold").floatValue = _velocityThreshold;
                so.FindProperty("_maxVelocity").floatValue = _maxVelocity;
            }
            so.ApplyModifiedProperties();
        }

        private void ConfigureSequence(HapbeatSequenceTrigger trigger)
        {
            var so = new SerializedObject(trigger);
            so.FindProperty("_onStartEntryIndex").intValue = _onStartEntryIndex;
            so.FindProperty("_onStopEntryIndex").intValue = _onStopEntryIndex;
            var delayProp = so.FindProperty("_startShotDelay");
            if (delayProp != null) delayProp.floatValue = _startShotDelay;
            so.ApplyModifiedProperties();
        }

        private int WireScannedEvents(GameObject go, HapbeatTriggerBase trigger)
        {
            int count = 0;
            var selected = _detectedEvents.Where(e => e.selected).ToList();

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                string fullType = comp.GetType().FullName;

                foreach (var ev in selected)
                {
                    if (ev.componentType != fullType) continue;
                    string method = WireMethods[Mathf.Clamp(ev.methodIndex, 0, WireMethods.Length - 1)];
                    var so = new SerializedObject(comp);
                    count += EnsureWired(so, ev.fieldPath, trigger, method);
                    so.ApplyModifiedProperties();
                }
            }
            return count;
        }

        private int EnsureWired(SerializedObject so, string fieldName,
            UnityEngine.Object target, string methodName)
        {
            var prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                string alt = fieldName.Replace("m_First", "m_").Replace("m_Last", "m_");
                if (alt != fieldName) prop = so.FindProperty(alt);
            }
            if (prop == null) return 0;

            var callsProp = prop.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (callsProp == null)
            {
                var inner = prop.FindPropertyRelative("m_Event");
                if (inner != null)
                    callsProp = inner.FindPropertyRelative("m_PersistentCalls.m_Calls");
            }
            if (callsProp == null) return 0;

            for (int i = 0; i < callsProp.arraySize; i++)
            {
                var call = callsProp.GetArrayElementAtIndex(i);
                if (call.FindPropertyRelative("m_Target").objectReferenceValue == target
                    && call.FindPropertyRelative("m_MethodName").stringValue == methodName)
                    return 0;
            }

            callsProp.arraySize++;
            var nc = callsProp.GetArrayElementAtIndex(callsProp.arraySize - 1);
            nc.FindPropertyRelative("m_Target").objectReferenceValue = target;
            nc.FindPropertyRelative("m_MethodName").stringValue = methodName;
            nc.FindPropertyRelative("m_Mode").intValue = 1;
            nc.FindPropertyRelative("m_CallState").intValue = 2;
            return 1;
        }

        // =====================================================================
        // Cleanup
        // =====================================================================

        private void CleanupBatch(List<GameObject> targets)
        {
            if (!EditorUtility.DisplayDialog("⚠ Hapbeat Cleanup",
                $"⚠ この操作は破壊的です。\n\n" +
                $"対象: {targets.Count} 個の GameObject\n\n" +
                "除去されるもの:\n" +
                "・ HapbeatTriggerBase コンポーネント (UnityEventTrigger, CollisionTrigger 等)\n" +
                "・ HapbeatParameterBinding\n" +
                "・ HapbeatEvent\n" +
                "・ 上記に向けた UnityEvent 接続\n\n" +
                "価値ある設定を失う可能性があります。\n" +
                "Ctrl+Z で Undo 可能ですが、確認してから実行してください。",
                "実行", "キャンセル"))
                return;

            Undo.SetCurrentGroupName("Hapbeat Cleanup");
            int undoGroup = Undo.GetCurrentGroup();
            int removedComps = 0, removedWires = 0;

            foreach (var go in targets)
            {
                var hapComps = new List<Component>();
                hapComps.AddRange(go.GetComponents<HapbeatTriggerBase>());
                hapComps.AddRange(go.GetComponents<HapbeatParameterBinding>());
                var he = go.GetComponent<HapbeatEvent>();
                if (he != null) hapComps.Add(he);

                foreach (var hc in hapComps)
                    removedWires += RemoveWiring(go, hc);

                foreach (var hc in hapComps)
                {
                    Undo.DestroyObjectImmediate(hc);
                    removedComps++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            string msg = $"{removedComps} components, {removedWires} wires removed";
            Debug.Log($"[Hapbeat Cleanup] {msg}");
            EditorUtility.DisplayDialog("Hapbeat Cleanup", msg, "OK");
        }

        private int RemoveWiring(GameObject go, UnityEngine.Object triggerTarget)
        {
            int removed = 0;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp == triggerTarget) continue;
                if (comp is HapbeatTriggerBase || comp is HapbeatEvent
                    || comp is HapbeatParameterBinding)
                    continue;

                var so = new SerializedObject(comp);
                var iter = so.GetIterator();
                bool changed = false;

                while (iter.NextVisible(true))
                {
                    if (iter.propertyType == SerializedPropertyType.Generic
                        && iter.name == "m_Calls" && iter.isArray)
                    {
                        for (int i = iter.arraySize - 1; i >= 0; i--)
                        {
                            var t = iter.GetArrayElementAtIndex(i).FindPropertyRelative("m_Target");
                            if (t != null && t.objectReferenceValue == triggerTarget)
                            {
                                iter.DeleteArrayElementAtIndex(i);
                                removed++;
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) so.ApplyModifiedProperties();
            }
            return removed;
        }
    }
}
#endif
