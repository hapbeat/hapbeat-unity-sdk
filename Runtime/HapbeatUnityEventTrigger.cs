using System.Collections;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Generic haptic trigger that exposes public methods for use with UnityEvents.
    /// Wire Fire() to any UnityEvent: UI Button OnClick, Animation Event,
    /// XR Interaction Toolkit events, etc.
    /// Can be placed on a dedicated [Hapbeat Router] GameObject.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat UnityEvent Trigger")]
    public class HapbeatUnityEventTrigger : HapbeatTriggerBase
    {
        /// <summary>
        /// Fire the haptic event. Call from any UnityEvent.
        /// </summary>
        public void Fire()
        {
            FireHaptic();
        }

        /// <summary>
        /// Fire with a gain override. Useful for Animation Events with float parameter.
        /// </summary>
        public void FireWithGain(float gain)
        {
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = ResolveEntry();
            if (entry == null || string.IsNullOrEmpty(entry.eventId)) return;

            if (HapbeatManager.Instance == null) return;

            string label = string.IsNullOrEmpty(entry.displayName) ? entry.eventId : entry.displayName;
            string target = entry.HasTarget ? entry.target : null;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                // gain と同じく pan も発火時点の値を捕捉して遅延送信する
                // (送信時に読み直すと Fire→送信の間の変更が混ざる)。
                StartHapticDelayCoroutine(FireWithGainAfterDelay(entry.eventId, gain, Pan, label, target, delay));
                return;
            }
            HapbeatManager.Instance.Play(entry.eventId, gain, label, target, Pan);
        }

        private IEnumerator FireWithGainAfterDelay(string eventId, float gain, float pan, string label, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (!_triggerEnabled || HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.Play(eventId, gain, label, target, pan);
        }

        /// <summary>
        /// Stop the haptic event. Call from any UnityEvent.
        /// </summary>
        public void Stop()
        {
            StopHaptic();
        }
    }
}
