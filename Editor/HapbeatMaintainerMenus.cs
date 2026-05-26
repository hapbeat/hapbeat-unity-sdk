#if UNITY_EDITOR
using System.Collections.Generic;
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

            const string sdkRoot = "Assets/HapbeatSDK/SDK_Samples/Showcase";
            var pairs = new List<(string src, string dst)>
            {
                ($"{sdkRoot}/Scenes/Showcase.unity",
                 $"{samplesRoot}/Scenes/Showcase.unity"),
                ($"{sdkRoot}/Scenes/Showcase_Plain.unity",
                 $"{samplesRoot}/Scenes/Showcase_Plain.unity"),
                ($"{sdkRoot}/EventMaps/ShowcaseEventMap.asset",
                 $"{samplesRoot}/EventMaps/ShowcaseEventMap.asset"),
                ($"{sdkRoot}/Animation/DoorAnimator.controller",
                 $"{samplesRoot}/Animation/DoorAnimator.controller"),
            };
            RunSync("Showcase", pairs);
        }

        // ----------------------------------------------------------------
        // BasicExample
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Developer/Sync HapbeatSDK → Samples~ (BasicExample)", false, 1011)]
        public static void SyncBasicExampleToSamples()
        {
            if (!TryResolveSamplesRoot("BasicExample", out string samplesRoot)) return;

            const string sdkRoot = "Assets/HapbeatSDK/SDK_Samples/BasicExample";
            var pairs = new List<(string src, string dst)>
            {
                ($"{sdkRoot}/Scenes/BasicExample.unity",
                 $"{samplesRoot}/Scenes/BasicExample.unity"),
                ($"{sdkRoot}/EventMaps/BasicExampleEventMap.asset",
                 $"{samplesRoot}/EventMaps/BasicExampleEventMap.asset"),
                // Note: Kit (manifest.json + WAVs) is NOT synced — it lives
                // permanently in Samples~/BasicExample/Kit/ and is authored
                // there directly. Deploy mode copies it to HapbeatSDK/Kits/.
            };
            RunSync("BasicExample", pairs);
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

        private static void RunSync(string sampleName, List<(string src, string dst)> pairs)
        {
            // Validate sources first so we don't half-copy.
            var missing = new List<string>();
            foreach (var p in pairs)
            {
                if (!File.Exists(p.src))
                    missing.Add(p.src);
            }
            if (missing.Count > 0)
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    $"Sync source files for {sampleName} are missing:\n\n  " +
                    string.Join("\n  ", missing) + "\n\n" +
                    "Run `Hapbeat → Developer → Build Basic Example` first to scaffold them.",
                    "OK");
                return;
            }

            // Build the user-facing preview.
            var lines = new List<string>();
            foreach (var p in pairs)
                lines.Add($"  {p.src}\n    → {p.dst}");
            if (!EditorUtility.DisplayDialog(
                "Maintainer Sync",
                $"Copy authored {sampleName} assets into Samples~.\n" +
                "(.meta files are copied too, preserving GUIDs.)\n\n" +
                string.Join("\n", lines) + "\n\n" +
                "Existing files will be overwritten.",
                "Sync", "Cancel"))
                return;

            int copied = 0;
            foreach (var p in pairs)
            {
                CopyWithMeta(p.src, p.dst);
                copied++;
            }

            EditorUtility.DisplayDialog("Maintainer Sync",
                $"Synced {copied} asset(s) into Samples~/{sampleName}/.\n" +
                "Review the diff and commit to the repo.",
                "OK");
        }

        /// <summary>
        /// File.Copy <paramref name="src"/> → <paramref name="dst"/>, then copy
        /// <paramref name="src"/>.meta → <paramref name="dst"/>.meta if it
        /// exists. The .meta carries the asset's GUID so end users get a
        /// stable reference graph after Sample Import.
        /// </summary>
        private static void CopyWithMeta(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);

            string srcMeta = src + ".meta";
            string dstMeta = dst + ".meta";
            if (File.Exists(srcMeta))
                File.Copy(srcMeta, dstMeta, overwrite: true);
        }
    }
}
#endif
