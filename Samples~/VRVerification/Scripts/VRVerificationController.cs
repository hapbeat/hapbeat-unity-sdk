using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Hapbeat.Samples.VRVerification
{
    /// <summary>
    /// Minimal on-device VR verification rig. Reads the HMD pose via
    /// code-defined InputActions (<c>&lt;XRHMD&gt;/centerEyePosition</c> /
    /// <c>centerEyeRotation</c>) and applies it to a plain Camera Transform
    /// every <see cref="LateUpdate"/> — no TrackedPoseDriver, no XR
    /// Interaction Toolkit dependency.
    ///
    /// <para>
    /// Controller buttons drive the world-space
    /// <see cref="HapbeatAddressOverridePanel"/> (player/group steppers +
    /// Apply) and fire a one-shot test haptic via
    /// <see cref="HapbeatManager.Play(string, float, string, string)"/> so a
    /// single build, worn by multiple headsets, can be pointed at its own
    /// paired Hapbeat and the pairing can be confirmed on the spot:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>Right A (<c>primaryButton</c>) / Keyboard P — Player +</item>
    /// <item>Right B (<c>secondaryButton</c>) / Keyboard O — Player -</item>
    /// <item>Left X (<c>primaryButton</c>) / Keyboard G — Group +</item>
    /// <item>Left Y (<c>secondaryButton</c>) / Keyboard H — Group -</item>
    /// <item>Right Trigger / Keyboard Space — Apply + test haptic</item>
    /// <item>Left Trigger / Keyboard T — test haptic only (no Apply)</item>
    /// </list>
    /// </summary>
    [AddComponentMenu("Hapbeat/Samples/VR Verification Controller")]
    public class VRVerificationController : MonoBehaviour
    {
        /// <summary>Standard sample-kit event (DEC-040) used as the test haptic.</summary>
        private const string TestEventId = "sample-kit.sine_100hz";

        [Header("Wiring")]
        [Tooltip("World-space Address Override panel this controller drives.")]
        [SerializeField]
        private HapbeatAddressOverridePanel _panel;

        [Tooltip("Camera Transform the HMD pose is applied to (LateUpdate).")]
        [SerializeField]
        private Transform _cameraTransform;

        // --- HMD pose ---
        private InputAction _hmdPositionAction;
        private InputAction _hmdRotationAction;

        // --- Right hand ---
        private InputAction _rightPlayerUpAction;
        private InputAction _rightPlayerDownAction;
        private InputAction _rightApplyTestAction;

        // --- Left hand ---
        private InputAction _leftGroupUpAction;
        private InputAction _leftGroupDownAction;
        private InputAction _leftTestOnlyAction;

        private bool _loggedMissingManager;
        private bool _guideBuilt;

        private void OnEnable()
        {
            BuildHmdPoseActions();
            BuildRightHandActions();
            BuildLeftHandActions();

            if (!_guideBuilt)
            {
                BuildGuideText();
                _guideBuilt = true;
            }
        }

        private void OnDisable()
        {
            DisposeAction(ref _hmdPositionAction);
            DisposeAction(ref _hmdRotationAction);

            DisposeAction(ref _rightPlayerUpAction, OnRightPlayerUp);
            DisposeAction(ref _rightPlayerDownAction, OnRightPlayerDown);
            DisposeAction(ref _rightApplyTestAction, OnRightApplyAndTest);

            DisposeAction(ref _leftGroupUpAction, OnLeftGroupUp);
            DisposeAction(ref _leftGroupDownAction, OnLeftGroupDown);
            DisposeAction(ref _leftTestOnlyAction, OnLeftTestOnly);
        }

        private void LateUpdate()
        {
            ApplyHmdPose();
        }

        // ---------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------

        private void BuildHmdPoseActions()
        {
            _hmdPositionAction = new InputAction(
                name: "VRVerification/HmdPosition",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyePosition",
                expectedControlType: "Vector3");
            _hmdPositionAction.Enable();

            _hmdRotationAction = new InputAction(
                name: "VRVerification/HmdRotation",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyeRotation",
                expectedControlType: "Quaternion");
            _hmdRotationAction.Enable();
        }

        private void BuildRightHandActions()
        {
            _rightPlayerUpAction = new InputAction(
                name: "VRVerification/RightPlayerUp",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/primaryButton");
            _rightPlayerUpAction.AddBinding("<Keyboard>/p");
            _rightPlayerUpAction.performed += OnRightPlayerUp;
            _rightPlayerUpAction.Enable();

            _rightPlayerDownAction = new InputAction(
                name: "VRVerification/RightPlayerDown",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/secondaryButton");
            _rightPlayerDownAction.AddBinding("<Keyboard>/o");
            _rightPlayerDownAction.performed += OnRightPlayerDown;
            _rightPlayerDownAction.Enable();

            _rightApplyTestAction = new InputAction(
                name: "VRVerification/RightApplyAndTest",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/triggerPressed");
            _rightApplyTestAction.AddBinding("<Keyboard>/space");
            _rightApplyTestAction.performed += OnRightApplyAndTest;
            _rightApplyTestAction.Enable();
        }

        private void BuildLeftHandActions()
        {
            _leftGroupUpAction = new InputAction(
                name: "VRVerification/LeftGroupUp",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/primaryButton");
            _leftGroupUpAction.AddBinding("<Keyboard>/g");
            _leftGroupUpAction.performed += OnLeftGroupUp;
            _leftGroupUpAction.Enable();

            _leftGroupDownAction = new InputAction(
                name: "VRVerification/LeftGroupDown",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/secondaryButton");
            _leftGroupDownAction.AddBinding("<Keyboard>/h");
            _leftGroupDownAction.performed += OnLeftGroupDown;
            _leftGroupDownAction.Enable();

            _leftTestOnlyAction = new InputAction(
                name: "VRVerification/LeftTestOnly",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/triggerPressed");
            _leftTestOnlyAction.AddBinding("<Keyboard>/t");
            _leftTestOnlyAction.performed += OnLeftTestOnly;
            _leftTestOnlyAction.Enable();
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
        // HMD pose
        // ---------------------------------------------------------------

        private void ApplyHmdPose()
        {
            if (_cameraTransform == null) return;
            if (_hmdPositionAction == null || _hmdRotationAction == null) return;

            // No bound XR HMD device (e.g. desktop Editor Play mode without a
            // headset attached) — leave the authored camera Transform alone
            // instead of snapping it to the Vector3.zero / Quaternion.identity
            // default value the action would otherwise read.
            if (_hmdPositionAction.controls.Count == 0 || _hmdRotationAction.controls.Count == 0) return;

            _cameraTransform.localPosition = _hmdPositionAction.ReadValue<Vector3>();
            _cameraTransform.localRotation = _hmdRotationAction.ReadValue<Quaternion>();
        }

        // ---------------------------------------------------------------
        // Button handlers
        // ---------------------------------------------------------------

        private void OnRightPlayerUp(InputAction.CallbackContext ctx)
        {
            if (_panel != null) _panel.PlayerUp();
        }

        private void OnRightPlayerDown(InputAction.CallbackContext ctx)
        {
            if (_panel != null) _panel.PlayerDown();
        }

        private void OnLeftGroupUp(InputAction.CallbackContext ctx)
        {
            if (_panel != null) _panel.GroupUp();
        }

        private void OnLeftGroupDown(InputAction.CallbackContext ctx)
        {
            if (_panel != null) _panel.GroupDown();
        }

        private void OnRightApplyAndTest(InputAction.CallbackContext ctx)
        {
            if (_panel != null) _panel.Apply();
            PlayTestHaptic();
        }

        private void OnLeftTestOnly(InputAction.CallbackContext ctx)
        {
            PlayTestHaptic();
        }

        private void PlayTestHaptic()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null)
            {
                if (!_loggedMissingManager)
                {
                    Debug.LogWarning("[Hapbeat] VRVerificationController: HapbeatManager.Instance is null — " +
                        "cannot play test haptic. Ensure a GameObject with HapbeatManager exists in the scene.");
                    _loggedMissingManager = true;
                }
                return;
            }
            mgr.Play(TestEventId);
        }

        // ---------------------------------------------------------------
        // Guide text (world-space, always 3 lines — never shifts layout)
        // ---------------------------------------------------------------

        private void BuildGuideText()
        {
            const float scale = 0.001f; // 1 UI pixel = 1mm, matches HapbeatAddressOverridePanel default.
            const float worldWidth = 0.7f;
            const float worldHeight = 0.24f;

            var canvasGo = new GameObject("VRVerificationGuideCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(worldWidth / scale, worldHeight / scale);
            canvasGo.transform.localScale = Vector3.one * scale;

            // Anchor just below the Address Override panel so both stay
            // legible together in-headset; fall back to a spot in front of
            // this rig if no panel is wired.
            if (_panel != null)
            {
                canvasGo.transform.position = _panel.transform.position + new Vector3(0f, -0.2f, 0f);
                canvasGo.transform.rotation = _panel.transform.rotation;
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
            text.fontSize = 28;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            // Fixed 3-line block — content never changes at runtime, so this
            // never causes a layout shift.
            text.text =
                "R-A/B player+-   L-X/Y group+-\n" +
                "R-Trig apply+test   L-Trig test\n" +
                "kit: deploy sample-kit via Studio";
            var textRt = textGo.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;
        }
    }
}
