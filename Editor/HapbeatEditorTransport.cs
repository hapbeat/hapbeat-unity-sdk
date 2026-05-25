#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Edit-mode transport for test-playing haptic events from Editor windows
    /// (e.g. the EventMap window's "▶ Test Play" button) without entering Play mode.
    ///
    /// Wraps a standalone <see cref="HapbeatClient"/> that bypasses the runtime
    /// <see cref="HapbeatManager"/> singleton. The client is opened lazily on first
    /// use and disposed on domain reload or on Play-mode enter (to hand control
    /// back to the runtime manager so both don't fight over the UDP socket).
    ///
    /// Streaming in Edit mode is driven by an <see cref="EditorApplication.update"/>
    /// tick instead of a coroutine, because Editor-only code has no MonoBehaviour
    /// host to run <c>StartCoroutine</c> on.
    /// </summary>
    internal static class HapbeatEditorTransport
    {
        private static HapbeatClient _client;
        private static StreamState _stream;
        private static HapbeatConfig _cachedConfig;

        /// <summary>A stream run in progress (single state, no overlap).</summary>
        private class StreamState
        {
            public byte[] pcmBytes;
            public ushort sampleRate;
            public byte channels;
            public uint totalSamples;
            public float gain;
            public string target;
            public bool loop;

            public uint byteOffset;
            public int remaining;
            public DateTime startTime;
            public int iteration;
            public float bytesPerSecond;
        }

        static HapbeatEditorTransport()
        {
            // Clean up on domain reload / project close.
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            // Also when entering Play mode — the runtime HapbeatManager will take
            // over the UDP socket, so stop the edit-time client first.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    Dispose();
            };
        }

        // ── Status ───────────────────────────────────────────────────────────

        public static bool IsOpen => _client != null && _client.IsConnected;
        public static bool IsStreaming => _stream != null;

        public static string LastOpenError { get; private set; }

        // ── Open / close ─────────────────────────────────────────────────────

        /// <summary>
        /// Ensure the underlying <see cref="HapbeatClient"/> is open on the configured
        /// broadcast port. Returns true on success; on failure, stores the reason in
        /// <see cref="LastOpenError"/>.
        /// </summary>
        public static bool EnsureOpen()
        {
            if (IsOpen) return true;

            var cfg = ResolveConfig();
            int port = cfg != null ? cfg.port : 7700;

            try
            {
                _client = new HapbeatClient();
                _client.OpenBroadcast(port);
                EditorApplication.update -= TickStream;
                EditorApplication.update += TickStream;
                LastOpenError = null;
                Debug.Log($"[Hapbeat] Editor transport opened (UDP broadcast, port={port}).");
                return true;
            }
            catch (Exception ex)
            {
                LastOpenError = ex.Message;
                Debug.LogError($"[Hapbeat] Editor transport failed to open: {ex.Message}");
                _client?.Dispose();
                _client = null;
                return false;
            }
        }

        public static void Dispose()
        {
            if (_stream != null)
                SendStreamEnd();
            _stream = null;
            EditorApplication.update -= TickStream;

            if (_client != null)
            {
                try { _client.Dispose(); } catch { /* swallow on shutdown */ }
                _client = null;
            }
        }

        // ── Commands ─────────────────────────────────────────────────────────

        public static byte DefaultGroup
        {
            get
            {
                var cfg = ResolveConfig();
                return cfg != null && cfg.group >= 0 ? (byte)cfg.group : (byte)0;
            }
        }

        public static void Play(string eventId, float gain, string target = null)
        {
            if (!EnsureOpen()) return;
            if (string.IsNullOrEmpty(eventId))
            {
                Debug.LogWarning("[Hapbeat] Editor Play: eventId is empty.");
                return;
            }
            _client.SendPlay(eventId, 0, gain, target);
            Debug.Log($"[Hapbeat:Editor] \u25b6 Play \"{eventId}\" gain={gain:F2} " +
                      (string.IsNullOrEmpty(target) ? "(broadcast)" : $"target={target}"));
        }

        public static void Stop(string eventId, string target = null)
        {
            if (!EnsureOpen()) return;
            if (string.IsNullOrEmpty(eventId))
                _client.SendStopAll(target);
            else
                _client.SendStop(eventId, target);
        }

        public static void StopAll(string target = null)
        {
            if (!EnsureOpen()) return;
            _client.SendStopAll(target);
        }

        // ── Stream (AudioClip) ───────────────────────────────────────────────

        public static void StartStream(AudioClip clip, float gain, string target, bool loop)
        {
            if (!EnsureOpen()) return;
            if (clip == null)
            {
                Debug.LogWarning("[Hapbeat] Editor StartStream: clip is null.");
                return;
            }

            if (_stream != null)
                StopStream();

            // Decode AudioClip into PCM16 once.
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            byte[] pcm = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short v = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }

            _stream = new StreamState
            {
                pcmBytes = pcm,
                sampleRate = (ushort)clip.frequency,
                channels = (byte)clip.channels,
                totalSamples = (uint)clip.samples,
                gain = gain,
                target = target,
                loop = loop,
                byteOffset = 0,
                remaining = pcm.Length,
                startTime = DateTime.UtcNow,
                iteration = 1,
                bytesPerSecond = (ushort)clip.frequency * (byte)clip.channels * 2f,
            };

            // Seamless loop: single STREAM_BEGIN with totalSamples=0 (unknown length)
            // when looping, then keep feeding STREAM_DATA with monotonically advancing
            // offsets across iterations. See the matching comment in HapbeatManager.
            uint reportedTotalSamples = loop ? 0u : _stream.totalSamples;
            _client.SendStreamBegin(_stream.sampleRate, _stream.channels,
                HapbeatProtocol.AUDIO_FORMAT_PCM16, reportedTotalSamples, _stream.gain, _stream.target);

            Debug.Log($"[Hapbeat:Editor] \u266a StreamClip \"{clip.name}\" " +
                      $"{_stream.sampleRate}Hz/{_stream.channels}ch gain={gain:F2} " +
                      $"loop={loop} " +
                      (string.IsNullOrEmpty(target) ? "broadcast" : $"target={target}"));
        }

        public static void StopStream()
        {
            if (_stream == null) return;
            SendStreamEnd();
            Debug.Log($"[Hapbeat:Editor] \u25a0 Stream stopped after {_stream.iteration} iteration(s).");
            _stream = null;
        }

        private static void SendStreamEnd()
        {
            try { if (IsOpen) _client.SendStreamEnd(); } catch { }
        }

        /// <summary>
        /// EditorApplication.update tick — sends as many STREAM_DATA chunks as allowed
        /// by the real-time pacing budget, then handles loop/end transitions.
        /// </summary>
        private static void TickStream()
        {
            if (_stream == null) return;
            if (!IsOpen) { _stream = null; return; }

            const float sendAheadSeconds = 0.1f;
            int maxChunkSize = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;

            // Send a batch of chunks. Loop until we're sufficiently ahead of real time
            // or we run out of data in this iteration.
            while (_stream.remaining > 0)
            {
                float elapsed = (float)(DateTime.UtcNow - _stream.startTime).TotalSeconds;
                float sentDuration = _stream.byteOffset / _stream.bytesPerSecond;
                if (sentDuration > elapsed + sendAheadSeconds)
                    break; // wait for next tick

                int chunk = Mathf.Min(_stream.remaining, maxChunkSize);
                _client.SendStreamData(_stream.byteOffset, _stream.pcmBytes,
                    (int)_stream.byteOffset, chunk);
                _stream.byteOffset += (uint)chunk;
                _stream.remaining -= chunk;
            }

            if (_stream.remaining > 0) return; // still streaming this iteration

            // Iteration complete — send STREAM_END, then restart if looping.
            SendStreamEnd();

            if (!_stream.loop)
            {
                _stream = null;
                return;
            }

            // Re-arm for next loop iteration.
            _stream.iteration++;
            _stream.byteOffset = 0;
            _stream.remaining = _stream.pcmBytes.Length;
            _stream.startTime = DateTime.UtcNow;
            _client.SendStreamBegin(_stream.sampleRate, _stream.channels,
                HapbeatProtocol.AUDIO_FORMAT_PCM16, _stream.totalSamples, _stream.gain, _stream.target);
        }

        // ── Config ───────────────────────────────────────────────────────────

        /// <summary>
        /// Locate a <see cref="HapbeatConfig"/> asset. Prefers the Resources-loaded one
        /// (same as <see cref="HapbeatManager"/>), then falls back to AssetDatabase search.
        /// Caches the result for the duration of the Editor session.
        /// </summary>
        private static HapbeatConfig ResolveConfig()
        {
            if (_cachedConfig != null) return _cachedConfig;

            _cachedConfig = Resources.Load<HapbeatConfig>("HapbeatConfig");
            if (_cachedConfig != null) return _cachedConfig;

            var guids = AssetDatabase.FindAssets("t:HapbeatConfig");
            if (guids != null && guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _cachedConfig = AssetDatabase.LoadAssetAtPath<HapbeatConfig>(path);
            }
            return _cachedConfig;
        }

        // ── Menu for manual control (diagnostics) ────────────────────────────

        [MenuItem("Hapbeat/Close Edit-mode Transport", false, 60)]
        private static void CloseMenu() => Dispose();
    }
}
#endif
