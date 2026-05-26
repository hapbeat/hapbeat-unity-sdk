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
        }

        // ----------------------------------------------------------------
        // BasicExample
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Developer/Sync HapbeatSDK → Samples~ (BasicExample)", false, 1011)]
        public static void SyncBasicExampleToSamples()
        {
            if (!TryResolveSamplesRoot("BasicExample", out string samplesRoot)) return;
            // Note: Kit (manifest.json + WAVs) is authored directly under
            // Samples~/BasicExample/Kit/ — if the SDK_Samples copy doesn't
            // contain a Kit/ subfolder, that destination remains untouched.
            RunDirectorySync("BasicExample",
                sdkSrcRoot: "Assets/HapbeatSDK/SDK_Samples/BasicExample",
                samplesDstRoot: samplesRoot);
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
        /// Recursively mirror every file under <paramref name="sdkSrcRoot"/>
        /// into <paramref name="samplesDstRoot"/>. Includes <c>.meta</c> files
        /// (which carry the asset's GUID, so end users get a stable reference
        /// graph after Sample Import).
        /// <para>
        /// Add-or-overwrite only — files that exist in the destination but
        /// not in the source are LEFT alone. This keeps "permanent"
        /// destination-side content (e.g. Samples~/BasicExample/Kit/) safe
        /// when the source-side scratch area doesn't carry a copy.
        /// </para>
        /// </summary>
        private static void RunDirectorySync(string sampleName, string sdkSrcRoot, string samplesDstRoot)
        {
            if (!Directory.Exists(sdkSrcRoot))
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    $"Source folder not found:\n  {sdkSrcRoot}\n\n" +
                    "Author the sample under that path first (or use " +
                    "`Hapbeat → Developer → Build Basic Example` to scaffold).",
                    "OK");
                return;
            }

            // Confirm with the user.
            if (!EditorUtility.DisplayDialog(
                "Maintainer Sync",
                $"Copy {sampleName} from\n  {sdkSrcRoot}/\n" +
                $"into\n  {samplesDstRoot}/\n\n" +
                "All files including .meta are copied recursively.\n" +
                "Existing files will be overwritten.\n" +
                "Files that exist only in the destination are LEFT untouched " +
                "(safe for permanent destination-side content like Kit/).",
                "Sync", "Cancel"))
                return;

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
                $"Synced {copied} file(s) into Samples~/{sampleName}/.\n" +
                "Review the diff and commit to the repo.",
                "OK");
        }
    }
}
#endif
