using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hapbeat
{
    /// <summary>
    /// Screen-space or world-space rendering surface for <see cref="HapbeatAddressOverridePanel"/>.
    /// </summary>
    public enum HapbeatAddressOverridePanelSpace
    {
        /// <summary>Fixed 2D HUD overlay, anchored to the top-center of the screen. Default.</summary>
        ScreenSpaceOverlay,

        /// <summary>3D panel parented to this GameObject — for VR controllers / world-attached menus.</summary>
        WorldSpace,
    }

    /// <summary>
    /// Runtime UI for the global Player / Group address override
    /// (<see cref="HapbeatManager.SetAddressOverride"/>). Builds a small
    /// self-contained uGUI panel entirely at runtime (no scene wiring beyond
    /// attaching this component) so it can be dropped onto any GameObject
    /// without hand-editing UI hierarchy in the scene.
    ///
    /// <para>
    /// Demonstrates the "one identical build, many HMDs" flow: +/- steppers
    /// pick a Player / Group number (1..99, or below 1 = disabled), Apply
    /// calls <see cref="HapbeatManager.SetAddressOverride(int, int, bool)"/>
    /// with <c>persist: true</c>, and the status text shows both the value
    /// currently being edited and the value actually applied — including a
    /// live preview of what <c>player_1/pos_chest</c> resolves to via
    /// <see cref="HapbeatClient.ResolveTarget(string, int, int)"/>.
    /// </para>
    ///
    /// <para>
    /// <see cref="PlayerUp"/> / <see cref="PlayerDown"/> / <see cref="GroupUp"/> /
    /// <see cref="GroupDown"/> / <see cref="Apply"/> are public so external
    /// controllers (VR input bindings, custom UI, UnityEvents) can drive this
    /// panel without touching the built-in stepper buttons.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Address Override Panel")]
    public class HapbeatAddressOverridePanel : MonoBehaviour
    {
        private const string PreviewTarget = "player_1/pos_chest";

        [Header("Layout")]
        [Tooltip("ScreenSpaceOverlay = fixed 2D HUD (top-center). WorldSpace = 3D panel parented to this GameObject (e.g. for VR).")]
        [SerializeField]
        private HapbeatAddressOverridePanelSpace _space = HapbeatAddressOverridePanelSpace.ScreenSpaceOverlay;

        [Tooltip("World-space panel size in meters (ignored in ScreenSpaceOverlay). Default ~0.5m wide.")]
        [SerializeField]
        private Vector2 _worldSize = new Vector2(0.5f, 0.28f);

        [Tooltip("World-space local position offset relative to this GameObject (ignored in ScreenSpaceOverlay).")]
        [SerializeField]
        private Vector3 _worldLocalPosition = Vector3.zero;

        [Tooltip("World-space canvas scale — world units per UI pixel. Typical: 0.001 (1 UI pixel = 1mm). Ignored in ScreenSpaceOverlay.")]
        [SerializeField]
        private float _worldScale = 0.001f;

        private int _editingPlayer = -1;
        private int _editingGroup = -1;

        private Text _playerValueText;
        private Text _groupValueText;
        private Text _statusText;

        private bool _built;

        // The Canvas this panel builds at runtime. Kept as a field (rather than
        // relying on transform.Find) because Build() may re-parent it to the
        // scene root — see the nested-Canvas guard below — which decouples it
        // from this component's own transform hierarchy.
        private GameObject _canvasGo;

        private void OnEnable()
        {
            if (!_built)
            {
                Build();
                _built = true;
            }

            // Build() may have moved the Canvas out from under this GameObject
            // (nested-Canvas guard) — re-sync its active state explicitly since
            // Unity no longer does that for us via the hierarchy.
            if (_canvasGo != null)
                _canvasGo.SetActive(true);

            // Reflect whatever is currently active (a PlayerPrefs-restored value
            // from a previous session, or disabled) so the editing steppers start
            // from the real, in-effect state.
            var mgr = HapbeatManager.Instance;
            if (mgr != null)
            {
                _editingPlayer = mgr.OverridePlayer;
                _editingGroup = mgr.OverrideGroup;
            }

            RefreshLabels();
        }

        private void OnDisable()
        {
            // Mirror the disable — otherwise a re-parented Canvas (see Build())
            // keeps rendering after this component is disabled, since it's no
            // longer a child that Unity disables automatically.
            if (_canvasGo != null)
                _canvasGo.SetActive(false);
        }

        private void OnDestroy()
        {
            // Same reasoning as OnDisable: once re-parented to the scene root,
            // the Canvas no longer dies with this GameObject on its own.
            if (_canvasGo != null)
            {
                Destroy(_canvasGo);
                _canvasGo = null;
            }
        }

        private void Build()
        {
            if (EventSystem.current == null)
            {
                Debug.LogWarning("[Hapbeat] HapbeatAddressOverridePanel: No EventSystem found in scene — " +
                    "buttons will not receive clicks. Add one (e.g. via a Canvas) if this " +
                    "panel needs to be interactive.");
            }

            // --- Canvas (child of self, so it toggles with this GameObject) ---
            var canvasGo = new GameObject("AddressOverrideCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.sortingOrder = 50;

            // Nested-Canvas guard: a child Canvas inherits its parent Canvas's
            // render settings and cannot have its own RenderMode/anchoring — so
            // if this panel is attached under another Canvas (e.g. a screen-space
            // HUD), our ScreenSpaceOverlay/WorldSpace choice and screen anchors
            // above would silently be ignored and the panel would render wherever
            // the ancestor Canvas positions it. Detect that and re-parent our
            // Canvas to the scene root so it becomes independent, as intended.
            var ancestorCanvas = GetComponentInParent<Canvas>(true);
            if (ancestorCanvas != null && ancestorCanvas.gameObject != canvasGo)
            {
                Debug.LogWarning("[Hapbeat] HapbeatAddressOverridePanel: found under an ancestor Canvas " +
                    $"(\"{ancestorCanvas.name}\") — moved its own Canvas (\"{canvasGo.name}\") to the scene " +
                    "root so it can render as an independent RenderMode/anchor instead of inheriting the " +
                    "parent Canvas's settings.", this);
                canvasGo.transform.SetParent(null, false);
            }
            _canvasGo = canvasGo;

            bool worldSpace = _space == HapbeatAddressOverridePanelSpace.WorldSpace;
            if (worldSpace)
            {
                canvas.renderMode = RenderMode.WorldSpace;
                float scale = Mathf.Max(_worldScale, 0.0001f);
                var canvasRt = canvasGo.GetComponent<RectTransform>();
                canvasRt.sizeDelta = new Vector2(_worldSize.x / scale, _worldSize.y / scale);
                canvasGo.transform.localPosition = _worldLocalPosition;
                canvasGo.transform.localScale = Vector3.one * scale;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            }
            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Panel ---
            var panel = CreatePanel(canvasGo.transform, "Panel", new Color(0f, 0f, 0f, 0.6f));
            var panelRt = panel.GetComponent<RectTransform>();
            if (worldSpace)
            {
                // The canvas itself is already sized to _worldSize — fill it.
                panelRt.anchorMin = Vector2.zero;
                panelRt.anchorMax = Vector2.one;
                panelRt.offsetMin = Vector2.zero;
                panelRt.offsetMax = Vector2.zero;
            }
            else
            {
                // Screen-space: top-center, fixed size, doesn't overlap other
                // top-anchored HUD elements (guide text top-left, connection
                // status top-right).
                panelRt.anchorMin = new Vector2(0.5f, 1f);
                panelRt.anchorMax = new Vector2(0.5f, 1f);
                panelRt.pivot = new Vector2(0.5f, 1f);
                panelRt.sizeDelta = new Vector2(340f, 190f);
                // Top-anchored pivot: y is measured downward from the anchor, so a
                // small negative offset nudges the panel just below the screen edge.
                panelRt.anchoredPosition = new Vector2(0f, -8f);
            }

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateText(panel.transform, "Title", "Address Override", 16, FontStyle.Bold, TextAnchor.MiddleLeft);

            _playerValueText = CreateStepperRow(panel.transform, "Player", PlayerDown, PlayerUp);
            _groupValueText = CreateStepperRow(panel.transform, "Group", GroupDown, GroupUp);

            var applyButton = CreateButton(panel.transform, "ApplyButton", "Apply");
            applyButton.onClick.AddListener(Apply);

            // Fixed-height status block (2 lines reserved) so editing/applying
            // never shifts the panel size — see workspace layout-shift rule.
            _statusText = CreateText(panel.transform, "Status", "", 12, FontStyle.Normal, TextAnchor.UpperLeft);
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _statusText.verticalOverflow = VerticalWrapMode.Overflow;
            var statusLayoutElement = _statusText.gameObject.AddComponent<LayoutElement>();
            statusLayoutElement.minHeight = 34f;
            statusLayoutElement.preferredHeight = 34f;
        }

        private Text CreateStepperRow(Transform parent, string label, UnityEngine.Events.UnityAction onDec, UnityEngine.Events.UnityAction onInc)
        {
            var row = new GameObject(label + "Row", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 6f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            var rowElement = row.AddComponent<LayoutElement>();
            rowElement.preferredHeight = 26f;

            var labelText = CreateText(row.transform, "Label", label + ":", 13, FontStyle.Normal, TextAnchor.MiddleLeft);
            var labelLayoutElement = labelText.gameObject.AddComponent<LayoutElement>();
            labelLayoutElement.preferredWidth = 60f;

            CreateSmallButton(row.transform, "Dec", "-", onDec);

            var valueText = CreateText(row.transform, "Value", "-", 13, FontStyle.Bold, TextAnchor.MiddleCenter);
            var valueLayoutElement = valueText.gameObject.AddComponent<LayoutElement>();
            valueLayoutElement.preferredWidth = 48f;

            CreateSmallButton(row.transform, "Inc", "+", onInc);

            return valueText;
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        private static Text CreateText(Transform parent, string name, string content, int size, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = Color.white;
            text.text = content;
            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.15f);
            var button = go.AddComponent<Button>();
            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = 26f;

            var text = CreateText(go.transform, "Text", label, 13, FontStyle.Bold, TextAnchor.MiddleCenter);
            var textRt = text.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            return button;
        }

        private static Button CreateSmallButton(Transform parent, string name, string label, UnityEngine.Events.UnityAction onClick)
        {
            var button = CreateButton(parent, name, label);
            var layoutElement = button.GetComponent<LayoutElement>();
            layoutElement.preferredWidth = 26f;
            layoutElement.preferredHeight = 26f;
            button.onClick.AddListener(onClick);
            return button;
        }

        /// <summary>Step the editing Player number down. Wireable from UnityEvents / external controllers.</summary>
        public void PlayerDown() { _editingPlayer = StepDown(_editingPlayer); RefreshLabels(); }

        /// <summary>Step the editing Player number up. Wireable from UnityEvents / external controllers.</summary>
        public void PlayerUp() { _editingPlayer = StepUp(_editingPlayer); RefreshLabels(); }

        /// <summary>Step the editing Group number down. Wireable from UnityEvents / external controllers.</summary>
        public void GroupDown() { _editingGroup = StepDown(_editingGroup); RefreshLabels(); }

        /// <summary>Step the editing Group number up. Wireable from UnityEvents / external controllers.</summary>
        public void GroupUp() { _editingGroup = StepUp(_editingGroup); RefreshLabels(); }

        /// <summary>1..99 clamped stepper: 1 → "-" → -1 (disabled). Values are never
        /// clamped/wrapped beyond that — mirrors HapbeatClient.NormalizeOverride's
        /// "outside 1..99 = disabled" rule at the boundary the user can reach.</summary>
        private static int StepDown(int value)
        {
            if (value <= 1) return -1;
            return value - 1;
        }

        /// <summary>-1 (disabled) → "+" → 1, otherwise clamp at 99.</summary>
        private static int StepUp(int value)
        {
            if (value < 1) return 1;
            return Mathf.Min(99, value + 1);
        }

        /// <summary>Applies the currently-edited Player/Group as a persisted address
        /// override (<see cref="HapbeatManager.SetAddressOverride(int, int, bool)"/>
        /// with <c>persist: true</c>). Wireable from UnityEvents / external controllers.</summary>
        public void Apply()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null)
            {
                Debug.LogWarning("[Hapbeat] HapbeatAddressOverridePanel: HapbeatManager.Instance is null — cannot apply.");
                return;
            }
            mgr.SetAddressOverride(_editingPlayer, _editingGroup, persist: true);
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (_playerValueText != null) _playerValueText.text = LabelFor(_editingPlayer);
            if (_groupValueText != null) _groupValueText.text = LabelFor(_editingGroup);

            var mgr = HapbeatManager.Instance;
            int appliedPlayer = mgr != null ? mgr.OverridePlayer : -1;
            int appliedGroup = mgr != null ? mgr.OverrideGroup : -1;

            string editingResolved = HapbeatClient.ResolveTarget(PreviewTarget, _editingPlayer, _editingGroup);
            string appliedResolved = HapbeatClient.ResolveTarget(PreviewTarget, appliedPlayer, appliedGroup);

            if (_statusText != null)
            {
                _statusText.text =
                    $"editing: player={LabelFor(_editingPlayer)} group={LabelFor(_editingGroup)}  |  " +
                    $"例: {PreviewTarget} → {editingResolved}\n" +
                    $"applied: player={LabelFor(appliedPlayer)} group={LabelFor(appliedGroup)}  |  " +
                    $"例: {PreviewTarget} → {appliedResolved}";
            }
        }

        private static string LabelFor(int value) => value >= 1 ? value.ToString() : "disabled";
    }
}
