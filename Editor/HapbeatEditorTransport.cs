#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
    /// Streaming runs on a dedicated thread, the same arrangement
    /// <see cref="HapbeatManager"/> uses for the runtime.
    ///
    /// <para>
    /// It was originally paced from <see cref="EditorApplication.update"/> (Editor
    /// code has no MonoBehaviour to host a coroutine), which stuttered
    /// irregularly: that callback is not a steady clock. The Editor throttles it
    /// when its window is not focused, and it stops outright behind modal
    /// dialogs, native menus, asset imports and script compiles. With only a
    /// fraction of a second of audio buffered ahead, every such gap drains the
    /// device's ring buffer. Play mode never showed the problem because the
    /// runtime had already moved this work to a thread.
    /// </para>
    ///
    /// <para>
    /// <b>Threading rules</b> (why this is safe in the Editor): the thread only
    /// ever touches <see cref="HapbeatClient"/> socket sends -- no Unity API, no
    /// serialized object, no <c>AssetDatabase</c>. Shared state it may write is
    /// limited to its own <see cref="StreamState"/> plus one volatile flag; the
    /// main thread performs teardown. The thread is always joined before a domain
    /// reload or Play-mode enter (see the static constructor), so it can never
    /// outlive the AppDomain that owns it.
    /// </para>
    /// </summary>
    internal static class HapbeatEditorTransport
    {
        private static HapbeatClient _client;
        private static StreamState _stream;
        private static HapbeatConfig _cachedConfig;

        // Stream pump. _stream is handed to the thread on start and only read by
        // the main thread afterwards; teardown happens on the main thread once
        // _streamFinished is observed.
        private static Thread _streamThread;
        private static volatile bool _streamStopRequested;
        private static volatile bool _streamFinished;

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

        // Discovery upkeep. Well inside the client's known-device TTL (15 s) so a
        // device never ages out of the unicast set between test plays.
        private const double PingIntervalSeconds = 3.0;
        private static double _lastPingTime;

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
                // Mirror the persisted address override (PlayerPrefs) so "▶ Test Play"
                // previews the same target a running build would actually send —
                // otherwise Edit-mode tests would silently ignore an override that a
                // deployed build (via HapbeatManager) honors. There's no config-level
                // default anymore; only a previously-persisted override applies here.
                HapbeatManager.TryGetPersistedAddressOverride(out int savedPlayer, out int savedGroup);
                _client.SetAddressOverride(
                    HapbeatClient.NormalizeOverride(savedPlayer),
                    HapbeatClient.NormalizeOverride(savedGroup));
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;

                // Discover devices right away. Without a PING nothing ever PONGs,
                // so the client learns no device IPs and every test play falls back
                // to broadcast — which the AP buffers until its DTIM beacon, the
                // exact stutter Test Play was showing. The runtime avoids this
                // because HapbeatManager pings on connect; this transport drives a
                // bare client, so it has to do the same. It is also what lets the
                // client pin the right subnet on a multi-homed host.
                _client.SendPing();
                _lastPingTime = EditorApplication.timeSinceStartup;

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
            // Join first: the thread sends through _client, so tearing the client
            // down underneath it would be a use-after-dispose. This also runs on
            // domain reload / Play-mode enter, which is what keeps a stray thread
            // from surviving into the next AppDomain.
            StopStreamThread();
            _stream = null;
            EditorApplication.update -= Tick;

            if (_client != null)
            {
                try { _client.Dispose(); } catch { /* swallow on shutdown */ }
                _client = null;
            }
        }

        // ── Commands ─────────────────────────────────────────────────────────

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

            // Pin this session to the devices that have PONGed, exactly as
            // HapbeatManager does for the runtime. Streaming over broadcast is what
            // made Test Play stutter: the AP holds broadcast frames until its DTIM
            // beacon, while unicast goes out immediately. Falls back to broadcast on
            // its own when nothing has answered yet (returns -1).
            int streamTargets = _client.SetStreamUnicastTargets(
                _client.GetKnownDeviceIps(), target);

            // Seamless loop: single STREAM_BEGIN with totalSamples=0 (unknown length)
            // when looping, then keep feeding STREAM_DATA with monotonically advancing
            // offsets across iterations. See the matching comment in HapbeatManager.
            uint reportedTotalSamples = loop ? 0u : _stream.totalSamples;
            _client.SendStreamBegin(_stream.sampleRate, _stream.channels,
                HapbeatProtocol.AUDIO_FORMAT_PCM16, reportedTotalSamples, _stream.gain, _stream.target);

            _streamStopRequested = false;
            _streamFinished = false;
            _streamThread = new Thread(StreamThreadLoop)
            {
                Name = "HapbeatEditorStream",
                IsBackground = true,   // never block Editor shutdown
                Priority = System.Threading.ThreadPriority.AboveNormal,
            };
            _streamThread.Start();

            string routing = streamTargets < 0
                ? "broadcast (no device has answered a PING yet)"
                : $"unicast to {streamTargets} device(s)";
            Debug.Log($"[Hapbeat:Editor] \u266a StreamClip \"{clip.name}\" " +
                      $"{_stream.sampleRate}Hz/{_stream.channels}ch gain={gain:F2} " +
                      $"loop={loop} {routing}" +
                      (string.IsNullOrEmpty(target) ? "" : $" target={target}"));
        }

        public static void StopStream()
        {
            if (_stream == null) return;
            int iterations = _stream.iteration;
            StopStreamThread();
            // Drop the snapshot with the session that resolved it, so it can't
            // outlive the devices it was taken for (matches HapbeatManager).
            _client?.SetStreamUnicastTargets(null);
            Debug.Log($"[Hapbeat:Editor] \u25a0 Stream stopped after {iterations} iteration(s).");
            _stream = null;
        }

        /// <summary>
        /// Signal the pump and wait for it, then emit STREAM_END.
        ///
        /// STREAM_END is sent here rather than from the thread so there is exactly
        /// one place it can come from, whichever way the stream ends.
        /// </summary>
        private static void StopStreamThread()
        {
            if (_streamThread != null)
            {
                _streamStopRequested = true;
                // Bounded: the pump checks the flag every chunk, so this returns in
                // milliseconds. The timeout only guards against a wedged send, and
                // the thread is background so it cannot hold the Editor open.
                if (!_streamThread.Join(1000))
                    Debug.LogWarning("[Hapbeat:Editor] Stream thread did not stop in time.");
                _streamThread = null;
            }
            if (_stream != null)
                SendStreamEnd();
            _streamFinished = false;
        }

        /// <summary>
        /// Main-thread half of stream teardown: reacts to the pump finishing on its
        /// own (a non-looping clip running out). Everything that touches shared
        /// state or logs stays here rather than on the worker.
        /// </summary>
        private static void TickStreamTeardown()
        {
            if (!_streamFinished || _stream == null) return;
            StopStream();
        }

        private static void SendStreamEnd()
        {
            try { if (IsOpen) _client.SendStreamEnd(); } catch { }
        }

        /// <summary>
        /// Editor-loop driver: keeps discovery fresh and cleans up after a stream
        /// that finished on its own. The streaming itself is on its own thread —
        /// this callback is too irregular to pace audio from.
        /// </summary>
        private static void Tick()
        {
            TickDiscovery();
            TickStreamTeardown();
        }

        /// <summary>
        /// Re-PING periodically so known devices stay inside the client's liveness
        /// window. Letting them expire would silently drop test play back to
        /// broadcast mid-session — the same stutter as never pinging at all.
        /// </summary>
        private static void TickDiscovery()
        {
            if (!IsOpen) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastPingTime < PingIntervalSeconds) return;

            _lastPingTime = now;
            _client.SendPing();
        }

        /// <summary>
        /// Dedicated pump: pushes STREAM_DATA slightly ahead of real time until the
        /// clip runs out (or forever, when looping).
        ///
        /// Mirrors <see cref="HapbeatManager"/>'s stream thread, including the
        /// deliberate avoidance of <c>Thread.Sleep(1)</c> for short waits -- on
        /// Windows its floor is ~15.6 ms, which is longer than the lead being
        /// maintained and would itself cause the dropouts this exists to prevent.
        /// </summary>
        private static void StreamThreadLoop()
        {
            var state = _stream;
            if (state == null) { _streamFinished = true; return; }

            float sendAheadSeconds = ResolveSendAheadSeconds();
            int maxChunkSize = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
            var clock = Stopwatch.StartNew();

            try
            {
                while (!_streamStopRequested)
                {
                    if (state.remaining <= 0)
                    {
                        if (!state.loop) break;

                        // Re-arm for the next iteration. Offsets restart, so the
                        // device is told a new BEGIN, matching the runtime.
                        state.iteration++;
                        state.byteOffset = 0;
                        state.remaining = state.pcmBytes.Length;
                        clock.Restart();
                        _client.SendStreamBegin(state.sampleRate, state.channels,
                            HapbeatProtocol.AUDIO_FORMAT_PCM16, state.totalSamples, state.gain, state.target);
                        continue;
                    }

                    double elapsed = clock.Elapsed.TotalSeconds;
                    double sentDuration = state.byteOffset / state.bytesPerSecond;
                    double lead = sentDuration - elapsed;
                    if (lead > sendAheadSeconds)
                    {
                        // Wait only for the surplus, so the lead is topped up rather
                        // than drained to zero and refilled in bursts.
                        PreciseSleep(lead - sendAheadSeconds);
                        continue;
                    }

                    int chunk = Math.Min(state.remaining, maxChunkSize);
                    _client.SendStreamData(state.byteOffset, state.pcmBytes,
                        (int)state.byteOffset, chunk);
                    state.byteOffset += (uint)chunk;
                    state.remaining -= chunk;
                }
            }
            catch (Exception ex)
            {
                // A socket error must not take the Editor down with it.
                Debug.LogWarning($"[Hapbeat:Editor] Stream thread stopped: {ex.Message}");
            }
            finally
            {
                // The main thread sends STREAM_END and clears state; this only
                // reports that the pump is done.
                _streamFinished = true;
            }
        }

        /// <summary>Lead to keep buffered on the device, from config.</summary>
        private static float ResolveSendAheadSeconds()
        {
            var cfg = _cachedConfig; // already resolved by EnsureOpen; never touch AssetDatabase here
            float configured = cfg != null ? cfg.streamSendAheadSeconds : 0.05f;
            return Mathf.Max(configured, 0.05f);
        }

        /// <summary>
        /// Wait accurately for short durations.
        ///
        /// <c>Thread.Sleep(1)</c> has a ~15.6 ms floor on Windows, longer than the
        /// waits this pump asks for, so short waits spin instead. Copy of
        /// <see cref="HapbeatManager"/>'s helper -- that one is private, and the
        /// two loops are the same problem.
        /// </summary>
        private static void PreciseSleep(double seconds)
        {
            const double spinThresholdSeconds = 0.016;
            var sw = Stopwatch.StartNew();
            while (!_streamStopRequested)
            {
                double remaining = seconds - sw.Elapsed.TotalSeconds;
                if (remaining <= 0.0) return;
                if (remaining > spinThresholdSeconds)
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(200);
            }
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

        [MenuItem("Hapbeat/Close Edit-mode Transport", false, 97)]
        private static void CloseMenu() => Dispose();
    }
}
#endif
