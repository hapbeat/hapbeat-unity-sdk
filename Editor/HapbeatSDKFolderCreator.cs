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
        // Top-level (flat) menu — moved out of legacy "Hapbeat/Setup/" submenu in 2026-05-26.
        private const string kMenu = "Hapbeat/Create HapbeatSDK Folder";
        public const string kSdkRoot = "Assets/HapbeatSDK";
        public const string kKitsDir = kSdkRoot + "/Kits";
        public const string kScenesDir = kSdkRoot + "/Scenes";
        public const string kEventMapsDir = kSdkRoot + "/EventMaps";

        [MenuItem(kMenu, false, 33)]
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
                    "User-area folders are ready:\n\n" +
                    $"  Kits      : {kitsRoot}/\n" +
                    $"  Scenes    : {kScenesDir}/\n" +
                    $"  EventMaps : {kEventMapsDir}/\n\n" +
                    "Next: run `Hapbeat → Initial Scene Setup` to create an Event Router + EventMap,\n" +
                    "or deploy a Kit from Hapbeat Studio and it will land in this folder.",
                    "OK");
            }

            return kitsRoot;
        }

        private static void EnsureFolder(string assetPath)
        {
            string abs = Path.Combine(Application.dataPath,
                assetPath.Substring("Assets/".Length)).Replace('\\', '/');
            if (!Directory.Exists(abs))
            {
                Directory.CreateDirectory(abs);
                AssetDatabase.Refresh();
            }
        }
    }
}
#endif
