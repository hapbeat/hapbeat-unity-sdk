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

        // Display cache for the enter entry. -1 = none.
        [SerializeField, HideInInspector]
        private int _entryIndexOnEnter = -1;

        // Stable GUID of the entry fired on OnStateExit. Empty = don't fire on exit.
        [SerializeField, HideInInspector]
        private string _entryIdOnExit;

        // Display cache for the exit entry. -1 = none.
        [SerializeField, HideInInspector]
        private int _entryIndexOnExit = -1;

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
            if (string.IsNullOrEmpty(_entryIdOnEnter) && _entryIndexOnEnter < 0)
            {
                // No enter event configured — nothing to do, but still fall through
                // to the Required Previous State log path if verbose.
                if (_verboseLog)
                    Debug.Log($"[HapbeatState] {animator.gameObject.name}: OnStateEnter — no entry configured (skipped)", animator);
                return;
            }

            if (!string.IsNullOrEmpty(_requiredPreviousState))
            {
                // Unity の API には GetPreviousAnimatorStateInfo は無いため、
                // transition 中であるかを確認した上で、source state を
                // GetCurrentAnimatorStateInfo (transition 中は source を返す仕様) で取る。
                // - 通常の遷移 (A → B) の OnStateEnter(B) では IsInTransition = true、
                //   GetCurrentAnimatorStateInfo = A、GetNextAnimatorStateInfo = B となる。
                // - シーン開始直後の初期 state 入場 (Entry → Default) では IsInTransition
                //   = false で source state を取得できない。この場合は Required Previous
                //   が満たせない扱いとし、fire しない (空文字なら今のブロックに入らない)。
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

            var entry = ResolveEntry(_entryIdOnEnter, _entryIndexOnEnter);
            if (entry == null) return;
            _enterPlayback = FireEntry(animator, entry, "Enter");
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            // Always stop any stream started on enter, even if no exit event is configured.
            if (_enterPlayback != null)
            {
                if (_verboseLog)
                    Debug.Log($"[HapbeatState] {animator.gameObject.name}: stopping enter playback on exit", animator);
                _enterPlayback.Stop();
                _enterPlayback = null;
            }

            if (string.IsNullOrEmpty(_entryIdOnExit) && _entryIndexOnExit < 0)
                return;

            var entry = ResolveEntry(_entryIdOnExit, _entryIndexOnExit);
            if (entry == null) return;
            FireEntry(animator, entry, "Exit");
        }

        // --- Internal ---

        /// <summary>
        /// Resolve an entry, preferring stable GUID, falling back to the index cache.
        /// </summary>
        private HapbeatEventEntry ResolveEntry(string id, int index)
        {
            if (_eventMap == null) return null;
            if (!string.IsNullOrEmpty(id))
            {
                var byId = _eventMap.FindById(id);
                if (byId != null) return byId;
            }
            if (index >= 0 && index < _eventMap.entries.Count)
                return _eventMap.GetEntry(index);
            return null;
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
