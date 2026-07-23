using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Hapbeat.Samples.VRBasicExample
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
    /// <item>Right Stick-click / Menu / Keyboard Esc / on-screen Exit button — return to <see cref="_returnSceneName"/></item>
    /// </list>
    ///
    /// <para>
    /// Doubles as a "settings check" scene: wire <see cref="_returnSceneName"/>
    /// (or drag a Scene asset into the Editor-only helper field) to whatever
    /// scene launched this one, then Exit returns there once the address
    /// override has been confirmed. Leave it empty to disable Exit entirely.
    /// Intended usage: import this sample into your own project, add both
    /// scenes to Build Settings, and <c>SceneManager.LoadScene("VRBasicExample")</c>
    /// from your own scene to enter — Exit is the way back out.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Samples/VR Basic Example Controller")]
    public class VRBasicExampleController : MonoBehaviour
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

        [Header("Exit / Return")]
        [Tooltip("Scene to load when Exit fires (right stick-click / menu button / " +
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

        // --- Exit ---
        private InputAction _exitAction;

        private bool _loggedMissingManager;
        private bool _guideBuilt;

        // Diagnostic line ("R:OK L:--" etc.) refreshes once per second, not
        // every frame — see Update()/RefreshDiagnosticLine(). It always
        // occupies the same fixed line at the end of the guide text block, so
        // its content changing never shifts the guide's layout/size.
        private const float DiagnosticRefreshIntervalSeconds = 1f;
        private float _diagnosticTimer;
        private Text _guideText;

        private void OnEnable()
        {
            BuildHmdPoseActions();
            BuildRightHandActions();
            BuildLeftHandActions();
            BuildExitAction();

            if (!_guideBuilt)
            {
                BuildGuideText();
                _guideBuilt = true;
            }

            _diagnosticTimer = 0f;
            RefreshDiagnosticLine(); // don't wait a full second for the first reading
        }

        private void Update()
        {
            _diagnosticTimer += Time.deltaTime;
            if (_diagnosticTimer < DiagnosticRefreshIntervalSeconds) return;
            _diagnosticTimer = 0f;
            RefreshDiagnosticLine();
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

            DisposeAction(ref _exitAction, OnExit);
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
                name: "VRBasicExample/HmdPosition",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyePosition",
                expectedControlType: "Vector3");
            _hmdPositionAction.Enable();

            _hmdRotationAction = new InputAction(
                name: "VRBasicExample/HmdRotation",
                type: InputActionType.Value,
                binding: "<XRHMD>/centerEyeRotation",
                expectedControlType: "Quaternion");
            _hmdRotationAction.Enable();
        }

        private void BuildRightHandActions()
        {
            _rightPlayerUpAction = new InputAction(
                name: "VRBasicExample/RightPlayerUp",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/primaryButton");
            _rightPlayerUpAction.AddBinding("<Keyboard>/p");
            _rightPlayerUpAction.performed += OnRightPlayerUp;
            _rightPlayerUpAction.Enable();

            _rightPlayerDownAction = new InputAction(
                name: "VRBasicExample/RightPlayerDown",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/secondaryButton");
            _rightPlayerDownAction.AddBinding("<Keyboard>/o");
            _rightPlayerDownAction.performed += OnRightPlayerDown;
            _rightPlayerDownAction.Enable();

            _rightApplyTestAction = new InputAction(
                name: "VRBasicExample/RightApplyAndTest",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/triggerPressed");
            _rightApplyTestAction.AddBinding("<Keyboard>/space");
            _rightApplyTestAction.performed += OnRightApplyAndTest;
            _rightApplyTestAction.Enable();
        }

        private void BuildLeftHandActions()
        {
            _leftGroupUpAction = new InputAction(
                name: "VRBasicExample/LeftGroupUp",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/primaryButton");
            _leftGroupUpAction.AddBinding("<Keyboard>/g");
            _leftGroupUpAction.performed += OnLeftGroupUp;
            _leftGroupUpAction.Enable();

            _leftGroupDownAction = new InputAction(
                name: "VRBasicExample/LeftGroupDown",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/secondaryButton");
            _leftGroupDownAction.AddBinding("<Keyboard>/h");
            _leftGroupDownAction.performed += OnLeftGroupDown;
            _leftGroupDownAction.Enable();

            _leftTestOnlyAction = new InputAction(
                name: "VRBasicExample/LeftTestOnly",
                type: InputActionType.Button,
                binding: "<XRController>{LeftHand}/triggerPressed");
            _leftTestOnlyAction.AddBinding("<Keyboard>/t");
            _leftTestOnlyAction.performed += OnLeftTestOnly;
            _leftTestOnlyAction.Enable();
        }

        /// <summary>
        /// Exit binding, kept separate from <see cref="BuildRightHandActions"/> /
        /// <see cref="BuildLeftHandActions"/> since it isn't hand-specific: right
        /// stick-click and menu button are both bound as alternates (controller
        /// layouts vary in which one is free/reachable), plus Keyboard Esc for
        /// desktop testing. Always built regardless of <see cref="_returnSceneName"/>
        /// being empty — <see cref="ExitToScene"/> is what actually gates on that.
        /// </summary>
        private void BuildExitAction()
        {
            _exitAction = new InputAction(
                name: "VRBasicExample/Exit",
                type: InputActionType.Button,
                binding: "<XRController>{RightHand}/primary2DAxisClick");
            _exitAction.AddBinding("<XRController>{RightHand}/menuButton");
            _exitAction.AddBinding("<Keyboard>/escape");
            _exitAction.performed += OnExit;
            _exitAction.Enable();
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

        private void OnExit(InputAction.CallbackContext ctx) => ExitToScene();

        /// <summary>
        /// Loads <see cref="_returnSceneName"/>, returning to whatever scene
        /// launched this one. No-op (with a warning) if the name is empty or
        /// the scene isn't in Build Settings — wireable from UnityEvents /
        /// the on-screen Exit button in addition to the controller/keyboard
        /// bindings in <see cref="BuildExitAction"/>.
        /// </summary>
        public void ExitToScene()
        {
            if (string.IsNullOrEmpty(_returnSceneName))
            {
                Debug.LogWarning("[Hapbeat] VRBasicExampleController: Exit requested but no return scene is " +
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
                Debug.LogWarning($"[Hapbeat] VRBasicExampleController: failed to load return scene " +
                    $"\"{_returnSceneName}\" — is it added to Build Settings? ({ex.Message})");
            }
        }

        private void PlayTestHaptic()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null)
            {
                if (!_loggedMissingManager)
                {
                    Debug.LogWarning("[Hapbeat] VRBasicExampleController: HapbeatManager.Instance is null — " +
                        "cannot play test haptic. Ensure a GameObject with HapbeatManager exists in the scene.");
                    _loggedMissingManager = true;
                }
                return;
            }
            mgr.Play(TestEventId);
        }

        // ---------------------------------------------------------------
        // Guide text (world-space, always 6 fixed lines — never shifts layout)
        // ---------------------------------------------------------------

        // One action per line, verbatim — replaces the old 3-line combined
        // layout so each control reads unambiguously at a glance from 1.5m out.
        // The Exit line's target changes if _returnSceneName is edited at
        // authoring time, so this is built once (cached in _guideActionsTextCached)
        // rather than a compile-time const like before — it never changes again
        // at runtime, so this still never causes a layout shift.
        private string _guideActionsTextCached;

        private string BuildGuideActionsText()
        {
            string exitLine = string.IsNullOrEmpty(_returnSceneName)
                ? "Exit: (no scene set)"
                : $"Exit → {_returnSceneName}";
            return
                "R-A/B: player +/-\n" +
                "L-X/Y: group +/-\n" +
                "R-Trigger: apply + test\n" +
                "L-Trigger: test\n" +
                "R-Stick/Menu/Esc: " + exitLine;
        }

        private void BuildGuideText()
        {
            // Scale matches the enlarged HapbeatAddressOverridePanel default
            // (world-space physical size = worldWidth/worldHeight regardless
            // of scale — scale only trades off UI-pixel canvas resolution
            // against how large each fixed-pixel-sized font/element renders).
            // Bumped ~1.8x over the original 0.7m/0.001 pair for comfortable
            // reading at the rig's 1.5m panel distance, and widened/heightened
            // to fit the diagnostic + Exit lines added below (6 lines total).
            const float scale = 0.0018f;
            const float worldWidth = 1.0f;
            const float worldHeight = 0.55f;

            var canvasGo = new GameObject("VRBasicExampleGuideCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;

            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(worldWidth / scale, worldHeight / scale);
            canvasGo.transform.localScale = Vector3.one * scale;

            // Anchor just below the Address Override panel so both stay
            // legible together in-headset; fall back to a spot in front of
            // this rig if no panel is wired. Offset grown along with both
            // panels' larger footprints so there's still a clear gap between
            // them (was -0.2 when the panel was half this height).
            if (_panel != null)
            {
                canvasGo.transform.position = _panel.transform.position + new Vector3(0f, -0.5f, 0f);
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
        /// Adds a clickable "Exit" Button to the guide canvas alongside the
        /// controller/keyboard bindings in <see cref="BuildExitAction"/> — lets
        /// this "settings check" scene be exited with a mouse click too (e.g.
        /// desktop Editor testing without a headset attached). Requires a
        /// <see cref="GraphicRaycaster"/> + <see cref="Canvas.worldCamera"/> on
        /// this world-space canvas (added here); actually clicking it via a VR
        /// laser pointer would additionally need an XR ray interactor, which is
        /// out of scope for this minimal verification rig — the controller
        /// stick-click/menu and keyboard Esc bindings remain the primary way to
        /// exit in a headset.
        /// </summary>
        private void BuildExitButton(GameObject canvasGo)
        {
            if (EventSystem.current == null)
            {
                Debug.LogWarning("[Hapbeat] VRBasicExampleController: No EventSystem found in scene — " +
                    "the on-screen Exit button will not receive clicks (controller stick-click/menu and " +
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
        // Diagnostic line (right/left controller bind resolution)
        // ---------------------------------------------------------------

        private const string DiagnosticPlaceholder = "R:-- L:--";
        private const string EnableOculusTouchHint = "enable Oculus Touch profile (OpenXR)";

        /// <summary>
        /// Refreshes the guide's trailing diagnostic line with whether the
        /// right/left controller bindings currently resolve to a device
        /// (<see cref="InputAction.controls"/> non-empty). Each hand is
        /// checked via one representative action for that hand's physical
        /// device binding (<c>&lt;XRController&gt;{RightHand/LeftHand}/...</c>)
        /// — if that resolves, the other actions on the same hand/device do
        /// too. Always writes the same fixed line (text only, no line
        /// added/removed) so this never shifts the guide's layout.
        /// </summary>
        private void RefreshDiagnosticLine()
        {
            if (_guideText == null) return;

            bool rightOk = _rightPlayerUpAction != null && _rightPlayerUpAction.controls.Count > 0;
            bool leftOk = _leftGroupUpAction != null && _leftGroupUpAction.controls.Count > 0;

            string diagnostic = $"R:{(rightOk ? "OK" : "--")} L:{(leftOk ? "OK" : "--")}";
            if (!rightOk && !leftOk)
                diagnostic += $" ({EnableOculusTouchHint})";

            _guideText.text = _guideActionsTextCached + "\n" + diagnostic;
        }
    }
}
