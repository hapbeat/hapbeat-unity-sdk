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
    internal static class HapbeatSDKFolderCreator
    {
        private const string kMenu = "Hapbeat/Setup/Create HapbeatSDK Folder";
        public const string kSdkRoot = "Assets/HapbeatSDK";
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
            EnsureFolder(kScenesDir);
            EnsureFolder(kEventMapsDir);

            // Defer Kits creation (and marker placement) to the Kits-specific
            // creator so we don't duplicate marker logic. It honours an
            // existing marker location if the user moved it.
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
