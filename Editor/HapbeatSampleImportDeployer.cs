#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// End-user-facing menu to copy an imported Hapbeat sample from
    /// <c>Assets/Samples/Hapbeat SDK/&lt;version&gt;/&lt;sample&gt;/</c> into the
    /// user-owned area at <c>Assets/HapbeatSDK/</c>.
    ///
    /// <para>
    /// Without this command, users would have to manually drag-copy Scenes /
    /// EventMaps / AnimatorController / Kit folders into HapbeatSDK/ and
    /// rewire scene-side references — error-prone. This menu automates the
    /// file copy and reference rebake via <see cref="HapbeatSampleDeployment"/>,
    /// plus a folder-level Kit copy to the Studio-conventional kit root.
    /// </para>
    ///
    /// <para>Layout convention this menu enforces:</para>
    /// <list type="bullet">
    ///   <item>Scenes / EventMaps / Animation → <c>HapbeatSDK/SDK_Samples/&lt;sample&gt;/</c></item>
    ///   <item>Kit/&lt;kit-name&gt; → <c>HapbeatSDK/Kits/&lt;kit-name&gt;/</c> (Studio convention)</item>
    ///   <item>Audio / Scripts / Models / etc. are LEFT in the imported folder
    ///         — the EventMap keeps pointing at them. Deleting the sample
    ///         folder would break audio references; keep it imported.</item>
    /// </list>
    /// </summary>
    public static class HapbeatSampleImportDeployer
    {
        private const string kSamplesRoot = "Assets/Samples/Hapbeat SDK";

        // ── Menu entry point ──────────────────────────────────────────────

        [MenuItem("Hapbeat/Deploy Imported Sample", false, 52)]
        public static void DeployImportedSample()
        {
            var samples = DiscoverImportedSamples();
            if (samples.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Deploy Imported Sample",
                    "No imported Hapbeat samples found.\n\n" +
                    "Open Package Manager, select Hapbeat SDK, and import a sample first " +
                    "(e.g. Showcase).",
                    "OK");
                return;
            }

            // Single sample → ask for confirmation directly.
            if (samples.Count == 1)
            {
                Deploy(samples[0]);
                return;
            }

            // Multiple → show a picker.
            var menu = new GenericMenu();
            foreach (var s in samples)
            {
                var captured = s;
                menu.AddItem(new GUIContent($"Deploy {captured.Name}"), false,
                    () => Deploy(captured));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Deploy All"), false, () =>
            {
                foreach (var s in samples) Deploy(s);
            });
            menu.ShowAsContext();
        }

        // ── Discovery ─────────────────────────────────────────────────────

        /// <summary>One imported sample folder ready to deploy.</summary>
        private struct SampleInfo
        {
            public string Name;        // e.g. "Showcase"
            public string SourceRoot;  // e.g. "Assets/Samples/Hapbeat SDK/0.1.0/Showcase"
        }

        /// <summary>
        /// Walk <c>Assets/Samples/Hapbeat SDK/&lt;version&gt;/</c> for sample
        /// folders. Newer versions are preferred when the same sample exists
        /// under multiple version folders (rare; happens when a user keeps
        /// older imports around).
        /// </summary>
        private static List<SampleInfo> DiscoverImportedSamples()
        {
            var result = new List<SampleInfo>();
            if (!AssetDatabase.IsValidFolder(kSamplesRoot)) return result;

            foreach (string versionDir in AssetDatabase.GetSubFolders(kSamplesRoot))
            {
                foreach (string sampleDir in AssetDatabase.GetSubFolders(versionDir))
                {
                    string name = Path.GetFileName(sampleDir);
                    // Deduplicate by sample name; keep the lexicographically
                    // last version (heuristic for "newest").
                    int existing = result.FindIndex(s => s.Name == name);
                    if (existing >= 0)
                    {
                        if (string.CompareOrdinal(versionDir, Path.GetDirectoryName(result[existing].SourceRoot)) > 0)
                            result[existing] = new SampleInfo { Name = name, SourceRoot = sampleDir };
                    }
                    else
                    {
                        result.Add(new SampleInfo { Name = name, SourceRoot = sampleDir });
                    }
                }
            }
            return result;
        }

        // ── Deployment ────────────────────────────────────────────────────

        private static void Deploy(SampleInfo s)
        {
            HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
            string destRoot = $"{HapbeatSDKFolderCreator.kSdkRoot}/SDK_Samples/{s.Name}";

            // Enumerate the sample's standard subfolders.
            var scenes  = CollectAssetCopies(s.SourceRoot, destRoot, "Scenes",     "*.unity");
            var maps    = CollectAssetCopies(s.SourceRoot, destRoot, "EventMaps",  "*.asset");
            var anims   = CollectAssetCopies(s.SourceRoot, destRoot, "Animation",  "*.controller");
            string kitSrc = $"{s.SourceRoot}/Kit";
            bool hasKit  = AssetDatabase.IsValidFolder(kitSrc);

            // Preview to the user.
            var preview = new List<string>();
            foreach (var c in scenes) preview.Add($"  {c.sourcePath} → {c.destPath}");
            foreach (var c in maps)   preview.Add($"  {c.sourcePath} → {c.destPath}");
            foreach (var c in anims)  preview.Add($"  {c.sourcePath} → {c.destPath}");
            if (hasKit)
            {
                foreach (string kitDir in AssetDatabase.GetSubFolders(kitSrc))
                {
                    string kitName = Path.GetFileName(kitDir);
                    preview.Add($"  {kitDir} → {HapbeatSDKFolderCreator.kKitsDir}/{kitName}");
                }
            }

            if (preview.Count == 0)
            {
                EditorUtility.DisplayDialog($"Deploy {s.Name}",
                    $"No deployable assets found under {s.SourceRoot}.\n" +
                    "Expected at least one of Scenes/, EventMaps/, Animation/, Kit/.",
                    "OK");
                return;
            }

            string previewBody = string.Join("\n", preview);
            if (previewBody.Length > 1200) // Truncate for the dialog
                previewBody = previewBody.Substring(0, 1200) + "\n  ...";

            if (!EditorUtility.DisplayDialog(
                $"Deploy {s.Name} to HapbeatSDK",
                $"Copy {s.Name} from\n  {s.SourceRoot}/\n" +
                $"into\n  {destRoot}/ (Scenes / EventMaps / Animation)\n" +
                $"  {HapbeatSDKFolderCreator.kKitsDir}/ (Kit subfolders)\n\n" +
                "Existing destination files are overwritten.\n" +
                "Audio / Scripts / Models stay in the imported folder — the\n" +
                "EventMap will keep pointing at them. Don't delete the\n" +
                "imported sample folder while the EventMap references those clips.\n\n" +
                "Items to copy:\n" + previewBody,
                "Deploy", "Cancel"))
                return;

            // 1. Run the standard scene + EventMap + AnimatorController copy
            //    with reference rebake (HapbeatSampleDeployment does the heavy lifting).
            var result = HapbeatSampleDeployment.DeployScene(
                scenes:              scenes,
                eventMaps:           maps,
                animatorControllers: anims);

            // 2. Copy Kit subfolders to HapbeatSDK/Kits/<kit-name>/.
            int kitsCopied = 0;
            if (hasKit)
            {
                foreach (string kitDir in AssetDatabase.GetSubFolders(kitSrc))
                {
                    string kitName = Path.GetFileName(kitDir);
                    string dstKit = $"{HapbeatSDKFolderCreator.kKitsDir}/{kitName}";
                    HapbeatSampleDeployment.CopyKitFolder(kitDir, dstKit);
                    kitsCopied++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 3. Surface result + open the primary scene if one was deployed.
            EditorUtility.DisplayDialog(
                $"Deploy {s.Name}",
                $"Deployed {s.Name} to {destRoot}/\n" +
                (kitsCopied > 0 ? $"Copied {kitsCopied} Kit folder(s) to {HapbeatSDKFolderCreator.kKitsDir}/.\n" : "") +
                "Scene references have been rewired to the copied EventMap / AnimatorController.",
                "OK");

            if (!string.IsNullOrEmpty(result.primaryScenePath))
            {
                var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(result.primaryScenePath);
                if (sceneAsset != null)
                {
                    Selection.activeObject = sceneAsset;
                    EditorGUIUtility.PingObject(sceneAsset);
                }
            }
        }

        /// <summary>
        /// Build <see cref="HapbeatSampleDeployment.AssetCopy"/> pairs for every
        /// file matching <paramref name="pattern"/> under
        /// <c>&lt;srcRoot&gt;/&lt;subfolder&gt;/</c>. Destinations mirror the same
        /// relative structure under <c>&lt;destRoot&gt;/&lt;subfolder&gt;/</c>.
        /// </summary>
        private static List<HapbeatSampleDeployment.AssetCopy> CollectAssetCopies(
            string srcRoot, string destRoot, string subfolder, string pattern)
        {
            var list = new List<HapbeatSampleDeployment.AssetCopy>();
            string srcDir = $"{srcRoot}/{subfolder}";
            if (!AssetDatabase.IsValidFolder(srcDir)) return list;

            string absSrcDir = Path.Combine(Application.dataPath,
                srcDir.Substring("Assets/".Length)).Replace('\\', '/');
            if (!Directory.Exists(absSrcDir)) return list;

            foreach (var file in Directory.GetFiles(absSrcDir, pattern, SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                list.Add(new HapbeatSampleDeployment.AssetCopy
                {
                    sourcePath = $"{srcDir}/{fileName}",
                    destPath   = $"{destRoot}/{subfolder}/{fileName}",
                });
            }
            return list;
        }
    }
}
#endif
