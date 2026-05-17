#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor-side guarantee that every HapbeatEventEntry has its
    /// <see cref="HapbeatEventEntry.CachedManifestIntensity"/> populated from the
    /// owning Kit's manifest.json, without requiring the user to open the
    /// HapbeatEventMapWindow.
    /// <para>
    /// Triggers:
    /// <list type="bullet">
    ///   <item>Editor startup (InitializeOnLoad) — backfills any pre-existing
    ///         EventMap whose entries still have the -1 sentinel.</item>
    ///   <item>Any <c>manifest.json</c> import (Kit added / deployed / edited) —
    ///         invalidates the parser cache and re-resolves all EventMaps.</item>
    ///   <item>Any <c>.asset</c> import that is a HapbeatEventMap — fills
    ///         sentinels on the freshly-loaded map.</item>
    /// </list>
    /// </para>
    /// </summary>
    [InitializeOnLoad]
    public static class HapbeatEventMapAutoIntensityRefresher
    {
        static HapbeatEventMapAutoIntensityRefresher()
        {
            // Defer so we don't run during script reload / asset import.
            EditorApplication.delayCall += () => RefreshAll(invalidateParserCache: false);
        }

        /// <summary>
        /// Walk every HapbeatEventMap in the project and populate entries whose
        /// cache is still the unresolved sentinel (-1).
        /// </summary>
        /// <param name="invalidateParserCache">
        /// If true, force the manifest parser to re-read all manifest.json files
        /// AND refresh every cached value (not just sentinels). Use when a Kit
        /// manifest is known to have changed.
        /// </param>
        public static void RefreshAll(bool invalidateParserCache)
        {
            if (invalidateParserCache) HapbeatManifestIntensity.Invalidate();

            var guids = AssetDatabase.FindAssets("t:HapbeatEventMap");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map == null) continue;
                RefreshMap(map, forceAllEntries: invalidateParserCache);
            }
        }

        private static void RefreshMap(HapbeatEventMap map, bool forceAllEntries)
        {
            if (map == null || map.entries == null) return;

            bool changed = false;
            foreach (var entry in map.entries)
            {
                if (entry == null) continue;
                bool isSentinel = entry.CachedManifestIntensity < 0f;
                if (!forceAllEntries && !isSentinel) continue;

                if (HapbeatManifestIntensity.TryGetIntensity(entry, out float intensity))
                {
                    if (Math.Abs(entry.CachedManifestIntensity - intensity) > 0.0005f)
                    {
                        entry.SetCachedManifestIntensity(intensity);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(map);
                AssetDatabase.SaveAssetIfDirty(map);
            }
        }

        // ------------------------------------------------------------------
        // AssetPostprocessor — watch for Kit / EventMap imports.
        // ------------------------------------------------------------------
        private class Postprocessor : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] importedAssets,
                string[] deletedAssets,
                string[] movedAssets,
                string[] movedFromAssetPaths)
            {
                bool manifestChanged = false;
                bool eventMapImported = false;

                foreach (var p in importedAssets)
                {
                    // Manifest filename convention: <kitname>-manifest.json
                    // (formerly bare manifest.json). Match any file whose
                    // name contains "manifest" and ends with ".json".
                    if (IsManifestPath(p))
                        manifestChanged = true;
                    else if (p.EndsWith(".asset", StringComparison.Ordinal))
                    {
                        // Cheap probe — only fully load if it might be an EventMap.
                        var t = AssetDatabase.GetMainAssetTypeAtPath(p);
                        if (t == typeof(HapbeatEventMap))
                            eventMapImported = true;
                    }
                }

                if (!manifestChanged && !eventMapImported) return;

                // Defer to avoid re-entering the import pipeline.
                bool needFullInvalidate = manifestChanged;
                EditorApplication.delayCall += () => RefreshAll(invalidateParserCache: needFullInvalidate);
            }

            /// <summary>
            /// True if <paramref name="assetPath"/> is a Kit manifest file
            /// — matches "*manifest*.json" (case-insensitive). The canonical
            /// name is &lt;kitname&gt;-manifest.json, but any *manifest*.json
            /// at the path counts as a manifest change.
            /// </summary>
            private static bool IsManifestPath(string assetPath)
            {
                if (string.IsNullOrEmpty(assetPath)) return false;
                if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
                string fileName = System.IO.Path.GetFileName(assetPath);
                return fileName.IndexOf("manifest", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
#endif
