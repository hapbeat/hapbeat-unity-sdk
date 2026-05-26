using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Export a HapbeatEventMap as a Markdown summary that is easy for AI and
    /// humans to read. Unity's .asset YAML is verbose (m_FileID / GUID / enum
    /// integers), so this produces structured text in a sibling file.
    ///
    /// Output: <c>{MapName}.md</c> in the same folder as the EventMap asset.
    ///
    /// Menu: Hapbeat / Event Map / Export Markdown summary
    /// </summary>
    public static class HapbeatEventMapMarkdownExport
    {
        [MenuItem("Hapbeat/Export Event Map (Selected)", false, 50)]
        public static void ExportSelected()
        {
            var map = Selection.activeObject as HapbeatEventMap;
            if (map == null)
            {
                EditorUtility.DisplayDialog("No EventMap selected",
                    "Select a HapbeatEventMap (.asset) in the Project window first.",
                    "OK");
                return;
            }
            ExportToMarkdown(map);
        }

        [MenuItem("Hapbeat/Export Event Map (All in Project)", false, 51)]
        public static void ExportAll()
        {
            string[] guids = AssetDatabase.FindAssets("t:HapbeatEventMap");
            int n = 0;
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map != null) { ExportToMarkdown(map); n++; }
            }
            EditorUtility.DisplayDialog("Export done", $"Exported {n} EventMap(s) to Markdown.", "OK");
        }

        public static void ExportToMarkdown(HapbeatEventMap map)
        {
            string assetPath = AssetDatabase.GetAssetPath(map);
            if (string.IsNullOrEmpty(assetPath)) return;
            string dir = Path.GetDirectoryName(assetPath);
            string mdPath = Path.Combine(dir, $"{map.name}.md");

            var sb = new StringBuilder();
            sb.AppendLine($"# {map.name}");
            sb.AppendLine();
            sb.AppendLine($"_Auto-generated from `{assetPath}` (Hapbeat → Export Event Map). " +
                          "Edit via the EventMap window — change the `.asset`, not this file._");
            sb.AppendLine();
            sb.AppendLine($"**Entry count**: {map.entries.Count}");
            sb.AppendLine();

            for (int i = 0; i < map.entries.Count; i++)
            {
                var entry = map.entries[i];
                if (entry == null) continue;
                AppendEntry(sb, entry, i);
            }

            File.WriteAllText(mdPath, sb.ToString());
            AssetDatabase.ImportAsset(mdPath);
            Debug.Log($"[Hapbeat] Exported EventMap markdown: {mdPath}");
        }

        private static void AppendEntry(StringBuilder sb, HapbeatEventEntry e, int index)
        {
            string name = !string.IsNullOrEmpty(e.displayName) ? e.displayName : $"(entry {index})";
            sb.AppendLine($"## {name}");
            sb.AppendLine();

            sb.AppendLine($"- **id**: `{e.id}`");
            sb.AppendLine($"- **mode**: {e.mode}{(e.loop ? " (loop)" : "")}");
            if (e.mode == HapticMode.Command)
            {
                if (!string.IsNullOrEmpty(e.eventId))
                    sb.AppendLine($"- **eventId**: `{e.eventId}`");
            }
            else if (e.mode == HapticMode.StreamClip)
            {
                if (e.streamClip != null)
                    sb.AppendLine($"- **streamClip**: `{AssetDatabase.GetAssetPath(e.streamClip)}` " +
                                  $"({e.streamClip.frequency} Hz, {e.streamClip.channels}ch, {e.streamClip.length:F2}s)");
                else
                    sb.AppendLine($"- **streamClip**: (not assigned)");
            }
            sb.AppendLine($"- **gain (authored)**: {e.gain:F2}");
            float intensity = e.CachedManifestIntensity;
            sb.AppendLine($"- **manifest intensity (cached)**: {(intensity < 0f ? "(not resolved)" : intensity.ToString("F2"))}");
            sb.AppendLine($"- **effective gain**: {e.GetEffectiveGain():F2}");
            sb.AppendLine($"- **target**: {(e.HasTarget ? $"`{e.target}`" : "broadcast (any device)")}");

            // Bindings
            if (e.bindings != null && e.bindings.Count > 0)
            {
                sb.AppendLine($"- **Parameter Bindings ({e.bindings.Count})**:");
                foreach (var b in e.bindings)
                {
                    if (b == null) continue;
                    sb.AppendLine($"  - `{b.outputParameter}` ← " +
                                  $"{b.sourceProperty} (input {b.inputMin}..{b.inputMax}, " +
                                  $"{b.curveType}) → output {b.outputMin}..{b.outputMax}");
                }
            }
            else
            {
                sb.AppendLine($"- **Parameter Bindings**: none");
            }

            if (!string.IsNullOrEmpty(e.notes))
            {
                sb.AppendLine($"- **notes**: {e.notes}");
            }
            sb.AppendLine();
        }
    }
}
