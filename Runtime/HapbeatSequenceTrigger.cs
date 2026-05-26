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
    ///     while the loop is running. Uses the inherited <c>_entryId</c>
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
        // Stable GUID references for On Start / On Stop. Empty = (none).
        [SerializeField, HideInInspector]
        private string _onStartEntryId = "";
        [SerializeField, HideInInspector]
        private string _onStopEntryId = "";

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

        [Tooltip("Delay (seconds) between the Loop stop and the On Stop one-shot.\n" +
                 " 0     = no delay (fire immediately, may collide with loop's flush burst).\n" +
                 " 0.05  = default — safe margin for device to process ring-flush packets.\n" +
                 " 0.10  = extra-safe for Wi-Fi networks under load.\n\n" +
                 "Symmetric to Start Shot Delay, for the end of the sequence:\n" +
                 "after Loop stop the device receives STREAM_END + STREAM_BEGIN + STREAM_END\n" +
                 "(ring-flush pair) in rapid succession. Firing the On Stop one-shot\n" +
                 "immediately means its STREAM_BEGIN (StreamClip) or PLAY (Command) arrives\n" +
                 "while the device is still processing those packets, causing:\n" +
                 "  - StreamClip: shot mixed with loop residual → strength varies per trial\n" +
                 "  - Command:    PLAY occasionally dropped → shot silently missing\n\n" +
                 "0.05 default chosen empirically as a sweet spot — barely perceptible as a\n" +
                 "gap (≈1 audio chunk) but sufficient to fully isolate the two packet bursts.")]
        [SerializeField, Range(0f, 0.5f)]
        private float _stopShotDelay = 0.05f;

        [Tooltip("Ignore duplicate Fire()/Stop() calls that arrive while the sequence " +
                 "is already in the matching state.\n\n" +
                 "Why this matters (XRI specific):\n" +
                 "XRGrabInteractable.selectEntered fires for EVERY interactor that selects " +
                 "— so when a shape is put into a socket, selectEntered fires a second time " +
                 "(from the socket) and the trigger would re-fire On Start + re-start Loop, " +
                 "clobbering any socket-side snap feedback.\n\n" +
                 "Leave ON (default) and wire to selectEntered / selectExited OR to " +
                 "firstSelectEntered / lastSelectExited — either wiring behaves correctly.\n\n" +
                 "Turn OFF only if you deliberately want Fire() during Loop to re-fire the " +
                 "On Start one-shot (uncommon).")]
        [SerializeField]
        private bool _guardReentry = true;

        [Tooltip("Seconds after a Stop() during which incoming Fire() calls are ignored.\n\n" +
                 "Use this for socket-attach scenarios where XRI fires a chain of " +
                 "selectExited(hand) + selectEntered(socket) back-to-back, which would " +
                 "otherwise cause Stop → Fire and restart the hold loop. With a small " +
                 "cooldown (e.g. 0.15) the re-fire from the socket selection is absorbed, " +
                 "keeping the sequence quiet until the user explicitly grabs again.\n\n" +
                 "0 = disabled (default). Typical SnapFit setting: 0.15 \u2013 0.25.")]
        [SerializeField, Range(0f, 1f)]
        private float _postStopFireCooldown = 0f;

        // True between Fire() and Stop(). Fire() while true is ignored when
        // _guardReentry is on; Stop() while false is ignored likewise.
        private bool _isActive;
        // Timestamp of the last Stop() that actually took effect. Used by
        // _postStopFireCooldown to swallow the XRI-generated re-Fire that
        // arrives atomically when a held object is dropped into a socket.
        private float _lastStopTime = float.NegativeInfinity;

        // Handle to a pending delayed FireHaptic, so Stop() can cancel it if the
        // user released before the delay elapsed (i.e. Loop never started).
        private Coroutine _pendingLoopStart;

        // Handle to a pending On Stop one-shot scheduled by _stopShotDelay, so a
        // re-Fire() arriving inside the delay window can cancel it (the user
        // already moved on to a new hold; firing the stale stop-shot would be
        // wrong).
        private Coroutine _pendingStopShot;

        /// <summary>Stable id of the "on start" entry, or empty if (none).</summary>
        public string OnStartEntryId => _onStartEntryId;

        /// <summary>Stable id of the "on stop" entry, or empty if (none).</summary>
        public string OnStopEntryId => _onStopEntryId;

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
        /// Delay (seconds) between the Loop stop and the On Stop one-shot.
        /// 0 = fire immediately. See serialized field tooltip for the
        /// device packet-burst rationale.
        /// </summary>
        public float StopShotDelay
        {
            get => _stopShotDelay;
            set => _stopShotDelay = Mathf.Max(0f, value);
        }

        /// <summary>
        /// Plays the "on start" one-shot (if set) and starts the loop. Wire to
        /// e.g. <c>XRGrabInteractable.firstSelectEntered</c>.
        /// </summary>
        public void Fire()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.Fire() invoked on {name} " +
                          $"(onStart='{_onStartEntryId}', loop='{_entryId}', " +
                          $"onStop='{_onStopEntryId}', active={_isActive})", this);

            // Idempotent re-entry guard. See _guardReentry tooltip — XRI fires
            // selectEntered per-interactor, so a second Fire() often arrives
            // during the hold phase (e.g. when an object is attached to a
            // socket). Without this guard the On Start one-shot would re-fire
            // mid-hold, clobbering socket-side snap feedback and causing
            // double haptics.
            if (_guardReentry && _isActive)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] SequenceTrigger.Fire ignored: already active on {name} " +
                              "(second Fire during hold — likely from a secondary interactor). " +
                              "Disable Guard Re-entry on the trigger if you want re-fires to re-trigger On Start.", this);
                return;
            }
            // Post-Stop cooldown: absorb the XRI selectEntered(socket) that
            // typically follows selectExited(hand) within the same frame when
            // an object is dropped into a socket. Without this the sequence
            // Stops on hand-release then immediately Fires on socket-select,
            // restarting the hold loop. 0 = disabled (default).
            if (_postStopFireCooldown > 0f && Time.unscaledTime - _lastStopTime < _postStopFireCooldown)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] SequenceTrigger.Fire ignored: within {_postStopFireCooldown:F2}s post-Stop cooldown on {name} " +
                              "(likely the XRI hand-to-socket transfer re-fire — SnapFit workaround).", this);
                return;
            }

            _isActive = true;
            // If a previous Stop scheduled an On Stop one-shot via _stopShotDelay
            // and we re-Fire before the delay elapsed, drop the pending stop-shot —
            // firing it now would mean "user moved on but the old shot still plays
            // on top of the new sequence".
            CancelPendingStopShot();
            PlayOneShot(_onStartEntryId, "start");
            StartLoopAfterDelay();
        }

        /// <summary>
        /// Stops the loop and plays the "on stop" one-shot (if set). Wire to
        /// e.g. <c>XRGrabInteractable.lastSelectExited</c>.
        /// </summary>
        public void Stop()
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.Stop() invoked on {name} (active={_isActive})", this);

            // Always refresh the post-Stop cooldown anchor on EVERY Stop()
            // call, even ones that will be guarded out below. Reasoning:
            // in a SnapFit flow the shape receives two Stop calls in quick
            // succession — first from socket.hoverEntered (drops the loop),
            // second from lastSelectExited(hand) (ignored because already
            // inactive). If we only updated _lastStopTime inside the
            // "actually ran" path, the second Fire (from socket.selectEntered)
            // would compare against the hover-time Stop which might already
            // be past the cooldown window for a long-held object. Refreshing
            // unconditionally keeps the cooldown anchored to the most recent
            // "the user wants this stopped" signal.
            _lastStopTime = Time.unscaledTime;

            // Idempotent guard: ignore the Stop() body when we're not in the
            // hold phase. Prevents firing an on-stop one-shot when no loop
            // is running in the first place.
            if (_guardReentry && !_isActive)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] SequenceTrigger.Stop ignored: not active on {name} (cooldown refreshed)", this);
                return;
            }

            _isActive = false;
            CancelPendingLoop();
            CancelPendingStopShot(); // defensive: drop any stale stop-shot from a prior Stop

            // STREAM_END + ring-flush packets go out of StopHaptic. The On Stop
            // one-shot is scheduled after _stopShotDelay so its STREAM_BEGIN /
            // PLAY doesn't collide with the loop's flush burst on the device.
            // See _stopShotDelay tooltip for the symptom matrix.
            StopHaptic(); // inherited — stops the loop (sends STREAM_END + flush)

            if (_stopShotDelay > 0f && !string.IsNullOrEmpty(_onStopEntryId))
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] Sequence: delaying On Stop one-shot by {_stopShotDelay:F3}s on {name} " +
                              "(workaround for loop-stop flush burst vs. shot collision)", this);
                _pendingStopShot = StartCoroutine(StopShotCoroutine(_stopShotDelay));
            }
            else
            {
                PlayOneShot(_onStopEntryId, "end");
            }
        }

        /// <summary>
        /// Convenience overload accepting a gain multiplier (e.g. for impact
        /// velocity). Multiplies the configured entry gain on the one-shot only;
        /// the loop uses the entry's own gain.
        /// </summary>
        public void FireWithStartGain(float gainMultiplier)
        {
            if (_verboseLog)
                Debug.Log($"[Hapbeat] SequenceTrigger.FireWithStartGain({gainMultiplier:F2}) invoked on {name} (active={_isActive})", this);
            if (_guardReentry && _isActive)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] SequenceTrigger.FireWithStartGain ignored: already active on {name}", this);
                return;
            }
            if (_postStopFireCooldown > 0f && Time.unscaledTime - _lastStopTime < _postStopFireCooldown)
            {
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] SequenceTrigger.FireWithStartGain ignored: post-Stop cooldown on {name}", this);
                return;
            }
            _isActive = true;
            PlayOneShot(_onStartEntryId, "start", gainMultiplier);
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
        /// Symmetric counterpart to <see cref="CancelPendingLoop"/> — invoked
        /// when a fresh Fire() arrives during the <see cref="_stopShotDelay"/>
        /// window so the now-stale stop-shot doesn't fire on top of the new
        /// hold. Also called from <see cref="OnDisable"/> so the coroutine
        /// doesn't leak past component teardown.
        /// </summary>
        private void CancelPendingStopShot()
        {
            if (_pendingStopShot != null)
            {
                StopCoroutine(_pendingStopShot);
                _pendingStopShot = null;
                if (_verboseLog)
                    Debug.Log($"[Hapbeat] Sequence: cancelled pending On Stop one-shot on {name} " +
                              "(Fire re-engaged within the stop-shot delay window)", this);
            }
        }

        private IEnumerator StopShotCoroutine(float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _pendingStopShot = null;
            if (HapbeatManager.Instance == null) yield break;
            PlayOneShot(_onStopEntryId, "end");
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
            var entry = ResolveSequenceEntry(_onStartEntryId);
            if (entry == null) return 0f;
            if (entry.mode == HapticMode.Command) return 0f;
            if (entry.streamClip == null) return 0f;
            return entry.streamClip.length;
        }

        /// <summary>
        /// Resolve an On Start / On Stop entry by stable GUID. Returns null when
        /// the phase is (none), the id is stale, or the EventMap is unassigned.
        /// </summary>
        private HapbeatEventEntry ResolveSequenceEntry(string id)
        {
            if (_eventMap == null) return null;
            if (string.IsNullOrEmpty(id)) return null;
            return _eventMap.FindById(id);
        }

        protected override void OnDisable()
        {
            base.OnDisable();  // unsubscribe + flush pending haptic-delay coroutines
            // Don't leave the internal LoopStartCoroutine / StopShotCoroutine
            // dangling past component/object teardown (they're tracked
            // separately from the haptic-delay coroutines that base.OnDisable
            // handles).
            CancelPendingLoop();
            CancelPendingStopShot();
            // Also reset active state so re-enabling doesn't leave us stuck
            // thinking we're mid-hold.
            _isActive = false;
        }

        private void PlayOneShot(string entryId, string phase, float gainMultiplier = 1f)
        {
            // "(none)" — empty id means the user deliberately left this phase unset.
            if (string.IsNullOrEmpty(entryId)) return;
            if (!_triggerEnabled) return;
            if (_eventMap == null) return;

            var entry = ResolveSequenceEntry(entryId);
            if (entry == null)
            {
                if (_verboseLog)
                    Debug.LogWarning(
                        $"[Hapbeat] Sequence {phase}-shot: entry not found on {name} " +
                        $"(id='{entryId}')", this);
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
            // Per-trigger _gainMultiplier composes on top of both, mirroring
            // the policy in HapbeatTriggerBase.FireHaptic.
            float gain = entry.GetEffectiveGain() * gainMultiplier * _gainMultiplier;

            if (_verboseLog)
                Debug.Log($"[Hapbeat] Sequence {phase}-shot: '{label}' mode={entry.mode} gain={gain:F2}", this);

            // Latency compensation (start / stop one-shots). Loop phase delegates
            // to inherited FireHaptic which already applies the same offset.
            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f)
            {
                StartHapticDelayCoroutine(PlayOneShotAfterDelay(entry, gain, label, target, phase, delay));
                return;
            }
            DispatchOneShot(entry, gain, label, target, phase);
        }

        private IEnumerator PlayOneShotAfterDelay(
            HapbeatEventEntry entry, float gain, string label, string target, string phase, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (!_triggerEnabled || HapbeatManager.Instance == null) yield break;
            DispatchOneShot(entry, gain, label, target, phase);
        }

        private void DispatchOneShot(
            HapbeatEventEntry entry, float gain, string label, string target, string phase)
        {
            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        if (_verboseLog) Debug.Log($"[Hapbeat] Sequence {phase}-shot: Command mode but eventId is empty on entry '{label}'", this);
                        return;
                    }
                    HapbeatManager.Instance.Play(entry.eventId, gain, label, target);
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
