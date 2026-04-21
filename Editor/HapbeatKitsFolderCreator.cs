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
    ///   2. User runs <c>Hapbeat &gt; Setup &gt; Create HapbeatKits Folder</c>
    ///      (or triggers the "Create it now?" dialog from EventMap "Reveal").
    ///   3. User reads the Readme (Inspector), clicks "Open Hapbeat Studio".
    ///   4. User points Studio at this folder; Studio auto-scaffolds kits.
    ///   5. Unity AssetDatabase picks up the new files.
    /// </summary>
    internal static class HapbeatKitsFolderCreator
    {
        private const string kMenu = "Hapbeat/Setup/Create HapbeatKits Folder";
        private const string kResetMenu = "Hapbeat/Setup/Reset HapbeatKits Readme";
        private const string kReadmeName = "HapbeatKitsReadme.asset";
        private const string kLegacyReadmeName = "README.md";

        [MenuItem(kMenu, false, 100)]
        private static void CreateFolderMenu()
        {
            EnsureFolderAndReadme(openReadme: true);
        }

        [MenuItem(kResetMenu, false, 101)]
        private static void ResetReadmeMenu()
        {
            string folder = HapbeatKitsReadme.FindKitsRootPath()
                            ?? HapbeatKitsReadme.DefaultKitsRootPath;
            string readmeAssetPath = $"{folder}/{kReadmeName}";
            var existing = AssetDatabase.LoadAssetAtPath<HapbeatKitsReadme>(readmeAssetPath);
            if (existing == null)
            {
                EnsureFolderAndReadme(openReadme: true);
                return;
            }
            // Nothing to reset in the serialized form (content is in the Editor),
            // so just bump templateVersion and ping so user re-sees the inspector.
            existing.templateVersion = "1";
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
            Selection.activeObject = existing;
            EditorGUIUtility.PingObject(existing);
        }

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

            // Migrate away from the old Markdown readme: leave the file on disk
            // (the user might have edited it), but log a hint so they can clean up.
            string legacyPath = $"{targetFolder}/{kLegacyReadmeName}";
            if (File.Exists(Path.GetFullPath(legacyPath)))
            {
                Debug.Log(
                    $"[Hapbeat] 旧 {legacyPath} が残っています。" +
                    $"新しい {kReadmeName} に置き換え済みなので、不要なら削除してかまいません。");
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
