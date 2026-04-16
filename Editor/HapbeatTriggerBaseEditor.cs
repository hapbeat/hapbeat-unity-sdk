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

        // Banner style: bold white on colored background
        private static GUIStyle _bannerStyle;
        private static GUIStyle BannerStyle => _bannerStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 12,
            normal = { textColor = Color.white },
            padding = new RectOffset(4, 4, 2, 2)
        };
        private static readonly Color BannerColor = new Color(0.25f, 0.5f, 0.85f, 0.9f);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // --- Entry name banner ---
            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;
            if (eventMap != null && eventMap.entries.Count > 0)
            {
                int idx = Mathf.Clamp(_entryIndexProp.intValue, 0, eventMap.entries.Count - 1);
                var entry = eventMap.GetEntry(idx);
                if (entry != null)
                {
                    string bannerText = !string.IsNullOrEmpty(entry.displayName)
                        ? $"[{entry.displayName}]  {entry.GetModePrefix()}{entry.GetSummary()}"
                        : $"[{idx}]  {entry.GetModePrefix()}{entry.GetSummary()}";

                    var rect = EditorGUILayout.GetControlRect(false, 20);
                    EditorGUI.DrawRect(rect, BannerColor);
                    GUI.Label(rect, bannerText, BannerStyle);
                    EditorGUILayout.Space(2);
                }
            }

            // Draw the event map field
            EditorGUILayout.PropertyField(_eventMapProp);

            // Draw entry index as dropdown
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
