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
            // DrawFull already renders its own "Address Override (this device)"
            // bold heading — no separate section header needed here.
            HapbeatAddressOverrideStatusGUI.DrawFull(HapbeatManager.Instance, Repaint);
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

            EditorGUILayout.LabelField($"On device (OLED): \"{preview}\"", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"   {(preview?.Length ?? 0)} / {HapbeatConfig.MaxAppNameLength} chars " +
                "— longer strings are truncated by the protocol.",
                EditorStyles.miniLabel);
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

            EditorGUILayout.LabelField("Connected", isConnected.ToString(), EditorStyles.miniLabel);

            if (!isConnected)
                return;

            EditorGUILayout.LabelField("Mode", manager.IsBroadcast ? "broadcast" : "unicast", EditorStyles.miniLabel);
            // Port is read from the config asset (public field), not from the
            // internal HapbeatClient — this window only uses HapbeatManager's
            // public surface.
            EditorGUILayout.LabelField("Port", config != null ? config.port.ToString() : "-", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Alive devices", manager.AliveDeviceCount.ToString(), EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Discovered devices", EditorStyles.miniBoldLabel);
            if (manager.DiscoveredDevices.Count == 0)
            {
                EditorGUILayout.LabelField("(none — call Discover())", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var device in manager.DiscoveredDevices)
                    EditorGUILayout.LabelField($"  {device.name}", device.ipAddress, EditorStyles.miniLabel);
            }
        }
    }
}
#endif
