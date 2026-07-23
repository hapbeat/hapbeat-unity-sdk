using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Hapbeat.Samples.VRConfigExample
{
    /// <summary>
    /// Minimal on-device VR verification rig. Drives a
    /// <see cref="UnityEngine.InputSystem.XR.TrackedPoseDriver"/> (added to
    /// <see cref="_cameraTransform"/> at runtime, configured entirely in code —
    /// no XR Interaction Toolkit dependency, no scene-authored driver) so the
    /// HMD pose is sampled with the Input System's own before-render timing
    /// instead of a hand-written <c>LateUpdate</c> apply.
    ///
    /// <para>
    /// <b>Why not a manual LateUpdate apply (as this sample used to do):</b>
    /// a component-driven <c>LateUpdate</c> only resamples the HMD pose once
    /// per Update, then the XR compositor does its own late-latch/reprojection
    /// pass right before the frame is actually submitted. World-fixed UI
    /// (this rig's guide text / Address Override panel) parented under a pose
    /// that was sampled slightly earlier than the compositor's reprojection
    /// reference visibly micro-jitters relative to the reprojected view.
    /// <c>TrackedPoseDriver</c> updates via <c>InputSystem.onAfterUpdate</c>
    /// with <see cref="TrackedPoseDriver.UpdateType.UpdateAndBeforeRender"/>,
    /// which resamples again right before render — the same timing the
    /// compositor itself uses — removing that mismatch.
    /// </para>
    ///
    /// <para>
    /// <b>Single-hand-complete controls (L/R symmetric):</b> every action below
    /// is bound to <i>both</i> hands' equivalent physical control, so a single
    /// hand — either hand — can operate the whole rig. There is no left-does-X /
    /// right-does-Y split (that was this sample's previous scheme):
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>Stick tilt (either hand) — toggle focus between Player / Group
    /// (focused row highlighted on the panel). Keyboard: Tab.</item>
    /// <item>A/B or X/Y (either hand) — +/- the focused value. Keyboard: =/-.</item>
    /// <item>Trigger short press (either hand) — test playback (Keyboard: Space).
    /// Trigger held &gt; <see cref="LongPressThresholdSeconds"/>s — Apply
    /// (with a visual flash on the panel's Apply button). Detected by
    /// <see cref="PollTrigger"/> polling <c>IsPressed()</c> every frame — see the
    /// remarks below on why this isn't <c>started</c>/<c>canceled</c>-driven.</item>
    /// <item>Stick click (either hand) — <see cref="Recenter"/> the panel + guide
    /// in front of the camera. Keyboard: R.</item>
    /// <item>Left-hand Menu button / Keyboard Esc / on-screen Exit button — return
    /// to <see cref="_returnSceneName"/>. <b>Not</b> bound to the right controller:
    /// on Quest, the right Touch controller's "system" button is reserved by the
    /// OS (opens the Quest system menu) and is never delivered to the app's Input
    /// System actions — this is why this sample's original Exit binding on the
    /// right-hand menu button silently never fired on-device. Only the left
    /// controller's Menu button is actually deliverable to apps.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Binding paths verified against the actual OpenXR device layout</b> (Meta
    /// Quest Touch Plus/Pro and the base Oculus Touch profile all expose the exact
    /// same <c>InputControl</c> member names/aliases — see
    /// <c>com.unity.xr.openxr</c>'s <c>OculusTouchControllerProfile.cs</c> /
    /// <c>MetaQuestTouchPlusControllerProfile.cs</c> / <c>MetaQuestTouchProControllerProfile.cs</c>):
    /// stick <b>tilt</b> binds to <c>primary2DAxis</c>, which matches only because
    /// it happens to be a literal alias of the <c>thumbstick</c> control. Stick
    /// <b>click</b>'s control member is named <c>thumbstickClicked</c> — its
    /// aliases are <c>JoystickOrPadPressed</c> / <c>thumbstickClick</c> /
    /// <c>joystickClicked</c>, none of which is <c>primary2DAxisClick</c>. A bare
    /// <c>primary2DAxisClick</c> path segment is only that control's OpenXR
    /// <i>usage</i> tag, not a name/alias, and Input System only matches path
    /// segments against usages when written in <c>{brace}</c> form — confirmed in
    /// Unity's own shipped sample,
    /// <c>com.unity.xr.openxr/Samples~/Controller/ControllerSampleActions.inputactions</c>,
    /// which binds it as <c>&lt;XRController&gt;{Hand}/{primary2DAxisClick}</c>. This
    /// sample previously bound the bare (unbraced) form, which silently never
    /// resolved — the actual bug behind stick-click never firing on-device. Both
    /// the corrected name-based binding and the corrected braced-usage binding are
    /// now registered for redundancy. Menu binds both the exact member name
    /// <c>menu</c> (as Unity's own sample does) and its <c>menuButton</c> alias,
    /// left hand only.
    /// </para>
    ///
    /// <para>
    /// The trailing guide line also reports the most recently detected control's
    /// hand + name (e.g. <c>last: RightHand/thumbstickClicked</c>) — see
    /// <see cref="RecordLastControl"/> — for on-device triage of which physical
    /// control an input actually resolved to.
    /// </para>
    ///
    /// <para>
    /// Test playback fires <see cref="_testTrigger"/> — a
    /// <see cref="HapbeatUnityEventTrigger"/> wired (in the Inspector) to a
    /// StreamClip EventMap entry, played through <see cref="HapbeatManager"/>
    /// the same way <c>BasicExample</c> does. StreamClip mode needs no Kit
    /// installed on the device.
    /// </para>
    ///
    /// <para>
    /// Doubles as a "settings check" scene: wire <see cref="_returnSceneName"/>
    /// (or drag a Scene asset into the Editor-only helper field) to whatever
    /// scene launched this one, then Exit returns there once the address
    /// override has been confirmed. Leave it empty to disable Exit entirely.
    /// Intended usage: import this sample into your own project, add both
    /// scenes to Build Settings, and <c>SceneManager.LoadScene("VRConfigExample")</c>
    /// from your own scene to enter — Exit is the way back out.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Samples/VR Config Example Controller")]
    public class VRConfigExampleController : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("World-space Address Override panel this controller drives.")]
        [SerializeField]
        private HapbeatAddressOverridePanel _panel;

        [Tooltip("Camera Transform driven by a runtime-added TrackedPoseDriver.")]
        [SerializeField]
        private Transform _cameraTransform;

        [Tooltip("Test-playback trigger — a HapbeatUnityEventTrigger wired to a " +
            "StreamClip EventMap entry. Fired on trigger short-press. Played via " +
            "HapbeatManager, same path as BasicExample.")]
        [SerializeField]
        private HapbeatUnityEventTrigger _testTrigger;

        [Header("Exit / Return")]
        [Tooltip("Scene to load when Exit fires (left-hand menu button / " +
            "Keyboard Esc / on-screen Exit button). Must be added to Build Settings. " +
            "Leave empty to disable Exit entirely.")]
        [SerializeField]
        private string _returnSceneName = "";

#if UNITY_EDITOR
        [Tooltip("Editor-only convenience: drag a Scene asset here and Return Scene Name " +
            "above is kept in sync automatically (OnValidate). Optional — you can type " +
            "the scene name directly instead.")]
        [SerializeField]
        private UnityEditor.SceneAsset _returnSceneAsset;

        private void OnValidate()
        {
            if (_returnSceneAsset != null)
                _returnSceneName = _returnSceneAsset.name;
        }
#endif

        // --- Tuning ---
        private const float StickTiltThreshold = 0.6f;
        private const float LongPressThresholdSeconds = 0.6f;
        private const float RecenterDistanceMeters = 1.5f;

        // --- Focus state (which panel row +/- currently adjusts) ---
        private HapbeatAddressOverrideFocusField _focusedField = HapbeatAddressOverrideFocusField.Player;

        // --- Shared (either-hand) actions ---
        private InputAction _focusStickAction;      // Value Vector2 — tilt-debounced toggle
        private InputAction _focusToggleKeyAction;   // Button — Keyboard Tab alternate
        private InputAction _incAction;
        private InputAction _decAction;
        private InputAction _triggerAction;
        private InputAction _recenterAction;
        private InputAction _exitAction;

        // --- Diagnostic-only probes (one representative control per hand) ---
        private InputAction _rightHandProbeAction;
        private InputAction _leftHandProbeAction;

        private bool _stickPastThreshold;

        // --- Trigger hold state (polled — see PollTrigger) ---
        private bool _triggerWasPressed;
        private float _triggerPressStartTime = -1f;
        private bool _applyFiredThisHold;

        private bool _loggedMissingManager;
        private bool _loggedMissingTestTrigger;
        private bool _guideBuilt;

        // Last control that actually resolved and fired a performed callback
        // (hand + control name), refreshed by RecordLastControl and shown on the
        // guide's trailing diagnostic line — see BuildDiagnosticProbes remarks
        // for why the shared either-hand actions can't attribute this on their
        // own.
        private string _lastControlName = "(none yet)";

        // Diagnostic line ("Controllers: R OK / L --" etc.) refreshes once per
        // second, not every frame — see Update()/RefreshDiagnosticLine(). It
        // always occupies the same fixed line at the end of the guide text
        // block, so its content changing never shifts the guide's layout/size.
        private const float DiagnosticRefreshIntervalSeconds = 1f;
        private float _diagnosticTimer;
        private Text _guideText;

        // Guide canvas transform, kept relative to _panel (see BuildGuideText /
        // RepositionGuideRelativeToPanel) so Recenter() can move both together.
        private Transform _guideCanvasTransform;

        private void OnEnable()
        {
            SetupHmdPoseDriver();
            BuildSharedActions();
            BuildDiagnosticProbes();

            if (!_guideBuilt)
            {
                BuildGuideText();
                _guideBuilt = true;
            }

            if (_panel != null) _panel.SetFocusedField(_focusedField);

            // Startup recenter — puts the panel/guide in front of wherever the
            // headset happens to be looking the moment this scene loads, rather
            // than at a fixed world position that may be behind the wearer.
            Recenter();

            _diagnosticTimer = 0f;
            RefreshDiagnosticLine(); // don't wait a full second for the first reading
        }

        private void Update()
        {
            PollFocusStick();
            PollTrigger();

            _diagnosticTimer += Time.deltaTime;
            if (_diagnosticTimer < DiagnosticRefreshIntervalSeconds) return;
            _diagnosticTimer = 0f;
            RefreshDiagnosticLine();
        }

        private void OnDisable()
        {
            DisposeAction(ref _focusStickAction);
            DisposeAction(ref _focusToggleKeyAction, OnFocusToggleKey);
            DisposeAction(ref _incAction, OnInc);
            DisposeAction(ref _decAction, OnDec);
            DisposeAction(ref _triggerAction, RecordLastControl); // diagnostic-only subscription — hold/short-press logic is polled, not event-driven (see PollTrigger)
            DisposeAction(ref _recenterAction, OnRecenter);
            DisposeAction(ref _exitAction, OnExit);
            DisposeAction(ref _rightHandProbeAction);
            DisposeAction(ref _leftHandProbeAction);

            _triggerWasPressed = false;
            _triggerPressStartTime = -1f;
            _applyFiredThisHold = false;
            _stickPastThreshold = false;
        }

        // ---------------------------------------------------------------
        // HMD pose (TrackedPoseDriver, code-configured)
        // ---------------------------------------------------------------

        private void SetupHmdPoseDriver()
        {
            if (_cameraTransform == null) return;

            var driver = _cameraTransform.GetComponent<TrackedPoseDriver>();
            if (driver == null)
                driver = _cameraTransform.gameObject.AddComponent<TrackedPoseDriver>();

            var positionAction = new InputAction(
                name: "VRConfigExample/HmdPosition",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyePosition",
                expectedControlType: "Vector3");
            var rotationAction = new InputAction(
                name: "VRConfigExample/HmdRotation",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyeRotation",
                expectedControlType: "Quaternion");

            // Property setters bind + auto-enable the actions themselves
            // (TrackedPoseDriver.positionInput/rotationInput), so no manual
            // .Enable() call is needed here.
            driver.positionInput = new InputActionProperty(positionAction);
            driver.rotationInput = new InputActionProperty(rotationAction);
            driver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            driver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
            // No trackingStateInput is wired — TrackedPoseDriver treats that as
            // "always valid" on its own, so ignoreTrackingState just makes that
            // explicit rather than relying on the implicit null-action fallback.
            driver.ignoreTrackingState = true;
        }

        // ---------------------------------------------------------------
        // Setup — shared (either-hand) actions
        // ---------------------------------------------------------------

        private void BuildSharedActions()
        {
            _focusStickAction = new InputAction(
                name: "VRConfigExample/FocusStick",
                type: InputActionType.Value,
                binding: "<XRController>{LeftHand}/primary2DAxis",
                expectedControlType: "Vector2");
            _focusStickAction.AddBinding("<XRController>{RightHand}/primary2DAxis");
            _focusStickAction.Enable();

            _focusToggleKeyAction = new InputAction(
                name: "VRConfigExample/FocusToggleKey",
                type: InputActionType.Button,
                binding: "<Keyboard>/tab");
            _focusToggleKeyAction.performed += OnFocusToggleKey;
            _focusToggleKeyAction.Enable();

            _incAction = new InputAction(
                name: "VRConfigExample/Inc",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/primaryButton");
            _incAction.AddBinding("<XRController>{RightHand}/primaryButton");
            _incAction.AddBinding("<Keyboard>/equals");
            _incAction.performed += OnInc;
            _incAction.Enable();

            _decAction = new InputAction(
                name: "VRConfigExample/Dec",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/secondaryButton");
            _decAction.AddBinding("<XRController>{RightHand}/secondaryButton");
            _decAction.AddBinding("<Keyboard>/minus");
            _decAction.performed += OnDec;
            _decAction.Enable();

            // Button-type action so IsPressed() reflects the analog trigger's own
            // press-point threshold — PollTrigger() reads this every frame instead
            // of subscribing started/canceled (see PollTrigger remarks for why).
            // performed is still subscribed, but purely for the diagnostic
            // "last:" line (RecordLastControl) — it has no effect on hold/short-
            // press behavior.
            _triggerAction = new InputAction(
                name: "VRConfigExample/TestOrApply",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/triggerPressed");
            _triggerAction.AddBinding("<XRController>{RightHand}/triggerPressed");
            _triggerAction.AddBinding("<Keyboard>/space");
            _triggerAction.performed += RecordLastControl;
            _triggerAction.Enable();

            // Stick click — the concrete OpenXR device layout's control member is
            // named "thumbstickClicked" (aliases: JoystickOrPadPressed /
            // thumbstickClick / joystickClicked); "primary2DAxisClick" is only its
            // usage tag, which Input System only matches via the braced
            // "{primary2DAxisClick}" usage-path form. Both are bound below for
            // redundancy — see the class doc for the source-verified detail (this
            // sample previously bound the bare, non-matching "primary2DAxisClick"
            // form, which is why stick-click never fired on-device).
            _recenterAction = new InputAction(
                name: "VRConfigExample/Recenter",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/thumbstickClicked");
            _recenterAction.AddBinding("<XRController>{RightHand}/thumbstickClicked");
            _recenterAction.AddBinding("<XRController>{LeftHand}/{primary2DAxisClick}");
            _recenterAction.AddBinding("<XRController>{RightHand}/{primary2DAxisClick}");
            _recenterAction.AddBinding("<Keyboard>/r");
            _recenterAction.performed += OnRecenter;
            _recenterAction.Enable();

            // Left-hand Menu button only — see the class doc for why the right
            // controller's equivalent button is deliberately excluded (Quest
            // reserves it for the system menu; it never reaches app input). Both
            // the exact control member name ("menu" — as Unity's own OpenXR
            // Controller sample binds it) and its "menuButton" alias are bound,
            // left hand only, for redundancy.
            _exitAction = new InputAction(
                name: "VRConfigExample/Exit",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/menu");
            _exitAction.AddBinding("<XRController>{LeftHand}/menuButton");
            _exitAction.AddBinding("<Keyboard>/escape");
            _exitAction.performed += OnExit;
            _exitAction.Enable();
        }

        /// <summary>
        /// Diagnostic-only probes: one representative binding per hand (not
        /// wired to any handler), purely so <see cref="RefreshDiagnosticLine"/>
        /// can report per-hand controller presence independently of the shared
        /// either-hand action objects above (which merge both hands' controls
        /// into one action and can't attribute resolution back to a single hand).
        /// </summary>
        private void BuildDiagnosticProbes()
        {
            _rightHandProbeAction = new InputAction(
                name: "VRConfigExample/RightHandProbe",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/primaryButton");
            _rightHandProbeAction.Enable();

            _leftHandProbeAction = new InputAction(
                name: "VRConfigExample/LeftHandProbe",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/primaryButton");
            _leftHandProbeAction.Enable();
        }

        /// <summary>
        /// Records the hand + control name of whatever <c>InputAction</c>
        /// callback last fired, for the guide's trailing "last:" diagnostic
        /// (see <see cref="RefreshDiagnosticLine"/>). Subscribed to every
        /// shared action's <c>performed</c> callback (in addition to that
        /// action's own handler) so the diagnostic reflects the actual
        /// physical control that resolved on-device — the shared either-hand
        /// actions can't otherwise attribute a firing back to a single
        /// control/hand from the outside (see <see cref="BuildDiagnosticProbes"/>
        /// remarks on the same either-hand-merging limitation).
        /// </summary>
        private void RecordLastControl(InputAction.CallbackContext ctx)
        {
            var control = ctx.control;
            if (control == null) return;

            string hand = "?";
            var device = control.device;
            if (device != null)
            {
                if (device is Keyboard) hand = "Keyboard";
                else if (device.usages.Count > 0) hand = device.usages[0].ToString();
                else hand = device.displayName;
            }
            _lastControlName = $"{hand}/{control.name}";
        }

        private static void DisposeAction(ref InputAction action)
        {
            if (action == null) return;
            action.Disable();
            action.Dispose();
            action = null;
        }

        private static void DisposeAction(ref InputAction action, System.Action<InputAction.CallbackContext> performedHandler)
        {
            if (action == null) return;
            action.performed -= performedHandler;
            action.Disable();
            action.Dispose();
            action = null;
        }

        // ---------------------------------------------------------------
        // Focus stick (tilt-debounced toggle between Player/Group)
        // ---------------------------------------------------------------

        private void PollFocusStick()
        {
            if (_focusStickAction == null) return;

            Vector2 v = _focusStickAction.ReadValue<Vector2>();
            float mag = Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y));
            bool past = mag >= StickTiltThreshold;

            // Edge-detect: only toggle the instant the tilt crosses the
            // threshold, not on every frame it stays held past it.
            if (past && !_stickPastThreshold)
                ToggleFocus();

            _stickPastThreshold = past;
        }

        private void OnFocusToggleKey(InputAction.CallbackContext ctx)
        {
            RecordLastControl(ctx);
            ToggleFocus();
        }

        private void ToggleFocus()
        {
            _focusedField = _focusedField == HapbeatAddressOverrideFocusField.Player
                ? HapbeatAddressOverrideFocusField.Group
                : HapbeatAddressOverrideFocusField.Player;
            if (_panel != null) _panel.SetFocusedField(_focusedField);
        }

        // ---------------------------------------------------------------
        // Inc / Dec (applies to whichever field currently has focus)
        // ---------------------------------------------------------------

        private void OnInc(InputAction.CallbackContext ctx)
        {
            RecordLastControl(ctx);
            AdjustFocusedValue(+1);
        }

        private void OnDec(InputAction.CallbackContext ctx)
        {
            RecordLastControl(ctx);
            AdjustFocusedValue(-1);
        }

        private void AdjustFocusedValue(int delta)
        {
            if (_panel == null) return;
            switch (_focusedField)
            {
                case HapbeatAddressOverrideFocusField.Player:
                    if (delta > 0) _panel.PlayerUp(); else _panel.PlayerDown();
                    break;
                case HapbeatAddressOverrideFocusField.Group:
                    if (delta > 0) _panel.GroupUp(); else _panel.GroupDown();
                    break;
            }
        }

        // ---------------------------------------------------------------
        // Trigger: short press = test playback, long press (>0.6s) = Apply
        // ---------------------------------------------------------------

        /// <summary>
        /// Polls <see cref="_triggerAction"/>'s <c>IsPressed()</c> every frame
        /// instead of relying on <c>started</c>/<c>canceled</c> callbacks. On
        /// Quest 3s + Meta Quest Touch Plus/Pro, the analog trigger's
        /// started/performed/canceled event sequence was unreliable for a
        /// sustained hold-to-Apply gesture — a real device symptom this rig
        /// exists to catch, not an Editor-only quirk (A/B/X/Y and stick tilt,
        /// which only ever fire a single <c>performed</c>, were unaffected).
        /// Polling <c>IsPressed()</c> directly sidesteps the callback sequence
        /// entirely: it's just "is the button down right now", read fresh
        /// every <see cref="Update"/>.
        ///
        /// <para>
        /// Short-press (test playback) and long-press (Apply) are mutually
        /// exclusive per press/release cycle: <see cref="_applyFiredThisHold"/>
        /// latches the instant the hold threshold is crossed, and release only
        /// fires the short-press test if that latch is still false. A press
        /// released before <see cref="LongPressThresholdSeconds"/> elapses
        /// therefore always fires the short-press test — the hold check simply
        /// never got the chance to fire first.
        /// </para>
        /// </summary>
        private void PollTrigger()
        {
            if (_triggerAction == null) return;
            bool pressed = _triggerAction.IsPressed();

            if (pressed && !_triggerWasPressed)
            {
                // Rising edge — press just started.
                _triggerPressStartTime = Time.unscaledTime;
                _applyFiredThisHold = false;
            }
            else if (pressed && _triggerWasPressed && !_applyFiredThisHold)
            {
                // Held past the threshold — fire Apply exactly once for this hold.
                if (_triggerPressStartTime >= 0f &&
                    Time.unscaledTime - _triggerPressStartTime >= LongPressThresholdSeconds)
                {
                    _applyFiredThisHold = true;
                    if (_panel != null) _panel.Apply(); // Apply() flashes its own button — no extra feedback needed here.
                }
            }
            else if (!pressed && _triggerWasPressed)
            {
                // Released — short-press test only if Apply didn't already
                // fire during this same hold (mutual exclusion).
                if (!_applyFiredThisHold)
                    PlayTestHaptic();
                _triggerPressStartTime = -1f;
                _applyFiredThisHold = false;
            }

            _triggerWasPressed = pressed;
        }

        private void PlayTestHaptic()
        {
            if (_testTrigger == null)
            {
                if (!_loggedMissingTestTrigger)
                {
                    Debug.LogWarning("[Hapbeat] VRConfigExampleController: no test trigger wired — " +
                        "assign a HapbeatUnityEventTrigger (StreamClip entry) in the Inspector.");
                    _loggedMissingTestTrigger = true;
                }
                return;
            }
            if (HapbeatManager.Instance == null)
            {
                if (!_loggedMissingManager)
                {
                    Debug.LogWarning("[Hapbeat] VRConfigExampleController: HapbeatManager.Instance is null — " +
                        "cannot play test haptic. Ensure a GameObject with HapbeatManager exists in the scene.");
                    _loggedMissingManager = true;
                }
                return;
            }
            _testTrigger.Fire();
        }

        // ---------------------------------------------------------------
        // Recenter (stick click) — panel + guide move to camera-front, yaw only
        // ---------------------------------------------------------------

        private void OnRecenter(InputAction.CallbackContext ctx)
        {
            RecordLastControl(ctx);
            Recenter();
        }

        /// <summary>
        /// Moves the Address Override panel (and the guide text, which follows
        /// it — see <see cref="RepositionGuideRelativeToPanel"/>) to
        /// <see cref="RecenterDistanceMeters"/> in front of the camera, at the
        /// camera's current eye height, facing the camera's yaw only (pitch/roll
        /// ignored so the panel never tips when the wearer looks up/down).
        /// Called once at startup (<see cref="OnEnable"/>) and on every
        /// stick-click / Keyboard R afterwards.
        /// </summary>
        public void Recenter()
        {
            if (_cameraTransform == null || _panel == null) return;

            Vector3 camPos = _cameraTransform.position;
            Vector3 fwd = _cameraTransform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f)
                fwd = Vector3.forward; // camera pointing straight up/down — deterministic fallback
            else
                fwd.Normalize();

            Vector3 targetPos = new Vector3(
                camPos.x + fwd.x * RecenterDistanceMeters,
                camPos.y,
                camPos.z + fwd.z * RecenterDistanceMeters);
            Quaternion targetRot = Quaternion.LookRotation(fwd, Vector3.up);

            _panel.transform.SetPositionAndRotation(targetPos, targetRot);
            RepositionGuideRelativeToPanel();
        }

        // ---------------------------------------------------------------
        // Exit
        // ---------------------------------------------------------------

        private void OnExit(InputAction.CallbackContext ctx)
        {
            RecordLastControl(ctx);
            ExitToScene();
        }

        /// <summary>
        /// Loads <see cref="_returnSceneName"/>, returning to whatever scene
        /// launched this one. No-op (with a warning) if the name is empty or
        /// the scene isn't in Build Settings — wireable from UnityEvents /
        /// the on-screen Exit button in addition to the controller/keyboard
        /// bindings in <see cref="BuildSharedActions"/>.
        /// </summary>
        public void ExitToScene()
        {
            if (string.IsNullOrEmpty(_returnSceneName))
            {
                Debug.LogWarning("[Hapbeat] VRConfigExampleController: Exit requested but no return scene is " +
                    "set. Set \"Return Scene Name\" in the Inspector (or drag a Scene asset into the Editor-only " +
                    "field) to enable Exit.");
                return;
            }

            try
            {
                SceneManager.LoadScene(_returnSceneName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Hapbeat] VRConfigExampleController: failed to load return scene " +
                    $"\"{_returnSceneName}\" — is it added to Build Settings? ({ex.Message})");
            }
        }

        // ---------------------------------------------------------------
        // Guide text (world-space, always 6 fixed lines — never shifts layout)
        // ---------------------------------------------------------------

        // One action per line, verbatim. The Exit line's target changes if
        // _returnSceneName is edited at authoring time, so this is built once
        // (cached in _guideActionsTextCached) rather than a compile-time const —
        // it never changes again at runtime, so this still never causes a
        // layout shift.
        private string _guideActionsTextCached;

        private string BuildGuideActionsText()
        {
            string exitLine = string.IsNullOrEmpty(_returnSceneName)
                ? "Exit: (no scene set)"
                : $"Exit → {_returnSceneName}";
            return
                "Stick: focus Player/Group\n" +
                "A/B (X/Y): focused +/-\n" +
                "Trigger tap: test | hold: apply\n" +
                "Stick-click: recenter\n" +
                "L-Menu/Esc: " + exitLine;
        }

        private void BuildGuideText()
        {
            // Scale matches the enlarged HapbeatAddressOverridePanel default
            // (world-space physical size = worldWidth/worldHeight regardless
            // of scale — scale only trades off UI-pixel canvas resolution
            // against how large each fixed-pixel-sized font/element renders).
            const float scale = 0.0018f;
            const float worldWidth = 1.0f;
            const float worldHeight = 0.55f;

            var canvasGo = new GameObject("VRConfigExampleGuideCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(worldWidth / scale, worldHeight / scale);
            canvasGo.transform.localScale = Vector3.one * scale;

            _guideCanvasTransform = canvasGo.transform;

            // Anchor just below the Address Override panel so both stay
            // legible together in-headset; fall back to a spot in front of
            // this rig if no panel is wired. Recenter() re-applies this same
            // relative offset (RepositionGuideRelativeToPanel) whenever the
            // panel moves, so this initial placement is superseded by the
            // startup Recenter() call in OnEnable when a panel is wired.
            if (_panel != null)
            {
                RepositionGuideRelativeToPanel();
            }
            else
            {
                canvasGo.transform.localPosition = new Vector3(0f, 1.0f, 1.5f);
            }

            var bg = new GameObject("Background", typeof(RectTransform));
            bg.transform.SetParent(canvasGo.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.55f);
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;

            var textGo = new GameObject("GuideText", typeof(RectTransform));
            textGo.transform.SetParent(canvasGo.transform, false);
            var text = textGo.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 30;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false; // purely informational — never the click target
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            _guideText = text;
            _guideActionsTextCached = BuildGuideActionsText();
            // Fixed 6-line block (5 static action lines, incl. Exit, + 1
            // diagnostic line): only the diagnostic line's content ever changes
            // at runtime (see RefreshDiagnosticLine), so this never causes a
            // layout shift.
            _guideText.text = _guideActionsTextCached + "\n" + DiagnosticPlaceholder;

            BuildExitButton(canvasGo);
        }

        /// <summary>
        /// Keeps the guide canvas positioned just below the panel, matching its
        /// rotation — the same relative offset <see cref="BuildGuideText"/> used
        /// to set up once. Re-applied by <see cref="Recenter"/> every time the
        /// panel moves so the two always travel together.
        /// </summary>
        private void RepositionGuideRelativeToPanel()
        {
            if (_guideCanvasTransform == null || _panel == null) return;
            _guideCanvasTransform.position = _panel.transform.position + new Vector3(0f, -0.5f, 0f);
            _guideCanvasTransform.rotation = _panel.transform.rotation;
        }

        /// <summary>
        /// Adds a clickable "Exit" Button to the guide canvas alongside the
        /// controller/keyboard bindings in <see cref="BuildSharedActions"/> —
        /// lets this "settings check" scene be exited with a mouse click too
        /// (e.g. desktop Editor testing without a headset attached). Requires a
        /// <see cref="GraphicRaycaster"/> + <see cref="Canvas.worldCamera"/> on
        /// this world-space canvas (added here); actually clicking it via a VR
        /// laser pointer would additionally need an XR ray interactor, which is
        /// out of scope for this minimal verification rig — the controller
        /// menu-button and keyboard Esc bindings remain the primary way to
        /// exit in a headset.
        /// </summary>
        private void BuildExitButton(GameObject canvasGo)
        {
            if (EventSystem.current == null)
            {
                Debug.LogWarning("[Hapbeat] VRConfigExampleController: No EventSystem found in scene — " +
                    "the on-screen Exit button will not receive clicks (controller menu button and " +
                    "Keyboard Esc still work).");
            }

            if (canvasGo.GetComponent<GraphicRaycaster>() == null)
                canvasGo.AddComponent<GraphicRaycaster>();

            var canvas = canvasGo.GetComponent<Canvas>();
            if (canvas.worldCamera == null)
                canvas.worldCamera = _cameraTransform != null ? _cameraTransform.GetComponent<Camera>() : Camera.main;

            var buttonGo = new GameObject("ExitButton", typeof(RectTransform));
            buttonGo.transform.SetParent(canvasGo.transform, false);
            var buttonRt = buttonGo.GetComponent<RectTransform>();
            buttonRt.anchorMin = new Vector2(1f, 0f);
            buttonRt.anchorMax = new Vector2(1f, 0f);
            buttonRt.pivot = new Vector2(1f, 0f);
            buttonRt.sizeDelta = new Vector2(140f, 50f);
            buttonRt.anchoredPosition = new Vector2(-10f, 10f);

            var buttonImage = buttonGo.AddComponent<Image>();
            buttonImage.color = new Color(0.55f, 0.15f, 0.15f, 0.9f);
            var button = buttonGo.AddComponent<Button>();
            button.onClick.AddListener(ExitToScene);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(buttonGo.transform, false);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = Vector2.zero;
            labelRt.offsetMax = Vector2.zero;
            var label = labelGo.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = 22;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.text = "Exit";
            label.raycastTarget = false;
        }

        // ---------------------------------------------------------------
        // Diagnostic line ("Controllers: R OK / L --")
        // ---------------------------------------------------------------

        private const string DiagnosticPlaceholder = "Controllers: R -- / L --";
        private const string EnableOculusTouchHint = "enable Oculus Touch profile (OpenXR)";

        /// <summary>
        /// Refreshes the guide's trailing diagnostic line with whether the
        /// right/left controller bindings currently resolve to a device
        /// (<see cref="InputAction.controls"/> non-empty), via the dedicated
        /// per-hand probes built in <see cref="BuildDiagnosticProbes"/>. Always
        /// writes the same fixed line (text only, no line added/removed) so
        /// this never shifts the guide's layout.
        /// </summary>
        private void RefreshDiagnosticLine()
        {
            if (_guideText == null) return;

            bool rightOk = _rightHandProbeAction != null && _rightHandProbeAction.controls.Count > 0;
            bool leftOk = _leftHandProbeAction != null && _leftHandProbeAction.controls.Count > 0;

            string diagnostic = $"Controllers: R {(rightOk ? "OK" : "--")} / L {(leftOk ? "OK" : "--")} | last: {_lastControlName}";
            if (!rightOk && !leftOk)
                diagnostic += $" ({EnableOculusTouchHint})";

            _guideText.text = _guideActionsTextCached + "\n" + diagnostic;
        }
    }
}
