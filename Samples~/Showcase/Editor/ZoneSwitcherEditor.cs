using UnityEditor;
using UnityEngine;
using Hapbeat.Samples.Tutorial;

namespace Hapbeat.Samples.Tutorial.EditorTools
{
    /// <summary>
    /// ZoneSwitcher の Inspector で <c>_initialZone</c> を素の int field ではなく
    /// 「1: Bowling」「2: Door」のように zone label 付き Popup で選べるようにする。
    /// それ以外のフィールドはデフォルト描画。
    /// </summary>
    [CustomEditor(typeof(ZoneSwitcher))]
    public class ZoneSwitcherEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var iter = serializedObject.GetIterator();
            iter.NextVisible(true); // m_Script
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(iter, true);

            while (iter.NextVisible(false))
            {
                if (iter.name == "_initialZone")
                    DrawInitialZonePopup(iter);
                else
                    EditorGUILayout.PropertyField(iter, true);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawZoneLayoutUtilities();
        }

        private void DrawZoneLayoutUtilities()
        {
            var switcher = (ZoneSwitcher)target;
            var zonesProp = serializedObject.FindProperty("_zones");
            if (zonesProp == null || zonesProp.arraySize == 0) return;

            EditorGUILayout.LabelField("Edit-time layout", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Scene 上の zone root 配置ヘルパー。Play 時は ZoneSwitcher が active zone を " +
                "_activeZonePosition (= 通常 (0,0,0)) に自動移動するので、" +
                "Edit 時はここで好きに散らして編集できます。",
                MessageType.Info);

            if (GUILayout.Button(new GUIContent(
                "Spread X (15m step)",
                "Z1〜ZN を X 軸に等間隔配置 (例: 5 zone なら -30, -15, 0, 15, 30)。" +
                "scene ビューで各 zone を個別に編集したい時に。")))
            {
                int n = 0;
                float step = 15f;
                float start = -((switcher.Zones.Count - 1) * step) / 2f;
                foreach (var z in switcher.Zones)
                {
                    if (z == null || z.root == null) { n++; continue; }
                    Undo.RecordObject(z.root.transform, "Spread Zone Root");
                    z.root.transform.position = new Vector3(start + n * step, 0f, 0f);
                    n++;
                }
                Debug.Log($"[ZoneSwitcher] Spread {n} zone roots along X axis (step={step}m).");
            }
        }

        private void DrawInitialZonePopup(SerializedProperty prop)
        {
            var zonesProp = serializedObject.FindProperty("_zones");
            int zoneCount = zonesProp != null ? zonesProp.arraySize : 0;

            if (zoneCount == 0)
            {
                EditorGUILayout.PropertyField(prop, true);
                EditorGUILayout.HelpBox("Zones が空。先にエントリを追加してください。", MessageType.Info);
                return;
            }

            // Build options array: "1: Bowling", "2: Door", ...
            var options = new string[zoneCount];
            for (int i = 0; i < zoneCount; i++)
            {
                var entry = zonesProp.GetArrayElementAtIndex(i);
                var labelProp = entry.FindPropertyRelative("label");
                string label = labelProp != null && !string.IsNullOrEmpty(labelProp.stringValue)
                    ? labelProp.stringValue : "(no label)";
                options[i] = $"{i + 1}: {label}";
            }

            // current value is 1-based, dropdown index is 0-based
            int currentIdx = Mathf.Clamp(prop.intValue - 1, 0, zoneCount - 1);
            int newIdx = EditorGUILayout.Popup(
                new GUIContent("Initial Zone", "Play 開始時に表示するゾーン"),
                currentIdx, options);
            if (newIdx != currentIdx)
                prop.intValue = newIdx + 1;
        }
    }
}
