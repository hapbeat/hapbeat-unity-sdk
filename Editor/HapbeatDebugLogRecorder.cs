#if UNITY_EDITOR
using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Captures Hapbeat-relevant Debug.Log output to a text file on disk so the log
    /// can be shared without pasting huge blocks into a chat/issue tracker.
    ///
    /// Filter: only lines whose (rich-text-stripped) content contains one of a small
    /// set of Hapbeat prefixes are captured. The filter avoids mixing in XRI / Meta
    /// XR / OpenXR / etc. noise that the Unity Console otherwise accumulates.
    ///
    /// Recording state is persisted in <see cref="EditorPrefs"/> so a Unity domain
    /// reload during script recompilation does not interrupt an in-flight capture.
    /// </summary>
    [InitializeOnLoad]
    internal static class HapbeatDebugLogRecorder
    {
        private const string kRecordingPathPref = "Hapbeat.Debug.LogRecorder.ActivePath";

        // Substrings that qualify a Debug.Log line for capture. Match happens after
        // rich-text tags (<color>...</color>) are stripped so colored prefixes still
        // hit. Keep this list short — the goal is to filter OUT unrelated noise.
        private static readonly string[] kCaptureSubstrings =
        {
            "[Hapbeat",       // "[Hapbeat]", "[Hapbeat Batch Setup]", "[HapbeatBinding]" ...
            "[EventLog",      // HapbeatEventLogger output
            "[DragDrop",      // EventMap window drag&drop debug
        };

        private static readonly Regex kRichTextRegex = new Regex("<.*?>", RegexOptions.Compiled);

        private static StreamWriter _writer;
        private static string _activePath;
        private static DateTime _startedAt;

        // Guards _writer access — Application.logMessageReceivedThreaded fires from
        // worker threads, so start/stop (main thread) and writes (any thread) must
        // not race on disposal.
        private static readonly object _writerLock = new object();

        private const string kMenuStart  = "Hapbeat/Start Log Recording";
        private const string kMenuStop   = "Hapbeat/Stop Log Recording";
        private const string kMenuReveal = "Hapbeat/Debug/Logs/Reveal Current File";
        private const string kMenuFolder = "Hapbeat/Debug/Logs/Open Logs Folder";
        private const string kMenuDump   = "Hapbeat/Debug/Logs/Dump Last Recording to Console";

        static HapbeatDebugLogRecorder()
        {
            // Resume a capture that was active before the domain reload.
            string resumePath = EditorPrefs.GetString(kRecordingPathPref, "");
            if (!string.IsNullOrEmpty(resumePath))
            {
                try
                {
                    ResumeRecording(resumePath);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Hapbeat] Could not resume log recording to '{resumePath}': {ex.Message}");
                    EditorPrefs.DeleteKey(kRecordingPathPref);
                }
            }
        }

        // ----------------- menu commands -----------------

        [MenuItem(kMenuStart, false, 520)]
        private static void StartRecording()
        {
            lock (_writerLock)
            {
                if (_writer != null)
                {
                    Debug.LogWarning($"[Hapbeat] Log recording already active: {_activePath}");
                    return;
                }

                string dir = GetLogsDirectory();
                Directory.CreateDirectory(dir);

                _startedAt = DateTime.Now;
                string filename = $"hapbeat-events-{_startedAt:yyyyMMdd-HHmmss}.log";
                _activePath = Path.GetFullPath(Path.Combine(dir, filename));

                _writer = new StreamWriter(_activePath, append: false) { AutoFlush = true };
                _writer.WriteLine($"# Hapbeat Debug Log Recording");
                _writer.WriteLine($"# Started: {_startedAt:O}");
                _writer.WriteLine($"# Unity project: {Path.GetFullPath(Application.dataPath)}");
                _writer.WriteLine($"# Filter: {string.Join(", ", kCaptureSubstrings)}");
                _writer.WriteLine($"#");
            }

            Application.logMessageReceivedThreaded += OnLogMessage;
            EditorPrefs.SetString(kRecordingPathPref, _activePath);

            Debug.Log($"[Hapbeat] Log recording started \u2192 {_activePath}");
        }

        [MenuItem(kMenuStart, true)]
        private static bool StartRecordingValidate() => _writer == null;

        [MenuItem(kMenuStop, false, 521)]
        private static void StopRecording()
        {
            // Detach the handler first. Removing a delegate while another thread is
            // inside it is safe — the running invocation completes, subsequent
            // dispatches don't include this callback.
            Application.logMessageReceivedThreaded -= OnLogMessage;

            string path;
            lock (_writerLock)
            {
                if (_writer == null)
                {
                    Debug.LogWarning("[Hapbeat] No active log recording to stop.");
                    return;
                }
                _writer.WriteLine($"#");
                _writer.WriteLine($"# Stopped: {DateTime.Now:O}  (duration {(DateTime.Now - _startedAt).TotalSeconds:F1}s)");
                try { _writer.Close(); } catch { /* ignore */ }
                _writer = null;
                path = _activePath;
                _activePath = null;
            }

            EditorPrefs.DeleteKey(kRecordingPathPref);

            Debug.Log($"[Hapbeat] Log recording stopped \u2192 {path}");
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem(kMenuStop, true)]
        private static bool StopRecordingValidate() => _writer != null;

        [MenuItem(kMenuReveal, false, 522)]
        private static void RevealCurrent()
        {
            if (string.IsNullOrEmpty(_activePath) || !File.Exists(_activePath))
            {
                Debug.LogWarning("[Hapbeat] No active recording to reveal.");
                return;
            }
            EditorUtility.RevealInFinder(_activePath);
        }

        [MenuItem(kMenuReveal, true)]
        private static bool RevealCurrentValidate() => _writer != null && File.Exists(_activePath);

        [MenuItem(kMenuFolder, false, 523)]
        private static void OpenLogsFolder()
        {
            string dir = GetLogsDirectory();
            Directory.CreateDirectory(dir);
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem(kMenuDump, false, 524)]
        private static void DumpLastToConsole()
        {
            string dir = GetLogsDirectory();
            if (!Directory.Exists(dir))
            {
                Debug.LogWarning($"[Hapbeat] Logs folder does not exist yet: {dir}");
                return;
            }

            var files = new DirectoryInfo(dir).GetFiles("hapbeat-events-*.log");
            if (files.Length == 0)
            {
                Debug.LogWarning("[Hapbeat] No hapbeat-events-*.log files found.");
                return;
            }

            Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            var latest = files[0];

            string content;
            try
            {
                content = File.ReadAllText(latest.FullName);
            }
            catch (IOException ex)
            {
                // Most likely cause: the file is open (we're recording). Use shared read.
                using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                content = sr.ReadToEnd();
                Debug.Log($"[Hapbeat] (read with shared access: {ex.Message})");
            }

            Debug.Log($"[Hapbeat] Last recording: {latest.FullName}\n(modified {latest.LastWriteTime:O}, {latest.Length} bytes)\n\n{content}");
        }

        // ----------------- log capture -----------------

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            // Cheap early-outs BEFORE taking the lock.
            if (_writer == null) return;
            if (!ShouldCapture(condition)) return;

            string tag = type switch
            {
                LogType.Error     => "ERR",
                LogType.Exception => "EXC",
                LogType.Assert    => "AST",
                LogType.Warning   => "WRN",
                _                 => "INF",
            };

            string stripped = StripRichText(condition);

            // Single-line entry — easy for downstream tools (grep, tail) to consume.
            // If the condition itself contains newlines, replace with " \u21B5 " so the
            // entry stays on one line.
            if (stripped.IndexOf('\n') >= 0)
                stripped = stripped.Replace("\r", "").Replace("\n", " \u21B5 ");

            string ts = DateTime.Now.ToString("HH:mm:ss.fff");

            lock (_writerLock)
            {
                if (_writer == null) return; // Stopped while we were preparing the line
                try
                {
                    _writer.WriteLine($"{ts} {tag} {stripped}");
                }
                catch
                {
                    // Writer might be disposed mid-shutdown — swallow to avoid recursive logging.
                }
            }
        }

        private static bool ShouldCapture(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return false;

            string stripped = StripRichText(condition);
            for (int i = 0; i < kCaptureSubstrings.Length; i++)
            {
                if (stripped.IndexOf(kCaptureSubstrings[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            // Fast path: no '<', no rich text.
            if (s.IndexOf('<') < 0) return s;
            return kRichTextRegex.Replace(s, "");
        }

        private static string GetLogsDirectory()
        {
            // "{project}/Assets/.." \u2192 "{project}/Logs".
            // Keeps logs outside Assets so they do not trigger asset import.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
        }

        private static void ResumeRecording(string path)
        {
            lock (_writerLock)
            {
                _activePath = path;
                _startedAt = DateTime.Now;
                _writer = new StreamWriter(path, append: true) { AutoFlush = true };
                _writer.WriteLine($"#");
                _writer.WriteLine($"# Resumed after domain reload at {_startedAt:O}");
                _writer.WriteLine($"#");
            }
            Application.logMessageReceivedThreaded += OnLogMessage;
        }
    }
}
#endif
