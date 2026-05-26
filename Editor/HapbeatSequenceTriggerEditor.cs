#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="HapbeatSequenceTrigger"/>. Shows all three
    /// entries (On Start / Loop / On Stop) as dropdowns. Selections are written
    /// by stable GUID so reorders don't break wiring.
    /// </summary>
    [CustomEditor(typeof(HapbeatSequenceTrigger))]
    [CanEditMultipleObjects]
    public class HapbeatSequenceTriggerEditor : HapbeatTriggerBaseEditor
    {
        private SerializedProperty _onStartIdProp;
        private SerializedProperty _onStopIdProp;
        private bool _showUsageGuide;

        protected override void OnEnable()
        {
            base.OnEnable();
            _onStartIdProp = serializedObject.FindProperty("_onStartEntryId");
            _onStopIdProp = serializedObject.FindProperty("_onStopEntryId");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var eventMapProp = serializedObject.FindProperty("_eventMap");
            EditorGUILayout.PropertyField(eventMapProp);

            var eventMap = eventMapProp.objectReferenceValue as HapbeatEventMap;
            if (eventMap == null || eventMap.entries.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Assign an Event Map and add at least one entry.",
                    MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Sequence Entries", EditorStyles.boldLabel);

            float prev = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(160f, EditorGUIUtility.currentViewWidth * 0.45f);
            try
            {
                DrawOptionalEntryDropdown(eventMap, _onStartIdProp,
                    "On Start",
                    "Optional one-shot haptic fired by Fire() (the 'attack' moment: " +
                    "grab click, button click, enter ping). Set to (none) to skip.");

                DrawEntryDropdown(eventMap,
                    serializedObject.FindProperty("_entryId"),
                    "Loop (hold)",
                    "Continuous haptic that plays between Fire() and Stop(). " +
                    "Normally a StreamClip with loop=true, optionally driven by a " +
                    "HapbeatParameterBinding for dynamic gain / pan modulation.");

                DrawOptionalEntryDropdown(eventMap, _onStopIdProp,
                    "On Stop",
                    "Optional one-shot haptic fired by Stop() (the 'release' moment: " +
                    "release thud, button release, exit ping). Set to (none) to skip.");
            }
            finally
            {
                EditorGUIUtility.labelWidth = prev;
            }

            EditorGUILayout.Space(4);
            DrawPropertiesExcluding(
                serializedObject,
                "_eventMap", "_entryId",
                "_onStartEntryId", "_onStopEntryId",
                "m_Script");

            EditorGUILayout.Space(5);
            _showUsageGuide = EditorGUILayout.Foldout(_showUsageGuide, "Usage Guide", true);
            if (_showUsageGuide)
            {
                EditorGUILayout.HelpBox(
                    "Grab / Hold / Release wiring:\n\n" +
                    "  XRGrabInteractable.firstSelectEntered  →  Fire()\n" +
                    "  XRGrabInteractable.lastSelectExited   →  Stop()\n\n" +
                    "Fire() plays the On Start one-shot, then begins Loop.\n" +
                    "Stop() ends Loop, then plays the On Stop one-shot.\n\n" +
                    "Set unused phases to \"(none)\".",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Use FireWithStartGain(float) (e.g. from an Animation Event) to pass strength in.\n" +
                    "Only the On Start gain is overridden (Loop uses the entry's gain as-is).",
                    MessageType.None);
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Entry dropdown that includes a leading "(none)" option, mapped to an
        /// empty id. Used for Sequence's optional On Start / On Stop phases.
        /// </summary>
        private static void DrawOptionalEntryDropdown(
            HapbeatEventMap eventMap,
            SerializedProperty idProp,
            string label,
            string tooltip)
        {
            string[] names = eventMap.GetDisplayNames();
            string[] options = new string[names.Length + 1];
            options[0] = "(none)";
            System.Array.Copy(names, 0, options, 1, names.Length);

            // popup index: 0 = (none), 1..N = entry index + 1.
            int currentEntryIdx = -1;
            if (!string.IsNullOrEmpty(idProp.stringValue))
                currentEntryIdx = eventMap.IndexOfId(idProp.stringValue);
            int popupIdx = currentEntryIdx >= 0 ? currentEntryIdx + 1 : 0;

            int newPopupIdx = EditorGUILayout.Popup(new GUIContent(label, tooltip),
                popupIdx, options);

            int resolvedEntryIdx = newPopupIdx - 1; // -1 = none
            if (resolvedEntryIdx < 0)
            {
                if (!string.IsNullOrEmpty(idProp.stringValue))
                    idProp.stringValue = "";
            }
            else if (resolvedEntryIdx < eventMap.entries.Count)
            {
                var entry = eventMap.entries[resolvedEntryIdx];
                string newId = entry != null ? entry.id : "";
                if (idProp.stringValue != newId)
                {
                    idProp.stringValue = newId;
                    EditorUtility.SetDirty(eventMap);
                    AssetDatabase.SaveAssetIfDirty(eventMap);
                }
            }

            // Short readonly preview line for the resolved entry
            if (resolvedEntryIdx >= 0 && resolvedEntryIdx < eventMap.entries.Count)
            {
                var entry = eventMap.entries[resolvedEntryIdx];
                string summary = $"   mode={entry.mode}  gain={entry.gain:F2}" +
                                 (entry.HasTarget ? $"  target={entry.target}" : "");
                EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("   (disabled)", EditorStyles.miniLabel);
            }

            // Surface stale-id state.
            if (currentEntryIdx < 0 && !string.IsNullOrEmpty(idProp.stringValue))
            {
                EditorGUILayout.HelpBox(
                    "Selected entry not found in the EventMap (may have been deleted). " +
                    "Please pick an entry again.",
                    MessageType.Warning);
            }
        }
    }
}
#endif
