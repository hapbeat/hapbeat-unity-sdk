#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="HapbeatSequenceTrigger"/>. Shows all three
    /// entries (On Start / Loop / On Stop) as dropdowns with the shared event-map
    /// icon prefix, and adds a short wiring reminder for XRI grab interactions.
    /// </summary>
    [CustomEditor(typeof(HapbeatSequenceTrigger))]
    [CanEditMultipleObjects]
    public class HapbeatSequenceTriggerEditor : HapbeatTriggerBaseEditor
    {
        private SerializedProperty _onStartProp;
        private SerializedProperty _onStopProp;
        private bool _showUsageGuide;

        protected override void OnEnable()
        {
            base.OnEnable();
            _onStartProp = serializedObject.FindProperty("_onStartEntryIndex");
            _onStopProp = serializedObject.FindProperty("_onStopEntryIndex");
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
                    "Event Map \u3092\u8a2d\u5b9a\u3057\u3001\u30a8\u30f3\u30c8\u30ea\u3092\u8ffd\u52a0\u3057\u3066\u304f\u3060\u3055\u3044\u3002",
                    MessageType.Warning);
                serializedObject.ApplyModifiedProperties();
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Sequence Entries", EditorStyles.boldLabel);

            // Widen the label column so "On Start / Loop (hold) / On Stop" labels aren't
            // truncated in a narrow inspector.
            float prev = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(160f, EditorGUIUtility.currentViewWidth * 0.45f);
            try
            {
                DrawEntryDropdown(eventMap, _onStartProp,
                    "On Start",
                    "Optional one-shot haptic fired by Fire() (the 'attack' moment: " +
                    "grab click, button click, enter ping). Set to (none) to skip.",
                    allowNone: true);

                DrawEntryDropdown(eventMap, serializedObject.FindProperty("_entryIndex"),
                    "Loop (hold)",
                    "Continuous haptic that plays between Fire() and Stop(). " +
                    "Normally a StreamSource or a StreamClip with loop=true.",
                    allowNone: false);

                DrawEntryDropdown(eventMap, _onStopProp,
                    "On Stop",
                    "Optional one-shot haptic fired by Stop() (the 'release' moment: " +
                    "release thud, button release, exit ping). Set to (none) to skip.",
                    allowNone: true);
            }
            finally
            {
                EditorGUIUtility.labelWidth = prev;
            }

            // Remaining inherited fields (trigger enabled, cooldown, verbose log, ...)
            EditorGUILayout.Space(4);
            DrawPropertiesExcluding(
                serializedObject,
                "_eventMap", "_entryIndex", "_onStartEntryIndex", "_onStopEntryIndex", "m_Script");

            EditorGUILayout.Space(5);
            _showUsageGuide = EditorGUILayout.Foldout(_showUsageGuide, "Usage Guide", true);
            if (_showUsageGuide)
            {
                EditorGUILayout.HelpBox(
                    "Grab / Hold / Release \u30d1\u30bf\u30fc\u30f3\u306e wire:\n\n" +
                    "\u25b6 XRGrabInteractable.firstSelectEntered  \u2192  Fire()\n" +
                    "\u25b6 XRGrabInteractable.lastSelectExited   \u2192  Stop()\n\n" +
                    "Fire() \u306f On Start \u306e\u4e00\u767a\u30b7\u30e7\u30c3\u30c8\u3092\u9001\u308a\u3001\u305d\u306e\u307e\u307e Loop \u3092\u958b\u59cb\u3057\u307e\u3059\u3002\n" +
                    "Stop() \u306f Loop \u3092\u6b62\u3081\u3001On Stop \u306e\u4e00\u767a\u30b7\u30e7\u30c3\u30c8\u3092\u9001\u308a\u307e\u3059\u3002\n\n" +
                    "\u4f7f\u308f\u306a\u3044\u30d5\u30a7\u30fc\u30ba\u306f \u300c(none)\u300d \u306b\u3057\u307e\u3059\u3002\n" +
                    "On Start / On Stop \u306f Command \u307e\u305f\u306f StreamClip \u306b\u3057\u307e\u3059 \u2014 StreamSource \u306f\u4e00\u767a\u7528\u9014\u306b\u306f\u4e0d\u9069\u3002",
                    MessageType.Info);

                EditorGUILayout.HelpBox(
                    "Animation Event \u7b49\u304b\u3089\u5f37\u5ea6\u3092\u53d7\u3051\u6e21\u3057\u305f\u3044\u5834\u5408\u306f FireWithStartGain(float) \u3092\u4f7f\u3048\u307e\u3059\u3002\n" +
                    "On Start \u306e\u5f37\u5ea6\u3060\u3051\u4e0a\u66f8\u304d\u3055\u308c\u307e\u3059\uff08Loop \u306f\u30a8\u30f3\u30c8\u30ea\u8a2d\u5b9a\u306e gain \u3092\u305d\u306e\u307e\u307e\u4f7f\u7528\uff09\u3002",
                    MessageType.None);
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draw a popup for <paramref name="prop"/> showing EventMap entry display
        /// names (prefixed with mode icon). When <paramref name="allowNone"/>, the
        /// popup has a leading "(none)" option mapped to -1.
        /// </summary>
        private static void DrawEntryDropdown(
            HapbeatEventMap map,
            SerializedProperty prop,
            string label,
            string tooltip,
            bool allowNone)
        {
            string[] names = map.GetDisplayNames();

            string[] options;
            if (allowNone)
            {
                options = new string[names.Length + 1];
                options[0] = "(none)";
                System.Array.Copy(names, 0, options, 1, names.Length);
            }
            else
            {
                options = names;
            }

            // Map SerializedProperty value (-1..N-1) \u2192 popup index (0..N or 0..N-1).
            int popupIdx = allowNone ? prop.intValue + 1 : prop.intValue;
            popupIdx = Mathf.Clamp(popupIdx, 0, options.Length - 1);
            int newPopupIdx = EditorGUILayout.Popup(new GUIContent(label, tooltip), popupIdx, options);
            prop.intValue = allowNone ? newPopupIdx - 1 : newPopupIdx;

            // Small readonly preview line showing the resolved entry's mode/icon/target
            int resolvedIdx = allowNone ? newPopupIdx - 1 : newPopupIdx;
            if (resolvedIdx >= 0 && resolvedIdx < map.entries.Count)
            {
                var entry = map.entries[resolvedIdx];
                string summary = $"   mode={entry.mode}  gain={entry.gain:F2}" +
                                 (entry.HasTarget ? $"  target={entry.target}" : "");
                EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
            }
            else if (allowNone && resolvedIdx == -1)
            {
                EditorGUILayout.LabelField("   (disabled)", EditorStyles.miniLabel);
            }
        }
    }
}
#endif
