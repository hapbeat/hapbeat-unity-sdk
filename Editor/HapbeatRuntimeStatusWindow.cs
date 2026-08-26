#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Standalone per-machine status window: Address Override (saved / active),
    /// the resolved App Name that will actually be shown on the device OLED, and
    /// (Play mode only) connection / discovered-device state.
    /// <para>
    /// Complements <see cref="HapbeatManagerEditor"/>'s compact inline box —
    /// this window is the full view, reachable from the inspector's
    /// "Open Runtime Status" button or directly via the menu. All sections are
    /// drawn unconditionally (rows always present, values swapped for
    /// placeholder/"—" text) so nothing shifts layout as state changes.
    /// </para>
    /// </summary>
    public class HapbeatRuntimeStatusWindow : EditorWindow
    {
        [MenuItem("Hapbeat/Open Runtime Status", false, 13)]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatRuntimeStatusWindow>("Hapbeat Runtime Status");
            window.minSize = new Vector2(360, 320);
        }

        private void OnGUI()
        {
            // Loaded once per repaint and threaded through — avoids a second
            // Resources.Load in the Connection section just for the port number.
            var config = Resources.Load<HapbeatConfig>("HapbeatConfig");

            DrawAddressOverrideSection();
            EditorGUILayout.Space(10);
            DrawAppNameSection(config);
            EditorGUILayout.Space(10);
            DrawConnectionSection(config);
        }

        private void OnInspectorUpdate()
        {
            // Connection / device state changes continuously while playing —
            // repaint on the window's own timer instead of relying on external
            // events (mirrors HapbeatManagerEditor.RequiresConstantRepaint).
            if (Application.isPlaying)
                Repaint();
        }

        // ── Address Override ────────────────────────────────────────────

        private void DrawAddressOverrideSection()
        {
            // DrawFull already renders its own "Override Addressing (this device)"
            // bold heading — no separate section header needed here.
            HapbeatAddressOverrideStatusGUI.DrawFull(HapbeatManager.Instance, Repaint);

            EditorGUILayout.Space(6);
            DrawEditableOverrideRow();
        }

        // Session-local edit buffer for the "Save to this device" row below.
        // int.MinValue is the sentinel for "not yet initialized from the saved
        // value" — see DrawEditableOverrideRow. Lets the user edit Player/Group
        // directly (works outside Play Mode, unlike the +/- panel which only
        // exists at runtime) without needing to enter Play Mode first.
        private int _editPlayerField = int.MinValue;
        private int _editGroupField = int.MinValue;

        private void DrawEditableOverrideRow()
        {
            if (_editPlayerField == int.MinValue || _editGroupField == int.MinValue)
            {
                HapbeatManager.TryGetPersistedAddressOverride(out int savedPlayer, out int savedGroup);
                _editPlayerField = savedPlayer;
                _editGroupField = savedGroup;
            }

            // An axis pinned by the build isn't per-device state at all: its field
            // shows the forced value read-only, and Save skips it (see below).
            HapbeatAddressOverrideStatusGUI.GetBuildOverride(HapbeatManager.Instance, out int buildPlayer, out int buildGroup);
            bool playerForced = buildPlayer >= 1;
            bool groupForced = buildGroup >= 1;

            EditorGUILayout.LabelField("Edit (works outside Play Mode too)", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Player", GUILayout.Width(50));
            using (new EditorGUI.DisabledGroupScope(playerForced))
            {
                int shownPlayer = playerForced ? buildPlayer : _editPlayerField;
                shownPlayer = EditorGUILayout.IntField(shownPlayer, GUILayout.Width(50));
                if (!playerForced) _editPlayerField = shownPlayer;
            }
            GUILayout.Space(10);
            EditorGUILayout.LabelField("Group", GUILayout.Width(50));
            using (new EditorGUI.DisabledGroupScope(groupForced))
            {
                int shownGroup = groupForced ? buildGroup : _editGroupField;
                shownGroup = EditorGUILayout.IntField(shownGroup, GUILayout.Width(50));
                if (!groupForced) _editGroupField = shownGroup;
            }
            EditorGUILayout.EndHorizontal();

            // One line in every state (text swaps, no row appears/disappears).
            EditorGUILayout.LabelField(
                playerForced && groupForced
                    ? "(both axes forced by the build — nothing to save on this device)"
                    : (playerForced || groupForced)
                        ? $"(the {(playerForced ? "Player" : "Group")} axis is forced by the build and is not saved here)"
                        : "(< 1 = disabled; normalized to -1 outside 1..99 on Save)",
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledGroupScope(playerForced && groupForced))
            {
                if (GUILayout.Button("Save to this device"))
                {
                    int normPlayer = HapbeatClient.NormalizeOverride(_editPlayerField);
                    int normGroup = HapbeatClient.NormalizeOverride(_editGroupField);

                    if (Application.isPlaying && HapbeatManager.Instance != null)
                    {
                        // Play mode: go through the manager so the live client / any
                        // connected device (CONNECT_STATUS) picks it up immediately,
                        // not just PlayerPrefs. It also drops any forced axis on its own.
                        HapbeatManager.Instance.SetAddressOverride(normPlayer, normGroup, persist: true);
                    }
                    else
                    {
                        // Same rule as SetAddressOverride: never write a forced axis to
                        // PlayerPrefs, or a later build that un-forces it would inherit
                        // the value silently.
                        if (!playerForced)
                            PlayerPrefs.SetInt(HapbeatManager.PlayerPrefsKeyOverridePlayer, normPlayer);
                        if (!groupForced)
                            PlayerPrefs.SetInt(HapbeatManager.PlayerPrefsKeyOverrideGroup, normGroup);
                        PlayerPrefs.Save();
                    }

                    // Reflect the normalized values back into the edit fields so the
                    // UI doesn't show a stale un-normalized number after Save.
                    _editPlayerField = normPlayer;
                    _editGroupField = normGroup;
                    Repaint();
                }
            }
        }

        // ── App Name ─────────────────────────────────────────────────────

        /// <summary>
        /// Shows the configured appName template and the placeholder-resolved
        /// preview — i.e. the literal string that will be sent to the device
        /// OLED via CONNECT_STATUS.
        /// </summary>
        private void DrawAppNameSection(HapbeatConfig config)
        {
            EditorGUILayout.LabelField("App Name", EditorStyles.boldLabel);

            string template = config != null ? config.appName : null;

            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(template)
                    ? "Template: (no config) — falls back to Application.productName"
                    : $"Template: \"{template}\"",
                EditorStyles.miniLabel);

            string preview;
            if (Application.isPlaying && HapbeatManager.Instance != null)
            {
                preview = HapbeatManager.Instance.AppName;
            }
            else if (!string.IsNullOrEmpty(template))
            {
                // No running instance — compute the same substitution the runtime
                // would perform, using the currently-saved (or disabled) override.
                HapbeatManager.TryGetPersistedAddressOverride(out int savedPlayer, out int savedGroup);
                preview = HapbeatManager.ApplyAddressPlaceholders(template, savedPlayer, savedGroup);
            }
            else
            {
                preview = Application.productName;
            }

            EditorGUILayout.LabelField(
                $"On device (OLED): \"{HapbeatAddressOverrideStatusGUI.Colorize(preview)}\"",
                RichTextMiniLabel);
            EditorGUILayout.LabelField(
                $"   {HapbeatAddressOverrideStatusGUI.Colorize((preview?.Length ?? 0).ToString())} / " +
                $"{HapbeatConfig.MaxAppNameLength} chars — longer strings are truncated by the protocol.",
                RichTextMiniLabel);
        }

        // Rich-text copy of EditorStyles.miniLabel, shared by every row in this
        // window that colorizes a dynamic value via HapbeatAddressOverrideStatusGUI.Colorize.
        private static GUIStyle s_richTextMiniLabel;
        private static GUIStyle RichTextMiniLabel
        {
            get
            {
                if (s_richTextMiniLabel == null)
                    s_richTextMiniLabel = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                return s_richTextMiniLabel;
            }
        }

        // ── Connection ───────────────────────────────────────────────────

        private void DrawConnectionSection(HapbeatConfig config)
        {
            EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);

            bool isPlaying = Application.isPlaying;
            HapbeatManager manager = isPlaying ? HapbeatManager.Instance : null;
            bool isConnected = manager != null && manager.IsConnected;

            if (!isPlaying)
            {
                EditorGUILayout.LabelField("— (enter Play Mode)", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField("Connected", HapbeatAddressOverrideStatusGUI.Colorize(isConnected.ToString()), RichTextMiniLabel);

            if (!isConnected)
                return;

            EditorGUILayout.LabelField("Transport",
                HapbeatAddressOverrideStatusGUI.Colorize("WifiUdp"), RichTextMiniLabel);
            // Port is read from the config asset (public field), not from the
            // internal HapbeatClient — this window only uses HapbeatManager's
            // public surface.
            EditorGUILayout.LabelField("Port",
                HapbeatAddressOverrideStatusGUI.Colorize(config != null ? config.port.ToString() : "-"), RichTextMiniLabel);
            EditorGUILayout.LabelField("Alive devices",
                HapbeatAddressOverrideStatusGUI.Colorize(manager.AliveDeviceCount.ToString()), RichTextMiniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Discovered devices", EditorStyles.miniBoldLabel);
            if (manager.DiscoveredDevices.Count == 0)
            {
                EditorGUILayout.LabelField("(none — call Discover())", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var device in manager.DiscoveredDevices)
                    EditorGUILayout.LabelField($"  {device.name}", HapbeatAddressOverrideStatusGUI.Colorize(device.ipAddress), RichTextMiniLabel);
            }
        }
    }
}
#endif
