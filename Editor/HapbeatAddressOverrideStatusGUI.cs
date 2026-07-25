#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Shared drawing logic for the "Override Addressing (this device)" status —
    /// the saved (PlayerPrefs) value and the runtime-active value, plus a
    /// "Clear Saved Override" action. Extracted so <see cref="HapbeatManagerEditor"/>
    /// (compact inline box) and <see cref="HapbeatRuntimeStatusWindow"/> (full
    /// standalone view) don't duplicate the same PlayerPrefs / formatting logic.
    /// <para>
    /// Both rows are drawn unconditionally (never inserted/removed) so callers
    /// never shift surrounding layout — only the text and the button's enabled
    /// state change. See workspace layout-shift rule.
    /// </para>
    /// </summary>
    internal static class HapbeatAddressOverrideStatusGUI
    {
        // ── Value color highlighting ────────────────────────────────────
        //
        // Dynamic values (saved/active player-group numbers, connection info,
        // resolved appName, etc.) are wrapped in a rich-text <color> tag so they
        // stand out from their plain-white/black labels. EditorStyles.miniLabel's
        // richText flag isn't guaranteed across Unity versions/skins, so a
        // dedicated rich-text copy is used for any row that colorizes a value.
        // Hex differs per skin for readable contrast: warm yellow on the dark
        // Pro skin, darker amber-brown on the light Personal skin.
        private static GUIStyle s_miniLabelRichText;

        private static GUIStyle MiniLabelRichText
        {
            get
            {
                if (s_miniLabelRichText == null)
                    s_miniLabelRichText = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                return s_miniLabelRichText;
            }
        }

        private static string ValueColorHex => EditorGUIUtility.isProSkin ? "E5C069" : "8A5A00";

        /// <summary>Wraps <paramref name="value"/> in the shared value-highlight color tag.</summary>
        public static string Colorize(string value) => $"<color=#{ValueColorHex}>{value}</color>";

        /// <summary>-1 (disabled) renders as <paramref name="disabledLabel"/>; otherwise the raw number.</summary>
        public static string FormatOverrideValue(int value, string disabledLabel)
            => value >= 1 ? value.ToString() : disabledLabel;

        /// <summary>
        /// Full block: "Saved on this device" row, "Active (runtime)" row, and a
        /// "Clear Saved Override" button. <paramref name="manager"/> may be null
        /// (Edit mode, no scene instance) — the saved-value row still works via
        /// the static <see cref="HapbeatManager.TryGetPersistedAddressOverride"/>.
        /// </summary>
        public static void DrawFull(HapbeatManager manager, System.Action repaint)
        {
            EditorGUILayout.LabelField("Override Addressing (this device)", EditorStyles.boldLabel);
            DrawBuildRow(manager);
            DrawSavedRow();
            DrawActiveRow(manager);
            DrawClearButton(manager, repaint);
        }

        /// <summary>Compact variant: just the status rows, no button (callers
        /// that want the Clear action point users at the full Runtime Status window instead).</summary>
        public static void DrawCompact(HapbeatManager manager)
        {
            EditorGUILayout.LabelField("Override Addressing (this device)", EditorStyles.boldLabel);
            DrawBuildRow(manager);
            DrawSavedRow();
            DrawActiveRow(manager);
        }

        /// <summary>
        /// Reads the build-wide forced player/group (HapbeatConfig). Prefers a live
        /// <paramref name="manager"/> (it may hold an explicitly assigned config asset)
        /// and falls back to the Resources-loaded default, which is what the runtime
        /// itself loads when nothing is assigned. Values are already normalized:
        /// -1 = this axis is left to the device.
        /// </summary>
        public static void GetBuildOverride(HapbeatManager manager, out int player, out int group)
        {
            if (manager != null)
            {
                player = manager.BuildOverridePlayer;
                group = manager.BuildOverrideGroup;
                return;
            }

            var config = Resources.Load<HapbeatConfig>("HapbeatConfig");
            player = config != null ? HapbeatClient.NormalizeOverride(config.buildOverridePlayer) : -1;
            group = config != null ? HapbeatClient.NormalizeOverride(config.buildOverrideGroup) : -1;
        }

        /// <summary>True when at least one axis is pinned by the build — the point at which
        /// "Save to this device" / "Clear Saved Override" stop being the whole story.</summary>
        public static bool HasAnyBuildOverride(HapbeatManager manager)
        {
            GetBuildOverride(manager, out int player, out int group);
            return player >= 1 || group >= 1;
        }

        /// <summary>Build-wide forced values row. Always drawn (never inserted/removed) —
        /// see the layout-shift note on this class.</summary>
        public static void DrawBuildRow(HapbeatManager manager)
        {
            GetBuildOverride(manager, out int player, out int group);
            EditorGUILayout.LabelField(
                $"Build (forced): player={Colorize(FormatOverrideValue(player, "per-device"))}, " +
                $"group={Colorize(FormatOverrideValue(group, "per-device"))}",
                MiniLabelRichText);
            EditorGUILayout.LabelField(
                player >= 1 || group >= 1
                    ? "   A forced axis always wins over the saved/edited value below and cannot be changed on the device."
                    : "   Set in HapbeatConfig (Hapbeat > Settings > Override Addressing (this build)). 'per-device' = decided by the values below.",
                EditorStyles.miniLabel);
        }

        public static void DrawSavedRow()
        {
            bool hasSaved = HapbeatManager.TryGetPersistedAddressOverride(out int savedPlayer, out int savedGroup);
            string savedText = hasSaved
                ? $"Saved on this device: player={Colorize(FormatOverrideValue(savedPlayer, "(none)"))}, " +
                  $"group={Colorize(FormatOverrideValue(savedGroup, "(none)"))}"
                : "Saved on this device: none";
            EditorGUILayout.LabelField(savedText, MiniLabelRichText);
        }

        public static void DrawActiveRow(HapbeatManager manager)
        {
            string activeText;
            if (Application.isPlaying && manager != null)
            {
                activeText = $"Active (runtime): player={Colorize(FormatOverrideValue(manager.OverridePlayer, "disabled"))}, " +
                             $"group={Colorize(FormatOverrideValue(manager.OverrideGroup, "disabled"))}";
            }
            else
            {
                activeText = "Active (runtime): — (enter Play Mode)";
            }
            EditorGUILayout.LabelField(activeText, MiniLabelRichText);
        }

        /// <summary>Draws the "Clear Saved Override" button, disabled when there's nothing saved.
        /// Goes through <paramref name="manager"/> when a running Play-mode instance is available
        /// so the live override, connected client, and CONNECT_STATUS push all update together;
        /// otherwise deletes the PlayerPrefs keys directly.</summary>
        public static void DrawClearButton(HapbeatManager manager, System.Action repaint)
        {
            bool hasSaved = HapbeatManager.TryGetPersistedAddressOverride(out _, out _);

            // Both axes pinned by the build ⇒ nothing about the routing is
            // per-device, so clearing the saved values would change nothing.
            GetBuildOverride(manager, out int buildPlayer, out int buildGroup);
            bool allForced = buildPlayer >= 1 && buildGroup >= 1;

            EditorGUI.BeginDisabledGroup(!hasSaved || allForced);
            if (GUILayout.Button("Clear Saved Override"))
            {
                if (Application.isPlaying && manager != null)
                {
                    manager.ClearPersistedAddressOverride();
                }
                else
                {
                    PlayerPrefs.DeleteKey(HapbeatManager.PlayerPrefsKeyOverridePlayer);
                    PlayerPrefs.DeleteKey(HapbeatManager.PlayerPrefsKeyOverrideGroup);
                    PlayerPrefs.Save();
                }
                repaint?.Invoke();
            }
            EditorGUI.EndDisabledGroup();
        }
    }
}
#endif
