using System.Collections;
using System.Collections.Generic;
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

#if UNITY_EDITOR
        /// <summary>
        /// Direct field assignment for use by scene-builder Editor scripts.
        /// Bypasses SerializedObject to avoid Unity 6 inheritance-traversal
        /// issues with protected fields on the parent class (same fix as
        /// <see cref="HapbeatTriggerBase.EditorSetupEntry"/>).
        /// Caller must call EditorUtility.SetDirty(this) afterwards.
        /// </summary>
        public void EditorSetupEventMap(HapbeatEventMap map) => _eventMap = map;
#endif

        // Pending Play / Stop coroutines waiting on hapticDelaySeconds. Flushed
        // when the user edits latency mid-Play so stale fires don't deliver
        // late with the old delay. Mirrors HapbeatTriggerBase's tracker pattern.
        private readonly List<Coroutine> _pendingDelayCoroutines = new List<Coroutine>(4);

        protected virtual void OnEnable()
        {
            HapbeatManager.OnHapticDelayChanged += FlushPendingDelayCoroutines;
        }

        protected virtual void OnDisable()
        {
            HapbeatManager.OnHapticDelayChanged -= FlushPendingDelayCoroutines;
            FlushPendingDelayCoroutines();
        }

        /// <summary>Cancel all in-flight latency-deferred Play / Stop coroutines.</summary>
        public void FlushPendingDelayCoroutines()
        {
            for (int i = 0; i < _pendingDelayCoroutines.Count; i++)
            {
                var c = _pendingDelayCoroutines[i];
                if (c != null) StopCoroutine(c);
            }
            _pendingDelayCoroutines.Clear();
        }

        private Coroutine StartHapticDelayCoroutine(IEnumerator routine)
        {
            var holder = new Coroutine[1];
            holder[0] = StartCoroutine(WrapHapticDelayCoroutine(routine, holder));
            _pendingDelayCoroutines.Add(holder[0]);
            return holder[0];
        }

        private IEnumerator WrapHapticDelayCoroutine(IEnumerator inner, Coroutine[] selfHolder)
        {
            yield return StartCoroutine(inner);
            _pendingDelayCoroutines.Remove(selfHolder[0]);
        }

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
        /// Effective fire-time deferral (seconds) — global config + per-entry
        /// offset, clamped to >= 0. Mirrors <see cref="HapbeatTriggerBase.ComputeEffectiveDelaySeconds"/>.
        /// </summary>
        private static float ComputeEffectiveDelaySeconds(HapbeatEventEntry entry)
        {
            float global = HapbeatManager.Instance != null
                ? HapbeatManager.Instance.HapticDelaySeconds
                : 0f;
            float offset = entry != null ? entry.delayOffsetSeconds : 0f;
            return Mathf.Max(0f, global + offset);
        }

        /// <summary>
        /// Dispatch a Command-mode Play, deferring via coroutine when an
        /// effective delay is configured. Keeps the entry-resolution / gain
        /// math in the caller (only the wire send is delayed).
        /// </summary>
        private void DispatchPlay(HapbeatEventEntry entry, float gain)
        {
            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                StartHapticDelayCoroutine(PlayAfterDelay(entry.eventId, gain, entry.target, delay));
                return;
            }
            HapbeatManager.Instance.Play(entry.eventId, gain, target: entry.target);
        }

        private IEnumerator PlayAfterDelay(string eventId, float gain, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.Play(eventId, gain, target: target);
        }

        /// <summary>
        /// Dispatch a Command-mode Stop with the same delay as the matching Play
        /// (so the perceived loop / duration matches the caller's Fire→Stop
        /// interval rather than collapsing to zero).
        /// </summary>
        private void DispatchStop(HapbeatEventEntry entry)
        {
            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                StartHapticDelayCoroutine(StopAfterDelay(entry.eventId, entry.target, delay));
                return;
            }
            HapbeatManager.Instance.Stop(entry.eventId, target: entry.target);
        }

        private IEnumerator StopAfterDelay(string eventId, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.Stop(eventId, target: target);
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
            DispatchPlay(entry, g);
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
            DispatchPlay(entry, g);
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
            DispatchPlay(entry, gain);
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
            DispatchPlay(entry, gain);
        }

        /// <summary>
        /// Stop a haptic event by display name.
        /// </summary>
        protected void Stop(string displayName)
        {
            if (_eventMap == null || HapbeatManager.Instance == null) return;

            var entry = _eventMap.FindByName(displayName);
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            DispatchStop(entry);
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
