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
    /// Stream Gen 1 packets for endpoints classified <see cref="StreamEndpointMode.Legacy"/>
    /// (stream-session.md "Legacy receiver fallback"). Separate from the Gen 2 sink so
    /// Gen 2-only sinks keep compiling; a sink without it leaves Gen 1 endpoints unresolved.
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
        // Stream Gen 1 firmware cannot reject an old END, so a Gen 1 endpoint keeps the
        // END->BEGIN guard. Gen 2 endpoints have no cooldown (identity protects).
        private const double LegacyEndToBeginCooldownSeconds = 0.3;
        // A lost Gen 2 BEGIN silences its whole session: firmware accepts DATA only for
        // the active tuple. An identical BEGIN is idempotent on the receiver (equal
        // tuple is ignored without a decoder/buffer reset, stream-session.md
        // "Receiver ordering"), so Gen 2 sessions repeat it before the first DATA blocks
        // and then at least every 500 ms (Sender requirements 2). Gen 1 firmware
        // restarts on every BEGIN: no repeat.
        private const int BeginRepeatInitialBlocks = 3;
        private const int BeginRepeatIntervalBlocks = 50; // 500 ms of 10 ms blocks
        // Sender requirements 3: after 3 s or more without DATA (the process was
        // suspended, a debugger break, sleep) the receiver may have timed the session
        // out (5 s), so the session ENDs and BEGINs a new generation before more DATA.
        internal const double OwnGapSeconds = 3.0;
        // Re-splice (DEC-093): a source due inside blocks already sent ahead would play late by up to the send-ahead
        // (it joins behind the queued data). When the device holds more than its BEGIN flush threshold (512 frames =
        // 32 ms; this margin covers packets in flight), the session restarts under a new generation from the source's
        // start: firmware drops the queued data on that BEGIN and the re-mixed blocks follow at once.
        private const double RespliceMinQueuedSeconds = 0.04;
        // One pass never sends more than this many blocks to a session (a stalled thread catches up gradually).
        private const int MaxBlocksPerPass = 50;
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
            // Mixer-clock time (s) its first frame is due on the stream timeline (Add time + its start delay).
            public double StartSec;
            // Devices (by IP, so an address change keeps it) whose session STOP_ALL ended
            // while this source played there (ERROR STREAM_STOPPED, sdk-multi-stream.md
            // §6.13): this Playback never rejoins them.
            public readonly HashSet<IPAddress> RemovedDevices = new HashSet<IPAddress>();

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
            // Replaced by a new generation on the same lease (re-splice, own gap,
            // STREAM_TIMED_OUT / STREAM_LEASE_INVALID); the replaced one is kept so an
            // ERROR about it still matches this session.
            public HapbeatProtocol.StreamSessionIdentity Identity;
            public HapbeatProtocol.StreamSessionIdentity PreviousIdentity;
            public readonly StreamEndpointMode Mode;
            // CAPS haptic_channels == 1: one motor, so pan is not applied (§6.15).
            public bool MonoOutput;
            // Mixer-clock time (s) the last DATA block was sent (own-gap rule).
            public double LastDataSec;
            // Mixer-clock time (s) the next block this session sends is due: block k plays about the network latency
            // after its due time. Kept at "now" while the session sends nothing, so resuming never sends old silence.
            public double DueSec = double.NegativeInfinity;
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
        private readonly Func<string, HapbeatStreamPlaybackDeferReason> _resolveDeferReason;
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
        // The stream timeline: source start times and session due times are seconds on this clock.
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        // Added to the clock by tests that simulate a stalled sender (AdvanceClockForTest).
        private long _clockOffsetTicks;
        private double Now => (_clock.ElapsedTicks + Interlocked.Read(ref _clockOffsetTicks)) / (double)Stopwatch.Frequency;
        // Set by a re-splice so the scheduler sends the re-mixed blocks without waiting out its sleep.
        private volatile bool _wakeRequested;
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
            _resolveDeferReason = target =>
            {
                HapbeatClient client = getClient();
                return client != null
                    ? client.GetStreamDeferReason(target)
                    : HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint;
            };
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
            Func<string, string> resolveEffectiveTarget = null,
            Func<string, HapbeatStreamPlaybackDeferReason> resolveDeferReason = null)
        {
            _sink = sink;
            _legacySink = sink as IHapbeatLegacyEndpointStreamPacketSink;
            _resolveEndpoints = resolveEndpoints;
            _resolveEffectiveTarget = resolveEffectiveTarget ?? (target => target);
            _resolveDeferReason = resolveDeferReason ?? (_ => HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint);
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

        /// <param name="startDelaySeconds">The source starts this long after the call on the stream timeline
        /// (sample-accurate, DEC-093); 0 = at once. Every source plays the same network latency after its due
        /// time, whether or not others are streaming to the same device (re-splice).</param>
        public HapbeatStreamPlayback Add(AudioClip clip, float baselineGain, float initialGain,
            string authoredTarget, bool loop, double startDelaySeconds = 0)
        {
            if (clip == null) return null;
            RefreshSendAheadSeconds();
            var playback = new HapbeatStreamPlayback(
                baselineGain, initialGain, loop, OnPlaybackStopRequested);
            return AddSource(new Source(clip, playback, authoredTarget), startDelaySeconds);
        }

        internal HapbeatStreamPlayback AddSamples(float[] samples, int sampleRate, int channels,
            float baselineGain, float initialGain, string target, bool loop, double startDelaySeconds = 0)
        {
            RefreshSendAheadSeconds();
            var playback = new HapbeatStreamPlayback(
                baselineGain, initialGain, loop, OnPlaybackStopRequested);
            return AddSource(new Source(samples, sampleRate, channels, playback, target), startDelaySeconds);
        }

        private HapbeatStreamPlayback AddSource(Source source, double startDelaySeconds)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                source.StartSec = Now + Math.Max(0, startDelaySeconds);
                _sources.Add(source);
                ReconcileEndpointsLocked();
                RespliceLocked(source);
                UpdatePlaybackStatesLocked();
                StartThreadLocked();
            }
            return source.Playback;
        }

        /// <summary>
        /// Puts <paramref name="source"/> at its start on every session that already sent the blocks it falls in
        /// (sdk-multi-stream.md §6.14, see <see cref="RespliceMinQueuedSeconds"/>): the other sources' cursors go back
        /// to that block, the session ENDs its generation and BEGINs a new one, and the scheduler re-sends from there
        /// at once. Sessions that did not send that far, Stream Gen 1 sessions (no generations), or less than the
        /// minimum sent after the source's start (too short to be flushed) are left alone (the source then joins at
        /// the next unsent block). A one-shot that finished inside the re-sent blocks has already been removed: its
        /// last few ms are not sent again.
        /// </summary>
        private void RespliceLocked(Source source)
        {
            double now = Now;
            foreach (Session session in _sessions.Values)
            {
                if (!session.MatchingSources.Contains(source) || !session.BeginSent || session.EndSent ||
                    session.Mode != StreamEndpointMode.V2 || session.DataBlocksSent == 0) continue;
                if (source.StartSec >= session.DueSec - ChunkSeconds * 0.5) continue;
                if (session.DueSec - Math.Max(now, source.StartSec) < RespliceMinQueuedSeconds) continue;
                int blocks = (int)Math.Round((session.DueSec - Math.Max(now, source.StartSec)) / ChunkSeconds);
                if (blocks <= 0) continue;
                HapbeatProtocol.StreamSessionIdentity next = AllocateSessionIdentity(session.Endpoint, session.Lease);
                if (session.Lease.IsValid && !next.IsValid) continue;

                double from = session.DueSec - blocks * ChunkSeconds;
                var cursors = new List<Source>(session.Positions.Keys);
                foreach (Source other in cursors)
                {
                    int frames = other.Samples.Length / other.Channels;
                    double position;
                    if (from < other.StartSec) position = (from - other.StartSec) * other.SampleRate;
                    else
                    {
                        position = session.Positions[other] - blocks * ChunkSeconds * other.SampleRate;
                        if (position < 0) position = other.Playback.Loop && frames > 0 ? (position % frames + frames) % frames : 0;
                    }
                    session.Positions[other] = position;
                }
                // The new source enters at its start (never part way into it).
                session.Positions[source] = Math.Min(0, (from - source.StartSec) * source.SampleRate);

                ReplaceGenerationLocked(session, next);
                session.DueSec = from;
                _wakeRequested = true;
            }
        }

        /// <summary>
        /// ENDs <paramref name="session"/>'s generation and BEGINs <paramref name="next"/> on the same lease and
        /// endpoint. Source cursors, gains and the limiter stay as they are, so every source continues where it was
        /// (Sender requirements 8).
        /// </summary>
        private void ReplaceGenerationLocked(Session session, HapbeatProtocol.StreamSessionIdentity next)
        {
            _sink.End(session.Endpoint, session.Identity);
            session.PreviousIdentity = session.Identity;
            session.Identity = next;
            session.ByteOffset = 0;
            session.DataBlocksSent = 0;
            SendBegin(session);
        }

        /// <summary>
        /// Begins a new generation for an open Stream Gen 2 session that still has sources (own gap,
        /// STREAM_TIMED_OUT, a lease confirmed after STREAM_LEASE_INVALID). Returns false when the session is not
        /// open, is Stream Gen 1, or its lease has no generation left.
        /// </summary>
        private bool RestartGenerationLocked(Session session)
        {
            if (session.Mode != StreamEndpointMode.V2 || !session.BeginSent || session.EndSent ||
                session.MatchingSources.Count == 0) return false;
            HapbeatProtocol.StreamSessionIdentity next = AllocateSessionIdentity(session.Endpoint, session.Lease);
            if (session.Lease.IsValid && !next.IsValid) return false;
            ReplaceGenerationLocked(session, next);
            _wakeRequested = true;
            return true;
        }

        /// <summary>
        /// Applies an accepted stream ERROR from <paramref name="endpoint"/> (stream-session.md Sender requirements 5)
        /// to the Stream Gen 2 session there whose current or previous identity is <paramref name="identity"/>.
        /// Other endpoints are not affected. Called on the main thread.
        /// </summary>
        internal void HandleStreamError(IPEndPoint endpoint, ushort code, HapbeatProtocol.StreamSessionIdentity identity)
        {
            if (endpoint == null) return;
            lock (_lock)
            {
                if (_disposed) return;
                Session session = null;
                bool current = false;
                foreach (Session candidate in _sessions.Values)
                {
                    if (candidate.Mode != StreamEndpointMode.V2 || !candidate.Endpoint.Address.Equals(endpoint.Address))
                        continue;
                    if (SameSession(candidate.Identity, identity)) { session = candidate; current = true; break; }
                    if (SameSession(candidate.PreviousIdentity, identity)) session = candidate;
                }
                if (session == null) return;

                switch (code)
                {
                    case HapbeatProtocol.ERROR_STREAM_NOT_STARTED:
                        // Its BEGIN was lost: resend the same BEGIN now.
                        if (current && session.BeginSent && !session.EndSent) SendBegin(session);
                        break;
                    case HapbeatProtocol.ERROR_STREAM_TIMED_OUT:
                        if (current) RestartGenerationLocked(session);
                        break;
                    case HapbeatProtocol.ERROR_STREAM_STOPPED:
                        RemoveSourcesFromDeviceLocked(session);
                        break;
                    case HapbeatProtocol.ERROR_STREAM_ENDED:
                        _log($"Stream ERROR STREAM_ENDED from {endpoint} ({identity}): DATA after END (a sender bug).");
                        break;
                }
            }
        }

        /// <summary>
        /// STREAM_STOPPED (sdk-multi-stream.md §6.13): STOP_ALL ended this device's session, so every source in it
        /// leaves that device for good; its other devices keep playing. A source left with no device is finished.
        /// </summary>
        private void RemoveSourcesFromDeviceLocked(Session session)
        {
            if (session.MatchingSources.Count == 0) return;
            IPAddress device = session.Endpoint.Address;
            foreach (Source source in session.MatchingSources) source.RemovedDevices.Add(device);
            RebuildSessionMembershipLocked(Stopwatch.GetTimestamp());
            if (RemoveCompletedSourcesLocked()) ReconcileEndpointsLocked();
            UpdatePlaybackStatesLocked();
            StartThreadLocked();
        }

        /// <summary>
        /// A matched reply confirmed the lease STREAM_LEASE_INVALID questioned: the session on
        /// <paramref name="endpoint"/> begins a new generation (Sender requirements 5). Called on the main thread.
        /// </summary>
        internal void RestartSessionGeneration(IPEndPoint endpoint)
        {
            if (endpoint == null) return;
            lock (_lock)
            {
                if (_disposed) return;
                foreach (Session session in _sessions.Values)
                {
                    if (session.Endpoint.Address.Equals(endpoint.Address) && RestartGenerationLocked(session))
                        break;
                }
            }
        }

        /// <summary>
        /// Adds the effective target of every Deferred or Active source to <paramref name="targets"/>: the devices
        /// the SDK streams to or expects to stream to, which keep a stream lease.
        /// </summary>
        internal void CollectStreamTargets(List<string> targets)
        {
            lock (_lock)
            {
                for (int i = 0; i < _sources.Count; i++)
                    if (!_sources[i].Playback.IsStopped) targets.Add(_sources[i].EffectiveTarget);
            }
        }

        /// <summary>Simulates a sender that could not run for <paramref name="seconds"/> (tests).</summary>
        internal void AdvanceClockForTest(double seconds)
        {
            // Between scheduler passes, so no block is half-timed on either side of the jump.
            lock (_lock) Interlocked.Add(ref _clockOffsetTicks, (long)(seconds * Stopwatch.Frequency));
        }

        private static bool SameSession(HapbeatProtocol.StreamSessionIdentity a, HapbeatProtocol.StreamSessionIdentity b) =>
            a.Generation != 0 && a.Lease.Equals(b.Lease) && a.Generation == b.Generation;

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
                            _log("Stream sink has no Stream Gen 1 support; Gen 1 endpoints stay unresolved.");
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
                    exact.MonoOutput = wanted[key].HapticChannels == 1;
                    // A Gen 2 session repeats its BEGIN, which must carry the address the
                    // device reports now (else STREAM_TARGET_MISMATCH); the same tuple is
                    // still idempotent on the receiver.
                    if (!exact.BeginSent || exact.Mode == StreamEndpointMode.V2) exact.WireTarget = exact.Address;
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
                migration.MonoOutput = endpoint.HapticChannels == 1;
                // An unbegun session (e.g. held by the Gen 1 guard) and every repeated
                // Gen 2 BEGIN must carry the address the device reports now, or
                // firmware rejects the BEGIN.
                if (!migration.BeginSent || migration.Mode == StreamEndpointMode.V2)
                    migration.WireTarget = endpoint.Address;
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
            // change (Gen 1<->Gen 2 after a firmware update/rollback) likewise ENDs the
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
                // Gen 1 sessions carry no identity: Stream Gen 1 firmware has no lease.
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
                session.MonoOutput = endpoint.HapticChannels == 1;
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
                    // A Gen 1 session waiting out its 300 ms END->BEGIN guard counts as
                    // playing (sdk-multi-stream.md §3, §6.10).
                    bool playing = session.BeginSent ? !session.EndSent
                        : session.Mode == StreamEndpointMode.Legacy && session.Resolved;
                    if (playing && session.MatchingSources.Contains(source))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched) source.Playback.MarkActive();
                else source.Playback.MarkDeferred(_resolveDeferReason(source.EffectiveTarget));
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
            bool observedNaturalCompletion = false;
            try
            {
                while (!_stopRequested)
                {
                    long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
                    long mixStarted = Stopwatch.GetTimestamp();
                    double mixStartedSec = Now;
                    _wakeRequested = false;
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
                        // Each streaming session sends every block due before now + send-ahead (a fresh session:
                        // a burst from now; a re-spliced one: the re-mixed blocks too). An idle one stays at now.
                        double clock = Now, ahead = Math.Max(0.01f, Volatile.Read(ref _sendAheadSeconds));
                        foreach (var session in _sessions.Values)
                        {
                            if (session.BeginSent && !session.EndSent && session.MatchingSources.Count > 0)
                            {
                                // The receiver may have timed out a generation this thread could not feed.
                                if (session.Mode == StreamEndpointMode.V2 && session.DataBlocksSent > 0 &&
                                    clock - session.LastDataSec >= OwnGapSeconds)
                                    RestartGenerationLocked(session);
                                if (session.DueSec < clock - ChunkSeconds) session.DueSec = clock;
                                for (int b = 0; b < MaxBlocksPerPass && session.DueSec < clock + ahead; b++)
                                {
                                    MixAndSendSessionLocked(session, frames, mix, pcm);
                                    session.DueSec += ChunkSeconds;
                                }
                            }
                            else if (session.DueSec < clock) session.DueSec = clock;
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

                    // The next block is due one chunk later; a re-splice wakes the loop early.
                    SleepPrecisely(ChunkSeconds - (Now - mixStartedSec));
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
            session.LastDataSec = Now;
        }

        private static void MixSource(Source source, Session session, int frames, float[] mix)
        {
            // First block on this session: where the source's start falls in it (negative = later in the block or
            // after it); one that is already past (a deferred source, an endpoint joining late) starts at frame 0.
            if (!session.Positions.TryGetValue(source, out double position))
                position = Math.Min(0, (session.DueSec - source.StartSec) * source.SampleRate);
            double step = source.SampleRate / (double)OutputSampleRate;
            float gain = source.Playback.Gain;
            // One motor (§6.15): no pan, and both channels carry the source's mono mix,
            // which is what the receiver's (L+R)/2 plays anyway.
            bool mono = session.MonoOutput;
            source.Playback.GetStereoChannelGains(out float panL, out float panR);
            float gainL = mono ? gain : gain * panL;
            float gainR = mono ? gain : gain * panR;
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
                if (position < 0)
                {
                    // Not started yet (a start delay, or a source placed mid-block).
                    position += step;
                    continue;
                }
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
                if (mono && source.Channels != 1) left = right = (left + right) * 0.5f;
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
                if (source.Playback.IsStopped) continue;
                bool loop = source.Playback.Loop;
                if (loop && source.RemovedDevices.Count == 0) continue;
                bool matchedAnySession = false;
                bool completedEverywhere = true;
                foreach (var session in _sessions.Values)
                {
                    if (!session.MatchingSources.Contains(source)) continue;
                    matchedAnySession = true;
                    if (loop || !session.Positions.TryGetValue(source, out double position) ||
                        position < source.Samples.Length / source.Channels)
                    {
                        completedEverywhere = false;
                        break;
                    }
                }
                // A deferred one-shot has no matching endpoint yet. Keep it in the
                // registry so a later PONG can start it; unrelated endpoint sessions
                // must not make it look naturally complete. A device that STOP_ALL
                // removed it from counts as played; a loop removed from every device
                // it played on is stopped (§6.13).
                if (matchedAnySession ? completedEverywhere : source.RemovedDevices.Count > 0)
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
        /// Whether the identical Gen 2 BEGIN is re-sent before DATA block
        /// <paramref name="dataBlocksSent"/> (0-based). Block 0 follows the original
        /// BEGIN; blocks 1..<see cref="BeginRepeatInitialBlocks"/>-1 and every
        /// <see cref="BeginRepeatIntervalBlocks"/>-th block are preceded by a repeat.
        /// </summary>
        internal static bool ShouldRepeatBegin(int dataBlocksSent) =>
            dataBlocksSent > 0 &&
            (dataBlocksSent < BeginRepeatInitialBlocks || dataBlocksSent % BeginRepeatIntervalBlocks == 0);

        // Keyed by device endpoint, not session key: an address change on the same
        // device must not skip the guard, since Stream Gen 1 firmware ignores END payloads.
        private bool LegacyCooldownElapsedLocked(IPEndPoint endpoint, long now)
        {
            if (!_lastLegacyEndTicksByEndpoint.TryGetValue(endpoint.ToString(), out long endedAt)) return true;
            return (now - endedAt) / (double)Stopwatch.Frequency >= LegacyEndToBeginCooldownSeconds;
        }

        private static bool SessionMatchesSource(Session session, Source source)
        {
            return HapbeatClient.AddressMatches(source.EffectiveTarget, session.Address) &&
                (source.RemovedDevices.Count == 0 || !source.RemovedDevices.Contains(session.Endpoint.Address));
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
                // sessions, so Gen 2 endpoints never inherit it.
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
            while (!_stopRequested && !_wakeRequested)
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
