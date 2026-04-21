using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Three-phase haptic trigger: optional one-shot on Fire(), a continuous loop
    /// that runs between Fire() and Stop(), and an optional one-shot on Stop().
    ///
    /// Use for grab / hold / release style interactions:
    /// <list type="bullet">
    ///   <item><b>On Start</b> — impact moment (grab click, attach, enter).
    ///     Typically Command or StreamClip, not looping.</item>
    ///   <item><b>Loop</b> — continuous sustain (held rumble, drag scrape).
    ///     Typically StreamSource or a looping StreamClip. Uses the inherited
    ///     <c>_entryIndex</c> field so it plugs into the same FireHaptic /
    ///     StopHaptic pipeline as <see cref="HapbeatUnityEventTrigger"/>.</item>
    ///   <item><b>On Stop</b> — release moment (release thud, exit ping).
    ///     Typically Command or StreamClip, not looping.</item>
    /// </list>
    ///
    /// Wire <see cref="Fire"/> to <c>XRGrabInteractable.firstSelectEntered</c>
    /// and <see cref="Stop"/> to <c>lastSelectExited</c> (or equivalent events
    /// for non-XRI interactions). Set any unused phase to the "(none)" option
    /// in the Inspector.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Sequence Trigger")]
    public class HapbeatSequenceTrigger : HapbeatTriggerBase
    {
        [Header("Sequence")]
        [Tooltip("Event map entry played as a one-shot when Fire() is called. " +
                 "-1 = none. Use Command or StreamClip mode — StreamSource mode " +
                 "doesn't make sense for a one-shot.")]
        [SerializeField]
        private int _onStartEntryIndex = -1;

        [Tooltip("Event map entry played as a one-shot when Stop() is called. " +
                 "-1 = none. Use Command or StreamClip mode — StreamSource mode " +
                 "doesn't make sense for a one-shot.")]
        [SerializeField]
        private int _onStopEntryIndex = -1;

        /// <summary>Index of the "on start" one-shot entry, or -1 if none.</summary>
        public int OnStartEntryIndex => _onStartEntryIndex;

        /// <summary>Index of the "on stop" one-shot entry, or -1 if none.</summary>
        public int OnStopEntryIndex => _onStopEntryIndex;

        /// <summary>
        /// Plays the "on start" one-shot (if set) and starts the loop. Wire to
        /// e.g. <c>XRGrabInteractable.firstSelectEntered</c>.
        /// </summary>
        public void Fire()
        {
            PlayOneShot(_onStartEntryIndex, "start");
            FireHaptic(); // inherited — starts the loop using _entryIndex
        }

        /// <summary>
        /// Stops the loop and plays the "on stop" one-shot (if set). Wire to
        /// e.g. <c>XRGrabInteractable.lastSelectExited</c>.
        /// </summary>
        public void Stop()
        {
            StopHaptic(); // inherited — stops the loop
            PlayOneShot(_onStopEntryIndex, "end");
        }

        /// <summary>
        /// Convenience overload accepting a gain multiplier (e.g. for impact
        /// velocity). Multiplies the configured entry gain on the one-shot only;
        /// the loop uses the entry's own gain.
        /// </summary>
        public void FireWithStartGain(float gainMultiplier)
        {
            PlayOneShot(_onStartEntryIndex, "start", gainMultiplier);
            FireHaptic();
        }

        private void PlayOneShot(int entryIndex, string phase, float gainMultiplier = 1f)
        {
            if (entryIndex < 0) return; // "(none)"
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = _eventMap.GetEntry(entryIndex);
            if (entry == null)
            {
                if (_verboseLog)
                    Debug.LogWarning($"[Hapbeat] Sequence {phase}-shot: entry index {entryIndex} out of range on {name}", this);
                return;
            }

            if (HapbeatManager.Instance == null)
            {
                // Base class already logs a one-shot warning when this happens from
                // the loop path; we silently defer here to avoid double warnings.
                return;
            }

            string label = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            string target = entry.HasTarget ? entry.target : null;
            // Effective gain (entry.gain × manifest.intensity) for stream modes;
            // raw gain for Command (device applies intensity internally).
            float gain = entry.GetEffectiveGain() * gainMultiplier;

            if (_verboseLog)
                Debug.Log($"[Hapbeat] Sequence {phase}-shot: '{label}' mode={entry.mode} gain={gain:F2}", this);

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        if (_verboseLog) Debug.Log($"[Hapbeat] Sequence {phase}-shot: Command mode but eventId is empty on entry '{label}'", this);
                        return;
                    }
                    HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, label, target);
                    break;

                case HapticMode.StreamClip:
                    if (entry.streamClip == null)
                    {
                        if (_verboseLog) Debug.Log($"[Hapbeat] Sequence {phase}-shot: StreamClip mode but streamClip is null on entry '{label}'", this);
                        return;
                    }
                    // One-shots never loop — entry.loop applies only to the Loop phase,
                    // which goes through the inherited FireHaptic() path.
                    HapbeatManager.Instance.StreamAudioClip(entry.streamClip, gain, target, loop: false);
                    break;

                case HapticMode.StreamSource:
                    Debug.LogWarning(
                        $"[Hapbeat] Sequence {phase}-shot: entry '{label}' is StreamSource mode, which is " +
                        $"not supported for one-shots (it needs an AudioSource to capture from). " +
                        $"Use Command or StreamClip for the {phase}-shot entry.", this);
                    break;
            }
        }
    }
}
