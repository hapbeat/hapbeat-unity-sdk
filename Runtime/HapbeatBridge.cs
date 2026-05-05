using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Base class for project-specific haptic bridges.
    /// Subclass this to centralize all haptic logic in one file.
    /// Place on [Hapbeat Event Router] GameObject.
    ///
    /// Usage: Create a subclass in your project, add [SerializeField] references
    /// to your game objects (player, UI, etc.), and implement haptic logic
    /// using the helper methods provided here.
    /// </summary>
    public abstract class HapbeatBridge : MonoBehaviour
    {
        [Header("Hapbeat")]
        [Tooltip("Event map containing all haptic event definitions.")]
        [SerializeField]
        protected HapbeatEventMap _eventMap;

        /// <summary>The event map this bridge uses.</summary>
        public HapbeatEventMap EventMap => _eventMap;

        /// <summary>
        /// Apply the cached manifest intensity to a raw gain value.
        /// Wire gain = rawGain × manifest.intensity; falls back to rawGain when the
        /// cache is unresolved (sentinel -1). The device is a pure executor that
        /// plays req.gain as-is — it no longer reads manifest.intensity itself.
        /// </summary>
        private static float ApplyManifestIntensity(HapbeatEventEntry entry, float rawGain)
        {
            float intensity = entry.CachedManifestIntensity;
            return intensity < 0f ? rawGain : rawGain * intensity;
        }

        /// <summary>
        /// Play a haptic event by display name from the event map.
        /// </summary>
        /// <param name="displayName">Display name of the entry in the event map.</param>
        /// <param name="gainOverride">If >= 0, overrides the entry's gain value (before
        /// manifest.intensity is applied).</param>
        protected void Play(string displayName, float gainOverride = -1f)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId))
            {
                Debug.LogWarning($"[Hapbeat Bridge] Entry not found: '{displayName}'");
                return;
            }

            float rawGain = gainOverride >= 0f ? gainOverride : entry.gain;
            float g = ApplyManifestIntensity(entry, rawGain);
            HapbeatManager.Instance.Play(entry.eventId, g, entry.group);
        }

        /// <summary>
        /// Play a haptic event by entry index from the event map.
        /// </summary>
        protected void PlayByIndex(int entryIndex, float gainOverride = -1f)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.GetEntry(entryIndex);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float rawGain = gainOverride >= 0f ? gainOverride : entry.gain;
            float g = ApplyManifestIntensity(entry, rawGain);
            HapbeatManager.Instance.Play(entry.eventId, g, entry.group);
        }

        /// <summary>
        /// Play a haptic event with gain scaled by a velocity value.
        /// Useful for collision-based triggers where impact strength matters.
        /// manifest.intensity is applied on top of the velocity-scaled gain.
        /// </summary>
        /// <param name="displayName">Display name of the entry.</param>
        /// <param name="velocity">Current velocity magnitude.</param>
        /// <param name="minVelocity">Velocity below which no haptic fires.</param>
        /// <param name="maxVelocity">Velocity at which gain reaches the entry's full value.</param>
        protected void PlayScaled(string displayName, float velocity,
            float minVelocity = 0f, float maxVelocity = 10f)
        {
            if (velocity < minVelocity) return;
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float t = Mathf.Clamp01((velocity - minVelocity) / (maxVelocity - minVelocity));
            float rawGain = t * entry.gain;
            float gain = ApplyManifestIntensity(entry, rawGain);
            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group);
        }

        /// <summary>
        /// Play a haptic event with gain mapped through a custom curve.
        /// manifest.intensity is applied on top of the curve-evaluated gain.
        /// </summary>
        /// <param name="displayName">Display name of the entry.</param>
        /// <param name="inputValue">Input value (0-1 range recommended).</param>
        /// <param name="curve">Mapping curve (x=input, y=gain multiplier).</param>
        protected void PlayWithCurve(string displayName, float inputValue, AnimationCurve curve)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float rawGain = curve.Evaluate(inputValue) * entry.gain;
            float gain = ApplyManifestIntensity(entry, rawGain);
            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group);
        }

        /// <summary>
        /// Stop a haptic event by display name.
        /// </summary>
        protected void Stop(string displayName)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            HapbeatManager.Instance.Stop(entry.eventId, entry.group);
        }

        /// <summary>
        /// Stop all haptic events.
        /// </summary>
        protected void StopAll()
        {
            HapbeatManager.Instance?.StopAll();
        }
    }
}
