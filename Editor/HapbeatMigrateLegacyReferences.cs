#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Hapbeat.Editor
{
    /// <summary>
    /// One-shot migration: for every <see cref="HapbeatTriggerBase"/> in open
    /// scenes and every prefab asset that stores trigger references, populate
    /// the new <c>_entryId</c> (and <c>_onStartEntryId</c> / <c>_onStopEntryId</c>
    /// for sequence triggers) from the legacy <c>_entryIndex</c>-only data.
    ///
    /// <para>
    /// Safe to re-run: triggers whose id is already set are left alone.
    /// </para>
    /// </summary>
    public static class HapbeatMigrateLegacyReferences
    {
        [MenuItem("Hapbeat/Migrate Legacy Entry References", false, 900)]
        public static void MigrateAll()
        {
            if (!EditorUtility.DisplayDialog(
                    "Migrate Hapbeat legacy references",
                    "Scans open scenes + all prefab assets under Assets/ and writes " +
                    "stable entry ids for every HapbeatTriggerBase that still uses " +
                    "the legacy index-only reference.\n\n" +
                    "Safe to run at any time. Results in dirty scenes / prefabs that " +
                    "need to be saved.",
                    "Run", "Cancel"))
                return;

            int scenesTouched = 0, prefabsTouched = 0, triggersTouched = 0;

            // Open scenes
            for (int si = 0; si < SceneManager.sceneCount; si++)
            {
                var scene = SceneManager.GetSceneAt(si);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                int touchedInScene = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    touchedInScene += MigrateGameObject(root);
                }
                if (touchedInScene > 0)
                {
                    scenesTouched++;
                    triggersTouched += touchedInScene;
                    EditorSceneManager.MarkSceneDirty(scene);
                }
            }

            // Prefab assets
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in prefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var root = PrefabUtility.LoadPrefabContents(path);
                if (root == null) continue;
                try
                {
                    int touchedInPrefab = MigrateGameObject(root);
                    if (touchedInPrefab > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, path);
                        prefabsTouched++;
                        triggersTouched += touchedInPrefab;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            // Also proactively assign ids on every EventMap asset so new triggers
            // can be looked up by id right away.
            int mapsTouched = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:HapbeatEventMap"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map == null) continue;
                bool mapDirty = false;
                foreach (var entry in map.entries)
                {
                    if (entry == null) continue;
                    if (!entry.HasId)
                    {
                        _ = entry.id; // lazy-assign
                        mapDirty = true;
                    }
                }
                if (mapDirty)
                {
                    EditorUtility.SetDirty(map);
                    AssetDatabase.SaveAssetIfDirty(map);
                    mapsTouched++;
                }
            }

            AssetDatabase.Refresh();

            string msg =
                $"Migration complete:\n" +
                $"  {triggersTouched} trigger(s) updated\n" +
                $"  {scenesTouched} scene(s) dirtied\n" +
                $"  {prefabsTouched} prefab(s) saved\n" +
                $"  {mapsTouched} event map(s) updated";
            Debug.Log($"[Hapbeat] {msg.Replace('\n', ' ')}");
            EditorUtility.DisplayDialog("Hapbeat migration", msg, "OK");
        }

        /// <summary>
        /// Walk a GameObject hierarchy, migrating any trigger with empty ids.
        /// Returns the number of triggers that were updated.
        /// </summary>
        private static int MigrateGameObject(GameObject root)
        {
            int count = 0;
            var triggers = root.GetComponentsInChildren<HapbeatTriggerBase>(includeInactive: true);
            foreach (var trig in triggers)
            {
                if (trig == null || trig.EventMap == null) continue;

                var so = new SerializedObject(trig);
                bool changed = false;

                changed |= TryMigrate(so, trig.EventMap, "_entryId", "_entryIndex");
                changed |= TryMigrate(so, trig.EventMap, "_onStartEntryId", "_onStartEntryIndex");
                changed |= TryMigrate(so, trig.EventMap, "_onStopEntryId", "_onStopEntryIndex");

                if (changed)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(trig);
                    count++;
                }
            }
            return count;
        }

        private static bool TryMigrate(SerializedObject so, HapbeatEventMap map,
            string idPropName, string indexPropName)
        {
            var idProp = so.FindProperty(idPropName);
            var indexProp = so.FindProperty(indexPropName);
            if (idProp == null || indexProp == null) return false;
            if (!string.IsNullOrEmpty(idProp.stringValue)) return false;
            int idx = indexProp.intValue;
            if (idx < 0) return false; // legitimate "(none)" — leave empty id
            var entry = map.GetEntry(idx);
            if (entry == null) return false;
            idProp.stringValue = entry.id; // lazy-assigns on the entry too
            // Caller is expected to ApplyModifiedProperties + EditorUtility.SetDirty.
            // We also need to mark the EventMap dirty in case .id just lazy-assigned.
            EditorUtility.SetDirty(map);
            AssetDatabase.SaveAssetIfDirty(map);
            return true;
        }
    }
}
#endif
