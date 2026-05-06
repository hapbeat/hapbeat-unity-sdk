#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Creates the user-owned <c>Assets/HapbeatSDK/</c> directory skeleton:
    /// <list type="bullet">
    ///   <item><c>Assets/HapbeatSDK/Kits/</c>     — Kit (manifest.json + WAV). Marker asset
    ///        (HapbeatKitsReadme) is placed inside via <see cref="HapbeatKitsFolderCreator"/>.</item>
    ///   <item><c>Assets/HapbeatSDK/Scenes/</c>   — Generated sample scenes.</item>
    ///   <item><c>Assets/HapbeatSDK/EventMaps/</c> — Generated EventMap assets.</item>
    /// </list>
    ///
    /// Idempotent. Build Samples menu items invoke <see cref="EnsureLayout"/> on
    /// the way in so users don't have to run this manually, but exposing it as
    /// a stand-alone menu lets users prepare the layout when starting from
    /// Hapbeat Studio (kit-first workflow) without first building a sample.
    /// </summary>
    public static class HapbeatSDKFolderCreator
    {
        private const string kMenu = "Hapbeat/Setup/Create HapbeatSDK Folder";
        public const string kSdkRoot = "Assets/HapbeatSDK";
        public const string kKitsDir = kSdkRoot + "/Kits";
        public const string kScenesDir = kSdkRoot + "/Scenes";
        public const string kEventMapsDir = kSdkRoot + "/EventMaps";

        [MenuItem(kMenu, false, 90)]
        private static void CreateMenu()
        {
            EnsureLayout(verbose: true);
        }

        /// <summary>
        /// Ensure all four folders exist. Returns the resolved Kits root (may
        /// differ from <c>HapbeatSDK/Kits</c> if a marker already lives elsewhere).
        /// </summary>
        public static string EnsureLayout(bool verbose = false)
        {
            EnsureFolder(kSdkRoot);
            EnsureFolder(kKitsDir);
            EnsureFolder(kScenesDir);
            EnsureFolder(kEventMapsDir);

            // Migrate the marker from the legacy Assets/HapbeatKits/ location
            // if it exists. The marker is moved (preserves its GUID) so that
            // FindKitsRootPath() now resolves to HapbeatSDK/Kits/ and Build
            // Samples writes Kits there instead of the legacy folder.
            MigrateLegacyMarker();

            // Place the marker asset in HapbeatSDK/Kits/ if no marker exists.
            HapbeatKitsFolderCreator.EnsureFolderAndReadme(openReadme: false);
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath()
                              ?? HapbeatKitsReadme.DefaultKitsRootPath;

            if (verbose)
            {
                Debug.Log(
                    $"[Hapbeat] HapbeatSDK layout ready:\n" +
                    $"  Kits      : {kitsRoot}/\n" +
                    $"  Scenes    : {kScenesDir}/\n" +
                    $"  EventMaps : {kEventMapsDir}/");

                EditorUtility.DisplayDialog(
                    "HapbeatSDK Folder",
                    "ユーザー領域のフォルダを準備しました:\n\n" +
                    $"  Kits      : {kitsRoot}/\n" +
                    $"  Scenes    : {kScenesDir}/\n" +
                    $"  EventMaps : {kEventMapsDir}/\n\n" +
                    "次に Hapbeat / Build Samples / 1. Basic Example などを実行すると、\n" +
                    "このフォルダに Kit / EventMap / Scene が生成されます。",
                    "OK");
            }

            return kitsRoot;
        }

        // ----------------------------------------------------------------
        // Legacy marker migration (Assets/HapbeatKits → Assets/HapbeatSDK/Kits)
        // ----------------------------------------------------------------

        private const string kLegacyKitsRoot = "Assets/HapbeatKits";
        private const string kLegacyMarker = kLegacyKitsRoot + "/HapbeatKitsReadme.asset";
        private const string kNewMarker = kKitsDir + "/HapbeatKitsReadme.asset";

        private static void MigrateLegacyMarker()
        {
            if (!AssetDatabase.IsValidFolder(kLegacyKitsRoot)) return;
            var legacyAsset = AssetDatabase.LoadAssetAtPath<HapbeatKitsReadme>(kLegacyMarker);
            if (legacyAsset == null) return;

            // Move the marker (preserves its GUID).
            string err = AssetDatabase.MoveAsset(kLegacyMarker, kNewMarker);
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogWarning(
                    $"[Hapbeat] Failed to migrate marker from {kLegacyMarker} → {kNewMarker}: {err}\n" +
                    "Move HapbeatKitsReadme.asset manually if needed.");
                return;
            }
            Debug.Log($"[Hapbeat] Marker moved: {kLegacyMarker} → {kNewMarker}");

            // Inspect what's left in the legacy folder. If it's now empty (only
            // a stale .meta), delete it. Otherwise leave it for the user to
            // migrate kits manually.
            string legacyAbs = Path.Combine(Application.dataPath,
                kLegacyKitsRoot.Substring("Assets/".Length));
            if (!Directory.Exists(legacyAbs)) return;

            var leftoverFiles = Directory.GetFiles(legacyAbs);
            var leftoverDirs = Directory.GetDirectories(legacyAbs);
            if (leftoverFiles.Length == 0 && leftoverDirs.Length == 0)
            {
                AssetDatabase.DeleteAsset(kLegacyKitsRoot);
                Debug.Log($"[Hapbeat] Legacy folder {kLegacyKitsRoot}/ was empty and has been removed.");
            }
            else
            {
                Debug.LogWarning(
                    $"[Hapbeat] Legacy folder {kLegacyKitsRoot}/ still contains files. " +
                    $"Move any Kit subfolders into {kKitsDir}/ manually, then delete {kLegacyKitsRoot}/." +
                    $"\nRemaining items: {leftoverFiles.Length} file(s), {leftoverDirs.Length} dir(s).");
            }
        }

        private static void EnsureFolder(string assetPath)
        {
            string abs = Path.Combine(Application.dataPath,
                assetPath.Substring("Assets/".Length)).Replace('\\', '/');
            if (!Directory.Exists(abs))
                Directory.CreateDirectory(abs);
            AssetDatabase.Refresh();
        }
    }
}
#endif
