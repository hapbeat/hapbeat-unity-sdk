// Sample component — requires Unity XR Interaction Toolkit.
//
// Splits an interactable's UnityEvents by the hand that caused them. It belongs
// in XR Helpers rather than Runtime so the core SDK remains XRI-independent.

using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Hapbeat.Samples.XriHelpers
{
    /// <summary>
    /// Routes XRI select and hover callbacks to separate left- and right-hand
    /// UnityEvents. <see cref="RouteVoid"/>, <see cref="RouteFloat"/>, and
    /// <see cref="RouteVector2"/> use the most recent hand interaction, which
    /// lets uGUI value-change events retain their hand association.
    /// </summary>
    [AddComponentMenu("Hapbeat/Samples/Hapbeat XR Hand Side Router")]
    public sealed class HapbeatXRHandSideRouter : MonoBehaviour,
        IPointerEnterHandler, IPointerDownHandler, IBeginDragHandler, IDragHandler, IScrollHandler
    {
        [Header("Select")]
        public UnityEvent OnLeftSelectEntered;
        public UnityEvent OnRightSelectEntered;
        public UnityEvent OnLeftSelectExited;
        public UnityEvent OnRightSelectExited;
        public UnityEvent OnLeftFirstSelectEntered;
        public UnityEvent OnRightFirstSelectEntered;
        public UnityEvent OnLeftLastSelectExited;
        public UnityEvent OnRightLastSelectExited;

        [Header("Hover")]
        public UnityEvent OnLeftFirstHoverEntered;
        public UnityEvent OnRightFirstHoverEntered;
        public UnityEvent OnLeftLastHoverExited;
        public UnityEvent OnRightLastHoverExited;

        [Header("Last active hand")]
        public UnityEvent OnLeftRouted;
        public UnityEvent OnRightRouted;
        public UnityEvent<float> OnLeftRoutedFloat;
        public UnityEvent<float> OnRightRoutedFloat;
        public UnityEvent<Vector2> OnLeftRoutedVector2;
        public UnityEvent<Vector2> OnRightRoutedVector2;

        private XRBaseInteractable _interactable;
        private InteractorHandedness _lastHandedness = InteractorHandedness.None;

        private void OnEnable()
        {
            _interactable = GetComponent<XRBaseInteractable>();
            if (_interactable == null)
                return; // uGUI controls receive TrackedDeviceEventData instead of XRI selection events.
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
            _interactable.firstSelectEntered.AddListener(OnFirstSelectEntered);
            _interactable.lastSelectExited.AddListener(OnLastSelectExited);
            _interactable.firstHoverEntered.AddListener(OnFirstHoverEntered);
            _interactable.lastHoverExited.AddListener(OnLastHoverExited);
        }

        private void OnDisable()
        {
            if (_interactable == null) return;
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
            _interactable.firstSelectEntered.RemoveListener(OnFirstSelectEntered);
            _interactable.lastSelectExited.RemoveListener(OnLastSelectExited);
            _interactable.firstHoverEntered.RemoveListener(OnFirstHoverEntered);
            _interactable.lastHoverExited.RemoveListener(OnLastHoverExited);
        }

        public void RouteVoid()
        {
            if (_lastHandedness == InteractorHandedness.Left) OnLeftRouted?.Invoke();
            if (_lastHandedness == InteractorHandedness.Right) OnRightRouted?.Invoke();
        }

        public void RouteFloat(float value)
        {
            if (_lastHandedness == InteractorHandedness.Left) OnLeftRoutedFloat?.Invoke(value);
            if (_lastHandedness == InteractorHandedness.Right) OnRightRoutedFloat?.Invoke(value);
        }

        public void RouteVector2(Vector2 value)
        {
            if (_lastHandedness == InteractorHandedness.Left) OnLeftRoutedVector2?.Invoke(value);
            if (_lastHandedness == InteractorHandedness.Right) OnRightRoutedVector2?.Invoke(value);
        }

        public void OnPointerEnter(PointerEventData eventData) => RememberPointer(eventData);
        public void OnPointerDown(PointerEventData eventData) => RememberPointer(eventData);
        public void OnBeginDrag(PointerEventData eventData) => RememberPointer(eventData);
        public void OnDrag(PointerEventData eventData) => RememberPointer(eventData);
        public void OnScroll(PointerEventData eventData) => RememberPointer(eventData);

        private void RememberPointer(PointerEventData eventData)
        {
            if (eventData is TrackedDeviceEventData tracked && tracked.interactor is IXRInteractor interactor)
                RememberHand(interactor);
        }

        private void RememberHand(IXRInteractor interactor)
        {
            // Sockets have no hand. Preserve the hand that placed the object so
            // the socket's snap callback still reaches that hand's EventMap.
            if (interactor != null && interactor.handedness != InteractorHandedness.None)
                _lastHandedness = interactor.handedness;
        }

        private void OnSelectEntered(SelectEnterEventArgs args) => InvokeFor(args.interactorObject, OnLeftSelectEntered, OnRightSelectEntered);
        private void OnSelectExited(SelectExitEventArgs args) => InvokeFor(args.interactorObject, OnLeftSelectExited, OnRightSelectExited);
        private void OnFirstSelectEntered(SelectEnterEventArgs args) => InvokeFor(args.interactorObject, OnLeftFirstSelectEntered, OnRightFirstSelectEntered);
        private void OnLastSelectExited(SelectExitEventArgs args) => InvokeFor(args.interactorObject, OnLeftLastSelectExited, OnRightLastSelectExited);
        private void OnFirstHoverEntered(HoverEnterEventArgs args) => InvokeFor(args.interactorObject, OnLeftFirstHoverEntered, OnRightFirstHoverEntered);
        private void OnLastHoverExited(HoverExitEventArgs args) => InvokeFor(args.interactorObject, OnLeftLastHoverExited, OnRightLastHoverExited);

        private void InvokeFor(IXRInteractor interactor, UnityEvent leftEvent, UnityEvent rightEvent)
        {
            if (interactor == null) return;
            RememberHand(interactor);
            if (interactor.handedness == InteractorHandedness.Left) leftEvent?.Invoke();
            if (interactor.handedness == InteractorHandedness.Right) rightEvent?.Invoke();
        }
    }
}
