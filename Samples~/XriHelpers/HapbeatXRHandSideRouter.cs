// Sample component — requires Unity XR Interaction Toolkit.
//
// Splits an interactable's UnityEvents by the hand that caused them. It belongs
// in XR Helpers rather than Runtime so the core SDK remains XRI-independent.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Filtering;
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

        [Tooltip("For a physical poke button, route hover feedback only to the hand driving its XRPokeFilter. Other hovering/highlighted hands stay silent. Does not change XRI interaction logic.")]
        public bool RouteHoverToPokingHand;

        [Header("Last active hand")]
        public UnityEvent OnLeftRouted;
        public UnityEvent OnRightRouted;
        public UnityEvent<float> OnLeftRoutedFloat;
        public UnityEvent<float> OnRightRoutedFloat;
        public UnityEvent<Vector2> OnLeftRoutedVector2;
        public UnityEvent<Vector2> OnRightRoutedVector2;

        private XRBaseInteractable _interactable;
        private InteractorHandedness _lastHandedness = InteractorHandedness.None;
        private readonly HashSet<IXRInteractor> _hovering = new HashSet<IXRInteractor>();
        private readonly HashSet<IXRInteractor> _selecting = new HashSet<IXRInteractor>();
        private XRPokeFilter _pokeFilter;
        private InteractorHandedness _pokingHand;

        private void OnEnable()
        {
            _interactable = GetComponent<XRBaseInteractable>();
            if (_interactable == null)
                return; // uGUI controls receive TrackedDeviceEventData instead of XRI selection events.
            _pokeFilter = GetComponent<XRPokeFilter>();
            _interactable.selectEntered.AddListener(OnSelectEntered);
            _interactable.selectExited.AddListener(OnSelectExited);
            _interactable.hoverEntered.AddListener(OnHoverEntered);
            _interactable.hoverExited.AddListener(OnHoverExited);
        }

        private void OnDisable()
        {
            if (_interactable == null) return;
            _interactable.selectEntered.RemoveListener(OnSelectEntered);
            _interactable.selectExited.RemoveListener(OnSelectExited);
            _interactable.hoverEntered.RemoveListener(OnHoverEntered);
            _interactable.hoverExited.RemoveListener(OnHoverExited);
            if (RouteHoverToPokingHand) SetPokingHand(InteractorHandedness.None);
            else
            {
                if (ContainsHand(_hovering, InteractorHandedness.Left)) OnLeftLastHoverExited?.Invoke();
                if (ContainsHand(_hovering, InteractorHandedness.Right)) OnRightLastHoverExited?.Invoke();
            }
            if (ContainsHand(_selecting, InteractorHandedness.Left)) OnLeftLastSelectExited?.Invoke();
            if (ContainsHand(_selecting, InteractorHandedness.Right)) OnRightLastSelectExited?.Invoke();
            _hovering.Clear();
            _selecting.Clear();
        }

        private void LateUpdate()
        {
            if (!RouteHoverToPokingHand) return;
            var hand = InteractorHandedness.None;
            if (_pokeFilter != null && _pokeFilter.enabled && _pokeFilter.pokeStateData != null)
            {
                var data = _pokeFilter.pokeStateData.Value;
                // XRI's filter/interaction strength is shared by all hovering hands.
                // Its pokeInteractionPoint is the *source fingertip* position, so
                // match that point to a current poke interactor, not last hover.
                if (data.target != null && data.interactionStrength > 0f)
                {
                    var bestDistance = 0.005f * 0.005f;
                    foreach (var interactor in _interactable.interactorsHovering)
                    {
                        if (!(interactor is IPokeStateDataProvider) || interactor.handedness == InteractorHandedness.None)
                            continue;
                        var tip = interactor.GetAttachTransform(_interactable);
                        var distance = (tip.position - data.pokeInteractionPoint).sqrMagnitude;
                        if (distance >= bestDistance) continue;
                        bestDistance = distance;
                        hand = interactor.handedness;
                    }
                }
            }
            SetPokingHand(hand);
        }

        private void SetPokingHand(InteractorHandedness hand)
        {
            if (_pokingHand == hand) return;
            if (_pokingHand == InteractorHandedness.Left) OnLeftLastHoverExited?.Invoke();
            if (_pokingHand == InteractorHandedness.Right) OnRightLastHoverExited?.Invoke();
            _pokingHand = hand;
            if (hand == InteractorHandedness.Left) OnLeftFirstHoverEntered?.Invoke();
            if (hand == InteractorHandedness.Right) OnRightFirstHoverEntered?.Invoke();
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

        private void OnSelectEntered(SelectEnterEventArgs args)
        {
            var hand = args.interactorObject;
            bool first = !ContainsHand(_selecting, hand.handedness);
            if (!_selecting.Add(hand)) return;
            RememberHand(hand);
            InvokeFor(hand, OnLeftSelectEntered, OnRightSelectEntered);
            if (first) InvokeFor(hand, OnLeftFirstSelectEntered, OnRightFirstSelectEntered);
        }

        private void OnSelectExited(SelectExitEventArgs args)
        {
            var hand = args.interactorObject;
            if (!_selecting.Remove(hand)) return;
            RememberHand(hand);
            InvokeFor(hand, OnLeftSelectExited, OnRightSelectExited);
            if (!ContainsHand(_selecting, hand.handedness))
                InvokeFor(hand, OnLeftLastSelectExited, OnRightLastSelectExited);
        }

        private void OnHoverEntered(HoverEnterEventArgs args)
        {
            var hand = args.interactorObject;
            bool first = !ContainsHand(_hovering, hand.handedness);
            if (_hovering.Add(hand) && first && !RouteHoverToPokingHand)
                InvokeFor(hand, OnLeftFirstHoverEntered, OnRightFirstHoverEntered);
        }

        private void OnHoverExited(HoverExitEventArgs args)
        {
            var hand = args.interactorObject;
            if (_hovering.Remove(hand) && !ContainsHand(_hovering, hand.handedness) && !RouteHoverToPokingHand)
                InvokeFor(hand, OnLeftLastHoverExited, OnRightLastHoverExited);
            if (RouteHoverToPokingHand && !ContainsHand(_hovering, _pokingHand))
                SetPokingHand(InteractorHandedness.None);
        }

        private static bool ContainsHand(HashSet<IXRInteractor> interactors, InteractorHandedness hand)
        {
            foreach (var interactor in interactors)
                if (interactor.handedness == hand) return true;
            return false;
        }

        private void InvokeFor(IXRInteractor interactor, UnityEvent leftEvent, UnityEvent rightEvent)
        {
            if (interactor == null) return;
            // Hover is not ownership. Snap/value routing remembers select or UI input only.
            if (interactor.handedness == InteractorHandedness.Left) leftEvent?.Invoke();
            if (interactor.handedness == InteractorHandedness.Right) rightEvent?.Invoke();
        }
    }
}
