// Sample component — requires Unity XR Interaction Toolkit.
// Copy this file into your project if you hit the "socket transfer re-fires
// SequenceTrigger" problem described in the SnapFit troubleshooting notes.
//
// Why this exists:
//   XRGrabInteractable.selectEntered fires for EVERY interactor (hand AND
//   socket). SnapFit scenarios end up firing shape.SequenceTrigger.Fire
//   twice — once from the hand grab, once from the socket select — which
//   clobbers socket-side snap feedback and restarts the hold loop.
//
//   HapbeatSequenceTrigger's Post-Stop cooldown solves the "drop-into-socket"
//   direction, but the symmetric "grab-out-of-socket" direction also hits
//   the cooldown and breaks re-grab. This script routes select events
//   through type-specific UnityEvents so designers can wire:
//     OnHandSelected   → HapbeatSequenceTrigger.Fire
//     OnHandReleased   → HapbeatSequenceTrigger.Stop
//     OnSocketSelected → HapbeatSequenceTrigger.Stop  (or nothing)
//     OnSocketReleased → HapbeatSequenceTrigger.Fire  (optional)
//
//   and the SDK never has to guess which side a Fire came from.
//
// Usage:
//   1. Attach to the same GameObject as the XRGrabInteractable.
//   2. Wire the component's UnityEvents in the Inspector.
//   3. Clear the XRGrabInteractable.selectEntered / selectExited events so
//      they don't fire HapbeatSequenceTrigger directly — let this filter
//      route them.
//
// Note: this is NOT part of the core SDK (no XRI dependency in Runtime).
// It's provided as a sample for projects that use XRI + Socket interactors.
#if HAPBEAT_XRI
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Hapbeat.Samples.XriHelpers
{
    /// <summary>
    /// Routes <see cref="XRGrabInteractable"/> select events into four
    /// UnityEvents split by interactor kind (hand vs socket). Drop into a
    /// scene when SnapFit wiring needs to distinguish hand grabs from socket
    /// attachments — see file header for rationale.
    /// </summary>
    [RequireComponent(typeof(XRGrabInteractable))]
    [AddComponentMenu("Hapbeat/Samples/XR Grab Filter (hand vs socket)")]
    public class HapbeatXRGrabFilter : MonoBehaviour
    {
        [Header("Hand-side events (non-socket interactors)")]
        public UnityEvent OnHandSelected;
        public UnityEvent OnHandReleased;

        [Header("Socket-side events (XRSocketInteractor)")]
        public UnityEvent OnSocketSelected;
        public UnityEvent OnSocketReleased;

        private XRGrabInteractable _interactable;

        private void OnEnable()
        {
            _interactable = GetComponent<XRGrabInteractable>();
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
        }

        private void OnDisable()
        {
            if (_interactable == null) return;
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
        }

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor)
                OnSocketSelected?.Invoke();
            else
                OnHandSelected?.Invoke();
        }

        private void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject is XRSocketInteractor)
                OnSocketReleased?.Invoke();
            else
                OnHandReleased?.Invoke();
        }
    }
}
#endif
