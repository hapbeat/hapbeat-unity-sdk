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
    ///     A looping StreamClip entry. Attach a HapbeatParameterBinding to
    ///     modulate its gain / pan from game state (velocity, position …)
    ///     while the loop is running. Uses the inherited <c>_entryIndex</c>
    ///     field so it plugs into the same FireHaptic / StopHaptic pipeline
    ///     as <see cref="HapbeatUnityEventTrigger"/>.</item>
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
        // Stable GUID references for On Start / On Stop. Mirror the base class's
        // _entryId / _entryIndex pairing: _*EntryId is authoritative, _*EntryIndex
        // is a display cache + legacy fallback for scenes authored before ids.
        [SerializeField, HideInInspector]
        private string _onStartEntryId = "";
        [SerializeField, HideInInspector]
        private string _onStopEntryId = "";

        [Tooltip("List index of the On Start entry (display cache). -1 = none. " +
                 "The authoritative reference is the hidden stable GUID.")]
        [SerializeField]
        private int _onStartEntryIndex = -1;

        [Tooltip("List index of the On Stop entry (display cache). -1 = none. " +
                 "The authoritative reference is the hidden stable GUID.")]
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

        /// <summary>Index of the "on start" one-shot entry (display cache), or -1 if none.</summary>
        public int OnStartEntryIndex => _onStartEntryIndex;

        /// <summary>Index of the "on stop" one-shot entry (display cache), or -1 if none.</summary>
        public int OnStopEntryIndex => _onStopEntryIndex;

        /// <summary>Stable id of the "on start" entry, or empty if none / not yet migrated.</summary>
        public string OnStartEntryId => _onStartEntryId;

        /// <summary>Stable id of the "on stop" entry, or empty if none / not yet migrated.</summary>
        public string OnStopEntryId => _onStopEntryId;

        private void Awake()
        {
            // Best-effort in-memory migration: populate the id fields from the
            // legacy index fields on first load. Editor tooling persists the
            // migration to disk (HapbeatMigrateLegacyReferences); this path
            // covers runtime scenes that haven't been re-saved yet.
            if (_eventMap == null) return;
            if (string.IsNullOrEmpty(_onStartEntryId) && _onStartEntryIndex >= 0)
            {
                var e = _eventMap.GetEntry(_onStartEntryIndex);
                if (e != null) _onStartEntryId = e.id;
            }
            if (string.IsNullOrEmpty(_onStopEntryId) && _onStopEntryIndex >= 0)
            {
                var e = _eventMap.GetEntry(_onStopEntryIndex);
                if (e != null) _onStopEntryId = e.id;
            }
        }

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
                          $"(onStart id='{_onStartEntryId}' idx={_onStartEntryIndex}, " +
                          $"loop id='{_entryId}' idx={_entryIndex}, " +
                          $"onStop id='{_onStopEntryId}' idx={_onStopEntryIndex})", this);
            PlayOneShot(_onStartEntryId, _onStartEntryIndex, "start");
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
            PlayOneShot(_onStopEntryId, _onStopEntryIndex, "end");
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
            PlayOneShot(_onStartEntryId, _onStartEntryIndex, "start", gainMultiplier);
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
            if (_startShotDelay >= 0f) return _startShotDelay;
            if (_eventMap == null) return 0f;
            var entry = ResolveSequenceEntry(_onStartEntryId, _onStartEntryIndex);
            if (entry == null) return 0f;
            if (entry.mode == HapticMode.Command) return 0f;
            if (entry.streamClip == null) return 0f;
            return entry.streamClip.length;
        }

        /// <summary>
        /// Resolve an On Start / On Stop entry by id (authoritative) with
        /// <paramref name="legacyIndex"/> as a fallback when the id is empty.
        /// Mirrors <see cref="HapbeatTriggerBase.ResolveEntry"/>. Returns null
        /// when the phase is (none) (index &lt; 0 and id empty).
        /// </summary>
        private HapbeatEventEntry ResolveSequenceEntry(string id, int legacyIndex)
        {
            if (_eventMap == null) return null;
            if (!string.IsNullOrEmpty(id))
                return _eventMap.FindById(id);
            if (legacyIndex < 0) return null;
            return _eventMap.GetEntry(legacyIndex);
        }

        private void OnDisable()
        {
            // Don't leave a coroutine dangling past component/object teardown.
            CancelPendingLoop();
        }

        private void PlayOneShot(string entryId, int legacyIndex, string phase, float gainMultiplier = 1f)
        {
            // "(none)" — both id empty AND legacy index -1 means the user deliberately
            // left this phase unset.
            if (string.IsNullOrEmpty(entryId) && legacyIndex < 0) return;
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = ResolveSequenceEntry(entryId, legacyIndex);
            if (entry == null)
            {
                if (_verboseLog)
                    Debug.LogWarning(
                        $"[Hapbeat] Sequence {phase}-shot: entry not found on {name} " +
                        $"(id='{entryId}', idx={legacyIndex})", this);
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
            }
        }
    }
}
