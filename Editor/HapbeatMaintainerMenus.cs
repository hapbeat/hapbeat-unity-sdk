#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
// Alias to disambiguate from UnityEditor.PackageInfo (legacy AssetStore type).
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEditor.PackageManager;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Maintainer-only menus for SDK developers — sync authored sample assets
    /// (Scenes / EventMaps / AnimatorControllers) from the user-area
    /// (<c>Assets/HapbeatSDK/</c>) back into the package's <c>Samples~/</c>
    /// folder so they can be committed to the repository.
    ///
    /// <para>
    /// Why this exists: Unity's AssetDatabase cannot write into
    /// <c>Samples~/</c> because the tilde suffix marks the folder as
    /// non-Asset. SDK developers therefore scaffold the authored assets
    /// in the regular Assets/ area, verify them in Play mode, and then run
    /// this menu to copy the resulting files (plus their <c>.meta</c>
    /// siblings, preserving GUIDs) into <c>Samples~/</c> via plain
    /// <c>System.IO.File.Copy</c>. The copy works only when the package is
    /// referenced as a local (<c>file:</c>) package — Library/PackageCache
    /// copies fetched from a registry are read-only.
    /// </para>
    ///
    /// <para>
    /// Wipe-then-rebuild semantics: <c>Samples~/&lt;sample&gt;/</c> is treated
    /// as a build artifact, NOT a hand-authored location. The sync deletes
    /// the destination directory in full before copying, so files that
    /// existed only in <c>Samples~/</c> (e.g. orphans from a previous schema)
    /// cannot survive across runs. Author everything under
    /// <c>Assets/HapbeatSDK/SDK_Samples/&lt;sample&gt;/</c> and let sync
    /// be the single source of truth into the package.
    /// </para>
    ///
    /// <para>
    /// Exception — <c>Samples~/VRConfigExample/</c> is authored directly in
    /// the package repo and has no <c>SDK_Samples/</c> counterpart, so it is
    /// deliberately NOT covered by any sync menu here. Do not add one (there
    /// is no source to sync from), and do not extend the wipe to it: a
    /// "sync everything" convenience that deletes <c>Samples~/</c> wholesale
    /// would destroy the only copy. Edit it in the package repo, or import it
    /// into a project, edit, and copy the changed files back by hand.
    /// </para>
    /// </summary>
    public static class HapbeatMaintainerMenus
    {
        // ----------------------------------------------------------------
        // Showcase
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Developer/Sync HapbeatSDK → Samples~ (Showcase)", false, 1010)]
        public static void SyncShowcaseToSamples()
        {
            if (!TryResolveSamplesRoot("Showcase", out string samplesRoot)) return;
            RunDirectorySync("Showcase",
                sdkSrcRoot: "Assets/HapbeatSDK/SDK_Samples/Showcase",
                samplesDstRoot: samplesRoot);
            // Also sync the deployed Kit (HapbeatSDK/Kits/<kit-name>/) into
            // Samples~/Showcase/Kit/<kit-name>/ so the package ships the WAV
            // files the EventMap references. Without this step, fresh users
            // see "missing audio clip" on Sample import because the EventMap
            // GUIDs only resolve in the author's local project.
            SyncKitToSample(samplesRoot, "showcase-kit");
        }

        // ----------------------------------------------------------------
        // BasicExample
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Developer/Sync HapbeatSDK → Samples~ (BasicExample)", false, 1011)]
        public static void SyncBasicExampleToSamples()
        {
            if (!TryResolveSamplesRoot("BasicExample", out string samplesRoot)) return;
            RunDirectorySync("BasicExample",
                sdkSrcRoot: "Assets/HapbeatSDK/SDK_Samples/BasicExample",
                samplesDstRoot: samplesRoot);
            // Same Kit-sync rationale as Showcase (see SyncShowcaseToSamples).
            // If the BasicExample Kit isn't deployed locally this is a no-op.
            SyncKitToSample(samplesRoot, "basic-exam-kit");
        }

        /// <summary>
        /// Copy <c>Assets/HapbeatSDK/Kits/&lt;kitName&gt;/</c> into
        /// <c>&lt;samplesRoot&gt;/Kit/&lt;kitName&gt;/</c>, preserving <c>.meta</c>
        /// files so the WAV GUIDs in the package match what the bundled
        /// EventMap references.
        ///
        /// <para>
        /// Called after <see cref="RunDirectorySync"/> has wiped
        /// <c>Samples~/&lt;sample&gt;/</c>, so the Kit folder is always
        /// rebuilt from scratch. If the kit hasn't been deployed locally,
        /// the sample will ship without bundled WAVs — the warning below
        /// is a strong hint to deploy via Studio first.
        /// </para>
        /// </summary>
        private static void SyncKitToSample(string samplesRoot, string kitName)
        {
            string srcKitAbs = Path.Combine(Application.dataPath,
                $"HapbeatSDK/Kits/{kitName}").Replace('\\', '/');
            if (!Directory.Exists(srcKitAbs))
            {
                Debug.LogWarning($"[Hapbeat] SyncKitToSample skipped: {srcKitAbs} not found. " +
                          "Samples~/" + Path.GetFileName(samplesRoot) + "/Kit/ will ship empty — " +
                          "deploy the kit via Studio first if the EventMap references its WAVs.");
                return;
            }

            string dstKitAbs = Path.Combine(samplesRoot, "Kit", kitName).Replace('\\', '/');
            int copied = CopyDirectoryRecursivePreserveMeta(srcKitAbs, dstKitAbs);
            Debug.Log($"[Hapbeat] Kit '{kitName}' synced into Samples~: {copied} file(s) " +
                      $"copied (incl. .meta) to {dstKitAbs}/");
        }

        /// <summary>
        /// Recursive file copy that DOES include <c>.meta</c> files (unlike
        /// the deploy-direction kit copy, which strips them to force fresh
        /// GUIDs). For the maintainer-sync direction we WANT to preserve
        /// the local .meta GUIDs so the package ships stable references.
        /// </summary>
        private static int CopyDirectoryRecursivePreserveMeta(string srcAbs, string dstAbs)
        {
            int count = 0;
            Directory.CreateDirectory(dstAbs);
            foreach (var file in Directory.GetFiles(srcAbs))
            {
                string name = Path.GetFileName(file);
                if (name == ".DS_Store") continue;
                string dst = Path.Combine(dstAbs, name);
                File.Copy(file, dst, overwrite: true);
                count++;
            }
            foreach (var sub in Directory.GetDirectories(srcAbs))
            {
                string subName = Path.GetFileName(sub);
                count += CopyDirectoryRecursivePreserveMeta(sub, Path.Combine(dstAbs, subName));
            }
            return count;
        }

        // ----------------------------------------------------------------
        // Common
        // ----------------------------------------------------------------

        /// <summary>
        /// Resolve the filesystem path of the package's
        /// <c>Samples~/&lt;sampleName&gt;/</c> directory. Returns false (with
        /// a user dialog) if the package isn't installed as a local /
        /// embedded source — i.e. it's a read-only Library/PackageCache copy.
        /// </summary>
        private static bool TryResolveSamplesRoot(string sampleName, out string samplesRoot)
        {
            samplesRoot = null;

            // The Hapbeat runtime assembly belongs to com.hapbeat.sdk.
            var asm = typeof(HapbeatBridge).Assembly;
            var pkg = UpmPackageInfo.FindForAssembly(asm);
            if (pkg == null)
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    "Could not resolve the Hapbeat SDK package.\n" +
                    "Check that the project references the SDK via UPM.",
                    "OK");
                return false;
            }
            if (pkg.source != PackageSource.Local && pkg.source != PackageSource.Embedded)
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    "This menu only works when the SDK is referenced as a\n" +
                    "local (`file:`) package.\n\n" +
                    $"Current source: {pkg.source}\n" +
                    $"resolvedPath:  {pkg.resolvedPath}\n\n" +
                    "Read-only copies under Library/PackageCache can't be written to.",
                    "OK");
                return false;
            }

            samplesRoot = Path.Combine(pkg.resolvedPath, "Samples~", sampleName).Replace('\\', '/');
            return true;
        }

        /// <summary>
        /// Wipe <paramref name="samplesDstRoot"/> in full, then recursively
        /// mirror every file under <paramref name="sdkSrcRoot"/> into it.
        /// Includes <c>.meta</c> files (which carry the asset's GUID, so end
        /// users get a stable reference graph after Sample Import).
        /// <para>
        /// Wipe-then-rebuild is intentional: <c>Samples~/</c> is treated as
        /// a build artifact, not a hand-authored location. Anything that
        /// only exists in the destination is by definition stale (orphan
        /// from a previous schema, leftover after a rename, etc.) and must
        /// not survive a sync. The Kit subfolder is repopulated by the
        /// caller via <see cref="SyncKitToSample"/> after the wipe.
        /// </para>
        /// </summary>
        private static void RunDirectorySync(string sampleName, string sdkSrcRoot, string samplesDstRoot)
        {
            if (!Directory.Exists(sdkSrcRoot))
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    $"Source folder not found:\n  {sdkSrcRoot}\n\n" +
                    "Author the sample under that path first.",
                    "OK");
                return;
            }

            // Confirm with the user. The destination wipe is the load-bearing
            // bit, so it gets top billing in the prompt.
            if (!EditorUtility.DisplayDialog(
                "Maintainer Sync",
                $"Wipe & rebuild {sampleName} from\n  {sdkSrcRoot}/\n" +
                $"into\n  {samplesDstRoot}/\n\n" +
                "The destination directory will be DELETED first, then\n" +
                "recreated from the source (including .meta files).\n\n" +
                "Samples~/ is a build artifact — never edit it directly; the\n" +
                "next sync would wipe your changes.",
                "Wipe & Sync", "Cancel"))
                return;

            // Wipe destination so dest-only stale files (orphans, renames,
            // dropped schemas) cannot survive across syncs.
            if (Directory.Exists(samplesDstRoot))
            {
                Directory.Delete(samplesDstRoot, recursive: true);
            }

            int copied = 0;
            string srcRootNorm = sdkSrcRoot.Replace('\\', '/').TrimEnd('/');
            foreach (var srcFile in Directory.GetFiles(srcRootNorm, "*", SearchOption.AllDirectories))
            {
                string srcNorm = srcFile.Replace('\\', '/');
                string rel = srcNorm.Substring(srcRootNorm.Length).TrimStart('/');
                string dstFile = $"{samplesDstRoot.TrimEnd('/')}/{rel}";
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile));
                File.Copy(srcFile, dstFile, overwrite: true);
                copied++;
            }

            EditorUtility.DisplayDialog("Maintainer Sync",
                $"Wiped & rebuilt Samples~/{sampleName}/ ({copied} file(s) copied).\n" +
                "Kit/ is repopulated separately — check the console for the Kit sync log.\n" +
                "Review the diff and commit to the repo.",
                "OK");
        }
    }
}
#endif
