#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for all HapbeatTriggerBase subclasses.
    /// Renders the event reference as a dropdown of event-map entry display
    /// names. Under the hood the selection writes the entry's <b>stable GUID</b>
    /// (<c>_entryId</c>) plus a mirrored <c>_entryIndex</c> for display.
    /// </summary>
    [CustomEditor(typeof(HapbeatTriggerBase), true)]
    [CanEditMultipleObjects]
    public class HapbeatTriggerBaseEditor : UnityEditor.Editor
    {
        private SerializedProperty _eventMapProp;
        private SerializedProperty _entryIdProp;
        private SerializedProperty _entryIndexProp;

        protected virtual void OnEnable()
        {
            _eventMapProp = serializedObject.FindProperty("_eventMap");
            _entryIdProp = serializedObject.FindProperty("_entryId");
            _entryIndexProp = serializedObject.FindProperty("_entryIndex");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_eventMapProp);

            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;
            DrawEntryDropdown(eventMap, _entryIdProp, _entryIndexProp, "Event",
                "Select a haptic event from the event map. The reference is stored " +
                "by stable GUID so reordering entries won't break this trigger.");

            if (eventMap != null && eventMap.entries.Count > 0)
            {
                var entry = ResolveEntry(eventMap, _entryIdProp, _entryIndexProp);
                if (entry != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    if (entry.mode == HapticMode.Command)
                        EditorGUILayout.TextField("Event ID", entry.eventId);
                    else
                        EditorGUILayout.TextField("Mode", entry.mode.ToString());
                    EditorGUILayout.FloatField("Gain", entry.gain);
                    if (entry.HasTarget)
                        EditorGUILayout.TextField("Target", entry.target);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }
            }

            DrawPropertiesExcluding(serializedObject,
                "_eventMap", "_entryIndex", "_entryId", "m_Script");

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draw a dropdown of event map entries. On selection, writes both
        /// the stable id (<paramref name="idProp"/>) and the display-cache index
        /// (<paramref name="indexProp"/>). Handles migration from the legacy
        /// index-only state when <paramref name="idProp"/> is empty.
        /// </summary>
        public static void DrawEntryDropdown(
            HapbeatEventMap eventMap,
            SerializedProperty idProp,
            SerializedProperty indexProp,
            string label,
            string tooltip)
        {
            if (eventMap == null)
            {
                EditorGUILayout.HelpBox("Event Map を設定してください。", MessageType.Warning);
                return;
            }
            if (eventMap.entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Event Map にエントリがありません。", MessageType.Warning);
                return;
            }

            // Resolve currently-selected index: prefer id, fall back to index.
            int currentIndex = -1;
            if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
            {
                currentIndex = eventMap.IndexOfId(idProp.stringValue);
                // Stale id → clamp to index cache so the dropdown doesn't show random stuff.
                if (currentIndex < 0) currentIndex = Mathf.Clamp(indexProp.intValue, 0, eventMap.entries.Count - 1);
            }
            else
            {
                currentIndex = Mathf.Clamp(indexProp.intValue, 0, eventMap.entries.Count - 1);
            }

            string[] names = eventMap.GetDisplayNames();
            int newIndex = EditorGUILayout.Popup(new GUIContent(label, tooltip),
                currentIndex, names);

            if (newIndex >= 0 && newIndex < eventMap.entries.Count)
            {
                var entry = eventMap.entries[newIndex];
                // Lazy-assigns the id on the entry if empty. Mark the EventMap
                // dirty so the new id survives the next domain reload.
                string newId = entry != null ? entry.id : "";
                if (idProp != null && idProp.stringValue != newId)
                {
                    idProp.stringValue = newId;
                    EditorUtility.SetDirty(eventMap);
                    AssetDatabase.SaveAssetIfDirty(eventMap);
                }
                indexProp.intValue = newIndex;
            }
        }

        private static HapbeatEventEntry ResolveEntry(
            HapbeatEventMap map, SerializedProperty idProp, SerializedProperty indexProp)
        {
            if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
            {
                var e = map.FindById(idProp.stringValue);
                if (e != null) return e;
            }
            return map.GetEntry(indexProp.intValue);
        }
    }
}
#endif
