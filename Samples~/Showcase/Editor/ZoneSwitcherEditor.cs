#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Samples.Showcase.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="ZoneSwitcher"/>. Draws "Initial Zone" as
    /// a dropdown built from the configured <see cref="ZoneSwitcher.ZoneEntry.label"/>s
    /// instead of a raw 1..9 int slider, so authors pick a zone by name instead
    /// of having to remember which number maps to which entry in <c>_zones</c>.
    /// </summary>
    [CustomEditor(typeof(ZoneSwitcher))]
    public class ZoneSwitcherEditor : UnityEditor.Editor
    {
        private SerializedProperty _zonesProp;
        private SerializedProperty _initialZoneProp;

        private void OnEnable()
        {
            _zonesProp = serializedObject.FindProperty("_zones");
            _initialZoneProp = serializedObject.FindProperty("_initialZone");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawInitialZoneField();
            EditorGUILayout.Space();

            // 残りのフィールドはデフォルト描画 (Initial Zone は上で個別描画済み)
            DrawPropertiesExcluding(serializedObject, "m_Script", "_initialZone");

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawInitialZoneField()
        {
            int zoneCount = _zonesProp != null ? _zonesProp.arraySize : 0;

            var labels = new List<string>(zoneCount);
            for (int i = 0; i < zoneCount; i++)
            {
                var entry = _zonesProp.GetArrayElementAtIndex(i);
                var labelProp = entry?.FindPropertyRelative("label");
                string label = labelProp != null ? labelProp.stringValue : null;
                labels.Add(string.IsNullOrEmpty(label) ? $"Zone {i + 1}" : $"{i + 1}: {label}");
            }

            // Fallback: no zones configured yet (empty _zones list) — fall back
            // to the original 1..9 int field so authoring still works before
            // any ZoneEntry exists.
            if (labels.Count == 0)
            {
                EditorGUILayout.PropertyField(_initialZoneProp,
                    new GUIContent("Initial Zone", "起動時に表示するゾーン (1 始まり)。_zones が空のためドロップダウン化できません。"));
                return;
            }

            // Clamp defensively — a serialized value from before _zones shrank,
            // or before any labels existed, may be out of range.
            int currentIndex = Mathf.Clamp(_initialZoneProp.intValue - 1, 0, labels.Count - 1);
            int selectedIndex = EditorGUILayout.Popup(
                new GUIContent("Initial Zone", "起動時に表示するゾーン。"),
                currentIndex, labels.ToArray());
            _initialZoneProp.intValue = selectedIndex + 1;
        }
    }
}
#endif
