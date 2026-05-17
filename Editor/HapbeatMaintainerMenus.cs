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
        // Tutorial
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Maintainers/Sync HapbeatSDK → Samples~ (Tutorial)", false, 201)]
        public static void SyncTutorialToSamples()
        {
            if (!TryResolveSamplesRoot("Tutorial", out string samplesRoot)) return;

            const string sdkRoot = "Assets/HapbeatSDK/SDK_Samples/Tutorial";
            var pairs = new List<(string src, string dst)>
            {
                ($"{sdkRoot}/Scenes/Tutorial.unity",
                 $"{samplesRoot}/Scenes/Tutorial.unity"),
                ($"{sdkRoot}/Scenes/Tutorial_Plain.unity",
                 $"{samplesRoot}/Scenes/Tutorial_Plain.unity"),
                ($"{sdkRoot}/EventMaps/TutorialEventMap.asset",
                 $"{samplesRoot}/EventMaps/TutorialEventMap.asset"),
                ($"{sdkRoot}/Animation/DoorAnimator.controller",
                 $"{samplesRoot}/Animation/DoorAnimator.controller"),
            };
            RunSync("Tutorial", pairs);
        }

        // ----------------------------------------------------------------
        // BasicExample
        // ----------------------------------------------------------------

        [MenuItem("Hapbeat/Maintainers/Sync HapbeatSDK → Samples~ (BasicExample)", false, 200)]
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
                    "Hapbeat SDK パッケージ情報を解決できませんでした。\n" +
                    "プロジェクトが SDK を UPM 経由で参照していることを確認してください。",
                    "OK");
                return false;
            }
            if (pkg.source != PackageSource.Local && pkg.source != PackageSource.Embedded)
            {
                EditorUtility.DisplayDialog("Maintainer Sync",
                    "このメニューは SDK を `file:` の local パッケージとして\n" +
                    "参照している場合にのみ使用できます。\n\n" +
                    $"現在の source: {pkg.source}\n" +
                    $"resolvedPath : {pkg.resolvedPath}\n\n" +
                    "Library/PackageCache 配下の read-only コピーには書き戻せません。",
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
                    $"{sampleName} の sync 元ファイルが見つかりません:\n\n  " +
                    string.Join("\n  ", missing) + "\n\n" +
                    "`Hapbeat → Build Samples` で先に scaffold を実行してください。",
                    "OK");
                return;
            }

            // Build the user-facing preview.
            var lines = new List<string>();
            foreach (var p in pairs)
                lines.Add($"  {p.src}\n    → {p.dst}");
            if (!EditorUtility.DisplayDialog(
                "Maintainer Sync",
                $"{sampleName} 用 authored 資産を Samples~ にコピーします。\n" +
                "(.meta も同時にコピーして GUID を保全します)\n\n" +
                string.Join("\n", lines) + "\n\n" +
                "既存のファイルは上書きされます。",
                "同期する", "キャンセル"))
                return;

            int copied = 0;
            foreach (var p in pairs)
            {
                CopyWithMeta(p.src, p.dst);
                copied++;
            }

            EditorUtility.DisplayDialog("Maintainer Sync",
                $"{copied} 個の asset を Samples~/{sampleName}/ に同期しました。\n" +
                "差分を確認したら repo に commit してください。",
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
