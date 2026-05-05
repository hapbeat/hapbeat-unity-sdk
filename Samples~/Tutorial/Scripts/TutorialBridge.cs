using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Tutorial-scope HapbeatBridge subclass.
    /// Centralizes all script-driven haptic calls for the Tutorial sample,
    /// and lets <see cref="TargetPickerUI"/> override the device target at
    /// runtime so users can experiment with Hapbeat's targeting feature.
    ///
    /// Wired Trigger components (HapbeatCollisionTrigger / HapbeatAnimatorTrigger /
    /// HapbeatSequenceTrigger / HapbeatTickEmitter) bypass this bridge and use
    /// the entry's <c>target</c> field directly — that's the "fixed target,
    /// designed at event-authoring time" pattern. Script-driven calls below
    /// use <see cref="CurrentTarget"/> so users can see the "dynamic target,
    /// chosen at runtime" pattern.
    /// </summary>
    public class TutorialBridge : HapbeatBridge
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
            if (EventMap == null || HapbeatManager.Instance == null) return;
            var entry = EventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId))
            {
                Debug.LogWarning($"[TutorialBridge] Entry not found: '{displayName}'");
                return;
            }

            float rawGain = gainOverride >= 0f ? gainOverride : entry.gain;
            float intensity = entry.CachedManifestIntensity;
            float gain = intensity < 0f ? rawGain : rawGain * intensity;

            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, displayName, CurrentTarget);
        }

        /// <summary>
        /// Velocity-scaled fire with picker target.
        /// </summary>
        public void PlayScaledWithPickerTarget(string displayName, float velocity,
            float minVelocity = 0f, float maxVelocity = 5f)
        {
            if (velocity < minVelocity) return;
            if (EventMap == null || HapbeatManager.Instance == null) return;
            var entry = EventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float t = Mathf.Clamp01((velocity - minVelocity) / (maxVelocity - minVelocity));
            float rawGain = t * entry.gain;
            float intensity = entry.CachedManifestIntensity;
            float gain = intensity < 0f ? rawGain : rawGain * intensity;
            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, displayName, CurrentTarget);
        }

        /// <summary>
        /// Curve-mapped fire with picker target. Used by Charge &amp; Shot.
        /// </summary>
        public void PlayWithCurveAndPickerTarget(string displayName, float inputValue, AnimationCurve curve)
        {
            if (EventMap == null || HapbeatManager.Instance == null) return;
            var entry = EventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float rawGain = curve.Evaluate(inputValue) * entry.gain;
            float intensity = entry.CachedManifestIntensity;
            float gain = intensity < 0f ? rawGain : rawGain * intensity;
            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, displayName, CurrentTarget);
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
    }
}
