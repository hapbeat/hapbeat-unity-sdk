#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Shared helpers for deploying authored sample assets (Scene + EventMap +
    /// AnimatorController etc.) from the imported sample folder
    /// (<c>Assets/Samples/Hapbeat SDK/&lt;version&gt;/&lt;sample&gt;/</c>) into the
    /// user-editable area (<c>Assets/HapbeatSDK/{Scenes,EventMaps,Animation}/</c>).
    ///
    /// <para>Workflow:</para>
    /// <list type="bullet">
    ///   <item>Sample build menus call <see cref="DeployScene"/> with a description
    ///         of the assets to copy and their source/destination paths.</item>
    ///   <item>Each asset is copied via <c>AssetDatabase.CopyAsset</c>, which
    ///         assigns a fresh GUID to the destination.</item>
    ///   <item>The destination scene is then opened and walked once, swapping
    ///         every reference to the source-side EventMap (and optional
    ///         AnimatorController) to the freshly copied versions, so the
    ///         HapbeatSDK copy is self-contained and survives even if the user
    ///         later deletes the imported sample folder.</item>
    /// </list>
    /// <para>AudioClips that the EventMap references are intentionally NOT
    /// copied — they stay in the imported sample folder. The user needs to
    /// keep the sample imported for the WAVs to resolve.</para>
    /// </summary>
    public static class HapbeatSampleDeployment
    {
        /// <summary>One row in <see cref="DeployScene"/>'s asset list.</summary>
        public struct AssetCopy
        {
            public string sourcePath;
            public string destPath;
        }

        /// <summary>
        /// Copy a set of assets (Scene, EventMap, AnimatorController, …) from
        /// the imported sample folder to <c>Assets/HapbeatSDK/</c>, then open
        /// each copied scene and rebake its inter-asset references so the
        /// HapbeatSDK copy points at the HapbeatSDK-side EventMap /
        /// AnimatorController rather than the sample-side originals.
        /// </summary>
        /// <param name="scenes">Scene files to copy (and rebake).</param>
        /// <param name="eventMaps">EventMap assets to copy. References inside the
        /// copied scenes will be remapped from the source map → copied map.</param>
        /// <param name="animatorControllers">Optional AnimatorController assets
        /// to copy + remap.</param>
        /// <returns>Deployment result with the primary copied scene path and
        /// references to the copied EventMaps (for any extra sample-specific
        /// rebakes such as relinking AudioClip references to new Kit WAVs).</returns>
        public static DeployResult DeployScene(
            IReadOnlyList<AssetCopy> scenes,
            IReadOnlyList<AssetCopy> eventMaps,
            IReadOnlyList<AssetCopy> animatorControllers = null)
        {
            // 1. Copy auxiliary assets (EventMap, AnimatorController) first so
            //    their destination GUIDs are stable before we rebake.
            var mapRemap = new Dictionary<string, HapbeatEventMap>();
            foreach (var c in eventMaps)
            {
                if (!CopyAssetOverwrite(c.sourcePath, c.destPath)) continue;
                var dst = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(c.destPath);
                if (dst != null) mapRemap[c.sourcePath] = dst;
            }

            var acRemap = new Dictionary<string, RuntimeAnimatorController>();
            if (animatorControllers != null)
            {
                foreach (var c in animatorControllers)
                {
                    if (!CopyAssetOverwrite(c.sourcePath, c.destPath)) continue;
                    var dst = AssetDatabase.LoadAssetAtPath<AnimatorController>(c.destPath);
                    if (dst != null) acRemap[c.sourcePath] = dst;
                }
            }

            // 2. Copy each scene and rebake its references in-place.
            string primaryScenePath = null;
            for (int i = 0; i < scenes.Count; i++)
            {
                var c = scenes[i];
                if (!CopyAssetOverwrite(c.sourcePath, c.destPath)) continue;
                if (i == 0) primaryScenePath = c.destPath;
                RebakeSceneReferences(c.destPath, mapRemap, acRemap);
            }

            AssetDatabase.SaveAssets();
            return new DeployResult
            {
                primaryScenePath = primaryScenePath,
                copiedEventMaps  = mapRemap,
            };
        }

        /// <summary>Outcome of <see cref="DeployScene"/>.</summary>
        public struct DeployResult
        {
            /// <summary>Path of the first scene that was successfully copied
            /// (so the caller can reopen it after deployment), or null.</summary>
            public string primaryScenePath;
            /// <summary>Map from source EventMap path → copied EventMap asset
            /// in the destination. Callers can use this to perform extra
            /// rebakes (e.g. relinking AudioClip refs after a Kit copy).</summary>
            public Dictionary<string, HapbeatEventMap> copiedEventMaps;
        }

        // ----------------------------------------------------------------
        // Internals
        // ----------------------------------------------------------------

        private static bool CopyAssetOverwrite(string srcPath, string dstPath)
        {
            if (AssetDatabase.LoadAssetAtPath<Object>(srcPath) == null)
            {
                Debug.LogWarning($"[Hapbeat] Sample asset missing: {srcPath}");
                return false;
            }
            // Delete any prior copy so AssetDatabase.CopyAsset can proceed
            // without rename collisions. The destination's GUID will be fresh.
            if (AssetDatabase.LoadAssetAtPath<Object>(dstPath) != null)
                AssetDatabase.DeleteAsset(dstPath);

            if (!AssetDatabase.CopyAsset(srcPath, dstPath))
            {
                Debug.LogError($"[Hapbeat] CopyAsset failed: {srcPath} → {dstPath}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Open <paramref name="scenePath"/> and walk every Hapbeat component,
        /// remapping references to the source-side EventMap /
        /// AnimatorController to the copied versions.
        /// </summary>
        private static void RebakeSceneReferences(
            string scenePath,
            Dictionary<string, HapbeatEventMap> mapRemap,
            Dictionary<string, RuntimeAnimatorController> acRemap)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // Bridges (HapbeatBridge subclasses).
            foreach (var bridge in Object.FindObjectsByType<HapbeatBridge>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var newMap = ResolveRemap(bridge.EventMap, mapRemap);
                if (newMap != null && newMap != bridge.EventMap)
                {
                    bridge.EditorSetupEventMap(newMap);
                    EditorUtility.SetDirty(bridge);
                }
            }

            // HapbeatTriggerBase + subclasses (includes Collision / Animator /
            // Sequence / TickEmitter / UnityEvent / etc.).
            foreach (var trig in Object.FindObjectsByType<HapbeatTriggerBase>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var newMap = ResolveRemap(trig.EventMap, mapRemap);
                if (newMap != null && newMap != trig.EventMap)
                {
                    trig.EditorSetupEntry(newMap, trig.EntryId, trig.EntryIndex);
                    EditorUtility.SetDirty(trig);
                }
                // Sequence trigger has secondary entry refs; the entry IDs
                // are preserved across the EventMap copy (entries are part
                // of the copied asset content), so we only need to swap the
                // EventMap reference — which EditorSetupEntry above already
                // handled on the base class. No extra work required.
            }

            // HapbeatParameterBinding: independent reference to an EventMap +
            // a binding preset id.
            foreach (var b in Object.FindObjectsByType<HapbeatParameterBinding>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var newMap = ResolveRemap(b.LinkedEventMap, mapRemap);
                if (newMap != null && newMap != b.LinkedEventMap)
                {
                    var so = new SerializedObject(b);
                    so.FindProperty("_linkedEventMap").objectReferenceValue = newMap;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(b);
                }
            }

            // Animator runtimeAnimatorController swaps. We can't easily look up
            // the source AC path from a live Animator, so we walk every Animator
            // and check whether its current controller is one of the source AC
            // paths in acRemap.
            if (acRemap != null && acRemap.Count > 0)
            {
                foreach (var anim in Object.FindObjectsByType<Animator>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    var current = anim.runtimeAnimatorController;
                    if (current == null) continue;
                    string currentPath = AssetDatabase.GetAssetPath(current);
                    if (string.IsNullOrEmpty(currentPath)) continue;
                    if (acRemap.TryGetValue(currentPath, out var copied))
                    {
                        anim.runtimeAnimatorController = copied;
                        EditorUtility.SetDirty(anim);
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, scenePath);
        }

        /// <summary>
        /// Given the EventMap the scene component currently references, return
        /// the copied EventMap that should replace it. Looks up by the source
        /// EventMap's asset path. Returns null if no remap is needed.
        /// </summary>
        private static HapbeatEventMap ResolveRemap(
            HapbeatEventMap current, Dictionary<string, HapbeatEventMap> mapRemap)
        {
            if (current == null || mapRemap == null) return null;
            string path = AssetDatabase.GetAssetPath(current);
            if (string.IsNullOrEmpty(path)) return null;
            return mapRemap.TryGetValue(path, out var copied) ? copied : null;
        }

        // ----------------------------------------------------------------
        // Move (GUID-preserving) — used by sample build flows that
        // RELOCATE imported sample assets into the user-editable area
        // instead of copying them. Because GUIDs are preserved, all scene
        // references (MonoScript / AudioClip / EventMap / AnimatorController)
        // stay valid without any rebake.
        // ----------------------------------------------------------------

        /// <summary>
        /// Move an asset or folder via <see cref="AssetDatabase.MoveAsset"/>.
        /// Preserves the source GUID. If the destination already exists it
        /// is deleted first so re-runs do not abort.
        /// </summary>
        public static bool MoveAssetForce(string srcAssetPath, string dstAssetPath)
        {
            bool srcExists = AssetDatabase.IsValidFolder(srcAssetPath)
                          || AssetDatabase.LoadAssetAtPath<Object>(srcAssetPath) != null;
            if (!srcExists)
            {
                Debug.LogWarning($"[Hapbeat] MoveAsset: source not found: {srcAssetPath}");
                return false;
            }

            string dstParent = System.IO.Path.GetDirectoryName(dstAssetPath).Replace('\\', '/');
            if (!string.IsNullOrEmpty(dstParent))
                EnsureAssetFolder(dstParent);

            if (AssetDatabase.IsValidFolder(dstAssetPath)
                || AssetDatabase.LoadAssetAtPath<Object>(dstAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(dstAssetPath);
            }

            string err = AssetDatabase.MoveAsset(srcAssetPath, dstAssetPath);
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogError($"[Hapbeat] MoveAsset failed: {srcAssetPath} → {dstAssetPath}: {err}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Delete <paramref name="folderAssetPath"/> if it is an empty folder.
        /// Used to tidy up the imported sample folder after a Move-based
        /// deploy leaves it empty.
        /// </summary>
        public static void DeleteFolderIfEmpty(string folderAssetPath)
        {
            if (!AssetDatabase.IsValidFolder(folderAssetPath)) return;
            string abs = ToAbsolute(folderAssetPath);
            if (System.IO.Directory.GetFiles(abs).Length > 0) return;
            if (System.IO.Directory.GetDirectories(abs).Length > 0) return;
            AssetDatabase.DeleteAsset(folderAssetPath);
        }

        // ----------------------------------------------------------------
        // Folder helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Ensure an asset folder exists by creating any missing segments via
        /// AssetDatabase.CreateFolder so the .meta files are generated. Path
        /// must start with "Assets/" or "Packages/".
        /// </summary>
        public static void EnsureAssetFolder(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;
            string parent = System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // ----------------------------------------------------------------
        // Kit copy (raw file copy that skips .meta so destination GUIDs
        // are freshly generated by AssetDatabase.Refresh)
        // ----------------------------------------------------------------

        /// <summary>
        /// Copy a Kit folder from <paramref name="srcAssetPath"/> to
        /// <paramref name="dstAssetPath"/> using plain <see cref="System.IO.File.Copy"/>.
        /// <c>.meta</c> files are SKIPPED so the destination receives fresh
        /// GUIDs (sample-side and user-side Kit assets must not share GUIDs).
        /// Existing destination files are NOT overwritten — re-running Deploy
        /// preserves any local edits the user made.
        /// </summary>
        public static void CopyKitFolder(string srcAssetPath, string dstAssetPath)
        {
            string srcAbs = ToAbsolute(srcAssetPath);
            string dstAbs = ToAbsolute(dstAssetPath);
            if (!System.IO.Directory.Exists(srcAbs))
            {
                Debug.LogWarning($"[Hapbeat] Kit source not found: {srcAssetPath}");
                return;
            }
            CopyDirectoryRecursive(srcAbs, dstAbs);
            AssetDatabase.Refresh();
        }

        private static void CopyDirectoryRecursive(string srcAbs, string dstAbs)
        {
            System.IO.Directory.CreateDirectory(dstAbs);
            foreach (var file in System.IO.Directory.GetFiles(srcAbs))
            {
                string name = System.IO.Path.GetFileName(file);
                if (name.EndsWith(".meta")) continue;
                if (name == ".gitkeep") continue;
                string dst = System.IO.Path.Combine(dstAbs, name);
                if (!System.IO.File.Exists(dst))
                    System.IO.File.Copy(file, dst);
            }
            foreach (var dir in System.IO.Directory.GetDirectories(srcAbs))
            {
                string name = System.IO.Path.GetFileName(dir);
                CopyDirectoryRecursive(dir, System.IO.Path.Combine(dstAbs, name));
            }
        }

        private static string ToAbsolute(string assetPath)
        {
            if (assetPath.StartsWith("Assets/"))
                return System.IO.Path.Combine(Application.dataPath,
                    assetPath.Substring("Assets/".Length)).Replace('\\', '/');
            // For Packages/<pkg>/... paths, resolve via the package's filesystem path.
            if (assetPath.StartsWith("Packages/"))
            {
                int slash2 = assetPath.IndexOf('/', "Packages/".Length);
                if (slash2 > 0)
                {
                    string pkgName = assetPath.Substring("Packages/".Length, slash2 - "Packages/".Length);
                    var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                    if (pkg != null)
                        return System.IO.Path.Combine(pkg.resolvedPath,
                            assetPath.Substring(slash2 + 1)).Replace('\\', '/');
                }
            }
            return System.IO.Path.GetFullPath(assetPath);
        }
    }
}
#endif
