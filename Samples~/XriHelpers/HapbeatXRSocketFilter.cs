// Sample component — requires Unity XR Interaction Toolkit.
// Latched wrapper around XRSocketInteractor's hover events.
//
// Problem:
//   XRSocketInteractor has `hoverEntered` but not `firstHoverEntered`, and
//   when a selected interactable is removed from the socket, XRI sends:
//     selectExited → hoverEntered (because the object is still in range)
//   That second hoverEntered re-fires the snap sound even though it's not
//   a fresh approach — the user is pulling the object OUT, not snapping in.
//
//   This component latches on the first real hover and only unlatches once
//   the socket is fully idle again (neither hovering nor selecting anything).
//   Post-deselect re-hovers are absorbed silently.
//
// Usage:
//   1. Import the "XR Helpers" sample from the Package Manager (one-time).
//   2. Attach HapbeatXRSocketFilter to the same GameObject as the
//      XRSocketInteractor.
//   3. Wire HapbeatXRSocketFilter.OnInitialHoverEntered → the "snap" feedback
//      (e.g. HapbeatUnityEventTrigger.Fire + shape.SequenceTrigger.Stop).
//   4. Clear any direct wire on XRSocketInteractor.hoverEntered so it
//      doesn't fire twice.
//
// No XRI dependency leaks into the Runtime asmdef — this file lives under
// Samples~/ and is only compiled after the user imports it.

using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Hapbeat.Samples.XriHelpers
{
    /// <summary>
    /// Latched hover filter for <see cref="XRSocketInteractor"/>. Fires
    /// <see cref="OnInitialHoverEntered"/> once per engagement session and
    /// suppresses re-fires caused by select → deselect transitions.
    /// </summary>
    [RequireComponent(typeof(XRSocketInteractor))]
    [AddComponentMenu("Hapbeat/Samples/Hapbeat XR Socket Filter (latched hover)")]
    public class HapbeatXRSocketFilter : MonoBehaviour
    {
        [Header("Events")]
        [Tooltip("Fires once when an interactable first enters the socket's hover " +
                 "range. Does NOT re-fire on post-deselect re-hovers (that's the " +
                 "whole point of this component). Wire the snap sound here.")]
        public UnityEvent OnInitialHoverEntered;

        [Tooltip("Fires once when the interactable has fully disengaged from the " +
                 "socket (no longer hovered AND no longer selected). Use for " +
                 "cleanup / 'exited range' feedback.")]
        public UnityEvent OnFinalHoverExited;

        private XRSocketInteractor _socket;
        private bool _latched;
        // Whether a deferred idle check is already queued, so we don't stack
        // coroutines on top of each other (each hoverExited / selectExited
        // schedules one tick — dedupe to a single check per frame).
        private bool _idleCheckPending;

        private void OnEnable()
        {
            _socket = GetComponent<XRSocketInteractor>();
            _socket.hoverEntered.AddListener(OnHoverEntered);
            _socket.hoverExited.AddListener(OnHoverExited);
            _socket.selectExited.AddListener(OnSelectExited);
        }

        private void OnDisable()
        {
            if (_socket == null) return;
            _socket.hoverEntered.RemoveListener(OnHoverEntered);
            _socket.hoverExited.RemoveListener(OnHoverExited);
            _socket.selectExited.RemoveListener(OnSelectExited);
        }

        private void OnHoverEntered(HoverEnterEventArgs args)
        {
            // Latch on the very first engagement and fire. Subsequent
            // hoverEntered events (including the re-hover after a select →
            // deselect cycle) are absorbed until we fully disengage.
            if (!_latched)
            {
                _latched = true;
                OnInitialHoverEntered?.Invoke();
            }
        }

        private void OnHoverExited(HoverExitEventArgs args) => ScheduleIdleCheck();
        private void OnSelectExited(SelectExitEventArgs args) => ScheduleIdleCheck();

        /// <summary>
        /// Queue an "is the socket fully idle?" check for the end of the
        /// frame. Defers past the same-frame hoverExited → selectEntered
        /// transition so we don't unlatch mid-snap.
        /// </summary>
        private void ScheduleIdleCheck()
        {
            if (_idleCheckPending) return;
            _idleCheckPending = true;
            StartCoroutine(CheckIdleNextFrame());
        }

        private IEnumerator CheckIdleNextFrame()
        {
            // Wait one frame so any immediately-following selectEntered (or
            // hoverEntered for post-deselect re-hover) has had a chance to
            // update the socket's state.
            yield return null;
            _idleCheckPending = false;

            if (_socket == null) yield break;
            // Fully idle iff neither hover nor select collections contain anything.
            if (_socket.interactablesHovered.Count != 0) yield break;
            if (_socket.interactablesSelected.Count != 0) yield break;

            if (_latched)
            {
                _latched = false;
                OnFinalHoverExited?.Invoke();
            }
        }
    }
}
