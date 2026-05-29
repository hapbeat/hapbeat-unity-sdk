#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Detects when multiple versions of imported Hapbeat SDK samples coexist
    /// under <c>Assets/Samples/Hapbeat SDK/</c> and warns in the Console.
    ///
    /// Background: Unity's UPM Samples system imports samples to
    /// <c>Assets/Samples/&lt;package&gt;/&lt;version&gt;/&lt;sample&gt;/</c> and does
    /// NOT auto-clean old version folders when the package is updated. If a
    /// user upgrades the SDK and re-imports a sample, the old version's scripts
    /// remain — typically causing duplicate class definitions (compile errors)
    /// or, after a namespace migration, two parallel sample copies with the
    /// same scenes/assets that are easy to confuse.
    ///
    /// The fix is trivial — delete the older version folder. The hard part is
    /// just noticing. This guard fires once per Editor session at startup
    /// (and whenever the user explicitly runs the menu item) so the prompt is
    /// front-and-center the moment the bad state appears.
    /// </summary>
    [InitializeOnLoad]
    internal static class HapbeatSampleVersionGuard
    {
        private const string SamplesRoot = "Assets/Samples/Hapbeat SDK";
        private const string SessionKey = "Hapbeat.SampleVersionGuard.Warned";

        static HapbeatSampleVersionGuard()
        {
            // delayCall defers until AssetDatabase is fully initialized.
            EditorApplication.delayCall += CheckOnce;
        }

        [MenuItem("Hapbeat/Diagnostics/Check Sample Versions")]
        private static void CheckFromMenu()
        {
            // Allow a manual re-check after the user cleans things up.
            SessionState.EraseBool(SessionKey);
            CheckOnce();
            // If the check passes silently, give explicit feedback so the menu
            // item feels responsive.
            if (SessionState.GetBool(SessionKey, false) && !_warnedThisCheck)
                Debug.Log("[Hapbeat] Sample versions OK — only one version present (or none imported).");
        }

        private static bool _warnedThisCheck;

        private static void CheckOnce()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);
            _warnedThisCheck = false;

            try { Check(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[Hapbeat] Sample version guard error: {e.Message}");
            }
        }

        private static void Check()
        {
            if (!AssetDatabase.IsValidFolder(SamplesRoot)) return;

            // AssetDatabase.GetSubFolders returns "Assets/Samples/Hapbeat SDK/0.2.0"-style paths.
            var versions = AssetDatabase.GetSubFolders(SamplesRoot)
                .Select(Path.GetFileName)
                .Where(IsLikelyVersion)
                .OrderBy(v => v, StringComparer.Ordinal)
                .ToArray();

            if (versions.Length <= 1) return;

            string latest = versions[versions.Length - 1];
            string olderList = string.Join(", ", versions.Take(versions.Length - 1));

            _warnedThisCheck = true;
            Debug.LogWarning(
                "[Hapbeat] Multiple Hapbeat SDK sample versions detected under " +
                $"'{SamplesRoot}/': {string.Join(", ", versions)}.\n" +
                "Unity does not clean old sample imports when a package is updated, so " +
                "leftover copies cause duplicate class definitions or stale scenes side-by-side " +
                "with the latest version.\n" +
                $"→ Fix: delete the older folder(s) ({olderList}) and keep only the latest ({latest}). " +
                "Re-run this check via [Hapbeat/Diagnostics/Check Sample Versions] after cleanup.");
        }

        private static bool IsLikelyVersion(string name)
        {
            // Heuristic: version folders start with a digit or 'v'. Skips
            // user-created note folders that happen to share the parent.
            if (string.IsNullOrEmpty(name)) return false;
            char c0 = name[0];
            return char.IsDigit(c0) || c0 == 'v' || c0 == 'V';
        }
    }
}
#endif
