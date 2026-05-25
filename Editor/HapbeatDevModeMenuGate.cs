#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;
// Alias to disambiguate from UnityEditor.PackageInfo (legacy AssetStore type).
using UpmPackageInfo = UnityEditor.PackageManager.PackageInfo;
using UpmPackageSource = UnityEditor.PackageManager.PackageSource;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Hides SDK-developer-only menus when the Hapbeat SDK package is
    /// installed as a read-only consumer (UPM Git URL, registry, tarball,
    /// etc.). Local / Embedded installs keep the full menu set.
    ///
    /// <para>Why hide rather than disable: end users shouldn't see
    /// scaffolding / sync tooling that depends on writable package
    /// sources. Showing greyed-out items would still expose the existence
    /// of dev workflows that they can't use, which is confusing.</para>
    ///
    /// <para>Mechanism: <c>UnityEditor.Menu.RemoveMenuItem(string)</c> is
    /// called via <see cref="EditorApplication.delayCall"/> on every
    /// assembly reload (because <c>[MenuItem]</c> attributes re-register
    /// their items each reload, we have to remove them again afterwards).
    /// The method is <c>internal</c> in Unity 6, so we resolve it via
    /// reflection — if a future Unity version removes it entirely, the
    /// dev menus degrade to "visible but functionally inert in user mode"
    /// rather than breaking the SDK.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class HapbeatDevModeMenuGate
    {
        // Cached reflection target: UnityEditor.Menu.RemoveMenuItem(string).
        // Internal in Unity 6 — public access would compile-error.
        private static readonly MethodInfo s_removeMenuItem = ResolveRemoveMenuItem();

        private static MethodInfo ResolveRemoveMenuItem()
        {
            return typeof(Menu).GetMethod(
                "RemoveMenuItem",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
        }

        /// <summary>
        /// Menu paths that should only be visible to SDK developers
        /// (Local / Embedded installs). End users never see these.
        /// </summary>
        private static readonly string[] kDevOnlyMenuPaths = new[]
        {
            // Build Samples — scaffolds / deploys SDK-shipped samples into
            // Assets/HapbeatSDK/SDK_Samples/. End users open imported
            // sample scenes directly from Assets/Samples/.../ instead.
            "Hapbeat/Build Samples/1. Basic Example",
            "Hapbeat/Build Samples/2. Showcase (full scene)",
            // Maintainer Sync — writes back into the package's Samples~/
            // folder, requires Local/Embedded source to be useful at all.
            "Hapbeat/Maintainers/Sync HapbeatSDK → Samples~ (Showcase)",
            "Hapbeat/Maintainers/Sync HapbeatSDK → Samples~ (BasicExample)",
        };

        static HapbeatDevModeMenuGate()
        {
            if (IsLocalDevInstall()) return;
            // Delay until Unity has registered all [MenuItem] attributes
            // for this domain reload, then remove the dev-only ones.
            EditorApplication.delayCall += RemoveDevMenus;
        }

        private static void RemoveDevMenus()
        {
            if (s_removeMenuItem == null)
            {
                Debug.LogWarning(
                    "[Hapbeat] UnityEditor.Menu.RemoveMenuItem is not available " +
                    "in this Unity version. SDK-developer-only menus will remain " +
                    "visible in user installs (functionally inert).");
                return;
            }

            foreach (var path in kDevOnlyMenuPaths)
            {
                try
                {
                    s_removeMenuItem.Invoke(null, new object[] { path });
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Hapbeat] Failed to remove menu '{path}': {e.Message}");
                }
            }
        }

        /// <summary>
        /// True when the Hapbeat SDK package is installed in a writable
        /// form — Local (<c>file:</c> reference) or Embedded
        /// (<c>Packages/&lt;pkg&gt;</c> in the project). Other sources
        /// (Git URL, Registry, Tarball, Built-in) are treated as
        /// end-user installs.
        ///
        /// <para>If the assembly is not in any UPM package (e.g. dropped
        /// into <c>Assets/</c> directly), we conservatively treat it as
        /// developer mode so the tooling stays available.</para>
        /// </summary>
        public static bool IsLocalDevInstall()
        {
            var pkg = UpmPackageInfo.FindForAssembly(typeof(HapbeatBridge).Assembly);
            if (pkg == null) return true;
            return pkg.source == UpmPackageSource.Local
                || pkg.source == UpmPackageSource.Embedded;
        }
    }
}
#endif
