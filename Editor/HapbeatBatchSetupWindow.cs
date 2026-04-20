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
    /// Supports both HapbeatUnityEventTrigger and HapbeatCollisionTrigger.
    /// </summary>
    public class HapbeatBatchSetupWindow : EditorWindow
    {
        private enum TriggerType { UnityEventTrigger, CollisionTrigger }
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

        // --- Targets ---
        private List<GameObject> _targets = new List<GameObject>();
        private Vector2 _targetScrollPos;
        private Vector2 _mainScrollPos;

        // --- Clone from reference ---
        private GameObject _referenceObject;
        private List<CloneTriggerInfo> _refTriggers = new List<CloneTriggerInfo>();

        private struct CloneTriggerInfo
        {
            public System.Type triggerType;
            public HapbeatEventMap eventMap;
            public int entryIndex;
            public float cooldown;
            public string displayName; // for UI
            public List<WireInfo> wires;
        }

        private struct WireInfo
        {
            public string componentType; // fully qualified
            public string fieldPath;
            public string methodName;
        }

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
            DrawCloneSection();
            EditorGUILayout.Space(6);

            // Manual setup (only when not using clone)
            if (_referenceObject == null)
            {
                DrawTriggerTypeSection();
                EditorGUILayout.Space(6);
                DrawTriggerSettings();
                EditorGUILayout.Space(6);

                if (_triggerType == TriggerType.UnityEventTrigger)
                {
                    AutoScanIfNeeded();
                    DrawEventWiringSection();
                }
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

            // Drag & drop area (generous height)
            var dropRect = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            var dropStyle = new GUIStyle(EditorStyles.helpBox)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11
            };
            GUI.Box(dropRect, "Drag & Drop GameObjects here", dropStyle);
            HandleDragDrop(dropRect);

            // Buttons
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

            // Target list (compact scroll)
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
        // Clone from Reference
        // =====================================================================

        private void DrawCloneSection()
        {
            EditorGUILayout.LabelField("Clone from Reference", EditorStyles.boldLabel);

            var newRef = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Reference", "Drag an object that already has Hapbeat triggers.\nIts full trigger + wiring setup will be cloned to all targets."),
                _referenceObject, typeof(GameObject), true);

            if (newRef != _referenceObject)
            {
                _referenceObject = newRef;
                _refTriggers.Clear();
                if (newRef != null)
                    ScanReferenceTriggers(newRef);
            }

            if (_referenceObject != null && _refTriggers.Count > 0)
            {
                EditorGUI.indentLevel++;
                foreach (var rt in _refTriggers)
                {
                    string wires = rt.wires.Count > 0
                        ? string.Join(", ", rt.wires.ConvertAll(w => w.fieldPath.Replace("m_", "").Replace("First", "").Replace("Last", "")))
                        : "(manual)";
                    EditorGUILayout.LabelField($"{rt.displayName}  \u2192  {wires}", EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }
            else if (_referenceObject != null)
            {
                EditorGUILayout.HelpBox("No Hapbeat triggers found on reference object.", MessageType.Info);
                _referenceObject = null;
            }
            else
            {
                EditorGUILayout.LabelField("Set a reference to clone its triggers, or configure manually below.", EditorStyles.miniLabel);
            }
        }

        private void ScanReferenceTriggers(GameObject refObj)
        {
            _refTriggers.Clear();
            foreach (var trigger in refObj.GetComponents<HapbeatTriggerBase>())
            {
                if (trigger == null || trigger.EventMap == null) continue;
                var entry = trigger.EventMap.GetEntry(trigger.EntryIndex);
                string name = entry != null && !string.IsNullOrEmpty(entry.displayName)
                    ? entry.displayName
                    : entry != null ? entry.GetSummary() : $"[{trigger.EntryIndex}]";

                var info = new CloneTriggerInfo
                {
                    triggerType = trigger.GetType(),
                    eventMap = trigger.EventMap,
                    entryIndex = trigger.EntryIndex,
                    cooldown = 0f, // read via SerializedObject
                    displayName = name,
                    wires = new List<WireInfo>()
                };

                // Read cooldown
                var so = new SerializedObject(trigger);
                var cdProp = so.FindProperty("_cooldown");
                if (cdProp != null) info.cooldown = cdProp.floatValue;

                // Find wired events
                foreach (var comp in refObj.GetComponents<Component>())
                {
                    if (comp == null || comp is HapbeatTriggerBase) continue;
                    var cso = new SerializedObject(comp);
                    var iter = cso.GetIterator();
                    while (iter.NextVisible(true))
                    {
                        if (iter.propertyType != SerializedPropertyType.Generic || iter.depth > 2) continue;
                        var calls = FindCalls(iter);
                        if (calls == null) continue;
                        for (int c = 0; c < calls.arraySize; c++)
                        {
                            var call = calls.GetArrayElementAtIndex(c);
                            if (call.FindPropertyRelative("m_Target").objectReferenceValue == trigger)
                            {
                                info.wires.Add(new WireInfo
                                {
                                    componentType = comp.GetType().FullName,
                                    fieldPath = iter.name,
                                    methodName = call.FindPropertyRelative("m_MethodName").stringValue
                                });
                            }
                        }
                    }
                }

                _refTriggers.Add(info);
            }
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
                    "UnityEvent \u306b\u63a5\u7d9a\u3057\u3066\u767a\u706b\u3002\u63b4\u3080/\u96e2\u3059/\u30dc\u30bf\u30f3\u7b49\u306e\u96e2\u6563\u30a4\u30d9\u30f3\u30c8\u5411\u3051\u3002",
                TriggerType.CollisionTrigger =>
                    "\u7269\u7406\u884d\u7a81/\u30c8\u30ea\u30ac\u30fc\u3067\u767a\u706b\u3002\u6295\u3052\u305f\u7269\u304c\u5f53\u305f\u308b\u7b49\u306e\u7269\u7406\u63a5\u89e6\u5411\u3051\u3002",
                _ => ""
            };
            EditorGUILayout.LabelField(desc, EditorStyles.miniLabel);
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
            else
            {
                EditorGUILayout.HelpBox(
                    _eventMap == null ? "Event Map \u3092\u8a2d\u5b9a\u3057\u3066\u304f\u3060\u3055\u3044\u3002"
                                      : "Event Map \u306b\u30a8\u30f3\u30c8\u30ea\u304c\u3042\u308a\u307e\u305b\u3093\u3002",
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
                    _targets.Any(t => t != null) ? "UnityEvent \u304c\u898b\u3064\u304b\u308a\u307e\u305b\u3093\u3067\u3057\u305f\u3002"
                                                 : "\u30bf\u30fc\u30b2\u30c3\u30c8\u3092\u8ffd\u52a0\u3059\u308b\u3068\u81ea\u52d5\u30b9\u30ad\u30e3\u30f3\u3057\u307e\u3059\u3002",
                    EditorStyles.miniLabel);
                return;
            }

            // Compact scrollable list (~8 rows)
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

            // Quick select/deselect
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
            var savedSelections = LoadEventSelections();
            var groups = new Dictionary<string, DetectedEventGroup>();

            foreach (var go in targets)
            {
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    if (comp is HapbeatTriggerBase || comp is HapbeatEvent || comp is HapbeatAudioBridge)
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
                            // Restore selection from saved prefs, or auto-select common ones
                            bool sel;
                            if (savedSelections.ContainsKey(key))
                                sel = savedSelections[key];
                            else
                                sel = fieldName == "m_SelectEntered"
                                   || fieldName == "m_SelectExited"
                                   || fieldName == "m_OnClick";

                            // Default method: Stop for "exited" events, Fire otherwise
                            int defaultMethod = fieldName.Contains("Exited") ? 1 : 0;

                            groups[key] = new DetectedEventGroup
                            {
                                displayName = $"{typeName} / {cleanField}",
                                componentType = comp.GetType().FullName,
                                fieldPath = fieldName,
                                objectCount = 1,
                                selected = sel,
                                methodIndex = defaultMethod
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

        // =====================================================================
        // Actions
        // =====================================================================

        private void DrawActions()
        {
            var validTargets = _targets.Where(t => t != null).ToList();
            bool isCloneMode = _referenceObject != null && _refTriggers.Count > 0;
            bool canApply = validTargets.Count > 0
                && (isCloneMode || (_eventMap != null && _eventMap.entries.Count > 0));

            var origColor = GUI.backgroundColor;

            EditorGUI.BeginDisabledGroup(!canApply);
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            string applyLabel = isCloneMode
                ? $"Clone to {validTargets.Count} objects"
                : $"Apply ({validTargets.Count} objects)";
            if (GUILayout.Button(applyLabel, GUILayout.Height(26)))
            {
                if (isCloneMode)
                    CloneApply(validTargets);
                else
                    ApplyBatch(validTargets);
            }
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
                isCloneMode ? "Clone: reference object \u306e\u5168\u30c8\u30ea\u30ac\u30fc + \u914d\u7dda\u3092\u8907\u88fd\u3002  Cleanup: Hapbeat \u5168\u9664\u53bb\u3002"
                            : "Apply: \u540c\u3058\u7a2e\u5225+Entry \u306f\u4e0a\u66f8\u304d\u3002  Cleanup: Hapbeat \u5168\u9664\u53bb\u3002",
                EditorStyles.miniLabel);
        }

        // =====================================================================
        // Apply
        // =====================================================================

        private void CloneApply(List<GameObject> targets)
        {
            Undo.SetCurrentGroupName("Hapbeat Clone");
            int undoGroup = Undo.GetCurrentGroup();
            int added = 0, wired = 0;

            foreach (var go in targets)
            {
                foreach (var rt in _refTriggers)
                {
                    // Add trigger component
                    HapbeatTriggerBase trigger;
                    if (rt.triggerType == typeof(HapbeatCollisionTrigger))
                        trigger = Undo.AddComponent<HapbeatCollisionTrigger>(go);
                    else
                        trigger = Undo.AddComponent<HapbeatUnityEventTrigger>(go);

                    // Configure
                    var so = new SerializedObject(trigger);
                    so.FindProperty("_eventMap").objectReferenceValue = rt.eventMap;
                    so.FindProperty("_entryIndex").intValue = rt.entryIndex;
                    so.FindProperty("_cooldown").floatValue = rt.cooldown;
                    so.ApplyModifiedProperties();
                    added++;

                    // Wire events — find matching components on target
                    foreach (var wire in rt.wires)
                    {
                        foreach (var comp in go.GetComponents<Component>())
                        {
                            if (comp == null || comp.GetType().FullName != wire.componentType) continue;
                            var cso = new SerializedObject(comp);
                            wired += EnsureWired(cso, wire.fieldPath, trigger, wire.methodName);
                            cso.ApplyModifiedProperties();
                        }
                    }

                    // StreamSource: also set up AudioSource and bindings from EventEntry presets
                    var refEntry = rt.eventMap != null ? rt.eventMap.GetEntry(rt.entryIndex) : null;
                    if (refEntry != null && refEntry.mode == HapticMode.StreamSource)
                    {
                        SetupAudioSourceForStreamSource(go, refEntry);
                        ApplyBindingPresets(go, refEntry);
                    }
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            string msg = $"{added} triggers added, {wired} events wired";
            Debug.Log($"[Hapbeat Clone] {msg}");
            EditorUtility.DisplayDialog("Hapbeat Clone", msg, "OK");
        }

        private void ApplyBatch(List<GameObject> targets)
        {
            Undo.SetCurrentGroupName("Hapbeat Batch Setup");
            int undoGroup = Undo.GetCurrentGroup();
            int added = 0, updated = 0, wired = 0, sourcesAdded = 0;

            // Check if selected entry is StreamSource
            var entry = _eventMap != null ? _eventMap.GetEntry(_entryIndex) : null;
            bool isStreamSource = entry != null && entry.mode == HapticMode.StreamSource;

            foreach (var go in targets)
            {
                if (_triggerType == TriggerType.UnityEventTrigger)
                {
                    var trigger = FindOrCreate<HapbeatUnityEventTrigger>(go, out bool isNew);
                    ConfigureBase(trigger);
                    if (isNew) added++; else updated++;
                    wired += WireScannedEvents(go, trigger);

                    // StreamSource: ensure AudioSource exists with the clip, add bindings
                    if (isStreamSource)
                    {
                        if (SetupAudioSourceForStreamSource(go, entry))
                            sourcesAdded++;
                        ApplyBindingPresets(go, entry);
                    }
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
                + (sourcesAdded > 0 ? $", {sourcesAdded} AudioSources added" : "");
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
        ///
        /// Each existing binding is only reused once per Apply invocation.
        ///
        /// The resulting component is LINKED to the preset via
        /// <c>_linkedEventMap + _linkedBindingId</c> — tuning values (inputMin/Max,
        /// curve, outputMin/Max, debug options) are read live from the preset at
        /// runtime. The local SerializedFields are also populated as a visible cache
        /// and as a fallback for when the link is later cleared.
        /// </summary>
        private void ApplyBindingPresets(GameObject go, HapbeatEventEntry entry)
        {
            if (entry.bindings == null || entry.bindings.Count == 0) return;

            var existing = new List<HapbeatParameterBinding>(go.GetComponents<HapbeatParameterBinding>());
            var consumed = new HashSet<HapbeatParameterBinding>();

            foreach (var preset in entry.bindings)
            {
                // Touch the id getter so lazy-assigned GUIDs get persisted.
                string presetId = preset.id;

                // Resolve source Transform
                Transform srcT = ResolveTransformPath(go.transform, preset.sourceTransformPath);
                if (srcT == null)
                {
                    Debug.LogWarning($"[Hapbeat] Binding: source path '{preset.sourceTransformPath}' not found on {go.name}. Skipping.");
                    continue;
                }

                // Phase 1: already linked to this preset
                var binding = FindLinkedBinding(existing, consumed, _eventMap, presetId);

                // Phase 2: upgrade an unlinked binding that looks like a match
                if (binding == null)
                    binding = FindUpgradeCandidate(existing, consumed, preset);

                // Phase 3: new
                if (binding == null)
                {
                    binding = Undo.AddComponent<HapbeatParameterBinding>(go);
                }
                else
                {
                    Undo.RecordObject(binding, "Update Hapbeat Binding");
                }
                consumed.Add(binding);

                var bso = new SerializedObject(binding);
                bso.FindProperty("_linkedEventMap").objectReferenceValue = _eventMap;
                bso.FindProperty("_linkedBindingId").stringValue = presetId;
                bso.FindProperty("_sourceTransform").objectReferenceValue = srcT;
                // Local fields: populate so the Inspector shows meaningful defaults and
                // so the binding still behaves sanely if the user later clears the link.
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

        /// <summary>
        /// Return the first binding in <paramref name="candidates"/> whose link points to
        /// <paramref name="map"/> + <paramref name="id"/> and is not already consumed.
        /// </summary>
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

        /// <summary>
        /// Return the first unlinked binding whose sourceProperty and outputParameter match
        /// <paramref name="preset"/>. Used to migrate pre-link bindings to linked ones
        /// (saves users from manually deleting old duplicates before re-running Apply).
        /// </summary>
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
                if (b.LinkedEventMap != null) continue; // already linked to something
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

        /// <summary>Resolve a relative Transform path. Empty or "." returns root.</summary>
        private static Transform ResolveTransformPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path) || path == ".") return root;
            return root.Find(path);
        }

        /// <summary>
        /// Ensure the target GameObject has a Hapbeat-dedicated AudioSource (tagged with HapbeatAudioBridge).
        /// If a HapbeatAudioBridge already exists, reuse its AudioSource.
        /// Otherwise, add a new AudioSource + HapbeatAudioBridge pair — even if other AudioSources exist.
        /// Returns true if a new AudioSource was added.
        /// </summary>
        private bool SetupAudioSourceForStreamSource(GameObject go, HapbeatEventEntry entry)
        {
            // Find existing Hapbeat-tagged AudioSource (one with HapbeatAudioBridge)
            AudioSource audioSource = null;
            foreach (var bridge in go.GetComponents<HapbeatAudioBridge>())
            {
                audioSource = bridge.GetComponent<AudioSource>();
                if (audioSource != null) break;
            }

            bool wasAdded = false;
            if (audioSource == null)
            {
                // Always add a new dedicated AudioSource (even if other AudioSources exist on the GameObject)
                audioSource = Undo.AddComponent<AudioSource>(go);
                audioSource.playOnAwake = false;
                audioSource.spatialBlend = 1f; // default to 3D
                // Immediately add HapbeatAudioBridge to mark this AudioSource as the haptic one
                var newBridge = Undo.AddComponent<HapbeatAudioBridge>(go);
                newBridge.AudioSourceAutoAdded = true; // mark for cleanup
                wasAdded = true;
            }
            else
            {
                Undo.RecordObject(audioSource, "Configure AudioSource");
            }

            if (entry.streamClip != null && audioSource.clip == null)
                audioSource.clip = entry.streamClip;

            audioSource.loop = entry.loop;
            return wasAdded;
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

        private int WireScannedEvents(GameObject go, HapbeatUnityEventTrigger trigger)
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
            if (!EditorUtility.DisplayDialog("\u26a0 Hapbeat Cleanup",
                $"\u26a0 \u3053\u306e\u64cd\u4f5c\u306f\u7834\u58ca\u7684\u3067\u3059\u3002\n\n" +
                $"\u5bfe\u8c61: {targets.Count} \u500b\u306e GameObject\n\n" +
                "\u9664\u53bb\u3055\u308c\u308b\u3082\u306e:\n" +
                "\u30fb HapbeatTriggerBase \u30b3\u30f3\u30dd\u30fc\u30cd\u30f3\u30c8 (UnityEventTrigger, CollisionTrigger \u7b49)\n" +
                "\u30fb HapbeatParameterBinding\n" +
                "\u30fb HapbeatAudioBridge\n" +
                "\u30fb HapbeatEvent\n" +
                "\u30fb Batch Setup \u304c\u81ea\u52d5\u8ffd\u52a0\u3057\u305f AudioSource (\u30de\u30fc\u30ab\u30fc\u5bfe\u8c61\u306e\u307f)\n" +
                "\u30fb \u4e0a\u8a18\u306b\u5411\u3051\u305f UnityEvent \u63a5\u7d9a\n\n" +
                "\u4fa1\u5024\u3042\u308b\u8a2d\u5b9a\u3092\u5931\u3046\u53ef\u80fd\u6027\u304c\u3042\u308a\u307e\u3059\u3002\n" +
                "Ctrl+Z \u3067 Undo \u53ef\u80fd\u3067\u3059\u304c\u3001\u78ba\u8a8d\u3057\u3066\u304b\u3089\u5b9f\u884c\u3057\u3066\u304f\u3060\u3055\u3044\u3002",
                "\u5b9f\u884c", "\u30ad\u30e3\u30f3\u30bb\u30eb"))
                return;

            Undo.SetCurrentGroupName("Hapbeat Cleanup");
            int undoGroup = Undo.GetCurrentGroup();
            int removedComps = 0, removedWires = 0, removedAudio = 0;

            foreach (var go in targets)
            {
                // Collect all Hapbeat-owned components
                var hapComps = new List<Component>();
                hapComps.AddRange(go.GetComponents<HapbeatTriggerBase>());       // UnityEventTrigger, CollisionTrigger, AnimatorTrigger
                hapComps.AddRange(go.GetComponents<HapbeatParameterBinding>()); // Parameter bindings
                hapComps.AddRange(go.GetComponents<HapbeatAudioBridge>());      // Audio bridges (possibly multiple)
                var he = go.GetComponent<HapbeatEvent>();
                if (he != null) hapComps.Add(he);

                // Only remove AudioSources that were explicitly auto-added by Batch Setup
                // (identified by HapbeatAudioBridge.AudioSourceAutoAdded flag).
                var audioSourcesToRemove = new List<AudioSource>();
                foreach (var bridge in go.GetComponents<HapbeatAudioBridge>())
                {
                    if (!bridge.AudioSourceAutoAdded) continue;
                    var src = bridge.GetComponent<AudioSource>();
                    if (src != null) audioSourcesToRemove.Add(src);
                }

                // Remove event wiring that points to any Hapbeat component
                foreach (var hc in hapComps)
                    removedWires += RemoveWiring(go, hc);

                // Destroy Hapbeat components
                foreach (var hc in hapComps)
                {
                    Undo.DestroyObjectImmediate(hc);
                    removedComps++;
                }

                // Destroy paired AudioSources (after bridges are gone)
                foreach (var src in audioSourcesToRemove)
                {
                    if (src == null) continue;
                    Undo.DestroyObjectImmediate(src);
                    removedAudio++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            string msg = $"{removedComps} components, {removedAudio} AudioSources, {removedWires} wires removed";
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
                    || comp is HapbeatAudioBridge || comp is HapbeatParameterBinding)
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
