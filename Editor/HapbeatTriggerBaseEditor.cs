#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for all HapbeatTriggerBase subclasses.
    /// Renders the event reference as a dropdown of event-map entry display
    /// names. Under the hood the selection writes the entry's <b>stable GUID</b>
    /// (<c>_entryId</c>) so reordering entries in the map does not break wiring.
    /// </summary>
    [CustomEditor(typeof(HapbeatTriggerBase), true)]
    [CanEditMultipleObjects]
    public class HapbeatTriggerBaseEditor : UnityEditor.Editor
    {
        private SerializedProperty _eventMapProp;
        private SerializedProperty _entryIdProp;

        protected virtual void OnEnable()
        {
            _eventMapProp = serializedObject.FindProperty("_eventMap");
            _entryIdProp = serializedObject.FindProperty("_entryId");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_eventMapProp);

            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;
            DrawEntryDropdown(eventMap, _entryIdProp, "Event",
                "Select a haptic event from the event map. The reference is stored " +
                "by stable GUID so reordering entries won't break this trigger.");

            if (eventMap != null && eventMap.entries.Count > 0)
            {
                var entry = ResolveEntry(eventMap, _entryIdProp);
                if (entry != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    if (entry.mode == HapticMode.Command)
                        EditorGUILayout.TextField("Event ID", entry.eventId);
                    else
                        EditorGUILayout.TextField("Mode", entry.mode.ToString());
                    EditorGUILayout.FloatField(
                        new GUIContent("Gain",
                            "Authored gain (0–2). " +
                            "Wire = Gain × Manifest Intensity × Trigger Multiplier.\n" +
                            "The device plays req.gain directly; manifest.intensity is " +
                            "applied by the SDK before sending."),
                        entry.gain);
                    if (entry.HasTarget)
                        EditorGUILayout.TextField("Target", entry.target);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }
            }

            DrawPropertiesExcluding(serializedObject,
                "_eventMap", "_entryId", "m_Script");

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draw a dropdown of event map entries. On selection, writes the entry's
        /// stable GUID into <paramref name="idProp"/>. Shows the currently-stored
        /// entry name if the id resolves; falls through to "(missing entry)" when
        /// the id is stale (referenced entry was deleted).
        /// </summary>
        public static void DrawEntryDropdown(
            HapbeatEventMap eventMap,
            SerializedProperty idProp,
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

            int currentIndex = -1;
            if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
                currentIndex = eventMap.IndexOfId(idProp.stringValue);

            string[] names = eventMap.GetDisplayNames();
            int displayIndex = currentIndex >= 0 ? currentIndex : -1;
            int newIndex = EditorGUILayout.Popup(new GUIContent(label, tooltip),
                displayIndex, names);

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
            }

            // Surface stale-id state to the designer so they know to re-pick.
            if (currentIndex < 0 && idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
            {
                EditorGUILayout.HelpBox(
                    "選択中の entry が EventMap に見つかりません (削除された可能性)。" +
                    "再度 entry を選択してください。",
                    MessageType.Warning);
            }
        }

        private static HapbeatEventEntry ResolveEntry(
            HapbeatEventMap map, SerializedProperty idProp)
        {
            if (idProp == null || string.IsNullOrEmpty(idProp.stringValue)) return null;
            return map.FindById(idProp.stringValue);
        }
    }
}
#endif
