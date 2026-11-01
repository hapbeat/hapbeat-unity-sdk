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
        /// Fire with the haptic's delay worked out by the caller (seconds, ≥ 0), instead of this device's offset
        /// (DEC-093): e.g. the haptic delay of <see cref="HapbeatAvSync.Split"/>, which already holds it, while the
        /// caller plays its sound with the sound delay. A StreamClip entry starts that much later on the stream
        /// timeline (sample-accurate).
        /// </summary>
        public void FireAfter(float delaySeconds)
        {
            FireHaptic(delaySeconds);
        }

        /// <summary>
        /// Fire with a call-time gain. Useful for Animation Events with float parameter.
        /// <paramref name="gain"/> multiplies the entry (it does not replace its gain or
        /// clip_gain): sent = clamp(clip_gain × entry.gain × <see cref="HapbeatTriggerBase.GainMultiplier"/>
        /// × <paramref name="gain"/>, 0, 1).
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

            // The call-time gain composes on top of the entry (clip_gain × entry.gain)
            // and the trigger multiplier, like every other fire path (contracts
            // kit-format.md "強さの式（正本）": the call's gain never replaces clip_gain).
            // Fired reports the factor on top of the entry's own gain
            // (contracts scene-recording.md §2 events[].gain).
            float callFactor = GainMultiplier * gain;
            ReportFired(entry, callFactor, entry.loop);
            float sendGain = entry.GetEffectiveGain() * callFactor;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                // gain と同じく pan も発火時点の値を捕捉して遅延送信する
                // (送信時に読み直すと Fire→送信の間の変更が混ざる)。
                StartHapticDelayCoroutine(FireWithGainAfterDelay(entry.eventId, sendGain, Pan, label, target, delay));
                return;
            }
            HapbeatManager.Instance.PlayUnreported(entry.eventId, sendGain, label, target, Pan);
        }

        private IEnumerator FireWithGainAfterDelay(string eventId, float gain, float pan, string label, string target, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (!_triggerEnabled || HapbeatManager.Instance == null) yield break;
            HapbeatManager.Instance.PlayUnreported(eventId, gain, label, target, pan);
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
