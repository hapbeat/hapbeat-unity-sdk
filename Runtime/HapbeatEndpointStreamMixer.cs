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
        void Begin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target);
        void Data(IPEndPoint endpoint, uint byteOffset, byte[] audioData, int dataOffset, int dataLength);
        void End(IPEndPoint endpoint);
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

        private sealed class Source
        {
            public readonly float[] Samples;
            public readonly int SampleRate;
            public readonly int Channels;
            public readonly bool Loop;
            public readonly string Target;
            public readonly HapbeatStreamPlayback Playback;
            public readonly HashSet<string> ExcludedEndpointKeys = new HashSet<string>();

            public Source(AudioClip clip, HapbeatStreamPlayback playback, bool loop, string target)
            {
                Samples = new float[clip.samples * clip.channels];
                clip.GetData(Samples, 0); // Unity API: called by Add on the main thread.
                SampleRate = clip.frequency;
                Channels = clip.channels;
                Loop = loop;
                Target = target;
                Playback = playback;
            }

            public Source(float[] samples, int sampleRate, int channels,
                HapbeatStreamPlayback playback, bool loop, string target)
            {
                Samples = samples ?? throw new ArgumentNullException(nameof(samples));
                SampleRate = sampleRate;
                Channels = channels;
                Loop = loop;
                Target = target;
                Playback = playback;
            }
        }

        private sealed class Session
        {
            public readonly IPEndPoint Endpoint;
            public readonly string Key;
            public readonly string Address;
            public readonly string WireTarget;
            public readonly Dictionary<Source, double> Positions = new Dictionary<Source, double>();
            public readonly HashSet<Source> MatchingSources = new HashSet<Source>();
            public uint ByteOffset;
            public bool EndSent;

            public Session(IPEndPoint endpoint, string key, string address, string wireTarget)
            {
                Endpoint = endpoint;
                Key = key;
                Address = address;
                WireTarget = wireTarget;
            }
        }

        private readonly object _lock = new object();
        private sealed class ClientPacketSink : IHapbeatEndpointStreamPacketSink
        {
            private readonly Func<HapbeatClient> _getClient;
            public ClientPacketSink(Func<HapbeatClient> getClient) { _getClient = getClient; }
            public void Begin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target) =>
                _getClient()?.SendStreamBeginTo(endpoint, sampleRate, channels, format, totalSamples, gain, target);
            public void Data(IPEndPoint endpoint, uint byteOffset, byte[] audioData, int dataOffset, int dataLength) =>
                _getClient()?.SendStreamDataTo(endpoint, byteOffset, audioData, dataOffset, dataLength);
            public void End(IPEndPoint endpoint) => _getClient()?.SendStreamEndTo(endpoint);
        }

        private readonly IHapbeatEndpointStreamPacketSink _sink;
        private readonly Func<string, List<HapbeatClient.StreamEndpoint>> _resolveEndpoints;
        private readonly Func<float> _getSendAheadSeconds;
        private readonly Action<string> _log;
        private readonly Action _beforeNaturalFinalize;
        private readonly Action _beforeStopFinalize;
        private readonly List<Source> _sources = new List<Source>();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
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
            Func<float> getSendAheadSeconds, Action<string> log)
        {
            _sink = new ClientPacketSink(getClient);
            _resolveEndpoints = resolveEndpoints;
            _getSendAheadSeconds = getSendAheadSeconds;
            _sendAheadSeconds = Math.Max(0.01f, getSendAheadSeconds());
            _log = log;
            _beforeNaturalFinalize = null;
            _beforeStopFinalize = null;
        }

        internal HapbeatEndpointStreamMixer(IHapbeatEndpointStreamPacketSink sink,
            Func<string, List<HapbeatClient.StreamEndpoint>> resolveEndpoints,
            Func<float> getSendAheadSeconds, Action<string> log,
            Action beforeNaturalFinalize = null, Action beforeStopFinalize = null)
        {
            _sink = sink;
            _resolveEndpoints = resolveEndpoints;
            _getSendAheadSeconds = getSendAheadSeconds;
            _sendAheadSeconds = Math.Max(0.01f, getSendAheadSeconds());
            _log = log;
            _beforeNaturalFinalize = beforeNaturalFinalize;
            _beforeStopFinalize = beforeStopFinalize;
        }

        public bool IsStreaming
        {
            get { lock (_lock) return _sessions.Count > 0; }
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
            string resolvedTarget, bool loop)
        {
            if (clip == null) return null;
            RefreshSendAheadSeconds();
            var playback = new HapbeatStreamPlayback(
                baselineGain, initialGain, OnPlaybackStopRequested);
            var source = new Source(clip, playback, loop, resolvedTarget);
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
                baselineGain, initialGain, OnPlaybackStopRequested);
            var source = new Source(samples, sampleRate, channels, playback, loop, target);
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

        public void StopAll(bool flush = false)
        {
            Thread thread;
            List<Session> terminationSessions;
            lock (_lock)
            {
                for (int i = 0; i < _sources.Count; i++) _sources[i].Playback.MarkStopped();
                _sources.Clear();
                terminationSessions = new List<Session>(_sessions.Values);
                // The caller owns termination packets for an explicit StopAll.
                // This remains true even if the bounded join times out, preventing
                // the old scheduler from sending a delayed END into a reconnected
                // client/session later.
                _suppressSchedulerTerminationPackets = true;
                _stopRequested = true;
                thread = _thread;
            }
            bool joined = thread == null || thread.Join(500);
            SendTerminationPackets(terminationSessions, flush);
            if (!joined)
            {
                _log("Stream mixer did not exit within 500ms.");
                return;
            }
            lock (_lock)
            {
                _sessions.Clear();
                _thread = null;
                _stopRequested = false;
                _suppressSchedulerTerminationPackets = false;
            }
        }

        public void StopTarget(string target, bool flush)
        {
            lock (_lock)
            {
                HashSet<Source> affectedSources = null;
                foreach (Session session in _sessions.Values)
                {
                    if (!HapbeatClient.AddressMatches(target, session.Address)) continue;
                    if (flush)
                    {
                        _sink.Begin(session.Endpoint, OutputSampleRate, OutputChannels,
                            HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, session.WireTarget);
                    }
                    foreach (Source source in session.MatchingSources)
                    {
                        source.ExcludedEndpointKeys.Add(session.Key);
                        if (affectedSources == null) affectedSources = new HashSet<Source>();
                        affectedSources.Add(source);
                    }
                }

                // An exact-target Deferred source has no session from which to infer
                // membership, but callers still expect the target-scoped stop to
                // retire it rather than let it revive on a later PONG.
                for (int i = 0; i < _sources.Count; i++)
                {
                    Source source = _sources[i];
                    bool exactTarget = string.Equals(target ?? string.Empty,
                        source.Target ?? string.Empty, StringComparison.Ordinal);
                    bool concreteDeferredMatch = !SourceHasSessionLocked(source) &&
                        (source.Target ?? string.Empty).IndexOf('*') < 0 &&
                        HapbeatClient.AddressMatches(target, source.Target);
                    if (!exactTarget && !concreteDeferredMatch) continue;
                    if (affectedSources == null) affectedSources = new HashSet<Source>();
                    affectedSources.Add(source);
                }

                ReconcileEndpointsLocked();
                bool removed = false;
                if (affectedSources != null)
                {
                    for (int i = _sources.Count - 1; i >= 0; i--)
                    {
                        Source source = _sources[i];
                        if (!affectedSources.Contains(source) || SourceHasSessionLocked(source)) continue;
                        source.Playback.MarkStopped();
                        RemoveSourceLocked(source, i);
                        removed = true;
                    }
                }
                if (removed) ReconcileEndpointsLocked();
                UpdatePlaybackStatesLocked();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            StopAll();
            _disposed = true;
        }

        private void ReconcileEndpointsLocked()
        {
            var wanted = new Dictionary<string, HapbeatClient.StreamEndpoint>();
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                if (source.Playback.IsStopped) continue;
                List<HapbeatClient.StreamEndpoint> endpoints = _resolveEndpoints(source.Target);
                if (endpoints == null) continue;
                for (int e = 0; e < endpoints.Count; e++)
                {
                    var endpoint = endpoints[e];
                    string key = endpoint.EndPoint.ToString();
                    if (source.ExcludedEndpointKeys.Contains(key)) continue;
                    wanted[key] = endpoint;
                }
            }

            var remove = new List<string>();
            foreach (var pair in _sessions)
                if (!wanted.ContainsKey(pair.Key)) remove.Add(pair.Key);
            for (int i = 0; i < remove.Count; i++)
            {
                Session session = _sessions[remove[i]];
                EndSessionLocked(session);
                _sessions.Remove(remove[i]);
            }

            foreach (var pair in wanted)
            {
                if (_sessions.TryGetValue(pair.Key, out Session existing))
                {
                    if (existing.Address == pair.Value.Address) continue;
                    EndSessionLocked(existing);
                    _sessions.Remove(pair.Key);
                }
                // STREAM_DATA has no target. Even though this is an explicit direct
                // endpoint, BEGIN must carry the PONG-resolved address so firmware
                // rejects it if that IP was reassigned before the next PONG refresh.
                var session = new Session(pair.Value.EndPoint, pair.Key, pair.Value.Address,
                    pair.Value.Address);
                _sessions.Add(pair.Key, session);
                _sink.Begin(session.Endpoint, OutputSampleRate, OutputChannels,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, session.WireTarget);
            }

            RebuildSessionMembershipLocked();
        }

        private void UpdatePlaybackStatesLocked()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                Source source = _sources[i];
                bool matched = false;
                foreach (var session in _sessions.Values)
                {
                    if (session.MatchingSources.Contains(source))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched) source.Playback.MarkActive();
                else source.Playback.MarkDeferred(HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint);
            }
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
                        hasSources = _sources.Count > 0;
                        if (!hasSources || _sessions.Count == 0)
                        {
                            observedNaturalCompletion = !hasSources;
                            break;
                        }
                        foreach (var session in _sessions.Values)
                            MixAndSendSessionLocked(session, frames, mix, pcm);
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
                    // The exiting thread owns the forced-stop END as well: this
                    // remains safe when StopAll's bounded join times out.
                    if (_stopRequested)
                    {
                        EndAllSessionsLocked();
                        _sessions.Clear();
                        _thread = null;
                        if (_sources.Count > 0)
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
                        EndAllSessionsLocked();
                        _sessions.Clear();
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

            for (int i = 0; i < mix.Length; i++)
            {
                int value = (int)(mix[i] * 32767f);
                if (value > short.MaxValue) value = short.MaxValue;
                else if (value < short.MinValue) value = short.MinValue;
                pcm[i * 2] = (byte)value;
                pcm[i * 2 + 1] = (byte)(value >> 8);
            }
            _sink.Data(session.Endpoint, session.ByteOffset, pcm, 0, pcm.Length);
            Interlocked.Add(ref _sentPcmBytes, pcm.Length);
            session.ByteOffset += (uint)pcm.Length;
        }

        private static void MixSource(Source source, Session session, int frames, float[] mix)
        {
            if (!session.Positions.TryGetValue(source, out double position)) position = 0;
            double step = source.SampleRate / (double)OutputSampleRate;
            float gain = source.Playback.Gain;
            source.Playback.GetStereoChannelGains(out float gainL, out float gainR);
            int sourceFrames = source.Samples.Length / source.Channels;
            for (int frame = 0; frame < frames; frame++)
            {
                if (position >= sourceFrames)
                {
                    if (!source.Loop) break;
                    position %= sourceFrames;
                }
                int index = (int)position;
                int next = Math.Min(index + 1, sourceFrames - 1);
                float fraction = (float)(position - index);
                float left = LerpSample(source, index, next, 0, fraction);
                float right = source.Channels == 1 ? left : LerpSample(source, index, next, 1, fraction);
                int output = frame * OutputChannels;
                mix[output] += left * gain * gainL;
                mix[output + 1] += right * gain * gainR;
                position += step;
            }
            session.Positions[source] = position;
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
                if (source.Loop || source.Playback.IsStopped) continue;
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
                session.MatchingSources.Remove(source);
            }
        }

        private void PruneSessionsWithoutSourcesLocked()
        {
            List<string> remove = null;
            foreach (var pair in _sessions)
            {
                if (pair.Value.MatchingSources.Count > 0) continue;
                if (remove == null) remove = new List<string>();
                remove.Add(pair.Key);
            }

            if (remove == null) return;
            for (int i = 0; i < remove.Count; i++)
            {
                Session session = _sessions[remove[i]];
                EndSessionLocked(session);
                _sessions.Remove(remove[i]);
            }
        }

        private void RebuildSessionMembershipLocked()
        {
            foreach (Session session in _sessions.Values)
            {
                session.MatchingSources.Clear();
                for (int i = 0; i < _sources.Count; i++)
                {
                    Source source = _sources[i];
                    if (!source.Playback.IsStopped && SessionMatchesSource(session, source))
                        session.MatchingSources.Add(source);
                }

                List<Source> stalePositions = null;
                foreach (Source source in session.Positions.Keys)
                {
                    if (session.MatchingSources.Contains(source)) continue;
                    if (stalePositions == null) stalePositions = new List<Source>();
                    stalePositions.Add(source);
                }
                if (stalePositions == null) continue;
                for (int i = 0; i < stalePositions.Count; i++)
                    session.Positions.Remove(stalePositions[i]);
            }

            PruneSessionsWithoutSourcesLocked();
        }

        private static bool SessionMatchesSource(Session session, Source source)
        {
            if (source.ExcludedEndpointKeys.Contains(session.Key)) return false;
            return HapbeatClient.AddressMatches(source.Target, session.Address);
        }

        private bool SourceHasSessionLocked(Source source)
        {
            foreach (Session session in _sessions.Values)
                if (session.MatchingSources.Contains(source)) return true;
            return false;
        }

        private void EndAllSessionsLocked()
        {
            foreach (var session in _sessions.Values) EndSessionLocked(session);
        }

        private void EndSessionLocked(Session session)
        {
            if (session.EndSent) return;
            session.EndSent = true;
            if (!_suppressSchedulerTerminationPackets)
                _sink.End(session.Endpoint);
        }

        private void SendTerminationPackets(List<Session> sessions, bool flush)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                Session session = sessions[i];
                if (flush)
                {
                    _sink.Begin(session.Endpoint, OutputSampleRate, OutputChannels,
                        HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, session.WireTarget);
                }
                _sink.End(session.Endpoint);
            }
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
