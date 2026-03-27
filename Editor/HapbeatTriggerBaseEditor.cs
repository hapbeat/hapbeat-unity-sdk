#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for all HapbeatTriggerBase subclasses.
    /// Renders _entryIndex as a dropdown of event map entry display names.
    /// </summary>
    [CustomEditor(typeof(HapbeatTriggerBase), true)]
    [CanEditMultipleObjects]
    public class HapbeatTriggerBaseEditor : UnityEditor.Editor
    {
        private SerializedProperty _eventMapProp;
        private SerializedProperty _entryIndexProp;

        protected virtual void OnEnable()
        {
            _eventMapProp = serializedObject.FindProperty("_eventMap");
            _entryIndexProp = serializedObject.FindProperty("_entryIndex");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Draw the event map field
            EditorGUILayout.PropertyField(_eventMapProp);

            // Draw entry index as dropdown
            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;
            if (eventMap != null && eventMap.entries.Count > 0)
            {
                string[] names = eventMap.GetDisplayNames();
                int currentIndex = Mathf.Clamp(_entryIndexProp.intValue, 0, names.Length - 1);
                int newIndex = EditorGUILayout.Popup(
                    new GUIContent("Event", "Select a haptic event from the event map"),
                    currentIndex, names);
                _entryIndexProp.intValue = newIndex;

                // Show preview of selected entry
                var entry = eventMap.GetEntry(newIndex);
                if (entry != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField("Event ID", entry.eventId);
                    EditorGUILayout.FloatField("Gain", entry.gain);
                    EditorGUILayout.IntField("Group", entry.group);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }
            }
            else if (eventMap != null && eventMap.entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Event Map にエントリがありません。Event Map アセットにイベントを追加してください。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Event Map を設定してください。", MessageType.Warning);
            }

            // Draw remaining properties (skip _eventMap and _entryIndex which we already drew)
            DrawPropertiesExcluding(serializedObject, "_eventMap", "_entryIndex", "m_Script");

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
