#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor-only lookup for the <c>parameters.intensity</c> value authored in a
    /// Kit's <c>manifest.json</c>, keyed either by eventId (for Command mode) or
    /// by AudioClip asset path (for StreamClip / StreamSource).
    ///
    /// Why this exists:
    /// <list type="bullet">
    ///   <item>Studio stores <c>intensity</c> as a manifest parameter, NOT baked into
    ///     the WAV amplitude. So for stream modes the SDK must multiply
    ///     <c>gain × intensity</c> itself — otherwise authored intensity is ignored
    ///     and every entry effectively plays at full strength.</item>
    ///   <item>Command mode devices have the intensity flashed as part of the Kit
    ///     binary and apply it internally, so the SDK sends raw gain for Command.</item>
    /// </list>
    ///
    /// This helper drives the <b>test-play</b> path and the Entry Detail "missing
    /// manifest intensity" warning. For runtime fire paths, the intensity is cached
    /// onto each <see cref="HapbeatEventEntry"/> (see <c>CachedManifestIntensity</c>)
    /// so the SDK has no manifest dependency at player runtime.
    /// </summary>
    internal static class HapbeatManifestIntensity
    {
        private struct ManifestEvent
        {
            public string kitRelPath;  // folder path relative to Assets/, e.g. "Assets/HapbeatKits/demo"
            public string eventId;     // e.g. "impact.hit-soft"
            public string clipRelPath; // manifest's clip field, e.g. "stream-clips/click.wav" or "click.wav"
            public float intensity;    // 0..1, default 1.0 if not specified
        }

        private static List<ManifestEvent> _cache;
        private static double _cacheTime = -1;
        private const double CacheTtlSeconds = 3.0;

        /// <summary>Force a re-parse on next lookup. Call after Studio re-deploys a Kit.</summary>
        public static void Invalidate()
        {
            _cache = null;
            _cacheTime = -1;
        }

        /// <summary>
        /// Look up the manifest intensity for a <see cref="HapbeatEventEntry"/>.
        /// Matching rule:
        /// <list type="bullet">
        ///   <item><b>Command</b>: match <c>eventId</c>.</item>
        ///   <item><b>StreamClip / StreamSource</b>: match by <c>streamClip</c>'s asset path
        ///     against <c>&lt;kit&gt;/&lt;manifest.clip&gt;</c>.</item>
        /// </list>
        /// </summary>
        public static bool TryGetIntensity(HapbeatEventEntry entry, out float intensity)
        {
            intensity = 1f;
            if (entry == null) return false;

            var all = LoadAll();

            if (entry.mode == HapticMode.Command)
            {
                if (string.IsNullOrEmpty(entry.eventId)) return false;
                foreach (var ev in all)
                {
                    if (ev.eventId == entry.eventId)
                    {
                        intensity = ev.intensity;
                        return true;
                    }
                }
                return false;
            }
            else // StreamClip / StreamSource
            {
                if (entry.streamClip == null) return false;
                string clipAssetPath = AssetDatabase.GetAssetPath(entry.streamClip);
                if (string.IsNullOrEmpty(clipAssetPath)) return false;
                // Normalize separators
                string clipPath = clipAssetPath.Replace('\\', '/');

                foreach (var ev in all)
                {
                    string expected = $"{ev.kitRelPath}/{ev.clipRelPath}".Replace('\\', '/');
                    if (clipPath == expected)
                    {
                        intensity = ev.intensity;
                        return true;
                    }
                }
                return false;
            }
        }

        // ── Manifest parsing ────────────────────────────────────────────────

        private static List<ManifestEvent> LoadAll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cache != null && now - _cacheTime < CacheTtlSeconds) return _cache;

            var result = new List<ManifestEvent>();
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot) && AssetDatabase.IsValidFolder(kitsRoot))
            {
                foreach (string kitDir in AssetDatabase.GetSubFolders(kitsRoot))
                {
                    string manifestAssetPath = $"{kitDir}/manifest.json";
                    string abs = Path.Combine(Application.dataPath,
                        manifestAssetPath.Substring("Assets/".Length));
                    if (!File.Exists(abs)) continue;
                    try
                    {
                        string json = File.ReadAllText(abs);
                        ParseEvents(json, kitDir, result);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[Hapbeat] Failed to parse {manifestAssetPath}: {e.Message}");
                    }
                }
            }

            _cache = result;
            _cacheTime = now;
            return result;
        }

        private static void ParseEvents(string json, string kitAssetPath, List<ManifestEvent> output)
        {
            // Locate top-level "events": { ... }
            var eventsMatch = Regex.Match(json, "\"events\"\\s*:\\s*\\{");
            if (!eventsMatch.Success) return;
            int blockStart = eventsMatch.Index + eventsMatch.Length;
            int blockEnd = FindMatchingBrace(json, blockStart);
            if (blockEnd < 0) return;
            string block = json.Substring(blockStart, blockEnd - blockStart);

            int pos = 0;
            while (pos < block.Length)
            {
                var keyMatch = Regex.Match(block.Substring(pos), "\"([^\"]+)\"\\s*:\\s*\\{");
                if (!keyMatch.Success) break;

                string eventId = keyMatch.Groups[1].Value;
                int entryStart = pos + keyMatch.Index + keyMatch.Length;
                int entryEnd = FindMatchingBrace(block, entryStart);
                if (entryEnd < 0) break;
                string body = block.Substring(entryStart, entryEnd - entryStart);

                // clip: may be absent for stream_source-without-WAV
                string clipRel = "";
                var clipMatch = Regex.Match(body, "\"clip\"\\s*:\\s*\"([^\"]*)\"");
                if (clipMatch.Success) clipRel = clipMatch.Groups[1].Value;

                // parameters.intensity — grab via the inner parameters block to avoid
                // accidentally matching any other "intensity" key further in the JSON.
                float intensity = 1f;
                var paramsMatch = Regex.Match(body, "\"parameters\"\\s*:\\s*\\{");
                if (paramsMatch.Success)
                {
                    int pStart = paramsMatch.Index + paramsMatch.Length;
                    int pEnd = FindMatchingBrace(body, pStart);
                    if (pEnd > 0)
                    {
                        string pBody = body.Substring(pStart, pEnd - pStart);
                        var intensityMatch = Regex.Match(pBody, "\"intensity\"\\s*:\\s*([0-9.]+)");
                        if (intensityMatch.Success)
                        {
                            float.TryParse(intensityMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out intensity);
                        }
                    }
                }

                output.Add(new ManifestEvent
                {
                    kitRelPath = kitAssetPath,
                    eventId = eventId,
                    clipRelPath = clipRel,
                    intensity = intensity,
                });

                pos = entryEnd + 1;
            }
        }

        private static int FindMatchingBrace(string s, int openAfterIdx)
        {
            int depth = 1;
            int i = openAfterIdx;
            bool inString = false;
            while (i < s.Length && depth > 0)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
                else if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                if (depth > 0) i++;
            }
            return depth == 0 ? i : -1;
        }
    }
}
#endif
