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
    /// by AudioClip asset path (for StreamClip).
    ///
    /// Why this exists:
    /// <list type="bullet">
    ///   <item>Studio stores <c>intensity</c> as a manifest parameter, NOT baked into
    ///     the WAV amplitude. The device is a pure executor that plays req.gain as-is
    ///     and no longer reads manifest.intensity at runtime. The SDK (sender) must
    ///     therefore multiply <c>gain × intensity</c> before putting the value on the
    ///     wire — for both Command and StreamClip modes.</item>
    /// </list>
    ///
    /// This helper drives the <b>test-play</b> path and the Entry Detail "missing
    /// manifest intensity" warning. For runtime fire paths, the intensity is cached
    /// onto each <see cref="HapbeatEventEntry"/> (see <c>CachedManifestIntensity</c>)
    /// so the SDK has no manifest dependency at player runtime.
    /// </summary>
    public static class HapbeatManifestIntensity
    {
        /// <summary>
        /// One parsed event from a Kit's manifest.json. Public so other
        /// Editor tooling (EventMap Window's "From Kit" dropdown, kit-name
        /// picker, etc.) can consume the same scanned data without
        /// duplicating the parser.
        /// </summary>
        public class KitManifestEvent
        {
            /// <summary>Kit folder asset path (e.g. "Assets/HapbeatSDK/Kits/tutorial-kit").</summary>
            public string kitDir;
            /// <summary>Kit folder name (e.g. "tutorial-kit"). Equals manifest's "name" by convention.</summary>
            public string kitName;
            /// <summary>Full event id (e.g. "tutorial-kit.pin_hit").</summary>
            public string eventId;
            /// <summary>Manifest's clip field (relative to kit dir, e.g. "stream-clips/foo.wav").
            /// May be empty for events without a packaged clip.</summary>
            public string clipRelPath;
            /// <summary>Event mode — "command" or "stream_clip". Defaults to "command" if absent.</summary>
            public string mode;
            /// <summary>Optional designer description from manifest.</summary>
            public string description;
            /// <summary>Authored intensity 0..1, defaults to 1.0 if absent.</summary>
            public float intensity;
        }

        private static List<KitManifestEvent> _cache;
        private static double _cacheTime = -1;
        private const double CacheTtlSeconds = 3.0;

        /// <summary>Force a re-parse on next lookup. Call after Studio re-deploys a Kit.</summary>
        public static void Invalidate()
        {
            _cache = null;
            _cacheTime = -1;
        }

        /// <summary>
        /// Return every Kit event discovered under <c>HapbeatSDK/Kits/</c>.
        /// Cached for <see cref="CacheTtlSeconds"/> seconds; call
        /// <see cref="Invalidate"/> to force a re-scan.
        /// </summary>
        public static IReadOnlyList<KitManifestEvent> LoadAllEvents() => LoadAll();

        /// <summary>
        /// Return the direct subfolder names under <c>HapbeatSDK/Kits/</c>
        /// — these are the canonical kit names (each maps to one
        /// <c>&lt;name&gt;-manifest.json</c>). Used by the EventMap Window
        /// to populate the category dropdown for Command-mode entries.
        /// </summary>
        public static IReadOnlyList<string> GetKitDirectoryNames()
        {
            var result = new List<string>();
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (string.IsNullOrEmpty(kitsRoot) || !AssetDatabase.IsValidFolder(kitsRoot))
                return result;
            foreach (string kitDir in AssetDatabase.GetSubFolders(kitsRoot))
                result.Add(Path.GetFileName(kitDir));
            return result;
        }

        /// <summary>
        /// Return the asset path of the kit folder for the given kit name
        /// (e.g. "tutorial-kit" → "Assets/HapbeatSDK/Kits/tutorial-kit"),
        /// or null if no such folder exists under the kits root.
        /// </summary>
        public static string GetKitDirectory(string kitName)
        {
            if (string.IsNullOrEmpty(kitName)) return null;
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (string.IsNullOrEmpty(kitsRoot)) return null;
            string candidate = $"{kitsRoot}/{kitName}";
            return AssetDatabase.IsValidFolder(candidate) ? candidate : null;
        }

        /// <summary>
        /// Look up the manifest intensity for a <see cref="HapbeatEventEntry"/>.
        /// Matching rule:
        /// <list type="bullet">
        ///   <item><b>Command</b>: match by <c>eventId</c>.</item>
        ///   <item><b>StreamClip</b>: match by <c>streamClip</c>'s asset
        ///     path against <c>&lt;kit&gt;/&lt;manifest.clip&gt;</c>. If that fails
        ///     (no clip set, or clip not found in any manifest), fall back to matching
        ///     by <c>eventId</c>.</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// Look up the manifest intensity for an entry. Convenience wrapper
        /// around <see cref="TryResolve"/> that discards the source-path info.
        /// </summary>
        public static bool TryGetIntensity(HapbeatEventEntry entry, out float intensity)
            => TryResolve(entry, out intensity, out _);

        /// <summary>
        /// Single resolution pass that returns the intensity AND the
        /// human-readable source description in one shot. Used by both
        /// runtime cache population and the EventMap Window UI.
        /// </summary>
        /// <param name="entry">Entry to resolve.</param>
        /// <param name="intensity">Resolved intensity (1.0 if no match).</param>
        /// <param name="source">
        /// Human-readable hit source:
        ///   "(override) {manifest path}" — per-entry override matched
        ///   "{kit asset path}"           — auto-resolved from a Kit
        ///   "" (empty)                   — no match
        /// </param>
        public static bool TryResolve(HapbeatEventEntry entry, out float intensity, out string source)
        {
            intensity = 1f;
            source = "";
            if (entry == null) return false;

            // Per-entry override: look up against THAT manifest only first.
            if (entry.manifestOverride != null)
            {
                var overrideEvents = LoadFromTextAsset(entry.manifestOverride);
                if (TryResolveFromList(overrideEvents, entry, out intensity, out _))
                {
                    source = $"(override) {AssetDatabase.GetAssetPath(entry.manifestOverride)}";
                    return true;
                }
                // If override was set but didn't match the entry, fall through
                // to global scan so the user still gets *something* — clearer
                // than a silent zero. The UI tooltip indicates auto-fallback.
            }

            if (TryResolveFromList(LoadAll(), entry, out intensity, out string kitPath))
            {
                source = kitPath;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the source description for an entry, or empty string if
        /// no match. Equivalent to <c>TryResolve(entry, out _, out source)</c>.
        /// </summary>
        public static string DescribeResolvedSource(HapbeatEventEntry entry)
        {
            TryResolve(entry, out _, out string source);
            return source;
        }

        /// <summary>
        /// Resolution against a specific event list. Sets both the intensity
        /// and the kit asset path of the matched entry (for source reporting).
        /// </summary>
        private static bool TryResolveFromList(
            List<KitManifestEvent> list, HapbeatEventEntry entry,
            out float intensity, out string kitPath)
        {
            intensity = 1f;
            kitPath = "";
            if (list == null || list.Count == 0) return false;

            // Command — match by eventId only.
            if (entry.mode == HapticMode.Command)
            {
                return TryMatchByEventId(list, entry.eventId, out intensity, out kitPath);
            }

            // StreamClip — prefer clip-path match (more specific).
            if (entry.streamClip != null)
            {
                string clipAssetPath = AssetDatabase.GetAssetPath(entry.streamClip);
                if (!string.IsNullOrEmpty(clipAssetPath))
                {
                    string clipPath = clipAssetPath.Replace('\\', '/');
                    foreach (var ev in list)
                    {
                        string expected = $"{ev.kitDir}/{ev.clipRelPath}".Replace('\\', '/');
                        if (clipPath == expected)
                        {
                            intensity = ev.intensity;
                            kitPath = ev.kitDir;
                            return true;
                        }
                    }
                }
            }

            // Fallback: eventId match.
            if (!string.IsNullOrEmpty(entry.eventId))
                return TryMatchByEventId(list, entry.eventId, out intensity, out kitPath);

            return false;
        }

        private static bool TryMatchByEventId(
            List<KitManifestEvent> all, string eventId,
            out float intensity, out string kitPath)
        {
            intensity = 1f;
            kitPath = "";
            if (string.IsNullOrEmpty(eventId)) return false;
            foreach (var ev in all)
            {
                if (ev.eventId == eventId)
                {
                    intensity = ev.intensity;
                    kitPath = ev.kitDir;
                    return true;
                }
            }
            return false;
        }

        // ── Manifest parsing ────────────────────────────────────────────────

        private static List<KitManifestEvent> LoadAll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cache != null && now - _cacheTime < CacheTtlSeconds) return _cache;

            var result = new List<KitManifestEvent>();
            // Studio's user-kits root (HapbeatSDK/Kits/).
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot)) ScanKitsRoot(kitsRoot, result);

            _cache = result;
            _cacheTime = now;
            return result;
        }

        private static void ScanKitsRoot(string kitsRoot, List<KitManifestEvent> result)
        {
            if (string.IsNullOrEmpty(kitsRoot) || !AssetDatabase.IsValidFolder(kitsRoot)) return;
            foreach (string kitDir in AssetDatabase.GetSubFolders(kitsRoot))
            {
                string manifestAssetPath = FindKitManifest(kitDir);
                if (string.IsNullOrEmpty(manifestAssetPath)) continue;
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

        /// <summary>
        /// Walk up from the given clip's asset folder until a kit manifest
        /// (<c>*manifest*.json</c>) is found in some ancestor directory.
        /// Returns the manifest TextAsset, or null if none is found within
        /// the Assets/ scope.
        ///
        /// <para>Typical layout: a clip lives at
        /// <c>Assets/HapbeatSDK/Kits/&lt;kit-name&gt;/stream-clips/foo.wav</c>;
        /// walking up two levels reaches the kit folder where the manifest
        /// resides.</para>
        /// </summary>
        public static UnityEngine.TextAsset FindManifestForClip(UnityEngine.Object clip)
        {
            if (clip == null) return null;
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return null;
            string dir = Path.GetDirectoryName(path).Replace('\\', '/');
            while (!string.IsNullOrEmpty(dir) &&
                   (dir.StartsWith("Assets/") || dir == "Assets"))
            {
                string manifestAssetPath = FindKitManifest(dir);
                if (!string.IsNullOrEmpty(manifestAssetPath))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(manifestAssetPath);
                    if (asset != null) return asset;
                }
                int idx = dir.LastIndexOf('/');
                if (idx < 0) break;
                dir = dir.Substring(0, idx);
            }
            return null;
        }

        /// <summary>
        /// Locate the manifest file inside a Kit folder. Naming convention is
        /// <c>&lt;kitname&gt;-manifest.json</c> (e.g. <c>tutorial-kit-manifest.json</c>).
        /// Falls back to any <c>*manifest*.json</c> in the kit folder if the
        /// preferred name isn't present, so kits authored with non-conventional
        /// names still resolve. Returns null when no manifest is found.
        /// </summary>
        internal static string FindKitManifest(string kitAssetDir)
        {
            if (string.IsNullOrEmpty(kitAssetDir)) return null;
            string kitName = Path.GetFileName(kitAssetDir);

            // Preferred: <kitname>-manifest.json (fast path).
            string preferred = $"{kitAssetDir}/{kitName}-manifest.json";
            string preferredAbs = Path.Combine(Application.dataPath,
                preferred.Substring("Assets/".Length));
            if (File.Exists(preferredAbs)) return preferred;

            // Fallback: any *manifest*.json in this kit folder (top-level only).
            string kitAbs = Path.Combine(Application.dataPath,
                kitAssetDir.Substring("Assets/".Length));
            if (!Directory.Exists(kitAbs)) return null;
            foreach (var file in Directory.GetFiles(kitAbs, "*manifest*.json",
                SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                // Return as asset path
                return $"{kitAssetDir}/{name}";
            }
            return null;
        }

        /// <summary>
        /// Parse a manifest TextAsset (asset content already loaded by Unity)
        /// into a single-Kit event list. Used by per-entry override resolution
        /// where the designer specifies the exact manifest to consult.
        /// </summary>
        private static List<KitManifestEvent> LoadFromTextAsset(UnityEngine.TextAsset manifest)
        {
            var result = new List<KitManifestEvent>();
            if (manifest == null) return result;
            string assetPath = AssetDatabase.GetAssetPath(manifest);
            if (string.IsNullOrEmpty(assetPath)) return result;
            // Kit folder = parent of the manifest.json file.
            string kitDir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            try { ParseEvents(manifest.text, kitDir, result); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Hapbeat] Failed to parse override manifest {assetPath}: {e.Message}");
            }
            return result;
        }

        private static void ParseEvents(string json, string kitAssetPath, List<KitManifestEvent> output)
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

                // mode: defaults to "command" per kit-format spec when absent.
                string mode = "command";
                var modeMatch = Regex.Match(body, "\"mode\"\\s*:\\s*\"([^\"]+)\"");
                if (modeMatch.Success) mode = modeMatch.Groups[1].Value;

                // description: optional designer notes.
                string description = "";
                var descMatch = Regex.Match(body, "\"description\"\\s*:\\s*\"([^\"]*)\"");
                if (descMatch.Success) description = descMatch.Groups[1].Value;

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

                output.Add(new KitManifestEvent
                {
                    kitDir = kitAssetPath,
                    kitName = Path.GetFileName(kitAssetPath),
                    eventId = eventId,
                    clipRelPath = clipRel,
                    mode = mode,
                    description = description,
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
