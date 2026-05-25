#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Creates the HapbeatKits root folder with a <see cref="HapbeatKitsReadme"/>
    /// marker asset inside. The marker both (a) tracks the folder so users can
    /// move/rename it freely and (b) renders a Unity-native onboarding screen
    /// in the Inspector (cf. URP / Starter Assets).
    ///
    /// First-time flow:
    ///   1. SDK is imported via UPM or .unitypackage.
    ///   2. User runs <c>Hapbeat &gt; Setup &gt; Create HapbeatSDK Folder</c>
    ///      (or triggers the "Create it now?" dialog from EventMap "Reveal").
    ///   3. User reads the Readme (Inspector), clicks "Open Hapbeat Studio".
    ///   4. User points Studio at this folder; Studio auto-scaffolds kits.
    ///   5. Unity AssetDatabase picks up the new files.
    /// </summary>
    internal static class HapbeatKitsFolderCreator
    {
        private const string kReadmeName = "HapbeatKitsReadme.asset";

        // Note: this class no longer exposes a top-level menu item. Folder /
        // marker creation is invoked by HapbeatSDKFolderCreator (or directly
        // from Build Samples flows) via EnsureFolderAndReadme(false). The
        // previous "Hapbeat / Setup / Create HapbeatKits Folder" and
        // "Reset HapbeatKits Readme" menu entries were removed in favour of a
        // single unified "Create HapbeatSDK Folder" entry.

        /// <summary>
        /// Create the HapbeatKits folder (at the existing marker location if one
        /// exists, otherwise at <see cref="HapbeatKitsReadme.DefaultKitsRootPath"/>)
        /// and a <see cref="HapbeatKitsReadme"/> marker asset inside it.
        /// Idempotent — safe to call repeatedly.
        /// </summary>
        /// <param name="openReadme">If true, select and ping the readme in the
        /// Inspector after creation (welcome-screen UX). Pass false when the
        /// caller shows its own follow-up UI.</param>
        /// <returns>True if the folder + marker exist after the call.</returns>
        internal static bool EnsureFolderAndReadme(bool openReadme)
        {
            // If a marker already exists anywhere in the project, don't create a
            // duplicate — just surface that one.
            string existingRoot = HapbeatKitsReadme.FindKitsRootPath();
            string targetFolder = existingRoot ?? HapbeatKitsReadme.DefaultKitsRootPath;

            // Create the folder on disk if needed (handles nested paths like Assets/Content/Kits)
            string folderAbsPath = Path.GetFullPath(targetFolder);
            if (!Directory.Exists(folderAbsPath))
            {
                Directory.CreateDirectory(folderAbsPath);
                AssetDatabase.Refresh();
            }

            string readmeAssetPath = $"{targetFolder}/{kReadmeName}";
            var readme = AssetDatabase.LoadAssetAtPath<HapbeatKitsReadme>(readmeAssetPath);
            if (readme == null)
            {
                readme = ScriptableObject.CreateInstance<HapbeatKitsReadme>();
                AssetDatabase.CreateAsset(readme, readmeAssetPath);
                AssetDatabase.SaveAssets();
            }

            if (openReadme && readme != null)
            {
                Selection.activeObject = readme;
                EditorGUIUtility.PingObject(readme);
            }

            Debug.Log($"[Hapbeat] Ready at {targetFolder}/ (marker: {kReadmeName}).");
            return AssetDatabase.IsValidFolder(targetFolder)
                   && AssetDatabase.LoadAssetAtPath<HapbeatKitsReadme>(readmeAssetPath) != null;
        }
    }
}
#endif
