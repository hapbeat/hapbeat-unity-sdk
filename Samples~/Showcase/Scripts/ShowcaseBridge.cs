using UnityEngine;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Showcase-scope HapbeatBridge subclass.
    /// Centralizes all script-driven haptic calls for the Showcase sample,
    /// and lets <see cref="TargetPickerUI"/> override the device target at
    /// runtime so users can experiment with Hapbeat's targeting feature.
    ///
    /// <para>
    /// Mode handling: each call inspects <c>entry.mode</c> and routes to the
    /// matching transport. <see cref="HapticMode.StreamClip"/> entries are
    /// streamed as <c>Manager.StreamAudioClip</c> so the Showcase works
    /// out of the box without a Hapbeat Studio Kit on the device — only the
    /// SDK + the WAV files shipped with this sample are needed.
    /// <see cref="HapticMode.Command"/> entries fall through to
    /// <c>Manager.Play</c> for users who have set up a Kit.
    /// </para>
    ///
    /// Wired Trigger components (HapbeatCollisionTrigger / HapbeatAnimatorTrigger /
    /// HapbeatSequenceTrigger / HapbeatTickEmitter) bypass this bridge and use
    /// the entry's <c>target</c> field directly — that's the "fixed target,
    /// designed at event-authoring time" pattern. Script-driven calls below
    /// use <see cref="CurrentTarget"/> so users can see the "dynamic target,
    /// chosen at runtime" pattern.
    /// </summary>
    public class ShowcaseBridge : HapbeatBridge
    {
        /// <summary>
        /// Current target string applied to script-driven calls.
        /// Empty = broadcast. Set by <see cref="TargetPickerUI"/>.
        /// </summary>
        public string CurrentTarget { get; set; } = "";

        /// <summary>
        /// Fire an event using the picker-selected target (overrides entry.target).
        /// Used by Stream Console / Charge & Shot / global hotkeys.
        /// </summary>
        public void PlayWithPickerTarget(string displayName, float gainOverride = -1f)
        {
            var entry = ResolveEntry(displayName);
            if (entry == null) return;

            float gain = ResolveGain(entry, gainOverride >= 0f ? gainOverride : entry.gain);
            DispatchOneShot(entry, gain);
        }

        /// <summary>
        /// Velocity-scaled fire with picker target.
        /// </summary>
        public void PlayScaledWithPickerTarget(string displayName, float velocity,
            float minVelocity = 0f, float maxVelocity = 5f)
        {
            if (velocity < minVelocity) return;
            var entry = ResolveEntry(displayName);
            if (entry == null) return;

            float t = Mathf.Clamp01((velocity - minVelocity) / (maxVelocity - minVelocity));
            float gain = ResolveGain(entry, t * entry.gain);
            DispatchOneShot(entry, gain);
        }

        /// <summary>
        /// Curve-mapped fire with picker target. Used by Charge &amp; Shot.
        /// </summary>
        public void PlayWithCurveAndPickerTarget(string displayName, float inputValue, AnimationCurve curve)
        {
            var entry = ResolveEntry(displayName);
            if (entry == null) return;

            float gain = ResolveGain(entry, curve.Evaluate(inputValue) * entry.gain);
            DispatchOneShot(entry, gain);
        }

        /// <summary>
        /// Stream a clip with picker target. Returns a handle for dynamic Gain/Pan modulation.
        /// </summary>
        public HapbeatStreamPlayback StreamWithPickerTarget(AudioClip clip, float gain = 1f, bool loop = false)
        {
            if (HapbeatManager.Instance == null || clip == null) return null;
            return HapbeatManager.Instance.StreamAudioClip(clip, gain, CurrentTarget, loop);
        }

        /// <summary>
        /// Stop the active stream (delegates to Manager).
        /// </summary>
        public void StopStream()
        {
            HapbeatManager.Instance?.StopStream();
        }

        /// <summary>
        /// Send a Ping for connection / latency check.
        /// </summary>
        public void SendPing()
        {
            HapbeatManager.Instance?.Ping();
        }

        /// <summary>
        /// Find an entry's gain (for UI display).
        /// </summary>
        public float GetEntryGain(string displayName)
        {
            if (EventMap == null) return 1f;
            var entry = EventMap.FindByName(displayName);
            return entry != null ? entry.gain : 1f;
        }

        // -----------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------

        private HapbeatEventEntry ResolveEntry(string displayName)
        {
            if (EventMap == null || HapbeatManager.Instance == null) return null;
            var entry = EventMap.FindByName(displayName);
            if (entry == null)
            {
                Debug.LogWarning($"[ShowcaseBridge] Entry not found: '{displayName}'");
                return null;
            }
            return entry;
        }

        private static float ResolveGain(HapbeatEventEntry entry, float rawGain)
        {
            float intensity = entry.CachedManifestIntensity;
            return intensity < 0f ? rawGain : rawGain * intensity;
        }

        /// <summary>
        /// Send a one-shot fire respecting <c>entry.mode</c>:
        /// <list type="bullet">
        ///   <item>StreamClip → <c>Manager.StreamAudioClip(entry.streamClip, gain, target, loop)</c></item>
        ///   <item>Command → <c>Manager.Play(entry.eventId, gain, displayName, target)</c></item>
        /// </list>
        /// </summary>
        private void DispatchOneShot(HapbeatEventEntry entry, float gain)
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;

            switch (entry.mode)
            {
                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        Debug.LogWarning($"[ShowcaseBridge] StreamClip entry '{entry.displayName}' has no AudioClip.");
                        return;
                    }
                    // For Showcase we treat script-driven StreamClip fires as one-shot
                    // (loop=false). The Sequence loop path is handled by HapbeatSequenceTrigger.
                    mgr.StreamAudioClip(entry.streamClip, gain, CurrentTarget, loop: false);
                    break;

                case HapticMode.Command:
                default:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        Debug.LogWarning($"[ShowcaseBridge] Command entry '{entry.displayName}' has no event id.");
                        return;
                    }
                    mgr.Play(entry.eventId, gain, entry.displayName, CurrentTarget);
                    break;
            }
        }
    }
}
