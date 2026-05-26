using System.Collections;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// State-machine-native haptic trigger: attached directly to an Animator
    /// state in the AnimatorController asset, fires a haptic event on
    /// <see cref="OnStateEnter"/> and/or <see cref="OnStateExit"/>.
    ///
    /// <para>
    /// This is the recommended Animator-integration path in the SDK. Unlike a
    /// scene-side <c>MonoBehaviour</c>-based trigger that polls Animator
    /// parameters, this behaviour is invoked by the Animator runtime exactly
    /// when the state is entered / exited, so the event ⇔ state correspondence
    /// is 1:1 and visible inside the Animator window where designers work.
    /// </para>
    ///
    /// <para>
    /// <b>Per-instance lifecycle:</b> Unity creates a fresh runtime copy of every
    /// StateMachineBehaviour for each Animator that plays the controller, so
    /// per-instance fields like <see cref="_enterPlayback"/> are safe (one Animator
    /// instance — one playback handle).
    /// </para>
    ///
    /// <para>
    /// <b>StreamClip Stop semantics:</b> when the enter event fires a looping
    /// StreamClip, the resulting playback handle is stored. On <see cref="OnStateExit"/>
    /// the handle is automatically stopped before any exit event fires, so a
    /// looping creak/hum tied to a state ends cleanly when the state is left.
    /// </para>
    ///
    /// <para>
    /// Reference fields (<see cref="_eventMap"/>, <see cref="_entryIdOnEnter"/>,
    /// <see cref="_entryIdOnExit"/>) live on the AnimatorController asset, not the
    /// scene. EventMap is a ScriptableObject so the asset → asset reference is
    /// straightforward; scene GameObject references would not work here.
    /// </para>
    /// </summary>
    public class HapbeatStateBehaviour : StateMachineBehaviour
    {
        [Header("Hapbeat Event")]
        [Tooltip("The event map asset containing haptic event definitions.")]
        [SerializeField]
        private HapbeatEventMap _eventMap;

        // Stable GUID of the entry fired on OnStateEnter. Empty = don't fire on enter.
        [SerializeField, HideInInspector]
        private string _entryIdOnEnter;

        // Stable GUID of the entry fired on OnStateExit. Empty = don't fire on exit.
        [SerializeField, HideInInspector]
        private string _entryIdOnExit;

        [Header("Transition Filter")]
        [Tooltip("Optional. If non-empty, OnStateEnter fires only when the " +
                 "previous state matches this name (exact match on state name, " +
                 "not full path). Use to bind haptics to specific A→B transitions, " +
                 "e.g. fire 'rattle' only on Closed→LockedRattle, not Open→LockedRattle.\n" +
                 "Leave empty to fire on enter regardless of source.")]
        [SerializeField]
        private string _requiredPreviousState = "";

        [Header("Gain")]
        [Tooltip("Per-behaviour gain multiplier applied on top of entry.gain × manifest.intensity.\n" +
                 "1.0 = use entry default. Range [0, 2].")]
        [SerializeField, Range(0f, 2f)]
        private float _gainMultiplier = 1f;

        [Header("Diagnostics")]
        [Tooltip("Log every Enter/Exit fire and all early-return reasons to the Unity console.")]
        [SerializeField]
        private bool _verboseLog = false;

        // Per-Animator instance handle to the currently-playing StreamClip from
        // the enter event (if any). Cleared on Exit so the stream stops cleanly.
        private HapbeatStreamPlayback _enterPlayback;

        // Pending fire coroutines created when effectiveDelay > 0. Tracked so
        // OnStateExit can cancel a still-undelivered Enter fire when the state
        // was left before the delay elapsed (preserves Fire→Stop integrity).
        // Coroutines run on HapbeatManager (singleton MonoBehaviour) since
        // StateMachineBehaviour itself cannot host coroutines.
        private Coroutine _pendingEnterFire;
        private Coroutine _pendingExitFire;

        // --- Public read-only accessors (parity with HapbeatTriggerBase) ---

        /// <summary>The event map referenced by this behaviour.</summary>
        public HapbeatEventMap EventMap => _eventMap;

        /// <summary>Stable GUID of the entry fired on OnStateEnter (or empty).</summary>
        public string EntryIdOnEnter => _entryIdOnEnter;

        /// <summary>Stable GUID of the entry fired on OnStateExit (or empty).</summary>
        public string EntryIdOnExit => _entryIdOnExit;

        /// <summary>Required previous-state name for OnStateEnter (or empty = any).</summary>
        public string RequiredPreviousState => _requiredPreviousState;

        /// <summary>Per-behaviour gain multiplier.</summary>
        public float GainMultiplier
        {
            get => _gainMultiplier;
            set => _gainMultiplier = Mathf.Clamp(value, 0f, 2f);
        }

        // --- StateMachineBehaviour callbacks ---

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (string.IsNullOrEmpty(_entryIdOnEnter))
            {
                // No enter event configured — nothing to do, but still fall through
                // to the Required Previous State log path if verbose.
                if (_verboseLog)
                    Debug.Log($"[HapbeatState] {animator.gameObject.name}: OnStateEnter — no entry configured (skipped)", animator);
                return;
            }

            if (!string.IsNullOrEmpty(_requiredPreviousState))
            {
                // Unity has no GetPreviousAnimatorStateInfo, so we check
                // whether the animator is in a transition and read the source
                // state via GetCurrentAnimatorStateInfo (during a transition
                // it returns the source state).
                // - Normal transition A → B: OnStateEnter(B) has IsInTransition = true,
                //   GetCurrentAnimatorStateInfo = A, GetNextAnimatorStateInfo = B.
                // - Initial state entry at scene start (Entry → Default): IsInTransition
                //   = false and the source state cannot be read. In that case we
                //   treat Required Previous as unsatisfied and don't fire (empty
                //   string skips this whole block).
                if (!animator.IsInTransition(layerIndex))
                {
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] {animator.gameObject.name}: OnStateEnter skipped " +
                                  $"(no active transition — initial state entry; required previous " +
                                  $"'{_requiredPreviousState}' cannot be verified)", animator);
                    return;
                }
                var prev = animator.GetCurrentAnimatorStateInfo(layerIndex);
                if (!prev.IsName(_requiredPreviousState))
                {
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] {animator.gameObject.name}: OnStateEnter skipped " +
                                  $"(previous state did not match '{_requiredPreviousState}')", animator);
                    return;
                }
            }

            var entry = ResolveEntry(_entryIdOnEnter);
            if (entry == null) return;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f && HapbeatManager.Instance != null)
            {
                // Cancel any stale pending Enter from a previous re-entry.
                if (_pendingEnterFire != null)
                {
                    HapbeatManager.Instance.StopCoroutine(_pendingEnterFire);
                    _pendingEnterFire = null;
                }
                if (_verboseLog)
                    Debug.Log($"[HapbeatState] {animator.gameObject.name}: Enter deferred by " +
                              $"{delay * 1000f:F0}ms (global={HapbeatManager.Instance.HapticDelaySeconds:F3}s + " +
                              $"entry.offset={entry.delayOffsetSeconds:F3}s)", animator);
                _pendingEnterFire = HapbeatManager.Instance.StartTrackedHapticDelay(EnterAfterDelay(animator, entry, delay));
                return;
            }

            _enterPlayback = FireEntry(animator, entry, "Enter");
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            // Resolve enter entry up-front so we can use its delay for the
            // stop-of-enter-playback coroutine (keeps the Enter playback's
            // audio-haptic alignment intact on exit).
            HapbeatEventEntry enterEntry = null;
            if (!string.IsNullOrEmpty(_entryIdOnEnter))
                enterEntry = ResolveEntry(_entryIdOnEnter);
            float enterDelay = ComputeEffectiveDelaySeconds(enterEntry);

            // Pending Enter that hasn't fired yet → **leave it pending**. The
            // haptic should fire even if the Animator state was briefer than
            // the configured delay, because the audio it's compensating against
            // is also in-flight and will be heard. Cancelling here would cause
            // the user to hear the audio but feel no haptic — exactly the
            // mismatch the delay system is trying to prevent (the classic
            // "lock state was 0.1s long, but global delay was 0.2s" case).
            //
            // Edge case (acceptable limitation): looping StreamClip Enter whose
            // state ended before the delay elapsed → the loop will start after
            // the delay with no matching Stop, resulting in an orphan loop.
            // Avoid pairing brief Animator states with looping StreamClip when
            // global delay > state duration. Non-loop entries (Command +
            // StreamClip one-shot) end naturally and have no orphan risk.
            _pendingEnterFire = null;  // clear local tracking; coroutine still runs to completion

            // Active playback from a previously-fired Enter → stop it. If a
            // delay is configured, defer the stop by the same amount so the
            // perceived loop duration matches the Fire→Stop call interval.
            if (_enterPlayback != null)
            {
                var playbackToStop = _enterPlayback;
                _enterPlayback = null;
                if (enterDelay > 0f && HapbeatManager.Instance != null)
                {
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] {animator.gameObject.name}: stopping enter playback " +
                                  $"deferred by {enterDelay * 1000f:F0}ms", animator);
                    HapbeatManager.Instance.StartTrackedHapticDelay(StopPlaybackAfterDelay(playbackToStop, enterDelay));
                }
                else
                {
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] {animator.gameObject.name}: stopping enter playback on exit", animator);
                    playbackToStop.Stop();
                }
            }

            if (string.IsNullOrEmpty(_entryIdOnExit))
                return;

            var entry = ResolveEntry(_entryIdOnExit);
            if (entry == null) return;

            float delay = ComputeEffectiveDelaySeconds(entry);
            if (delay > 0f && HapbeatManager.Instance != null)
            {
                if (_pendingExitFire != null)
                {
                    HapbeatManager.Instance.StopCoroutine(_pendingExitFire);
                    _pendingExitFire = null;
                }
                if (_verboseLog)
                    Debug.Log($"[HapbeatState] {animator.gameObject.name}: Exit deferred by " +
                              $"{delay * 1000f:F0}ms", animator);
                _pendingExitFire = HapbeatManager.Instance.StartTrackedHapticDelay(ExitAfterDelay(animator, entry, delay));
                return;
            }

            FireEntry(animator, entry, "Exit");
        }

        // --- Delay helpers ---

        /// <summary>
        /// Effective fire-time deferral for an entry (mirrors
        /// <see cref="HapbeatTriggerBase.ComputeEffectiveDelaySeconds"/>).
        /// </summary>
        private static float ComputeEffectiveDelaySeconds(HapbeatEventEntry entry)
        {
            float global = HapbeatManager.Instance != null
                ? HapbeatManager.Instance.HapticDelaySeconds
                : 0f;
            float offset = entry != null ? entry.delayOffsetSeconds : 0f;
            return Mathf.Max(0f, global + offset);
        }

        private IEnumerator EnterAfterDelay(Animator animator, HapbeatEventEntry entry, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _pendingEnterFire = null;
            // Animator may have been destroyed while we were waiting.
            if (animator == null) yield break;
            if (HapbeatManager.Instance == null) yield break;
            _enterPlayback = FireEntry(animator, entry, "Enter");
        }

        private IEnumerator ExitAfterDelay(Animator animator, HapbeatEventEntry entry, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            _pendingExitFire = null;
            if (animator == null) yield break;
            if (HapbeatManager.Instance == null) yield break;
            FireEntry(animator, entry, "Exit");
        }

        private static IEnumerator StopPlaybackAfterDelay(HapbeatStreamPlayback playback, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (playback != null && !playback.IsStopped)
                playback.Stop();
        }

        // --- Internal ---

        /// <summary>Resolve an entry by stable GUID. Returns null when id is empty or stale.</summary>
        private HapbeatEventEntry ResolveEntry(string id)
        {
            if (_eventMap == null) return null;
            if (string.IsNullOrEmpty(id)) return null;
            return _eventMap.FindById(id);
        }

        /// <summary>
        /// Send the entry's event through the singleton <see cref="HapbeatManager"/>.
        /// Returns the playback handle when the entry is a StreamClip, else null.
        /// </summary>
        private HapbeatStreamPlayback FireEntry(Animator animator, HapbeatEventEntry entry, string phase)
        {
            if (HapbeatManager.Instance == null)
            {
                if (_verboseLog)
                    Debug.LogWarning(
                        $"[HapbeatState] {animator.gameObject.name}: Fire {phase} rejected — " +
                        "no HapbeatManager in scene.", animator);
                return null;
            }

            string baseName = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            string label = $"{animator.gameObject.name}/state {phase}: {baseName}";
            string target = entry.HasTarget ? entry.target : null;

            switch (entry.mode)
            {
                case HapticMode.Command:
                {
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        if (_verboseLog)
                            Debug.Log($"[HapbeatState] {label}: Command mode but eventId is empty (skipped)", animator);
                        return null;
                    }
                    float commandGain = entry.GetEffectiveGain() * _gainMultiplier;
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] Fire Command ({phase}): eventId='{entry.eventId}' " +
                                  $"target='{target ?? "(broadcast)"}' gain={commandGain:F2}", animator);
                    HapbeatManager.Instance.Play(entry.eventId, commandGain, label, target);
                    return null;
                }

                case HapticMode.StreamClip:
                {
                    if (entry.streamClip == null)
                    {
                        if (_verboseLog)
                            Debug.Log($"[HapbeatState] {label}: StreamClip mode but streamClip is null (skipped)", animator);
                        return null;
                    }
                    float streamGain = entry.GetEffectiveGain() * _gainMultiplier;
                    if (_verboseLog)
                        Debug.Log($"[HapbeatState] Fire StreamClip ({phase}): clip='{entry.streamClip.name}' " +
                                  $"target='{target ?? "(broadcast)"}' gain={streamGain:F2} loop={entry.loop}", animator);
                    return HapbeatManager.Instance.StreamAudioClip(
                        entry.streamClip, streamGain, streamGain, target, entry.loop);
                }
            }
            return null;
        }
    }
}
