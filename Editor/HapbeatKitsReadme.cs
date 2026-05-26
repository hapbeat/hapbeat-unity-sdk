#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Dual-purpose asset that (1) marks its parent folder as the HapbeatKits
    /// root for the SDK, and (2) renders a styled onboarding / welcome screen in
    /// the Inspector — same UX pattern as Unity's URP / Starter Assets readmes.
    ///
    /// Because the asset is tracked by AssetDatabase, the user can rename or
    /// move the kits folder anywhere in the project and the SDK still locates
    /// it via <see cref="FindKitsRootPath"/>. No ProjectSettings UI needed.
    /// </summary>
    public class HapbeatKitsReadme : ScriptableObject
    {
        /// <summary>Bumped by the SDK when the readme content changes; lets
        /// "Reset Readme" know when the on-disk asset is out of date.</summary>
        public string templateVersion = "2";

        // ── Folder resolution ────────────────────────────────────────────────

        /// <summary>
        /// Find the Asset-relative path of the folder that holds a
        /// HapbeatKitsReadme asset, or <c>null</c> if no marker exists.
        /// If multiple are found a warning is logged and the first is used.
        /// </summary>
        public static string FindKitsRootPath()
        {
            string[] guids = AssetDatabase.FindAssets($"t:{nameof(HapbeatKitsReadme)}");
            if (guids == null || guids.Length == 0) return null;
            if (guids.Length > 1)
            {
                Debug.LogWarning(
                    "[Hapbeat] Multiple HapbeatKitsReadme assets found — using the first one.\n" +
                    "Remove extra markers:\n" +
                    string.Join("\n", System.Array.ConvertAll(guids, AssetDatabase.GUIDToAssetPath)));
            }
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            string dir = Path.GetDirectoryName(assetPath);
            return string.IsNullOrEmpty(dir) ? null : dir.Replace('\\', '/');
        }

        /// <summary>
        /// Resolve the kits root path, or return the SDK's default initial location
        /// if no marker exists. Useful for "Create Folder" flows that need a default.
        /// </summary>
        internal const string DefaultKitsRootPath = "Assets/HapbeatSDK/Kits";
    }

    [CustomEditor(typeof(HapbeatKitsReadme))]
    internal class HapbeatKitsReadmeEditor : UnityEditor.Editor
    {
        private const string kStudioUrl = "https://devtools.hapbeat.com/studio/";
        private const string kDocsUrl = "https://devtools.hapbeat.com/docs/sdk-integration/unity-sdk/getting-started/";

        private GUIStyle _h1;
        private GUIStyle _h2;
        private GUIStyle _body;
        private GUIStyle _mono;
        private GUIStyle _muted;
        private bool _stylesReady;

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _h1 = new GUIStyle(EditorStyles.largeLabel)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            _h2 = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin = new RectOffset(0, 0, 8, 4),
            };
            _body = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = true,
            };
            _mono = new GUIStyle(EditorStyles.label)
            {
                font = EditorStyles.miniFont,
                fontSize = 11,
                wordWrap = true,
            };
            _muted = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
            };
            _stylesReady = true;
        }

        /// <summary>Suppress the default header so we can render a branded one.</summary>
        protected override void OnHeaderGUI()
        {
            EnsureStyles();
            GUILayout.Space(8);
            GUILayout.Label("Hapbeat Kits", _h1);
            GUILayout.Space(2);
            GUILayout.Label(
                "Haptic content authored in Hapbeat Studio, consumed by Unity at runtime.",
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(6);
            DrawSeparator();
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            var readme = (HapbeatKitsReadme)target;

            // ── Primary actions ──────────────────────────────────────────────
            GUILayout.Space(4);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Open Hapbeat Studio", GUILayout.Height(30)))
                    Application.OpenURL(kStudioUrl);
                if (GUILayout.Button("Getting Started", GUILayout.Height(30), GUILayout.Width(120)))
                    Application.OpenURL(kDocsUrl);
            }

            // ── What is this folder ──────────────────────────────────────────
            GUILayout.Label("What is this folder?", _h2);
            GUILayout.Label(
                "This folder holds Hapbeat <b>Kits</b> — haptic content (manifest.json + " +
                "audio files) authored with Hapbeat Studio in the browser. Unity reads " +
                "these files directly; the SDK locates this folder via the <i>" +
                nameof(HapbeatKitsReadme) + "</i> asset, so you can move or rename it freely.",
                _body);

            // ── Setup steps ──────────────────────────────────────────────────
            GUILayout.Label("Getting started", _h2);
            DrawStep("1", "Open Studio (button above) in a Chromium-based browser.");
            DrawStep("2", "In Studio, set <b>Working Directory</b> to this folder.");
            DrawStep("3", "Studio auto-scaffolds <i>&lt;kit-name&gt;/&lt;kit-name&gt;-manifest.json</i> + clip folders.");
            DrawStep("4", "Create an EventMap (<i>Create &gt; Hapbeat &gt; Event Map</i>) and pick Kit events from the \"From Kit ▾\" dropdown.");

            // ── Event modes ──────────────────────────────────────────────────
            GUILayout.Label("Event modes", _h2);
            using (new GUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawModeRow("FIRE", "(command)",
                    "Device plays a pre-flashed Kit clip (Event ID → UDP)",
                    "install-clips/");
                DrawModeRow("CLIP", "(stream_clip)",
                    "SDK streams a Kit WAV over UDP as PCM16",
                    "stream-clips/");
            }

            // ── Strength model ───────────────────────────────────────────────
            GUILayout.Label("Strength model", _h2);
            GUILayout.Label(
                "<b>Final output = WAV amplitude × intensity × SDK_gain × device volume</b>",
                _body);
            GUILayout.Label(
                "intensity — set by the content creator in Studio (0.0 – 1.0).\n" +
                "SDK_gain — set on the Unity side (entry.gain × binding, 0.0 – 2.0, default 1.0).",
                _muted);

            // ── Troubleshooting ──────────────────────────────────────────────
            GUILayout.Label("Troubleshooting", _h2);
            DrawBullet("Studio doesn't detect audio → use PCM WAV in install-clips/ or stream-clips/");
            DrawBullet("Device doesn't vibrate → flash the Kit from Manager, and check that event mode is <b>FIRE (command)</b>");
            DrawBullet("Changes not reflected in Unity → right-click this folder → <b>Reimport</b>");

            // ── Version control ──────────────────────────────────────────────
            GUILayout.Label("Version control", _h2);
            GUILayout.Label(
                "To keep large WAV files out of git, add the following to <i>.gitignore</i>:",
                _body);
            using (new GUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(
                    "Assets/**/HapbeatSDK/Kits/**/install-clips/*.wav\nAssets/**/HapbeatSDK/Kits/**/stream-clips/*.wav",
                    _mono);
            }

            // ── Footer ───────────────────────────────────────────────────────
            GUILayout.Space(10);
            DrawSeparator();
            GUILayout.Space(4);
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label($"Template v{readme.templateVersion}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Path: {AssetDatabase.GetAssetPath(readme)}",
                    new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight });
            }
        }

        private void DrawStep(string num, string bodyText)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(num + ".", EditorStyles.boldLabel, GUILayout.Width(20));
                GUILayout.Label(bodyText, _body);
            }
        }

        private void DrawBullet(string bodyText)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("•", GUILayout.Width(14));
                GUILayout.Label(bodyText, _body);
            }
        }

        private void DrawModeRow(string mode, string techName, string desc, string source)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(mode, EditorStyles.boldLabel, GUILayout.Width(44));
                GUILayout.Label(techName, _muted, GUILayout.Width(92));
                GUILayout.Label(desc, _body);
                GUILayout.Label(source, _muted, GUILayout.Width(150));
            }
        }

        private static void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.15f));
        }
    }
}
#endif
