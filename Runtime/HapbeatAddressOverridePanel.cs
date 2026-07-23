using System;
using System.Collections;
using System.Collections.Generic;
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
    /// The panel is a single all-GUI control surface: +/- steppers pick a
    /// Player / Group number (1..99, or below 1 = disabled), a Play button
    /// fires whatever test-playback callback an external controller wires to
    /// <see cref="OnPlayRequested"/>, Apply calls
    /// <see cref="HapbeatManager.SetAddressOverride(int, int, bool)"/> with
    /// <c>persist: true</c>, and Exit fires whatever scene-transition callback
    /// is wired to <see cref="OnExitRequested"/> — the panel itself has no
    /// notion of test triggers or scenes, that logic is injected by the
    /// caller (see <c>VRConfigExampleController</c>). A single status line
    /// shows a live preview of what <c>player_1/pos_chest/group_1</c> resolves
    /// to via <see cref="HapbeatClient.ResolveTarget(string, int, int)"/> for
    /// the currently-edited values — highlighted while it differs from what's
    /// actually applied, plain once Apply catches it up.
    /// </para>
    ///
    /// <para>
    /// <b>2D focus-navigation grid.</b> Every button the panel builds (the
    /// four steppers, Play, Apply, Exit) is registered into a shared
    /// column/row grid via <see cref="RegisterFocusable"/> — external buttons
    /// can be added to the same grid the same way. <see cref="MoveFocus"/>
    /// steps the focused cell by one grid unit in a cardinal direction (no
    /// diagonals — callers should resolve a dominant axis first), and
    /// <see cref="ActivateFocused"/> invokes whichever button currently holds
    /// focus. The first button registered is focused immediately (internal
    /// state only), but the yellow focus <i>highlight</i> itself stays hidden
    /// until focus-grid navigation is actually in use — see
    /// <see cref="ShowFocusHighlight"/> — so mouse/touch-only consumers (e.g.
    /// Showcase's AddressOverrideDemo) never see a stray highlight that
    /// nothing is driving. VR-style callers that want the highlight visible
    /// from the very first frame (e.g. <c>VRConfigExampleController</c>) call
    /// <see cref="ShowFocusHighlight"/> once at startup instead of waiting for
    /// the first nav input.
    /// Activating any button briefly flashes it near-white
    /// (<see cref="ActionFlashSeconds"/>s) as confirmation, which matters most
    /// for VR controller input where there's no other click feedback.
    /// </para>
    ///
    /// <para>
    /// <see cref="PlayerUp"/> / <see cref="PlayerDown"/> / <see cref="GroupUp"/> /
    /// <see cref="GroupDown"/> / <see cref="Apply"/> remain public so external
    /// controllers (VR input bindings, custom UI, UnityEvents) can drive this
    /// panel directly in addition to the focus-grid path.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Address Override Panel")]
    public class HapbeatAddressOverridePanel : MonoBehaviour
    {
        private const string PreviewTarget = "player_1/pos_chest/group_1";

        // Player/Group value labels are always highlighted in this color so
        // they read as "the variable part" at a glance — distinct from their
        // white "Player:"/"Group:" labels. Also used for the status line while
        // the edited values differ from what's actually applied (see
        // RefreshLabels), so the same color consistently means
        // "edited/not-yet-applied" throughout the panel.
        private static readonly Color s_variableColor = new Color(1f, 0.85f, 0.2f);

        // Rich-text hex form of s_variableColor, used to highlight only the
        // segments of the status line's resolved-target preview that actually
        // changed relative to PreviewTarget (see RefreshLabels / BuildDiffHighlightedRichText).
        private static readonly string s_variableColorHex = ColorUtility.ToHtmlStringRGB(s_variableColor);

        // Focused-button background tint (see ApplyFocusVisual). Translucent
        // yellow, distinct from the buttons' plain translucent-white base so
        // the focused cell reads clearly against the dark panel backdrop.
        private static readonly Color s_focusHighlightColor = new Color(1f, 0.85f, 0.2f, 0.55f);

        // Activation flash — near-white rather than the old near-black flash
        // so it stands out against the panel's dark backdrop instead of
        // blending into it. Fires whenever any registered button's onClick
        // runs (see RegisterFocusable), regardless of whether it was clicked
        // directly or activated via ActivateFocused.
        private static readonly Color s_actionFlashColor = new Color(1f, 1f, 1f, 0.95f);
        private const float ActionFlashSeconds = 0.2f;

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

        // --- 2D focus-navigation grid ---

        private struct FocusEntry
        {
            public Button Button;
            public Image Image;
            public Color BaseColor;
        }

        // Column (x) / row (y) → registered button. Row 0 is topmost. See
        // RegisterFocusable / MoveFocus / ActivateFocused.
        private readonly Dictionary<Vector2Int, FocusEntry> _focusEntries = new Dictionary<Vector2Int, FocusEntry>();
        private Vector2Int _focusedCoord;
        private bool _hasFocus;

        // Gates the *visible* highlight independently of _hasFocus (see
        // ShowFocusHighlight) — focus is always tracked internally from the
        // moment the first button is registered, but nothing is painted with
        // s_focusHighlightColor until this is set, so desktop/mouse-only usage
        // (no focus-grid nav) never shows a highlight nothing is driving.
        private bool _focusHighlightVisible;
        private Coroutine _flashCoroutine;

        /// <summary>
        /// Invoked when the Play button is activated (click or
        /// <see cref="ActivateFocused"/>). The panel has no built-in notion of
        /// "test playback" — an external controller wires this to whatever
        /// test-trigger logic its scene uses.
        /// </summary>
        public event Action OnPlayRequested;

        /// <summary>
        /// Invoked when the Exit button is activated. As with
        /// <see cref="OnPlayRequested"/>, scene-transition logic is injected
        /// by the external controller — the panel itself has no notion of scenes.
        /// </summary>
        public event Action OnExitRequested;

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

            // Every (re-)activation starts focus back on the first button —
            // there is no "remembered" focus across enable/disable cycles.
            if (_focusEntries.Count > 0)
                SetFocusedCoord(new Vector2Int(0, 0));
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
            // raycastTarget = false: this backdrop only exists to visually
            // frame the panel. Only the Buttons' own Images need to
            // intercept clicks — every other Graphic here (background, labels,
            // value/status text) must stay non-blocking so it can never
            // shadow-raycast over UI that happens to render at the same
            // screen position (see CreateText's raycastTarget default below).
            var panel = CreatePanel(canvasGo.transform, "Panel", new Color(0f, 0f, 0f, 0.6f));
            panel.GetComponent<Image>().raycastTarget = false;
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
                // status top-right). Kept compact so it never covers them.
                panelRt.anchorMin = new Vector2(0.5f, 1f);
                panelRt.anchorMax = new Vector2(0.5f, 1f);
                panelRt.pivot = new Vector2(0.5f, 1f);
                panelRt.sizeDelta = new Vector2(300f, 180f);
                // Top-anchored pivot: y is measured downward from the anchor, so a
                // small negative offset nudges the panel just below the screen edge.
                panelRt.anchoredPosition = new Vector2(0f, -8f);
            }

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var titleText = CreateText(panel.transform, "Title", "Address Override", 11, FontStyle.Bold, TextAnchor.MiddleLeft);
            var titleLayoutElement = titleText.gameObject.AddComponent<LayoutElement>();
            titleLayoutElement.minHeight = 14f;
            titleLayoutElement.preferredHeight = 14f;

            // Grid layout (column = x, row = y):
            //   row 0: Player -   Player +
            //   row 1: Group -    Group +
            //   row 2: Play       Apply
            //   row 3: Exit
            _playerValueText = CreateStepperRow(panel.transform, "Player", 0, PlayerDown, PlayerUp);
            _groupValueText = CreateStepperRow(panel.transform, "Group", 1, GroupDown, GroupUp);

            var actionRow = CreateFullWidthRow(panel.transform, "ActionRow");
            CreateRowButton(actionRow, "PlayButton", "Play", new Vector2Int(0, 2), () => OnPlayRequested?.Invoke());
            CreateRowButton(actionRow, "ApplyButton", "Apply", new Vector2Int(1, 2), Apply);

            var exitRow = CreateFullWidthRow(panel.transform, "ExitRow");
            CreateRowButton(exitRow, "ExitButton", "Exit", new Vector2Int(0, 3), () => OnExitRequested?.Invoke());

            // Fixed-height, single-line status so editing/applying never shifts
            // the panel size — see workspace layout-shift rule. Never wraps
            // (Overflow, not Wrap) so it stays exactly one line.
            _statusText = CreateText(panel.transform, "Status", "", 12, FontStyle.Normal, TextAnchor.UpperLeft);
            _statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _statusText.verticalOverflow = VerticalWrapMode.Overflow;
            // Per-segment diff highlighting (RefreshLabels) wraps only the
            // changed segments in <color=#...> tags — the arrow's left side
            // (PreviewTarget) is never wrapped, so it always renders plain white.
            _statusText.supportRichText = true;
            var statusLayoutElement = _statusText.gameObject.AddComponent<LayoutElement>();
            statusLayoutElement.minHeight = 16f;
            statusLayoutElement.preferredHeight = 16f;
        }

        private Text CreateStepperRow(Transform parent, string label, int row, UnityEngine.Events.UnityAction onDec, UnityEngine.Events.UnityAction onInc)
        {
            var rowGo = new GameObject(label + "Row", typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);

            var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 6f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            var rowElement = rowGo.AddComponent<LayoutElement>();
            rowElement.preferredHeight = 26f;

            var labelText = CreateText(rowGo.transform, "Label", label + ":", 13, FontStyle.Normal, TextAnchor.MiddleLeft);
            var labelLayoutElement = labelText.gameObject.AddComponent<LayoutElement>();
            labelLayoutElement.preferredWidth = 60f;

            var decButton = CreateSmallButton(rowGo.transform, "Dec", "-", onDec);
            RegisterFocusable(new Vector2Int(0, row), decButton);

            // Value label toggles between a short digit ("3") and the longer
            // word "disabled". Both states share one fixed alignment
            // (MiddleCenter) and one fixed rect/font size (no bestFit) so the
            // glyphs never move — but that alone isn't enough: "disabled" is
            // wide enough to wrap onto a 2nd line at the old 48px width, and a
            // wrapped 2-line block centered (MiddleCenter) in a fixed-height
            // rect renders at a different vertical offset than a 1-line block,
            // which is what actually read as "vertical center shifts between
            // states". Widening the column and forcing Overflow (never Wrap)
            // keeps every state single-line so MiddleCenter centers identically.
            var valueText = CreateText(rowGo.transform, "Value", "-", 13, FontStyle.Bold, TextAnchor.MiddleCenter);
            valueText.color = s_variableColor; // the variable part — always highlighted, unlike the "Player:"/"Group:" label
            valueText.horizontalOverflow = HorizontalWrapMode.Overflow;
            valueText.verticalOverflow = VerticalWrapMode.Overflow;
            var valueLayoutElement = valueText.gameObject.AddComponent<LayoutElement>();
            valueLayoutElement.preferredWidth = 70f;
            valueLayoutElement.preferredHeight = 26f; // fixed rect height in both states, matches the row height

            var incButton = CreateSmallButton(rowGo.transform, "Inc", "+", onInc);
            RegisterFocusable(new Vector2Int(1, row), incButton);

            return valueText;
        }

        /// <summary>Full-width horizontal row (its buttons share the row's width equally) — used for the Play/Apply and Exit rows.</summary>
        private static Transform CreateFullWidthRow(Transform parent, string name)
        {
            var rowGo = new GameObject(name, typeof(RectTransform));
            rowGo.transform.SetParent(parent, false);
            var rowLayout = rowGo.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 6f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = true;
            rowLayout.childForceExpandHeight = false;
            var rowElement = rowGo.AddComponent<LayoutElement>();
            rowElement.preferredHeight = 26f;
            return rowGo.transform;
        }

        /// <summary>Creates a full-width row button, wires <paramref name="onClick"/>, and registers it into the focus grid at <paramref name="coord"/>.</summary>
        private Button CreateRowButton(Transform rowParent, string name, string label, Vector2Int coord, UnityEngine.Events.UnityAction onClick)
        {
            var button = CreateButton(rowParent, name, label);
            button.onClick.AddListener(onClick);
            RegisterFocusable(coord, button);
            return button;
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }

        // raycastTarget = false by default: every label/value/status Text this
        // panel creates is purely informational, never itself the target of a
        // click. Unity's Text component defaults raycastTarget to true, which
        // would otherwise let a Text's rect silently intercept a pointer event
        // meant for whatever renders at that same screen position (see the
        // Build()-time comment above the Panel background Image).
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
            text.raycastTarget = false;
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

        // ---------------------------------------------------------------
        // 2D focus-navigation grid
        // ---------------------------------------------------------------

        /// <summary>
        /// Registers <paramref name="button"/> into the panel's 2D
        /// focus-navigation grid at <paramref name="coord"/> (column = x, row
        /// = y — row 0 is topmost). External controllers can register their
        /// own buttons here so <see cref="MoveFocus"/> / <see cref="ActivateFocused"/>
        /// drive them the same way as the panel's own steppers/Play/Apply/Exit.
        /// The first button ever registered becomes focused immediately, but
        /// that's internal state only — see <see cref="ShowFocusHighlight"/>
        /// for when the visible highlight itself turns on.
        /// Re-registering an existing coord replaces its entry. No-op if
        /// <paramref name="button"/> is null.
        /// </summary>
        public void RegisterFocusable(Vector2Int coord, Button button)
        {
            if (button == null) return;

            var image = button.GetComponent<Image>();
            bool wasEmpty = _focusEntries.Count == 0;

            _focusEntries[coord] = new FocusEntry
            {
                Button = button,
                Image = image,
                BaseColor = image != null ? image.color : Color.white,
            };

            // Flash feedback on every activation of this button, regardless of
            // whether it was reached via a direct click or via ActivateFocused —
            // added once at registration time, not per-activation.
            button.onClick.AddListener(() => FlashButton(coord));

            if (wasEmpty)
                SetFocusedCoord(coord);
        }

        /// <summary>
        /// Steps the focused grid cell by exactly one unit in a cardinal
        /// direction (e.g. <c>Vector2Int.up</c>/<c>down</c>/<c>left</c>/<c>right</c>).
        /// Diagonals aren't resolved specially — pass a single dominant axis
        /// per call (callers driving an analog stick should pick whichever
        /// axis has the larger magnitude). Vertical moves jump to the nearest
        /// row that has an entry in the pressed direction, preferring the
        /// column closest to the current one (so moving down from a
        /// single-column row like Exit still lands somewhere sensible).
        /// Horizontal moves stay within the current row. No-op if there's
        /// nothing to move to in that direction, or if the grid is empty.
        /// </summary>
        public void MoveFocus(Vector2Int dir)
        {
            if (_focusEntries.Count == 0 || dir == Vector2Int.zero) return;

            // Real nav-grid usage has begun — reveal the highlight (no-op if
            // already visible, e.g. via an explicit ShowFocusHighlight() call).
            ShowFocusHighlight();

            Vector2Int? best = null;
            int bestPrimaryDist = int.MaxValue;
            int bestSecondaryDist = int.MaxValue;

            foreach (var coord in _focusEntries.Keys)
            {
                if (coord == _focusedCoord) continue;

                if (dir.x != 0)
                {
                    // Horizontal: same row only, strictly in the pressed direction.
                    if (coord.y != _focusedCoord.y) continue;
                    int dx = coord.x - _focusedCoord.x;
                    if (Mathf.Sign(dx) != Mathf.Sign(dir.x)) continue;
                    int dist = Mathf.Abs(dx);
                    if (dist < bestPrimaryDist)
                    {
                        bestPrimaryDist = dist;
                        best = coord;
                    }
                }
                else
                {
                    // Vertical: any row strictly in the pressed direction; nearest
                    // row first, then nearest column within that row.
                    int dy = coord.y - _focusedCoord.y;
                    if (Mathf.Sign(dy) != Mathf.Sign(dir.y)) continue;
                    int rowDist = Mathf.Abs(dy);
                    int colDist = Mathf.Abs(coord.x - _focusedCoord.x);
                    if (rowDist < bestPrimaryDist || (rowDist == bestPrimaryDist && colDist < bestSecondaryDist))
                    {
                        bestPrimaryDist = rowDist;
                        bestSecondaryDist = colDist;
                        best = coord;
                    }
                }
            }

            if (best.HasValue)
                SetFocusedCoord(best.Value);
        }

        /// <summary>Invokes whichever button currently holds focus (see <see cref="MoveFocus"/>), same as a direct click. No-op if nothing is focused yet.</summary>
        public void ActivateFocused()
        {
            // Real nav-grid usage has begun — reveal the highlight (no-op if
            // already visible, e.g. via an explicit ShowFocusHighlight() call).
            ShowFocusHighlight();

            if (_hasFocus && _focusEntries.TryGetValue(_focusedCoord, out var entry) && entry.Button != null)
                entry.Button.onClick.Invoke();
        }

        /// <summary>
        /// Reveals the 2D focus-navigation grid's yellow highlight (see class
        /// doc and <see cref="ApplyFocusVisual"/>). Focus is always tracked
        /// internally from the moment the first button is registered (so
        /// <see cref="ActivateFocused"/> always has a valid target), but no
        /// button is painted with the highlight color until this is called —
        /// this keeps mouse/touch-only consumers (e.g. Showcase's
        /// AddressOverrideDemo) free of a highlight nothing is driving.
        /// <see cref="MoveFocus"/> and <see cref="ActivateFocused"/> both call
        /// this on first use, so most focus-grid controllers never need to
        /// call it directly; VR-style callers that want the highlight visible
        /// from the very first frame (e.g. <c>VRConfigExampleController</c>)
        /// should call it explicitly at startup instead of waiting for the
        /// first nav input. Idempotent — safe to call more than once.
        /// </summary>
        public void ShowFocusHighlight()
        {
            if (_focusHighlightVisible) return;
            _focusHighlightVisible = true;
            ApplyFocusVisual();
        }

        private void SetFocusedCoord(Vector2Int coord)
        {
            if (!_focusEntries.ContainsKey(coord)) return;
            _focusedCoord = coord;
            _hasFocus = true;
            ApplyFocusVisual();
        }

        private void ApplyFocusVisual()
        {
            foreach (var kvp in _focusEntries)
            {
                if (kvp.Value.Image == null) continue;
                bool showHighlight = _focusHighlightVisible && _hasFocus && kvp.Key == _focusedCoord;
                kvp.Value.Image.color = showHighlight ? s_focusHighlightColor : kvp.Value.BaseColor;
            }
        }

        private void FlashButton(Vector2Int coord)
        {
            if (!_focusEntries.TryGetValue(coord, out var entry) || entry.Image == null) return;
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            _flashCoroutine = StartCoroutine(FlashRoutine(entry.Image));
        }

        private IEnumerator FlashRoutine(Image image)
        {
            image.color = s_actionFlashColor;
            yield return new WaitForSecondsRealtime(ActionFlashSeconds);
            // Restore via ApplyFocusVisual (not the captured base color) so the
            // right end-state applies whether or not this button is still
            // focused (e.g. a mouse click on a non-focused button shouldn't
            // end up looking focused, and a focused button should return to
            // its highlight, not its plain base color).
            ApplyFocusVisual();
            _flashCoroutine = null;
        }

        /// <summary>Step the editing Player number down. Wireable from UnityEvents / external controllers.</summary>
        public void PlayerDown() { _editingPlayer = StepDown(_editingPlayer); RefreshLabels(); DeselectEventSystem(); }

        /// <summary>Step the editing Player number up. Wireable from UnityEvents / external controllers.</summary>
        public void PlayerUp() { _editingPlayer = StepUp(_editingPlayer); RefreshLabels(); DeselectEventSystem(); }

        /// <summary>Step the editing Group number down. Wireable from UnityEvents / external controllers.</summary>
        public void GroupDown() { _editingGroup = StepDown(_editingGroup); RefreshLabels(); DeselectEventSystem(); }

        /// <summary>Step the editing Group number up. Wireable from UnityEvents / external controllers.</summary>
        public void GroupUp() { _editingGroup = StepUp(_editingGroup); RefreshLabels(); DeselectEventSystem(); }

        /// <summary>
        /// Clears the EventSystem's <c>currentSelectedGameObject</c> after this panel's
        /// buttons are clicked. Every other interactive control in the Showcase (e.g.
        /// GainSlider/PanSlider — see <c>UiDeselectOnPointerUp</c>) already deselects
        /// itself on pointer-up so a clicked Selectable never keeps UI focus; these
        /// stepper/Apply buttons were the one exception, leaving a button selected
        /// indefinitely after use (Unity's default Button behavior). A lingering
        /// selection is exactly the state <c>UiDeselectOnPointerUp</c> exists
        /// to avoid elsewhere in this scene — with the Input System's UI module, a
        /// stale <c>currentSelectedGameObject</c> can absorb/redirect subsequent
        /// pointer input intended for other UI (e.g. the Z4 Gain/Pan sliders) instead
        /// of just being a keyboard-navigation nuisance. Clearing it here keeps this
        /// panel consistent with the rest of the zone and removes that residue as a
        /// possible cause entirely, regardless of exact Input System internals.
        /// </summary>
        private static void DeselectEventSystem()
        {
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(null);
        }

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
            DeselectEventSystem();
        }

        private void RefreshLabels()
        {
            if (_playerValueText != null) _playerValueText.text = LabelFor(_editingPlayer);
            if (_groupValueText != null) _groupValueText.text = LabelFor(_editingGroup);

            var mgr = HapbeatManager.Instance;
            int appliedPlayer = mgr != null ? mgr.OverridePlayer : -1;
            int appliedGroup = mgr != null ? mgr.OverrideGroup : -1;

            string editingResolved = HapbeatClient.ResolveTarget(PreviewTarget, _editingPlayer, _editingGroup);

            // A single status line showing where the currently-*edited* values
            // would resolve to. The left side (PreviewTarget) is always plain
            // white. On the right side, only the segments that actually
            // differ from PreviewTarget are highlighted yellow — not the
            // whole line — so it's obvious at a glance exactly which slot(s)
            // Apply would change. Once the edited values match what's already
            // applied, the whole line renders plain (no highlighting). Never a
            // second line, so Apply never shifts the panel size (see workspace
            // layout-shift rule).
            bool pendingApply = _editingPlayer != appliedPlayer || _editingGroup != appliedGroup;

            if (_statusText != null)
            {
                string rightSide = pendingApply
                    ? BuildDiffHighlightedRichText(PreviewTarget, editingResolved)
                    : editingResolved;
                _statusText.text = $"{PreviewTarget} → {rightSide}";
                _statusText.color = Color.white; // base color; per-segment <color> tags do the highlighting
            }
        }

        /// <summary>
        /// Builds a rich-text version of <paramref name="resolved"/> where only the
        /// '/'-separated segments that differ from the segment at the same position
        /// in <paramref name="original"/> (including segments <paramref name="resolved"/>
        /// has that <paramref name="original"/> doesn't) are wrapped in
        /// <c>&lt;color=#...&gt;</c> tags using <see cref="s_variableColor"/>.
        /// Requires the target <c>Text</c> to have <c>supportRichText = true</c>.
        /// </summary>
        private static string BuildDiffHighlightedRichText(string original, string resolved)
        {
            string[] originalSegs = (original ?? string.Empty).Split('/');
            string[] resolvedSegs = (resolved ?? string.Empty).Split('/');

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < resolvedSegs.Length; i++)
            {
                if (i > 0) sb.Append('/');

                string seg = resolvedSegs[i];
                bool changed = i >= originalSegs.Length || originalSegs[i] != seg;
                if (changed)
                    sb.Append("<color=#").Append(s_variableColorHex).Append('>').Append(seg).Append("</color>");
                else
                    sb.Append(seg);
            }
            return sb.ToString();
        }

        private static string LabelFor(int value) => value >= 1 ? value.ToString() : "disabled";
    }
}
