#if UNITY_EDITOR
using System;
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
    /// StreamClip test play uses the same endpoint mixer as runtime. It sends only
    /// to PONG-resolved endpoints; it never broadcasts target-less STREAM_DATA.
    /// </summary>
    internal static class HapbeatEditorTransport
    {
        private static HapbeatClient _client;
        private static HapbeatEndpointStreamMixer _streamMixer;
        private static HapbeatConfig _cachedConfig;
        private static int _overridePlayer = -1;
        private static int _overrideGroup = -1;

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
        // deferred Test Play promptly joins when its target answers a PING.
        private const double PingIntervalSeconds = 3.0;
        private static double _lastPingTime;

        public static bool IsOpen => _client != null && _client.IsConnected;
        public static bool IsStreaming => _streamMixer != null && _streamMixer.IsStreaming;
        internal static bool HasStreamSources => _streamMixer != null && _streamMixer.HasSources;

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
                _overridePlayer = HapbeatClient.NormalizeOverride(savedPlayer);
                _overrideGroup = HapbeatClient.NormalizeOverride(savedGroup);
                _client.SetAddressOverride(_overridePlayer, _overrideGroup);
                EditorApplication.update -= Tick;
                EditorApplication.update += Tick;

                // Discover devices right away. StreamClip requires a PONG-resolved
                // endpoint, so it remains deferred until this exchange succeeds.
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
            _streamMixer?.Dispose();
            _streamMixer = null;
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

            if (_streamMixer != null)
                StopStream();

            string resolvedTarget = HapbeatClient.ResolveTarget(target, _overridePlayer, _overrideGroup);
            HapbeatStreamPlayback playback = GetStreamMixer().Add(clip, gain, gain, resolvedTarget, loop);
            if (playback.IsActive)
            {
                Debug.Log($"[Hapbeat:Editor] \u266a StreamClip \"{clip.name}\" " +
                          $"{clip.frequency}Hz/{clip.channels}ch gain={gain:F2} loop={loop} unicast" +
                          (string.IsNullOrEmpty(resolvedTarget) ? "" : $" target={resolvedTarget}"));
            }
            else
            {
                Debug.LogWarning($"[Hapbeat:Editor] StreamClip deferred: no PONG-resolved endpoint matches " +
                                 $"target '{resolvedTarget}'. STREAM_DATA was not broadcast.");
            }
        }

        public static void StopStream()
        {
            if (_streamMixer == null) return;
            _streamMixer.StopAll();
            Debug.Log("[Hapbeat:Editor] \u25a0 Stream stopped.");
        }

        /// <summary>
        /// Editor-loop driver: keeps discovery fresh and cleans up after a stream
        /// that finished on its own. The streaming itself is on its own thread —
        /// this callback is too irregular to pace audio from.
        /// </summary>
        private static void Tick()
        {
            _client?.DispatchMainThreadCallbacks();
            TickDiscovery();
            _streamMixer?.ReconcileEndpoints();
        }

        /// <summary>
        /// Re-PING periodically so PONG-resolved stream endpoints stay live.
        /// </summary>
        private static void TickDiscovery()
        {
            if (!IsOpen) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastPingTime < PingIntervalSeconds) return;

            _lastPingTime = now;
            _client.SendPing();
        }

        /// <summary>Lead to keep buffered on the device, from config.</summary>
        private static float ResolveSendAheadSeconds()
        {
            var cfg = _cachedConfig; // already resolved by EnsureOpen; never touch AssetDatabase here
            float configured = cfg != null ? cfg.streamSendAheadSeconds : 0.05f;
            return Mathf.Max(configured, 0.05f);
        }

        private static HapbeatEndpointStreamMixer GetStreamMixer()
        {
            if (_streamMixer != null) return _streamMixer;
            _streamMixer = new HapbeatEndpointStreamMixer(
                () => _client,
                target => _client != null
                    ? _client.GetResolvedStreamEndpoints(target)
                    : new System.Collections.Generic.List<HapbeatClient.StreamEndpoint>(),
                ResolveSendAheadSeconds,
                message => Debug.LogWarning($"[Hapbeat:Editor] {message}"));
            return _streamMixer;
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
