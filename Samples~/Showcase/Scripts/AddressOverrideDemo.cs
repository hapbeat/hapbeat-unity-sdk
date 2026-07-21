using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Z4_Stream demo of the global Player / Group address override
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
    /// </summary>
    public class AddressOverrideDemo : MonoBehaviour
    {
        private const string PreviewTarget = "player_1/pos_chest";

        private int _editingPlayer = -1;
        private int _editingGroup = -1;

        private Text _playerValueText;
        private Text _groupValueText;
        private Text _statusText;

        private bool _built;

        private void OnEnable()
        {
            if (!_built)
            {
                Build();
                _built = true;
            }

            // Reflect whatever is currently active (config default or a
            // PlayerPrefs-restored value from a previous session) so the
            // editing steppers start from the real, in-effect state.
            var mgr = HapbeatManager.Instance;
            if (mgr != null)
            {
                _editingPlayer = mgr.OverridePlayer;
                _editingGroup = mgr.OverrideGroup;
            }

            RefreshLabels();
        }

        private void Build()
        {
            if (EventSystem.current == null)
            {
                Debug.LogWarning("[AddressOverrideDemo] No EventSystem found in scene — " +
                    "buttons will not receive clicks. Add one (e.g. via a Canvas) if this " +
                    "panel needs to be interactive.");
            }

            // --- Canvas (child of self, so it toggles with this GameObject / zone) ---
            var canvasGo = new GameObject("AddressOverrideCanvas");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Panel: bottom-right corner, fixed size ---
            var panel = CreatePanel(canvasGo.transform, "Panel", new Color(0f, 0f, 0f, 0.6f));
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(1f, 0f);
            panelRt.anchorMax = new Vector2(1f, 0f);
            panelRt.pivot = new Vector2(1f, 0f);
            panelRt.sizeDelta = new Vector2(340f, 190f);
            panelRt.anchoredPosition = new Vector2(-16f, 16f);

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 10);
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateText(panel.transform, "Title", "Address Override", 16, FontStyle.Bold, TextAnchor.MiddleLeft);

            _playerValueText = CreateStepperRow(panel.transform, "Player", DecPlayer, IncPlayer);
            _groupValueText = CreateStepperRow(panel.transform, "Group", DecGroup, IncGroup);

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

        private void DecPlayer() { _editingPlayer = StepDown(_editingPlayer); RefreshLabels(); }
        private void IncPlayer() { _editingPlayer = StepUp(_editingPlayer); RefreshLabels(); }
        private void DecGroup() { _editingGroup = StepDown(_editingGroup); RefreshLabels(); }
        private void IncGroup() { _editingGroup = StepUp(_editingGroup); RefreshLabels(); }

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

        private void Apply()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null)
            {
                Debug.LogWarning("[AddressOverrideDemo] HapbeatManager.Instance is null — cannot apply.");
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
