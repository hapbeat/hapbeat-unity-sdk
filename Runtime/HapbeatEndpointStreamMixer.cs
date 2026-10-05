using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using UnityEngine;

namespace Hapbeat
{
    internal readonly struct HapbeatEndpointStreamMixerDiagnostics
    {
        public readonly long MixedChunkCount;
        public readonly long DeadlineMissCount;
        public readonly long SchedulerAllocatedBytes;
        public readonly long SentPcmBytes;
        public readonly double MaxMixMilliseconds;

        public HapbeatEndpointStreamMixerDiagnostics(long mixedChunkCount, long deadlineMissCount,
            long schedulerAllocatedBytes, long sentPcmBytes, long maxMixTicks)
        {
            MixedChunkCount = mixedChunkCount;
            DeadlineMissCount = deadlineMissCount;
            SchedulerAllocatedBytes = schedulerAllocatedBytes;
            SentPcmBytes = sentPcmBytes;
            MaxMixMilliseconds = maxMixTicks * 1000.0 / Stopwatch.Frequency;
        }
    }

    internal interface IHapbeatEndpointStreamPacketSink
    {
        void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
            ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target);
        void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
            uint byteOffset, byte[] audioData, int dataOffset, int dataLength);
        void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity);
    }

    /// <summary>
    /// Pre-v2 stream packets for endpoints classified <see cref="StreamEndpointMode.Legacy"/>
    /// (stream-session-v2.md "Legacy receiver fallback"). Separate from the v2 sink so
    /// v2-only sinks keep compiling; a sink without it leaves legacy endpoints unresolved.
    /// </summary>
    internal interface IHapbeatLegacyEndpointStreamPacketSink
    {
        void LegacyBegin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format,
            uint totalSamples, float gain, string target);
        void LegacyData(IPEndPoint endpoint, uint byteOffset, byte[] audioData, int dataOffset, int dataLength);
        void LegacyEnd(IPEndPoint endpoint);
    }

    /// <summary>
    /// Owns logical StreamClip sources and produces one PCM16 stream per resolved
    /// device endpoint. STREAM_DATA cannot carry a target, so every packet is sent
    /// to its explicit PONG-backed endpoint and never falls back to broadcast.
    /// </summary>
    internal sealed class HapbeatEndpointStreamMixer : IDisposable
    {
        // The current haptic PCM path is 16 kHz stereo PCM16. Normalizing here means
        // source clip format is not part of the public acceptance contract.
        private const ushort OutputSampleRate = 16000;
        private const byte OutputChannels = 2;
        private const float ChunkSeconds = 0.01f;
        private const double EmptySessionLingerSeconds = 0.3;
        // Pre-v2 firmware cannot reject an old END, so a legacy endpoint keeps the
        // pre-v2 END->BEGIN guard. v2 endpoints have no cooldown (identity protects).
        private const double LegacyEndToBeginCooldownSeconds = 0.3;
        // A lost v2 BEGIN silences its whole session: firmware accepts DATA only for
        // the active tuple. An identical BEGIN is idempotent on the receiver (equal
        // tuple is ignored without a decoder/buffer reset, stream-session-v2.md
        // "Receiver ordering"), so v2 sessions repeat it before the first DATA blocks
        // and then periodically. Legacy firmware restarts on every BEGIN: no repeat.
        private const int BeginRepeatInitialBlocks = 3;
        private const int BeginRepeatIntervalBlocks = 50; // 500 ms of 10 ms blocks
        // Mix bus (sdk-multi-stream.md §5.8): a block whose summed peak is within full
        // scale passes unchanged; one past it is scaled down to LimitTarget (attack
        // ramped within the block, release by LimitReleasePerBlock per block), and a
        // tanh soft knee above LimitKnee replaces a hard clamp for what is left over.
        private const float FullScale = 32767f;
        private const float LimitTarget = 0.9f * FullScale;
        private const float LimitKnee = 0.95f * FullScale;
        private const float LimitReleasePerBlock = 0.05f;

        private readonly struct ChannelGains
        {
            public readonly float Left;
            public readonly float Right;

            public ChannelGains(float left, float right)
            {
                Left = left;
                Right = right;
            }
        }

        private sealed class Source
        {
            public readonly float[] Samples;
            public readonly int SampleRate;
            public readonly int Channels;
            public readonly string AuthoredTarget;
            public string EffectiveTarget;
            public readonly HapbeatStreamPlayback Playback;

            public Source(AudioClip clip, HapbeatStreamPlayback playback, string target)
            {
                Samples = new float[clip.samples * clip.channels];
                clip.GetData(Samples, 0); // Unity API: called by Add on the main thread.
                SampleRate = clip.frequency;
                Channels = clip.channels;
                AuthoredTarget = target;
                Playback = playback;
            }

            public Source(float[] samples, int sampleRate, int channels,
                HapbeatStreamPlayback playback, string target)
            {
                Samples = samples ?? throw new ArgumentNullException(nameof(samples));
                SampleRate = sampleRate;
                Channels = channels;
                AuthoredTarget = target;
                Playback = playback;
            }
        }

        private sealed class Session
        {
            public IPEndPoint Endpoint;
            public string Key;
            public string Address;
            public string WireTarget;
            public readonly HapbeatProtocol.StreamLeaseIdentity Lease;
            public readonly HapbeatProtocol.StreamSessionIdentity Identity;
            public readonly StreamEndpointMode Mode;
            public readonly Dictionary<Source, double> Positions = new Dictionary<Source, double>();
            // Channel gains each source was mixed with at the end of the previous block (§5.7).
            public readonly Dictionary<Source, ChannelGains> Gains = new Dictionary<Source, ChannelGains>();
            // Limiter gain at the end of the previous block (§5.8); 1 = no reduction.
            public float Limit = 1f;
            public readonly HashSet<Source> MatchingSources = new HashSet<Source>();
            public uint ByteOffset;
            public int DataBlocksSent;
            public bool Resolved;
            public bool BeginSent;
            public bool EndSent;
            public long EmptySinceTicks;

            public Session(IPEndPoint endpoint, string key, string address, string wireTarget,
                HapbeatProtocol.StreamLeaseIdentity lease, HapbeatProtocol.StreamSessionIdentity identity,
                StreamEndpointMode mode = StreamEndpointMode.V2)
            {
                Endpoint = endpoint;
                Key = key;
                Address = address;
                WireTarget = wireTarget;
                Lease = lease;
                Identity = identity;
                Mode = mode;
            }
        }

        private readonly object _lock = new object();
        private sealed class ClientPacketSink : IHapbeatEndpointStreamPacketSink,
            IHapbeatLegacyEndpointStreamPacketSink
        {
            private readonly Func<HapbeatClient> _getClient;
            public ClientPacketSink(Func<HapbeatClient> getClient) { _getClient = getClient; }
            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target) =>
                _getClient()?.SendStreamBeginTo(endpoint, identity, sampleRate, channels, format, totalSamples, gain, target);
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength) =>
                _getClient()?.SendStreamDataTo(endpoint, identity, byteOffset, audioData, dataOffset, dataLength);
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity) =>
                _getClient()?.SendStreamEndTo(endpoint, identity);
            public void LegacyBegin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format,
                uint totalSamples, float gain, string target) =>
                _getClient()?.SendLegacyStreamBeginTo(endpoint, sampleRate, channels, format, totalSamples, gain, target);
            public void LegacyData(IPEndPoint endpoint, uint byteOffset, byte[] audioData,
                int dataOffset, int dataLength) =>
                _getClient()?.SendLegacyStreamDataTo(endpoint, byteOffset, audioData, dataOffset, dataLength);
            public void LegacyEnd(IPEndPoint endpoint) => _getClient()?.SendLegacyStreamEndTo(endpoint);
        }

        private readonly IHapbeatEndpointStreamPacketSink _sink;
        private readonly IHapbeatLegacyEndpointStreamPacketSink _legacySink;
        // Legacy session key -> Stopwatch ticks of its last legacy END (guard only).
        private readonly Dictionary<string, long> _lastLegacyEndTicksByEndpoint = new Dictionary<string, long>();
        private bool _loggedLegacySinkUnsupported;
        private readonly Func<string, List<HapbeatClient.StreamEndpoint>> _resolveEndpoints;
        private readonly Func<string, string> _resolveEffectiveTarget;
        private readonly Func<IPEndPoint, HapbeatProtocol.StreamLeaseIdentity,
            HapbeatProtocol.StreamSessionIdentity> _allocateSessionIdentity;
        private readonly Func<float> _getSendAheadSeconds;
        private readonly Action<string> _log;
        private readonly Action _beforeNaturalFinalize;
        private readonly Action _beforeStopFinalize;
        private readonly List<Source> _sources = new List<Source>();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
        // Sessions that ended while their device was unresolved, kept only for their
        // source cursors / gains / limiter state. A lease renewal (focus regain,
        // ReacquireStreamOwnership) leaves the device unresolved for one PING round
        // trip; the replacement session on the same device resumes from these instead
        // of frame 0. Entries drop with their sources (RemoveSourceLocked / StopAll).
        private readonly List<Session> _parkedSessions = new List<Session>();
        private readonly Dictionary<HapbeatProtocol.StreamLeaseIdentity, uint> _nextGenerationByLease =
            new Dictionary<HapbeatProtocol.StreamLeaseIdentity, uint>();
        private uint _nextSyntheticGeneration;
        private Thread _thread;
        private volatile bool _stopRequested;
        private volatile bool _suppressSchedulerTerminationPackets;
        private float _sendAheadSeconds;
        private long _mixedChunkCount;
        private long _deadlineMissCount;
        private long _schedulerAllocatedBytes;
        private long _sentPcmBytes;
        private long _maxMixTicks;
        private bool _disposed;

        public HapbeatEndpointStreamMixer(Func<HapbeatClient> getClient,
            Func<string, List<HapbeatClient.StreamEndpoint>> resolveEndpoints,
            Func<string, string> resolveEffectiveTarget,
            Func<float> getSendAheadSeconds, Action<string> log)
        {
            var clientSink = new ClientPacketSink(getClient);
            _sink = clientSink;
            _legacySink = clientSink;
            _resolveEndpoints = resolveEndpoints;
            _resolveEffectiveTarget = resolveEffectiveTarget ?? throw new ArgumentNullException(nameof(resolveEffectiveTarget));
            _allocateSessionIdentity = (endpoint, lease) =>
            {
                HapbeatClient client = getClient();
                return client != null && client.TryAllocateStreamSessionIdentity(endpoint, lease, out var identity)
                    ? identity
                    : default;
            };
            _getSendAheadSeconds = getSendAheadSeconds;
            _sendAheadSeconds = Math.Max(0.01f, getSendAheadSeconds());
            _log = log;
            _beforeNaturalFinalize = null;
            _beforeStopFinalize = null;
        }

        internal HapbeatEndpointStreamMixer(IHapbeatEndpointStreamPacketSink sink,
            Func<string, List<HapbeatClient.StreamEndpoint>> resolveEndpoints,
            Func<float> getSendAheadSeconds, Action<string> log,
            Action beforeNaturalFinalize = null, Action beforeStopFinalize = null,
            Func<string, string> resolveEffectiveTarget = null)
        {
            _sink = sink;
            _legacySink = sink as IHapbeatLegacyEndpointStreamPacketSink;
            _resolveEndpoints = resolveEndpoints;
            _resolveEffectiveTarget = resolveEffectiveTarget ?? (target => target);
            _allocateSessionIdentity = null;
            _getSendAheadSeconds = getSendAheadSeconds;
            _sendAheadSeconds = Math.Max(0.01f, getSendAheadSeconds());
            _log = log;
            _beforeNaturalFinalize = beforeNaturalFinalize;
            _beforeStopFinalize = beforeStopFinalize;
        }

        public bool IsStreaming
        {
            get
            {
                lock (_lock)
                {
                    foreach (Session session in _sessions.Values)
                        if (session.BeginSent && !session.EndSent) return true;
                    return false;
                }
            }
        }

        /// <summary>True while logical sources are registered, including Deferred sources.</summary>
        internal bool HasSources
        {
            get { lock (_lock) return _sources.Count > 0; }
        }

        public HapbeatStreamPlayback ActivePlayback
        {
            get
            {
                lock (_lock)
                {
                    for (int i = 0; i < _sources.Count; i++)
                        if (_sources[i].Playback.IsActive) return _sources[i].Playback;
                    return null;
                }
            }
        }

        internal HapbeatEndpointStreamMixerDiagnostics Diagnostics =>
            new HapbeatEndpointStreamMixerDiagnostics(
                Interlocked.Read(ref _mixedChunkCount),
                Interlocked.Read(ref _deadlineMissCount),
                Interlocked.Read(ref _schedulerAllocatedBytes),
                Interlocked.Read(ref _sentPcmBytes),
                Interlocked.Read(ref _maxMixTicks));

        internal void ResetDiagnostics()
        {
            Interlocked.Exchange(ref _mixedChunkCount, 0);
            Interlocked.Exchange(ref _deadlineMissCount, 0);
            Interlocked.Exchange(ref _schedulerAllocatedBytes, 0);
            Interlocked.Exchange(ref _sentPcmBytes, 0);
            Interlocked.Exchange(ref _maxMixTicks, 0);
        }

        public HapbeatStreamPlayback Add(AudioClip clip, float baselineGain, float initialGain,
            string authoredTarget, bool loop)
        {
            if (clip == null) return null;
            RefreshSendAheadSeconds();
            var playback = new HapbeatStreamPlayback(
                baselineGain, initialGain, loop, OnPlaybackStopRequested);
            var source = new Source(clip, playback, authoredTarget);
            lock (_lock)
            {
                ThrowIfDisposed();
                _sources.Add(source);
                ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
                StartThreadLocked();
            }
            return playback;
        }

        internal HapbeatStreamPlayback AddSamples(float[] samples, int sampleRate, int channels,
            float baselineGain, float initialGain, string target, bool loop)
        {
            RefreshSendAheadSeconds();
            var playback = new HapbeatStreamPlayback(
                baselineGain, initialGain, loop, OnPlaybackStopRequested);
            var source = new Source(samples, sampleRate, channels, playback, target);
            lock (_lock)
            {
                ThrowIfDisposed();
                _sources.Add(source);
                ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
                StartThreadLocked();
            }
            return playback;
        }

        /// <summary>Called after PONG callbacks have been dispatched on Unity's main thread.</summary>
        public void ReconcileEndpoints()
        {
            RefreshSendAheadSeconds();
            lock (_lock)
            {
                if (_disposed) return;
                ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
                StartThreadLocked();
            }
        }

        /// <summary>
        /// Re-resolves every logical source from its authored target after a runtime
        /// address override changes. Returns true when any source remains deferred
        /// and discovery should be asked for a fresh PONG.
        /// </summary>
        internal bool NotifyAddressOverrideChanged()
        {
            RefreshSendAheadSeconds();
            lock (_lock)
            {
                if (_disposed) return false;
                ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
                StartThreadLocked();
                return HasDeferredSourcesLocked();
            }
        }

        public void StopAll()
        {
            lock (_lock)
            {
                if (_disposed) return;
                for (int i = 0; i < _sources.Count; i++) _sources[i].Playback.MarkStopped();
                _sources.Clear();
                _parkedSessions.Clear();
                RebuildSessionMembershipLocked(Stopwatch.GetTimestamp());
                StartThreadLocked();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Thread thread;
            List<Session> terminationSessions;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                for (int i = 0; i < _sources.Count; i++) _sources[i].Playback.MarkStopped();
                _sources.Clear();
                _parkedSessions.Clear();
                terminationSessions = new List<Session>(_sessions.Values);
                _suppressSchedulerTerminationPackets = true;
                _stopRequested = true;
                thread = _thread;
            }
            bool joined = thread == null || thread.Join(500);
            SendTerminationPackets(terminationSessions);
            if (!joined) _log("Stream mixer did not exit within 500ms.");
            lock (_lock)
            {
                _sessions.Clear();
                if (joined) _thread = null;
            }
        }

        private void ReconcileEndpointsLocked()
        {
            var wanted = new Dictionary<string, HapbeatClient.StreamEndpoint>();
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                if (source.Playback.IsStopped) continue;
                source.EffectiveTarget = _resolveEffectiveTarget(source.AuthoredTarget);
                List<HapbeatClient.StreamEndpoint> endpoints = _resolveEndpoints(source.EffectiveTarget);
                if (endpoints == null) continue;
                for (int e = 0; e < endpoints.Count; e++)
                {
                    var endpoint = endpoints[e];
                    if (endpoint.Mode == StreamEndpointMode.Legacy && _legacySink == null)
                    {
                        if (!_loggedLegacySinkUnsupported)
                        {
                            _loggedLegacySinkUnsupported = true;
                            _log("Stream sink has no legacy (pre-v2) support; legacy endpoints stay unresolved.");
                        }
                        continue;
                    }
                    string key = EndpointKey(endpoint);
                    wanted[key] = endpoint;
                }
            }

            var unassignedSessions = new List<Session>();
            foreach (Session session in _sessions.Values)
            {
                session.Resolved = false;
                unassignedSessions.Add(session);
            }
            var remainingWanted = new List<string>();
            foreach (string key in wanted.Keys)
            {
                if (_sessions.TryGetValue(key, out Session exact))
                {
                    exact.Resolved = true;
                    exact.Endpoint = wanted[key].EndPoint;
                    exact.Address = wanted[key].Address;
                    if (!exact.BeginSent) exact.WireTarget = exact.Address;
                    unassignedSessions.Remove(exact);
                }
                else remainingWanted.Add(key);
            }

            // A PONG can report a new route or a new address for a stream that is
            // already armed. STREAM_DATA/END carry no target, so restarting across
            // that boundary risks an old-path END overtaking the new BEGIN. Preserve
            // the session and both cursors whenever either half identifies it.
            for (int i = remainingWanted.Count - 1; i >= 0; i--)
            {
                string key = remainingWanted[i];
                HapbeatClient.StreamEndpoint endpoint = wanted[key];
                Session migration = null;
                for (int s = 0; s < unassignedSessions.Count; s++)
                {
                    Session candidate = unassignedSessions[s];
                    bool stableLease = candidate.Lease.IsValid && endpoint.Lease.IsValid &&
                        candidate.Lease.Equals(endpoint.Lease);
                    // Route/address migration for lease-less sessions of the SAME
                    // wire format only; a mode change must END the old session.
                    bool mockRouteMigration = !candidate.Lease.IsValid && !endpoint.Lease.IsValid &&
                        candidate.Mode == endpoint.Mode &&
                        (candidate.Endpoint.Equals(endpoint.EndPoint) ||
                         string.Equals(candidate.Address, endpoint.Address, StringComparison.Ordinal));
                    if (stableLease || mockRouteMigration)
                    {
                        migration = candidate;
                        break;
                    }
                }
                if (migration == null) continue;

                _sessions.Remove(migration.Key);
                migration.Endpoint = endpoint.EndPoint;
                migration.Key = key;
                migration.Address = endpoint.Address;
                // An unbegun session (e.g. held by the legacy guard) must BEGIN with
                // the address the device reports now, or firmware rejects the BEGIN.
                if (!migration.BeginSent) migration.WireTarget = endpoint.Address;
                migration.Resolved = true;
                migration.EmptySinceTicks = 0;
                _sessions.Add(key, migration);
                unassignedSessions.Remove(migration);
                remainingWanted.RemoveAt(i);
            }

            long now = Stopwatch.GetTimestamp();
            List<Session> retiredSessions = null;

            // A device reboot or lease renewal changes its transport identity. End
            // the retired session with the identity it began under before a new
            // session starts; never migrate an old END onto the new token. A class
            // change (legacy<->v2 after a firmware update/rollback) likewise ENDs the
            // retired session immediately in its OLD format, without the idle linger.
            for (int s = unassignedSessions.Count - 1; s >= 0; s--)
            {
                Session retired = unassignedSessions[s];
                for (int w = 0; w < remainingWanted.Count; w++)
                {
                    HapbeatClient.StreamEndpoint replacement = wanted[remainingWanted[w]];
                    bool modeChange = retired.Mode != replacement.Mode;
                    bool leaseChange = retired.Lease.IsValid && replacement.Lease.IsValid &&
                        !retired.Lease.Equals(replacement.Lease);
                    if (!modeChange && !leaseChange) continue;
                    if (!retired.Endpoint.Equals(replacement.EndPoint) &&
                        !string.Equals(retired.Address, replacement.Address, StringComparison.Ordinal)) continue;

                    EndSessionLocked(retired);
                    _sessions.Remove(retired.Key);
                    unassignedSessions.RemoveAt(s);
                    if (retiredSessions == null) retiredSessions = new List<Session>();
                    retiredSessions.Add(retired);
                    break;
                }
            }

            for (int i = 0; i < remainingWanted.Count; i++)
            {
                string key = remainingWanted[i];
                HapbeatClient.StreamEndpoint endpoint = wanted[key];
                // STREAM_DATA has no target. Even though this is an explicit direct
                // endpoint, BEGIN must carry the PONG-resolved address so firmware
                // rejects it if that IP was reassigned before the next PONG refresh.
                // Legacy sessions carry no identity: pre-v2 firmware has no lease.
                HapbeatProtocol.StreamSessionIdentity identity =
                    endpoint.Mode == StreamEndpointMode.Legacy
                        ? default
                        : AllocateSessionIdentity(endpoint.EndPoint, endpoint.Lease);
                if (endpoint.Lease.IsValid && !identity.IsValid)
                {
                    continue;
                }
                var session = new Session(endpoint.EndPoint, key, endpoint.Address, endpoint.Address,
                    endpoint.Lease, identity, endpoint.Mode);
                session.Resolved = true;
                // Only the identity is new: the same device continues every source
                // from where its previous session left it (no restart from frame 0).
                CarryCursorsLocked(session, retiredSessions);
                _sessions.Add(key, session);
            }

            RebuildSessionMembershipLocked(now);
            ProcessSessionLifecycleLocked(now);
        }

        private void UpdatePlaybackStatesLocked()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                bool matched = false;
                foreach (var session in _sessions.Values)
                {
                    if (session.BeginSent && !session.EndSent && session.MatchingSources.Contains(source))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched) source.Playback.MarkActive();
                else source.Playback.MarkDeferred(HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint);
            }
        }

        private bool HasDeferredSourcesLocked()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                if (!source.Playback.IsStopped && source.Playback.Status == HapbeatStreamPlaybackStatus.Deferred)
                    return true;
            }
            return false;
        }

        private void StartThreadLocked()
        {
            if (_thread != null || _sessions.Count == 0) return;
            _stopRequested = false;
            _suppressSchedulerTerminationPackets = false;
            _thread = new Thread(ThreadLoop) { Name = "HapbeatEndpointStreamMixer", IsBackground = true };
            _thread.Start();
        }

        private void ThreadLoop()
        {
            const int frames = (int)(OutputSampleRate * ChunkSeconds);
            var mix = new float[frames * OutputChannels];
            var pcm = new byte[mix.Length * 2];
            var watch = Stopwatch.StartNew();
            double sentSeconds = 0;
            bool observedNaturalCompletion = false;
            try
            {
                while (!_stopRequested)
                {
                    long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                    long mixStarted = Stopwatch.GetTimestamp();
                    bool hasSources;
                    lock (_lock)
                    {
                        if (_stopRequested) break;
                        if (RemoveStoppedSourcesLocked())
                        {
                            ReconcileEndpointsLocked();
                            UpdatePlaybackStatesLocked();
                        }
                        long now = Stopwatch.GetTimestamp();
                        ProcessSessionLifecycleLocked(now);
                        UpdatePlaybackStatesLocked();
                        hasSources = _sources.Count > 0;
                        if (_sessions.Count == 0)
                        {
                            observedNaturalCompletion = !hasSources;
                            break;
                        }
                        foreach (var session in _sessions.Values)
                        {
                            if (session.BeginSent && !session.EndSent && session.MatchingSources.Count > 0)
                                MixAndSendSessionLocked(session, frames, mix, pcm);
                        }
                        if (RemoveCompletedSourcesLocked())
                        {
                            ReconcileEndpointsLocked();
                            UpdatePlaybackStatesLocked();
                        }
                    }

                    long mixTicks = Stopwatch.GetTimestamp() - mixStarted;
                    long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
                    Interlocked.Increment(ref _mixedChunkCount);
                    if (mixTicks > Stopwatch.Frequency * ChunkSeconds)
                        Interlocked.Increment(ref _deadlineMissCount);
                    if (allocatedBytes > 0)
                        Interlocked.Add(ref _schedulerAllocatedBytes, allocatedBytes);
                    UpdateMaximum(ref _maxMixTicks, mixTicks);

                    sentSeconds += ChunkSeconds;
                    float ahead = Math.Max(0.01f, Volatile.Read(ref _sendAheadSeconds));
                    double sleep = sentSeconds - watch.Elapsed.TotalSeconds - ahead;
                    if (sleep > 0) SleepPrecisely(sleep);
                }
            }
            finally
            {
                if (observedNaturalCompletion && !_stopRequested)
                    _beforeNaturalFinalize?.Invoke();
                if (_stopRequested)
                    _beforeStopFinalize?.Invoke();
                lock (_lock)
                {
                    if (RemoveStoppedSourcesLocked())
                    {
                        ReconcileEndpointsLocked();
                        UpdatePlaybackStatesLocked();
                    }
                    // Dispose owns the forced-stop END even if its bounded join
                    // times out, so the old scheduler cannot emit a later duplicate.
                    if (_stopRequested)
                    {
                        EndAllSessionsLocked();
                        _sessions.Clear();
                        _thread = null;
                        if (_sources.Count > 0 && !_disposed)
                        {
                            _stopRequested = false;
                            ReconcileEndpointsLocked();
                            UpdatePlaybackStatesLocked();
                            StartThreadLocked();
                        }
                    }
                    else if (_sources.Count > 0)
                    {
                        // Add won the lock after this thread observed an empty set.
                        // Preserve the session and hand the new source to a fresh loop.
                        _thread = null;
                        StartThreadLocked();
                    }
                    else
                    {
                        // Normal completion already passed through the 300 ms empty
                        // session linger and lifecycle END before reaching here.
                        ProcessSessionLifecycleLocked(Stopwatch.GetTimestamp());
                    }
                    if (_sources.Count == 0 || _stopRequested) _thread = null;
                }
            }
        }

        private void MixAndSendSessionLocked(Session session, int frames, float[] mix, byte[] pcm)
        {
            Array.Clear(mix, 0, mix.Length);
            if (session.MatchingSources.Count == 0) return;
            foreach (Source source in session.MatchingSources)
            {
                if (source.Playback.IsStopped) continue;
                MixSource(source, session, frames, mix);
            }

            for (int i = 0; i < mix.Length; i++) mix[i] *= FullScale;
            session.Limit = LimitBlock(session.Limit, mix, frames);
            // The single final saturation to the PCM16 range (§5.3).
            for (int i = 0; i < mix.Length; i++)
            {
                float sample = mix[i];
                // Compare before the cast: (int)NaN is int.MinValue (full-scale
                // negative) and (int)+-Infinity is undefined. A NaN sample is silence.
                int value = float.IsNaN(sample) ? 0
                    : sample >= short.MaxValue ? short.MaxValue
                    : sample <= short.MinValue ? short.MinValue
                    : (int)sample;
                pcm[i * 2] = (byte)value;
                pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            if (session.Mode == StreamEndpointMode.Legacy)
            {
                _legacySink.LegacyData(session.Endpoint, session.ByteOffset, pcm, 0, pcm.Length);
            }
            else
            {
                if (ShouldRepeatBegin(session.DataBlocksSent)) SendBegin(session);
                _sink.Data(session.Endpoint, session.Identity, session.ByteOffset, pcm, 0, pcm.Length);
            }
            Interlocked.Add(ref _sentPcmBytes, pcm.Length);
            session.ByteOffset += (uint)pcm.Length;
            session.DataBlocksSent++;
        }

        private static void MixSource(Source source, Session session, int frames, float[] mix)
        {
            if (!session.Positions.TryGetValue(source, out double position)) position = 0;
            double step = source.SampleRate / (double)OutputSampleRate;
            float gain = source.Playback.Gain;
            source.Playback.GetStereoChannelGains(out float panL, out float panR);
            float gainL = gain * panL;
            float gainR = gain * panR;
            // A new source starts at its own gains (its PCM carries the onset); a changed
            // gain / pan ramps linearly across this block instead of stepping at its edge.
            ChannelGains start = session.Gains.TryGetValue(source, out ChannelGains previous)
                ? previous
                : new ChannelGains(gainL, gainR);
            session.Gains[source] = new ChannelGains(gainL, gainR);
            float stepL = (gainL - start.Left) / frames;
            float stepR = (gainR - start.Right) / frames;
            bool ramp = stepL != 0f || stepR != 0f;
            int sourceFrames = source.Samples.Length / source.Channels;
            for (int frame = 0; frame < frames; frame++)
            {
                if (position >= sourceFrames)
                {
                    // A zero-length loop has no frame to wrap to (position % 0 is NaN).
                    if (!source.Playback.Loop || sourceFrames == 0) break;
                    position %= sourceFrames;
                }
                int index = (int)position;
                int next = Math.Min(index + 1, sourceFrames - 1);
                float fraction = (float)(position - index);
                float left = LerpSample(source, index, next, 0, fraction);
                float right = source.Channels == 1 ? left : LerpSample(source, index, next, 1, fraction);
                int output = frame * OutputChannels;
                float frameGainL = ramp ? start.Left + stepL * (frame + 1) : gainL;
                float frameGainR = ramp ? start.Right + stepR * (frame + 1) : gainR;
                mix[output] += left * frameGainL;
                mix[output + 1] += right * frameGainR;
                position += step;
            }
            session.Positions[source] = position;
        }

        /// <summary>
        /// Applies the §5.8 limiter in place to one block of full-scale samples and
        /// returns the limiter gain the block ended at (the next block's start).
        /// </summary>
        private static float LimitBlock(float limit, float[] mix, int frames)
        {
            float peak = 0f;
            for (int i = 0; i < mix.Length; i++)
            {
                float magnitude = Math.Abs(mix[i]);
                if (magnitude > peak) peak = magnitude;
            }
            float want = peak > FullScale ? LimitTarget / peak : 1f;
            float start = limit;
            float end = want < start ? want : Math.Min(want, start + LimitReleasePerBlock);
            // A block within full scale at unity limiter gain stays bit-identical.
            if (start == 1f && end == 1f) return end;

            float step = (end - start) / frames;
            const float span = FullScale - LimitKnee;
            for (int frame = 0; frame < frames; frame++)
            {
                float gain = start + step * (frame + 1);
                for (int channel = 0; channel < OutputChannels; channel++)
                {
                    int i = frame * OutputChannels + channel;
                    float value = mix[i] * gain;
                    float magnitude = Math.Abs(value);
                    if (magnitude > LimitKnee)
                    {
                        float knee = LimitKnee + span * (float)Math.Tanh((magnitude - LimitKnee) / span);
                        value = value < 0f ? -knee : knee;
                    }
                    mix[i] = value;
                }
            }
            return end;
        }

        private static float LerpSample(Source source, int index, int next, int channel, float fraction)
        {
            int offset = Math.Min(channel, source.Channels - 1);
            float a = source.Samples[index * source.Channels + offset];
            float b = source.Samples[next * source.Channels + offset];
            return a + (b - a) * fraction;
        }

        private bool RemoveStoppedSourcesLocked()
        {
            bool changed = false;
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                if (!_sources[i].Playback.IsStopped) continue;
                RemoveSourceLocked(_sources[i], i);
                changed = true;
            }
            return changed;
        }

        private bool RemoveCompletedSourcesLocked()
        {
            bool changed = false;
            for (int i = _sources.Count - 1; i >= 0; i--)
            {
                Source source = _sources[i];
                if (source.Playback.Loop || source.Playback.IsStopped) continue;
                bool matchedAnySession = false;
                bool completedEverywhere = true;
                foreach (var session in _sessions.Values)
                {
                    if (!session.MatchingSources.Contains(source)) continue;
                    matchedAnySession = true;
                    if (!session.Positions.TryGetValue(source, out double position) ||
                        position < source.Samples.Length / source.Channels)
                    {
                        completedEverywhere = false;
                        break;
                    }
                }
                // A deferred one-shot has no matching endpoint yet. Keep it in the
                // registry so a later PONG can start it; unrelated endpoint sessions
                // must not make it look naturally complete.
                if (matchedAnySession && completedEverywhere)
                {
                    source.Playback.MarkStopped();
                    RemoveSourceLocked(source, i);
                    changed = true;
                }
            }
            return changed;
        }

        private void RemoveSourceLocked(Source source, int index)
        {
            _sources.RemoveAt(index);
            foreach (var session in _sessions.Values)
            {
                session.Positions.Remove(source);
                session.Gains.Remove(source);
                session.MatchingSources.Remove(source);
            }
            for (int i = _parkedSessions.Count - 1; i >= 0; i--)
            {
                Session parked = _parkedSessions[i];
                parked.Positions.Remove(source);
                parked.Gains.Remove(source);
                if (parked.Positions.Count == 0) _parkedSessions.RemoveAt(i);
            }
        }

        private void ParkSessionLocked(Session session)
        {
            for (int i = _parkedSessions.Count - 1; i >= 0; i--)
                if (IsSameDevice(_parkedSessions[i], session.Endpoint, session.Address))
                    _parkedSessions.RemoveAt(i);
            _parkedSessions.Add(session);
        }

        /// <summary>
        /// Copies source cursors, per-source gains and limiter state into a new session
        /// from the session it replaces on the same device: one retired in this
        /// reconcile pass, else one parked after ending while unresolved.
        /// </summary>
        private void CarryCursorsLocked(Session session, List<Session> retiredSessions)
        {
            Session previous = null;
            if (retiredSessions != null)
            {
                for (int i = 0; i < retiredSessions.Count && previous == null; i++)
                    if (IsSameDevice(retiredSessions[i], session.Endpoint, session.Address))
                        previous = retiredSessions[i];
            }
            for (int i = 0; i < _parkedSessions.Count; i++)
            {
                if (!IsSameDevice(_parkedSessions[i], session.Endpoint, session.Address)) continue;
                if (previous == null) previous = _parkedSessions[i];
                _parkedSessions.RemoveAt(i);
                break;
            }
            if (previous == null) return;
            foreach (var pair in previous.Positions) session.Positions[pair.Key] = pair.Value;
            foreach (var pair in previous.Gains) session.Gains[pair.Key] = pair.Value;
            session.Limit = previous.Limit;
        }

        private static bool IsSameDevice(Session session, IPEndPoint endpoint, string address) =>
            session.Endpoint.Equals(endpoint) ||
            string.Equals(session.Address, address, StringComparison.Ordinal);

        private void RebuildSessionMembershipLocked(long now)
        {
            foreach (Session session in _sessions.Values)
            {
                session.MatchingSources.Clear();
                for (int i = 0; i < _sources.Count; i++)
                {
                    Source source = _sources[i];
                    if (session.Resolved && !source.Playback.IsStopped && SessionMatchesSource(session, source))
                        session.MatchingSources.Add(source);
                }

                List<Source> stalePositions = null;
                foreach (Source source in session.Positions.Keys)
                {
                    if (session.MatchingSources.Contains(source)) continue;
                    // An unresolved session keeps the cursor of a source that still
                    // targets its device, so a replacement session can resume it.
                    if (!session.Resolved && !source.Playback.IsStopped &&
                        SessionMatchesSource(session, source)) continue;
                    if (stalePositions == null) stalePositions = new List<Source>();
                    stalePositions.Add(source);
                }
                if (session.MatchingSources.Count > 0) session.EmptySinceTicks = 0;
                else if (session.EmptySinceTicks == 0) session.EmptySinceTicks = now;
                if (stalePositions == null) continue;
                for (int i = 0; i < stalePositions.Count; i++)
                {
                    session.Positions.Remove(stalePositions[i]);
                    session.Gains.Remove(stalePositions[i]);
                }
            }
        }

        private void ProcessSessionLifecycleLocked(long now)
        {
            List<string> remove = null;
            foreach (var pair in _sessions)
            {
                Session session = pair.Value;
                if (session.MatchingSources.Count > 0)
                {
                    session.EmptySinceTicks = 0;
                    if (!session.BeginSent)
                    {
                        if (session.Mode == StreamEndpointMode.Legacy)
                        {
                            // Sources for this endpoint stay deferred until the guard
                            // elapses; other endpoints are unaffected.
                            if (!LegacyCooldownElapsedLocked(session.Endpoint, now)) continue;
                            _legacySink.LegacyBegin(session.Endpoint, OutputSampleRate, OutputChannels,
                                HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, session.WireTarget);
                        }
                        else
                        {
                            SendBegin(session);
                        }
                        session.BeginSent = true;
                    }
                    continue;
                }

                if (session.EmptySinceTicks == 0) session.EmptySinceTicks = now;
                double emptySeconds = (now - session.EmptySinceTicks) / (double)Stopwatch.Frequency;
                if (emptySeconds < EmptySessionLingerSeconds) continue;
                if (session.BeginSent) EndSessionLocked(session);
                if (!session.Resolved && session.Positions.Count > 0) ParkSessionLocked(session);
                if (remove == null) remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null) return;
            for (int i = 0; i < remove.Count; i++) _sessions.Remove(remove[i]);
        }

        private void SendBegin(Session session)
        {
            _sink.Begin(session.Endpoint, session.Identity, OutputSampleRate, OutputChannels,
                HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, session.WireTarget);
        }

        /// <summary>
        /// Whether the identical v2 BEGIN is re-sent before DATA block
        /// <paramref name="dataBlocksSent"/> (0-based). Block 0 follows the original
        /// BEGIN; blocks 1..<see cref="BeginRepeatInitialBlocks"/>-1 and every
        /// <see cref="BeginRepeatIntervalBlocks"/>-th block are preceded by a repeat.
        /// </summary>
        internal static bool ShouldRepeatBegin(int dataBlocksSent) =>
            dataBlocksSent > 0 &&
            (dataBlocksSent < BeginRepeatInitialBlocks || dataBlocksSent % BeginRepeatIntervalBlocks == 0);

        // Keyed by device endpoint, not session key: an address change on the same
        // device must not skip the guard, since pre-v2 firmware ignores END payloads.
        private bool LegacyCooldownElapsedLocked(IPEndPoint endpoint, long now)
        {
            if (!_lastLegacyEndTicksByEndpoint.TryGetValue(endpoint.ToString(), out long endedAt)) return true;
            return (now - endedAt) / (double)Stopwatch.Frequency >= LegacyEndToBeginCooldownSeconds;
        }

        private static bool SessionMatchesSource(Session session, Source source)
        {
            return HapbeatClient.AddressMatches(source.EffectiveTarget, session.Address);
        }

        private void EndAllSessionsLocked()
        {
            foreach (var session in _sessions.Values) EndSessionLocked(session);
        }

        private void EndSessionLocked(Session session)
        {
            if (!session.BeginSent || session.EndSent) return;
            session.EndSent = true;
            if (!_suppressSchedulerTerminationPackets)
            {
                SendEndPacket(session);
            }
        }

        private void SendEndPacket(Session session)
        {
            if (session.Mode == StreamEndpointMode.Legacy)
            {
                _legacySink.LegacyEnd(session.Endpoint);
                // The guard is recorded only on a legacy END and read only for legacy
                // sessions, so v2 endpoints never inherit it.
                _lastLegacyEndTicksByEndpoint[session.Endpoint.ToString()] = Stopwatch.GetTimestamp();
            }
            else
            {
                _sink.End(session.Endpoint, session.Identity);
            }
        }

        private void SendTerminationPackets(List<Session> sessions)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                Session session = sessions[i];
                if (session.BeginSent) SendEndPacket(session);
            }
        }

        private static string EndpointKey(HapbeatClient.StreamEndpoint endpoint) =>
            endpoint.Mode == StreamEndpointMode.Legacy
                ? "legacy|" + endpoint.EndPoint + "|" + (endpoint.Address ?? string.Empty)
                : endpoint.Lease.IsValid
                ? "lease:" + endpoint.Lease.DeviceBootId.ToString("X16") + ":" + endpoint.Lease.LeaseTicket
                : endpoint.EndPoint + "|" + (endpoint.Address ?? string.Empty);

        private HapbeatProtocol.StreamSessionIdentity AllocateSessionIdentity(IPEndPoint endpoint,
            HapbeatProtocol.StreamLeaseIdentity lease)
        {
            if (_allocateSessionIdentity != null)
                return _allocateSessionIdentity(endpoint, lease);
            if (!lease.IsValid)
                return new HapbeatProtocol.StreamSessionIdentity(lease, ++_nextSyntheticGeneration);

            _nextGenerationByLease.TryGetValue(lease, out uint previous);
            if (previous == uint.MaxValue)
                throw new InvalidOperationException("Stream generation is exhausted; renew the device lease before starting another session.");
            uint generation = previous + 1;
            _nextGenerationByLease[lease] = generation;
            return new HapbeatProtocol.StreamSessionIdentity(lease, generation);
        }

        private void SleepPrecisely(double seconds)
        {
            const double spinThresholdSeconds = 0.016;
            var watch = Stopwatch.StartNew();
            while (!_stopRequested)
            {
                double remaining = seconds - watch.Elapsed.TotalSeconds;
                if (remaining <= 0) return;
                if (remaining > spinThresholdSeconds) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }
        }

        private void RefreshSendAheadSeconds()
        {
            Volatile.Write(ref _sendAheadSeconds, Math.Max(0.01f, _getSendAheadSeconds()));
        }

        private void OnPlaybackStopRequested()
        {
            lock (_lock)
            {
                if (_disposed || _thread != null || !RemoveStoppedSourcesLocked()) return;
                ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
            }
        }

        private static void UpdateMaximum(ref long destination, long candidate)
        {
            long current = Interlocked.Read(ref destination);
            while (candidate > current)
            {
                long observed = Interlocked.CompareExchange(ref destination, candidate, current);
                if (observed == current) return;
                current = observed;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HapbeatEndpointStreamMixer));
        }
    }
}
