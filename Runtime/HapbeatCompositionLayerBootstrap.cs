#if HAPBEAT_HAS_COMPOSITION_LAYERS
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Creates the composition layer "keep-alive" before XR initializes, so that a
    /// <c>CompositionLayerManager</c> instance exists at the one moment OpenXR is willing to
    /// hand it a layer provider.
    ///
    /// <para>
    /// <b>Why this has to run this early.</b> The assignment happens in exactly one place —
    /// <c>OpenXRCompositionLayersFeature.OnSessionBegin</c> — and only
    /// <c>if (CompositionLayerManager.Instance != null)</c>. That property returns null once
    /// the manager has stopped itself, which it does on the first update that finds no
    /// composition layer alive (<c>CompositionLayerManager.Update</c> →
    /// <c>StopCompositionLayerManager</c>), and it stays stopped until some
    /// <c>CompositionLayer.OnEnable</c> restarts it. Meanwhile XR is brought up from
    /// <c>XRGeneralSettings</c>'s own runtime hooks — <c>InitXRSDK</c> at
    /// <see cref="RuntimeInitializeLoadType.AfterAssembliesLoaded"/> and <c>StartXRSDK</c> at
    /// <see cref="RuntimeInitializeLoadType.BeforeSplashScreen"/> — i.e. both before the first
    /// scene's <c>Awake</c>. A layer created from a panel's <c>OnEnable</c> is therefore
    /// created after the session has already begun, the feature's single assignment
    /// opportunity has passed, and no layer created afterwards is ever composited.
    /// </para>
    ///
    /// <para>
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> is the only load type that
    /// is strictly earlier than <c>AfterAssembliesLoaded</c> (ordering <i>within</i> one load
    /// type is not defined across assemblies, so matching XR's own phase would not be enough).
    /// </para>
    ///
    /// <para>
    /// <b>Opt-in.</b> A permanently resident composition layer is not something to impose on a
    /// project that never uses one, so this does nothing unless
    /// <see cref="HapbeatConfig.enableCompositionLayerSupport"/> is on.
    /// </para>
    /// </summary>
    internal static class HapbeatCompositionLayerBootstrap
    {
        // The texture-less layer that holds the manager open, kept for the whole run: an XR
        // session can end and begin again (a headset doff does that), and each re-begin needs
        // a live manager just as much as the first one did. Nothing destroys this.
        private static GameObject s_KeepAlive;

        // Tri-state cache of the config flag: null = not read yet. Cached because the panel
        // asks for it while building, which can be long after the config was loaded.
        private static bool? s_SupportEnabled;

        /// <summary>
        /// Whether <see cref="HapbeatConfig.enableCompositionLayerSupport"/> is on for this
        /// build. False (the default) means no keep-alive was created, so no panel can be
        /// promoted to a composition layer — the panel uses this to say so instead of waiting
        /// out its provider timeout and blaming the OpenXR feature.
        /// </summary>
        public static bool SupportEnabled
        {
            get
            {
                if (!s_SupportEnabled.HasValue)
                {
                    var config = Resources.Load<HapbeatConfig>("HapbeatConfig");
                    s_SupportEnabled = config != null && config.enableCompositionLayerSupport;
                }
                return s_SupportEnabled.Value;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void CreateKeepAliveBeforeXRStarts()
        {
            // Statics survive a Play-mode entry when Domain Reload is disabled, so re-read the
            // flag (the user may have toggled it) and don't create a second keep-alive.
            s_SupportEnabled = null;
            if (!SupportEnabled) return;
            if (s_KeepAlive != null) return;

            s_KeepAlive = HapbeatPanelCompositionLayerSurface.CreateManagerKeepAlive();
            if (s_KeepAlive == null) return;

            // Created before any scene exists, so it has to be moved to the DontDestroyOnLoad
            // scene explicitly or the first scene load takes it — and with it the manager.
            Object.DontDestroyOnLoad(s_KeepAlive);
            s_KeepAlive.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        }
    }
}
#endif
