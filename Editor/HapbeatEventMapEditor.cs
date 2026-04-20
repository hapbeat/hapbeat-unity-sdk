#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="HapbeatEventMap"/>. Adds:
    ///   - Portability: "Export as Unity Package" (bundles EventMap + referenced AudioClips).
    ///   - Non-destructive tuning: "Revert Play-mode changes on exit" toggle, plus
    ///     manual Snapshot / Restore buttons.
    /// </summary>
    [CustomEditor(typeof(HapbeatEventMap))]
    public class HapbeatEventMapEditor : UnityEditor.Editor
    {
        private const string kShowEntriesPrefKey = "Hapbeat.EventMap.ShowEntriesInInspector";

        private SerializedProperty _revertProp;
        private bool _showEntries;

        private void OnEnable()
        {
            _revertProp = serializedObject.FindProperty("revertPlayModeChanges");
            _showEntries = EditorPrefs.GetBool(kShowEntriesPrefKey, false);
        }

        public override void OnInspectorGUI()
        {
            var map = (HapbeatEventMap)target;

            // Unity's default labelWidth is based on the inspector width, which truncates
            // long labels ("Revert Play-mode Changes", "Display Name", ...) to "Revert Play-mode Ch",
            // "Displa", etc. Override to a generous width so labels are prioritised over
            // the value editor when the panel is narrow.
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(170f, EditorGUIUtility.currentViewWidth * 0.55f);

            try
            {
                DrawInspectorBody(map);
            }
            finally
            {
                EditorGUIUtility.labelWidth = prevLabelWidth;
            }
        }

        private void DrawInspectorBody(HapbeatEventMap map)
        {
            // --- Portability ---
            EditorGUILayout.LabelField("Portability", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Sharing: the .asset file IS the EventMap. To share across projects, " +
                "Export as Package (bundles referenced AudioClips) or drag-and-drop the " +
                "asset if both projects already have matching AudioClip GUIDs.",
                MessageType.None);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export as Unity Package\u2026", GUILayout.Height(22)))
                ExportAsPackage(map);
            if (GUILayout.Button("Reveal in Explorer", GUILayout.Height(22), GUILayout.Width(130)))
                EditorUtility.RevealInFinder(AssetDatabase.GetAssetPath(map));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // --- Play-mode tuning safety ---
            EditorGUILayout.LabelField("Play-mode Tuning", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUILayout.PropertyField(_revertProp,
                new GUIContent("Revert Play-mode Changes",
                    "Snapshot this asset when Play starts. On Play exit, restore the " +
                    "snapshot so edits made during Play are discarded. Useful for " +
                    "exploratory tuning."));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                if (GUILayout.Button("Snapshot Now", GUILayout.Height(22)))
                {
                    HapbeatEventMapPlaySnapshot.ManualSnapshot(map);
                }
            }
            using (new EditorGUI.DisabledScope(!HapbeatEventMapPlaySnapshot.HasSnapshot(map)))
            {
                if (GUILayout.Button("Restore Snapshot", GUILayout.Height(22)))
                {
                    if (EditorUtility.DisplayDialog("Restore EventMap Snapshot",
                        $"Restore '{map.name}' from the last saved snapshot? " +
                        "Current values (including any Play-mode edits) will be discarded.",
                        "Restore", "Cancel"))
                    {
                        HapbeatEventMapPlaySnapshot.RestoreSnapshot(map);
                    }
                }
            }
            EditorGUILayout.EndHorizontal();
            if (HapbeatEventMapPlaySnapshot.HasSnapshot(map))
            {
                EditorGUILayout.HelpBox(
                    $"Snapshot exists (taken {HapbeatEventMapPlaySnapshot.GetSnapshotTime(map):HH:mm:ss}).",
                    MessageType.None);
            }

            EditorGUILayout.Space(6);

            // --- Entries ---
            EditorGUILayout.LabelField("Entries", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                $"{map.entries.Count} entries. " +
                "The proper editor is Window > Hapbeat > Event Map — it gives each entry " +
                "a 2-column detail view (no label truncation) plus wiring introspection " +
                "and drag-drop source paths.",
                MessageType.None);

            if (GUILayout.Button("Open Event Map Window", GUILayout.Height(24)))
                EditorApplication.ExecuteMenuItem("Hapbeat/Event Map");

            // Raw entries list is hidden by default (labels truncate badly in the narrow
            // inspector). Expose via a toggle for the rare case someone wants it here.
            EditorGUI.BeginChangeCheck();
            _showEntries = EditorGUILayout.ToggleLeft(
                new GUIContent("Show raw entries list (advanced)",
                    "Render the built-in entries array in this inspector. Field labels " +
                    "may truncate in a narrow inspector — use the Event Map window instead " +
                    "for normal editing."),
                _showEntries);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(kShowEntriesPrefKey, _showEntries);

            if (_showEntries)
            {
                // Widen the label column so field names like "Display Name" and "Stream Clip"
                // aren't chopped to "Displa" / "Strea" when the inspector is narrow.
                float prevLabel = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = Mathf.Max(160f, EditorGUIUtility.currentViewWidth * 0.5f);
                DrawPropertiesExcluding(serializedObject, "m_Script", "revertPlayModeChanges");
                EditorGUIUtility.labelWidth = prevLabel;
            }

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Export the EventMap plus its full dependency chain (AudioClips, etc.) as a
        /// <c>.unitypackage</c>. The user picks the destination path.
        /// </summary>
        private static void ExportAsPackage(HapbeatEventMap map)
        {
            string assetPath = AssetDatabase.GetAssetPath(map);
            if (string.IsNullOrEmpty(assetPath))
            {
                EditorUtility.DisplayDialog("Export",
                    "EventMap is not saved as an asset yet. Save it to the project first.",
                    "OK");
                return;
            }

            string defaultName = $"{Path.GetFileNameWithoutExtension(assetPath)}.unitypackage";
            string outPath = EditorUtility.SaveFilePanel(
                "Export EventMap as Unity Package", "", defaultName, "unitypackage");
            if (string.IsNullOrEmpty(outPath)) return;

            AssetDatabase.ExportPackage(
                new[] { assetPath },
                outPath,
                ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

            EditorUtility.RevealInFinder(outPath);
            Debug.Log($"[Hapbeat] Exported '{map.name}' + dependencies \u2192 {outPath}");
        }
    }
}
#endif
