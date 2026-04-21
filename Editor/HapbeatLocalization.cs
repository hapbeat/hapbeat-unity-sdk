#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Minimal EN/JA bilingual helper for Hapbeat Editor UIs.
    ///
    /// Usage in code:
    ///   GUILayout.Label(Tr("Getting started", "はじめ方"));
    ///
    /// Language preference is stored in EditorPrefs and defaults to the OS
    /// system language. A toggle can be drawn anywhere with DrawLangToggle().
    ///
    /// Design intent: keep all strings inline (bilingual source) rather than
    /// external tables. If the string volume grows significantly, replace this
    /// with a proper localization layer later.
    /// </summary>
    internal static class HapbeatLocalization
    {
        private const string PrefKey = "Hapbeat.Lang";

        public enum Lang { EN = 0, JA = 1 }

        /// <summary>Currently selected display language (persisted in EditorPrefs).</summary>
        public static Lang CurrentLang
        {
            get => (Lang)EditorPrefs.GetInt(PrefKey, SystemDefault());
            set => EditorPrefs.SetInt(PrefKey, (int)value);
        }

        /// <summary>Return the localized string for the current language.</summary>
        public static string Tr(string en, string ja)
            => CurrentLang == Lang.JA ? ja : en;

        /// <summary>
        /// Draw a compact [EN] / [日] toggle row. Typical placement: footer or header of
        /// a custom Inspector or EditorWindow.
        /// </summary>
        public static void DrawLangToggle()
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                DrawToggleButton("EN", Lang.EN);
                DrawToggleButton("JP", Lang.JA);
            }
        }

        private static void DrawToggleButton(string label, Lang lang)
        {
            bool active = CurrentLang == lang;
            var style = active ? EditorStyles.miniButtonMid : EditorStyles.miniButtonMid;
            var prev = GUI.backgroundColor;
            if (active) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
            if (GUILayout.Button(label, EditorStyles.miniButton, GUILayout.Width(32)))
                CurrentLang = lang;
            GUI.backgroundColor = prev;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static int SystemDefault()
            => Application.systemLanguage == SystemLanguage.Japanese ? 1 : 0;
    }
}
#endif
