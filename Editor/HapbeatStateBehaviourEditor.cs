#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="HapbeatStateBehaviour"/>.
    /// Renders:
    /// <list type="bullet">
    ///   <item>EventMap reference + two entry dropdowns (Enter / Exit).</item>
    ///   <item>"Required Previous State" picker — dropdown of state names from the
    ///   enclosing AnimatorController, or text fallback if not resolvable.</item>
    ///   <item>Gain multiplier + verbose log toggle.</item>
    /// </list>
    /// <para>
    /// The base trigger's <see cref="HapbeatTriggerBaseEditor.DrawEntryDropdown"/>
    /// helper is reused for entry selection (stable GUID + display cache).
    /// </para>
    /// </summary>
    [CustomEditor(typeof(HapbeatStateBehaviour))]
    [CanEditMultipleObjects]
    public class HapbeatStateBehaviourEditor : UnityEditor.Editor
    {
        private SerializedProperty _eventMapProp;
        private SerializedProperty _entryIdOnEnterProp;
        private SerializedProperty _entryIndexOnEnterProp;
        private SerializedProperty _entryIdOnExitProp;
        private SerializedProperty _entryIndexOnExitProp;
        private SerializedProperty _requiredPreviousStateProp;
        private SerializedProperty _gainMultiplierProp;
        private SerializedProperty _verboseLogProp;

        private void OnEnable()
        {
            _eventMapProp = serializedObject.FindProperty("_eventMap");
            _entryIdOnEnterProp = serializedObject.FindProperty("_entryIdOnEnter");
            _entryIndexOnEnterProp = serializedObject.FindProperty("_entryIndexOnEnter");
            _entryIdOnExitProp = serializedObject.FindProperty("_entryIdOnExit");
            _entryIndexOnExitProp = serializedObject.FindProperty("_entryIndexOnExit");
            _requiredPreviousStateProp = serializedObject.FindProperty("_requiredPreviousState");
            _gainMultiplierProp = serializedObject.FindProperty("_gainMultiplier");
            _verboseLogProp = serializedObject.FindProperty("_verboseLog");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_eventMapProp,
                new GUIContent("Event Map",
                    "Asset containing haptic event definitions. Required for both Enter and Exit events."));

            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("On State Enter", EditorStyles.boldLabel);
            DrawEntryDropdownWithClear(eventMap, _entryIdOnEnterProp, _entryIndexOnEnterProp,
                "Entry",
                "Haptic event fired when this state is entered. Leave unset to skip OnEnter.");

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("On State Exit", EditorStyles.boldLabel);
            DrawEntryDropdownWithClear(eventMap, _entryIdOnExitProp, _entryIndexOnExitProp,
                "Entry",
                "Haptic event fired when this state is exited. Leave unset to skip OnExit. " +
                "If OnEnter fired a looping StreamClip, it is stopped automatically before this fires.");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Transition Filter", EditorStyles.boldLabel);
            DrawRequiredPreviousState();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Gain", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_gainMultiplierProp,
                new GUIContent("Gain Multiplier",
                    "Per-behaviour multiplier applied on top of entry.gain × manifest.intensity."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_verboseLogProp);

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Entry picker with an additional "(none)" option at the top so designers
        /// can explicitly say "no event on this phase". When selected, both id and
        /// index are cleared to the sentinel values.
        /// </summary>
        private static void DrawEntryDropdownWithClear(
            HapbeatEventMap eventMap,
            SerializedProperty idProp,
            SerializedProperty indexProp,
            string label,
            string tooltip)
        {
            if (eventMap == null)
            {
                EditorGUILayout.HelpBox("Event Map を設定してください。", MessageType.Info);
                return;
            }
            if (eventMap.entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Event Map にエントリがありません。", MessageType.Warning);
                return;
            }

            // Build "(none)" + entry names list. Display index 0 = none.
            var displayNames = eventMap.GetDisplayNames();
            var options = new string[displayNames.Length + 1];
            options[0] = "(none)";
            for (int i = 0; i < displayNames.Length; i++)
                options[i + 1] = displayNames[i];

            // Resolve current display index: 0 = none, else 1-based index of entry.
            int currentEntryIdx = -1;
            if (!string.IsNullOrEmpty(idProp.stringValue))
            {
                currentEntryIdx = eventMap.IndexOfId(idProp.stringValue);
                if (currentEntryIdx < 0)
                    currentEntryIdx = (indexProp.intValue >= 0 && indexProp.intValue < eventMap.entries.Count)
                        ? indexProp.intValue : -1;
            }
            else if (indexProp.intValue >= 0 && indexProp.intValue < eventMap.entries.Count)
            {
                currentEntryIdx = indexProp.intValue;
            }
            int currentDisplay = currentEntryIdx >= 0 ? currentEntryIdx + 1 : 0;

            int newDisplay = EditorGUILayout.Popup(new GUIContent(label, tooltip),
                currentDisplay, options);

            if (newDisplay == 0)
            {
                // (none) selected
                if (!string.IsNullOrEmpty(idProp.stringValue) || indexProp.intValue >= 0)
                {
                    idProp.stringValue = "";
                    indexProp.intValue = -1;
                }
            }
            else
            {
                int entryIdx = newDisplay - 1;
                if (entryIdx >= 0 && entryIdx < eventMap.entries.Count)
                {
                    var entry = eventMap.entries[entryIdx];
                    string newId = entry != null ? entry.id : "";
                    if (idProp.stringValue != newId)
                    {
                        idProp.stringValue = newId;
                        EditorUtility.SetDirty(eventMap);
                        AssetDatabase.SaveAssetIfDirty(eventMap);
                    }
                    indexProp.intValue = entryIdx;
                }
            }

            // Read-only summary of the resolved entry, if any.
            if (currentEntryIdx >= 0 && currentEntryIdx < eventMap.entries.Count)
            {
                var entry = eventMap.entries[currentEntryIdx];
                if (entry != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    if (entry.mode == HapticMode.Command)
                        EditorGUILayout.TextField("Event ID", entry.eventId);
                    else
                        EditorGUILayout.TextField("Mode",
                            entry.mode + (entry.loop ? " (loop)" : ""));
                    EditorGUILayout.FloatField("Gain", entry.gain);
                    if (entry.HasTarget)
                        EditorGUILayout.TextField("Target", entry.target);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }
            }
        }

        /// <summary>
        /// Renders the Required Previous State picker. Tries to enumerate the
        /// enclosing AnimatorController's states; falls back to a text field
        /// when the controller can't be resolved (e.g. multi-edit).
        /// </summary>
        private void DrawRequiredPreviousState()
        {
            // Resolve the AnimatorController we're nested inside.
            // StateMachineBehaviour assets are sub-assets of the AnimatorController.
            var beh = target as HapbeatStateBehaviour;
            AnimatorController controller = null;
            if (beh != null)
            {
                string path = AssetDatabase.GetAssetPath(beh);
                if (!string.IsNullOrEmpty(path))
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            }

            var stateNames = controller != null ? CollectStateNames(controller) : null;
            if (stateNames == null || stateNames.Count == 0)
            {
                EditorGUILayout.PropertyField(_requiredPreviousStateProp,
                    new GUIContent("Required Previous State",
                        "Leave empty to fire on enter regardless of source. " +
                        "Set to a state name to limit fire to specific A→B transitions."));
                EditorGUILayout.HelpBox(
                    "親 AnimatorController を解決できませんでした。Required Previous State は手入力で指定してください。",
                    MessageType.Info);
                return;
            }

            // Build "(any)" + state names list
            var options = new string[stateNames.Count + 1];
            options[0] = "(any)";
            for (int i = 0; i < stateNames.Count; i++) options[i + 1] = stateNames[i];

            string current = _requiredPreviousStateProp.stringValue;
            int currentIdx = string.IsNullOrEmpty(current)
                ? 0
                : Mathf.Max(0, stateNames.IndexOf(current) + 1);

            int newIdx = EditorGUILayout.Popup(
                new GUIContent("Required Previous State",
                    "(any) = fire on any incoming transition. " +
                    "Otherwise, fire only when the previous state matches the chosen one."),
                currentIdx, options);

            string newValue = newIdx <= 0 ? "" : stateNames[newIdx - 1];
            if (newValue != current)
                _requiredPreviousStateProp.stringValue = newValue;
        }

        /// <summary>
        /// Collect all state names across all layers and sub-state-machines.
        /// Returns names without duplicates, sorted alphabetically.
        /// </summary>
        private static List<string> CollectStateNames(AnimatorController controller)
        {
            var set = new HashSet<string>();
            foreach (var layer in controller.layers)
            {
                CollectStatesRecursive(layer.stateMachine, set);
            }
            var list = new List<string>(set);
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list;
        }

        private static void CollectStatesRecursive(AnimatorStateMachine sm, HashSet<string> outSet)
        {
            if (sm == null) return;
            foreach (var s in sm.states)
            {
                if (s.state != null && !string.IsNullOrEmpty(s.state.name))
                    outSet.Add(s.state.name);
            }
            foreach (var child in sm.stateMachines)
            {
                CollectStatesRecursive(child.stateMachine, outSet);
            }
        }
    }
}
#endif
