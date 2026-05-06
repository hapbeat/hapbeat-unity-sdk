#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using static Hapbeat.Editor.HapbeatLocalization;

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
                    "複数の HapbeatKitsReadme が見つかりました。先頭を使用します。\n" +
                    "Remove extra markers / 不要なマーカーは削除してください:\n" +
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
        private const string kSiteUrl = "https://hapbeat.com/";

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
                Tr("Haptic content authored in Hapbeat Studio, consumed by Unity at runtime.",
                   "Hapbeat Studio で制作した触覚コンテンツを Unity ランタイムが消費します。"),
                new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(6);
            DrawSeparator();
        }

        public override void OnInspectorGUI()
        {
            EnsureStyles();
            var readme = (HapbeatKitsReadme)target;

            // ── Language toggle ──────────────────────────────────────────────
            DrawLangToggle();
            GUILayout.Space(2);

            // ── Primary actions ──────────────────────────────────────────────
            GUILayout.Space(4);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Tr("Open Hapbeat Studio", "Hapbeat Studio を開く"), GUILayout.Height(30)))
                    Application.OpenURL(kStudioUrl);
                if (GUILayout.Button("hapbeat.com", GUILayout.Height(30), GUILayout.Width(120)))
                    Application.OpenURL(kSiteUrl);
            }

            // ── What is this folder ──────────────────────────────────────────
            GUILayout.Label(Tr("What is this folder?", "このフォルダとは？"), _h2);
            GUILayout.Label(
                Tr(
                    "This folder holds Hapbeat <b>Kits</b> — haptic content (manifest.json + " +
                    "audio files) authored with Hapbeat Studio in the browser. Unity reads " +
                    "these files directly; the SDK locates this folder via the <i>" +
                    nameof(HapbeatKitsReadme) + "</i> asset, so you can move or rename it freely.",

                    "このフォルダは Hapbeat <b>Kit</b>（ブラウザ上の Hapbeat Studio で制作した " +
                    "manifest.json + 音声ファイル）を格納します。Unity はこれらを直接参照します。" +
                    "SDK は <i>" + nameof(HapbeatKitsReadme) + "</i> マーカーアセットでフォルダを追跡するため、" +
                    "フォルダを自由に移動・リネームできます。"),
                _body);

            // ── Setup steps ──────────────────────────────────────────────────
            GUILayout.Label(Tr("Getting started", "はじめ方"), _h2);
            DrawStep("1", Tr(
                "Open Studio (button above) in a Chromium-based browser.",
                "上のボタンから Studio を Chromium 系ブラウザで開きます。"));
            DrawStep("2", Tr(
                "In Studio, set <b>Working Directory</b> to this folder.",
                "Studio の <b>作業ディレクトリ</b> をこのフォルダに設定します。"));
            DrawStep("3", Tr(
                "Studio auto-scaffolds <i>&lt;kit-id&gt;/manifest.json</i> + clip folders.",
                "Studio が <i>&lt;kit-id&gt;/manifest.json</i> とクリップフォルダを自動生成します。"));
            DrawStep("4", Tr(
                "Create an EventMap (<i>Create &gt; Hapbeat &gt; Event Map</i>) and pick Kit events from the \"From Kit ▾\" dropdown.",
                "EventMap を作成し（<i>Create &gt; Hapbeat &gt; Event Map</i>）、\"From Kit ▾\" ドロップダウンから Kit イベントを選びます。"));

            // ── Event modes ──────────────────────────────────────────────────
            GUILayout.Label(Tr("Event modes", "イベントモード"), _h2);
            using (new GUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawModeRow("FIRE", "(command)",
                    Tr("Device plays pre-flashed Kit clip (Event ID → UDP)",
                       "デバイス内フラッシュ済みの Kit クリップを再生（Event ID → UDP）"),
                    "install-clips/");
                DrawModeRow("CLIP", "(stream_clip)",
                    Tr("SDK streams a Kit WAV over UDP as PCM16",
                       "SDK が Kit の WAV を UDP でストリーム（PCM16）"),
                    "stream-clips/");
            }

            // ── Strength model ───────────────────────────────────────────────
            GUILayout.Label(Tr("Strength model", "強度モデル"), _h2);
            GUILayout.Label(
                Tr("<b>Final output = WAV amplitude × intensity × SDK_gain × device volume</b>",
                   "<b>最終出力 = WAV振幅 × intensity × SDK_gain × デバイス音量</b>"),
                _body);
            GUILayout.Label(
                Tr(
                    "intensity — authored in Studio by the content creator (0.0 – 1.0).\n" +
                    "SDK_gain — controlled on the Unity side (entry.gain × binding, 0.0 – 2.0, default 1.0).",
                    "intensity は Studio で制作者が決める基準値 (0.0 – 1.0)。\n" +
                    "SDK_gain は Unity 側 (entry.gain × binding) で可変 (0.0 – 2.0, 既定 1.0)。"),
                _muted);

            // ── Troubleshooting ──────────────────────────────────────────────
            GUILayout.Label(Tr("Troubleshooting", "トラブルシューティング"), _h2);
            DrawBullet(Tr(
                "Studio doesn't detect audio → use PCM WAV in install-clips/ or stream-clips/",
                "音源を Studio が認識しない → PCM WAV を install-clips/ または stream-clips/ に置く"));
            DrawBullet(Tr(
                "Device doesn't vibrate → flash the Kit from Manager, and check that event mode is <b>FIRE (command)</b>",
                "デバイスが鳴らない → Manager から Kit をフラッシュ、かつ event mode が <b>FIRE (command)</b> か確認"));
            DrawBullet(Tr(
                "Changes not reflected in Unity → right-click this folder → <b>Reimport</b>",
                "Unity に反映されない → このフォルダを右クリック → <b>Reimport</b>"));

            // ── Version control ──────────────────────────────────────────────
            GUILayout.Label(Tr("Version control", "バージョン管理"), _h2);
            GUILayout.Label(
                Tr(
                    "To keep large WAV files out of git, add the following to <i>.gitignore</i>:",
                    "大きな WAV を git 外で管理したい場合、<i>.gitignore</i> に以下を追加:"),
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
