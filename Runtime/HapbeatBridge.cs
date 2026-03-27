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
        /// Play a haptic event by display name from the event map.
        /// </summary>
        /// <param name="displayName">Display name of the entry in the event map.</param>
        /// <param name="gainOverride">If >= 0, overrides the entry's gain value.</param>
        protected void Play(string displayName, float gainOverride = -1f)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId))
            {
                Debug.LogWarning($"[Hapbeat Bridge] Entry not found: '{displayName}'");
                return;
            }

            float g = gainOverride >= 0f ? gainOverride : entry.gain;
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

            float g = gainOverride >= 0f ? gainOverride : entry.gain;
            HapbeatManager.Instance.Play(entry.eventId, g, entry.group);
        }

        /// <summary>
        /// Play a haptic event with gain scaled by a velocity value.
        /// Useful for collision-based triggers where impact strength matters.
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
            float gain = t * entry.gain;
            HapbeatManager.Instance.Play(entry.eventId, gain, entry.group);
        }

        /// <summary>
        /// Play a haptic event with gain mapped through a custom curve.
        /// </summary>
        /// <param name="displayName">Display name of the entry.</param>
        /// <param name="inputValue">Input value (0-1 range recommended).</param>
        /// <param name="curve">Mapping curve (x=input, y=gain multiplier).</param>
        protected void PlayWithCurve(string displayName, float inputValue, AnimationCurve curve)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            float gain = curve.Evaluate(inputValue) * entry.gain;
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
