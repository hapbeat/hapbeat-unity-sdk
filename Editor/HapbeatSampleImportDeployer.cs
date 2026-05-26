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
            Debug.Log($"[Hapbeat] Deploy Imported Sample: discovered {samples.Count} sample(s) under {kSamplesRoot}/");

            if (samples.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Deploy Imported Sample",
                    $"No imported Hapbeat samples found under {kSamplesRoot}/.\n\n" +
                    "Open Package Manager, select Hapbeat SDK, and import a sample first " +
                    "(e.g. Showcase).",
                    "OK");
                return;
            }

            // Walk samples one by one with a per-sample dialog. Avoids the
            // GenericMenu.ShowAsContext() pitfall — that popup needs an active
            // GUI mouse event to anchor itself and often appears off-screen (or
            // not at all) when fired from a [MenuItem] callback.
            //
            // Buttons:
            //   Deploy → run Deploy(sample) for this one, continue to next
            //   Skip   → skip this sample, continue to next
            //   Cancel → abort the whole loop
            int deployed = 0, skipped = 0;
            foreach (var s in samples)
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    $"Deploy {s.Name}?",
                    BuildSampleSummary(s),
                    "Deploy", "Cancel", "Skip");
                if (choice == 1) break; // Cancel — stop iterating
                if (choice == 2) { skipped++; continue; }
                if (Deploy(s)) deployed++;
            }

            Debug.Log($"[Hapbeat] Deploy Imported Sample done: deployed={deployed}, skipped={skipped}, total={samples.Count}.");
        }

        /// <summary>
        /// Per-sample summary shown in the confirmation dialog. Short enough
        /// to fit the OS modal — the deploy logic runs only if the user
        /// confirms with the Deploy button.
        /// </summary>
        private static string BuildSampleSummary(SampleInfo s)
        {
            string destRoot = $"{HapbeatSDKFolderCreator.kSdkRoot}/SDK_Samples/{s.Name}";
            return
                $"Copy from\n  {s.SourceRoot}/\ninto\n  {destRoot}/\n\n" +
                "Scenes / EventMaps / Animation are copied; Kit/ subfolders go to " +
                $"{HapbeatSDKFolderCreator.kKitsDir}/.\n" +
                "Audio / Scripts / Models stay in the imported folder " +
                "(the EventMap keeps pointing at them — don't delete the imported " +
                "sample folder while the deployed EventMap references those clips).";
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

        /// <summary>
        /// Perform the actual deploy. Returns true if at least one asset was
        /// copied; false if the sample contained no deployable files.
        /// Caller has already confirmed via the top-level
        /// <c>DeployImportedSample</c> menu dialog, so this method does not
        /// show its own pre-deploy confirmation.
        /// </summary>
        private static bool Deploy(SampleInfo s)
        {
            HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
            string destRoot = $"{HapbeatSDKFolderCreator.kSdkRoot}/SDK_Samples/{s.Name}";

            // Enumerate the sample's standard subfolders.
            var scenes  = CollectAssetCopies(s.SourceRoot, destRoot, "Scenes",     "*.unity");
            var maps    = CollectAssetCopies(s.SourceRoot, destRoot, "EventMaps",  "*.asset");
            var anims   = CollectAssetCopies(s.SourceRoot, destRoot, "Animation",  "*.controller");
            string kitSrc = $"{s.SourceRoot}/Kit";
            bool hasKit  = AssetDatabase.IsValidFolder(kitSrc);

            int totalItems = scenes.Count + maps.Count + anims.Count
                           + (hasKit ? AssetDatabase.GetSubFolders(kitSrc).Length : 0);
            if (totalItems == 0)
            {
                EditorUtility.DisplayDialog($"Deploy {s.Name}",
                    $"No deployable assets found under {s.SourceRoot}.\n" +
                    "Expected at least one of Scenes/, EventMaps/, Animation/, Kit/.",
                    "OK");
                return false;
            }

            Debug.Log($"[Hapbeat] Deploying {s.Name}: {scenes.Count} scene(s), " +
                      $"{maps.Count} EventMap(s), {anims.Count} controller(s), " +
                      $"{(hasKit ? AssetDatabase.GetSubFolders(kitSrc).Length : 0)} kit folder(s)");

            // 1. Scene + EventMap + AnimatorController copy with reference rebake.
            var result = HapbeatSampleDeployment.DeployScene(
                scenes:              scenes,
                eventMaps:           maps,
                animatorControllers: anims);

            // 2. Kit subfolders → HapbeatSDK/Kits/<kit-name>/.
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

            Debug.Log($"[Hapbeat] {s.Name} deployed to {destRoot}/" +
                      (kitsCopied > 0 ? $" (+ {kitsCopied} kit folder(s) at {HapbeatSDKFolderCreator.kKitsDir}/)" : ""));

            if (!string.IsNullOrEmpty(result.primaryScenePath))
            {
                var sceneAsset = AssetDatabase.LoadAssetAtPath<Object>(result.primaryScenePath);
                if (sceneAsset != null)
                {
                    Selection.activeObject = sceneAsset;
                    EditorGUIUtility.PingObject(sceneAsset);
                }
            }
            return true;
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
