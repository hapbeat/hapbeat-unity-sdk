using System.Collections;
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

        [Tooltip("Delay (seconds) between the On Start one-shot and the Loop phase start.\n" +
                 "-1 = auto (use the On Start clip's duration).\n" +
                 " 0 = no delay (fire loop immediately).\n" +
                 " >0 = custom delay in seconds.\n\n" +
                 "Workaround for a device-firmware issue where a rapid STREAM_BEGIN (loop) " +
                 "right after a STREAM_END (one-shot) can truncate the one-shot on the device. " +
                 "Auto lets the start shot finish playing before the loop stream takes over.\n\n" +
                 "Command-mode On Start doesn't conflict with stream loops, so the delay is " +
                 "skipped in that case regardless of this setting.")]
        [SerializeField]
        private float _startShotDelay = -1f;

        // Handle to a pending delayed FireHaptic, so Stop() can cancel it if the
        // user released before the delay elapsed (i.e. Loop never started).
        private Coroutine _pendingLoopStart;

        /// <summary>Index of the "on start" one-shot entry, or -1 if none.</summary>
        public int OnStartEntryIndex => _onStartEntryIndex;

        /// <summary>Index of the "on stop" one-shot entry, or -1 if none.</summary>
        public int OnStopEntryIndex => _onStopEntryIndex;

        /// <summary>
        /// Delay (seconds) between the On Start one-shot and the Loop phase start.
        /// -1 = auto (use On Start clip duration). See serialized field tooltip.
        /// </summary>
        public float StartShotDelay
        {
            get => _startShotDelay;
            set => _startShotDelay = value;
        }

        /// <summary>
        /// Plays the "on start" one-shot (if set) and starts the loop. Wire to
        /// e.g. <c>XRGrabInteractable.firstSelectEntered</c>.
        /// </summary>
        public void Fire()
        {
            // Top-level log so a verbose session can unambiguously tell whether
            // Fire() is being invoked at all (separating "UnityEvent not wired"
            // issues from "Sequence short-circuit" issues).
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.Fire() invoked on {name} " +
                          $"(onStart={_onStartEntryIndex}, loop={_entryIndex}, onStop={_onStopEntryIndex})", this);
            PlayOneShot(_onStartEntryIndex, "start");
            StartLoopAfterDelay();
        }

        /// <summary>
        /// Stops the loop and plays the "on stop" one-shot (if set). Wire to
        /// e.g. <c>XRGrabInteractable.lastSelectExited</c>.
        /// </summary>
        public void Stop()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.Stop() invoked on {name}", this);
            // Cancel a pending delayed loop start (user released before the
            // start-shot delay elapsed → the loop never kicked off, so there's
            // nothing for StopHaptic to stop on that front).
            CancelPendingLoop();
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
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.FireWithStartGain({gainMultiplier:F2}) invoked on {name}", this);
            PlayOneShot(_onStartEntryIndex, "start", gainMultiplier);
            // Loop uses the entry's own gain — gainMultiplier applies to the
            // start shot only (per existing contract).
            StartLoopAfterDelay();
        }

        // ── Delayed-loop-start plumbing ──────────────────────────────────────

        /// <summary>
        /// Start the Loop phase, possibly after a short delay so the preceding
        /// On Start one-shot can finish playing on the device. The delay is a
        /// workaround for a device-firmware issue where STREAM_BEGIN (loop)
        /// arriving shortly after STREAM_END (one-shot) truncates the one-shot
        /// on playback. See feedback_firmware_stream_transition.md for details.
        /// </summary>
        private void StartLoopAfterDelay()
        {
            CancelPendingLoop();

            float delay = ResolveStartShotDelay();
            if (delay <= 0f)
            {
                // No delay → behave like before (immediate loop start).
                FireHaptic();
                return;
            }

            if (_verboseLog)
                Debug.Log($"[Hapbeat] Sequence: delaying Loop start by {delay:F3}s on {name} " +
                          $"(workaround for rapid-stream device issue)", this);
            _pendingLoopStart = StartCoroutine(LoopStartCoroutine(delay));
        }

        private IEnumerator LoopStartCoroutine(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _pendingLoopStart = null;
            FireHaptic();
        }

        private void CancelPendingLoop()
        {
            if (_pendingLoopStart != null)
            {
                StopCoroutine(_pendingLoopStart);
                _pendingLoopStart = null;
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] Sequence: cancelled pending delayed loop-start on {name} " +
                              "(Stop arrived before the start-shot delay elapsed)", this);
            }
        }

        /// <summary>
        /// Compute the effective delay to wait after the On Start one-shot before
        /// starting the Loop phase. Returns 0 when a delay is unnecessary
        /// (no On Start entry, On Start is Command mode, or missing clip).
        /// </summary>
        private float ResolveStartShotDelay()
        {
            // Explicit non-negative delay always wins.
            if (_startShotDelay >= 0f) return _startShotDelay;

            // Auto (-1): use the start clip's duration for stream-mode one-shots,
            // otherwise no delay.
            if (_onStartEntryIndex < 0 || _eventMap == null) return 0f;
            var entry = _eventMap.GetEntry(_onStartEntryIndex);
            if (entry == null) return 0f;
            // Command mode uses flashed clips on-device — no stream collision with
            // the Loop phase, so no need to delay.
            if (entry.mode == HapticMode.Command) return 0f;
            if (entry.streamClip == null) return 0f;
            return entry.streamClip.length;
        }

        private void OnDisable()
        {
            // Don't leave a coroutine dangling past component/object teardown.
            CancelPendingLoop();
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
