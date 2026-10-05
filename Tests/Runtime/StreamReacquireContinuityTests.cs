using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Lease renewal on the same device (focus regain / ReacquireStreamOwnership) changes
    /// only the session identity: sources continue from their cursors instead of frame 0.
    /// Recording sink only; no socket is opened and nothing is sent.
    /// </summary>
    public sealed class StreamReacquireContinuityTests
    {
        private const string Address = "player_1/pos_l_arm/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
        private static readonly HapbeatProtocol.StreamLeaseIdentity FirstLease =
            new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7);
        private static readonly HapbeatProtocol.StreamLeaseIdentity RenewedLease =
            new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 8);

        private sealed class Sink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            private readonly List<(HapbeatProtocol.StreamSessionIdentity identity, byte[] pcm, long ticks)> _data =
                new List<(HapbeatProtocol.StreamSessionIdentity, byte[], long)>();
            private readonly List<HapbeatProtocol.StreamSessionIdentity> _begins =
                new List<HapbeatProtocol.StreamSessionIdentity>();
            private readonly List<HapbeatProtocol.StreamSessionIdentity> _ends =
                new List<HapbeatProtocol.StreamSessionIdentity>();

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target)
            {
                lock (_lock) _begins.Add(identity);
            }

            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
            {
                var copy = new byte[dataLength];
                Buffer.BlockCopy(audioData, dataOffset, copy, 0, dataLength);
                lock (_lock) _data.Add((identity, copy, Stopwatch.GetTimestamp()));
            }

            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            {
                lock (_lock) _ends.Add(identity);
            }

            public int CountData(HapbeatProtocol.StreamLeaseIdentity lease)
            {
                lock (_lock) return _data.FindAll(x => x.identity.Lease.Equals(lease)).Count;
            }

            public int CountEnds(HapbeatProtocol.StreamLeaseIdentity lease)
            {
                lock (_lock) return _ends.FindAll(x => x.Lease.Equals(lease)).Count;
            }

            public (byte[] pcm, long ticks) LastData(HapbeatProtocol.StreamLeaseIdentity lease)
            {
                lock (_lock)
                {
                    var last = _data.FindLast(x => x.identity.Lease.Equals(lease));
                    return (last.pcm, last.ticks);
                }
            }

            public (byte[] pcm, long ticks) FirstData(HapbeatProtocol.StreamLeaseIdentity lease)
            {
                lock (_lock)
                {
                    var first = _data.Find(x => x.identity.Lease.Equals(lease));
                    return (first.pcm, first.ticks);
                }
            }
        }

        // gapMilliseconds 0: the renewed PONG lands within the 300 ms empty-session linger
        // (the retired session hands its cursors over directly). 450: it lands after the
        // unresolved session already ended, so the cursors come from the parked state.
        [TestCase(true, 0, TestName = "Reacquire_LoopContinuesFromCursor_WithinLinger")]
        [TestCase(true, 450, TestName = "Reacquire_LoopContinuesFromCursor_AfterLinger")]
        [TestCase(false, 0, TestName = "Reacquire_OneShotContinuesFromCursor_WithinLinger")]
        [TestCase(false, 450, TestName = "Reacquire_OneShotContinuesFromCursor_AfterLinger")]
        public void LeaseRenewalOnSameDevice_ContinuesSourcesWithoutRestart(bool loop, int gapMilliseconds)
        {
            var sink = new Sink();
            HapbeatClient.StreamEndpoint? current = new HapbeatClient.StreamEndpoint(Device, Address, FirstLease);
            using var mixer = new HapbeatEndpointStreamMixer(sink, _ =>
                current.HasValue
                    ? new List<HapbeatClient.StreamEndpoint> { current.Value }
                    : new List<HapbeatClient.StreamEndpoint>(),
                () => 0.01f, _ => { });
            HapbeatStreamPlayback playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, "*/pos_l_arm", loop);
            WaitFor(() => sink.CountData(FirstLease) > 5);

            // ReacquireStreamOwnership: the client drops its leases, the mixer reconciles.
            current = null;
            mixer.ReconcileEndpoints();
            int frozenData = sink.CountData(FirstLease);
            if (gapMilliseconds > 0)
            {
                Thread.Sleep(gapMilliseconds);
                Assert.AreEqual(1, sink.CountEnds(FirstLease), "the unresolved session ends after the linger");
            }
            Assert.AreEqual(frozenData, sink.CountData(FirstLease), "no DATA under the relinquished lease");
            Assert.AreNotEqual(HapbeatStreamPlaybackStatus.Stopped, playback.Status);

            // The PONG for the fresh incarnation reports a new ticket on the same device.
            long renewedAt = Stopwatch.GetTimestamp();
            current = new HapbeatClient.StreamEndpoint(Device, Address, RenewedLease);
            mixer.ReconcileEndpoints();
            WaitFor(() => sink.CountData(RenewedLease) > 0);

            short lastOld = ReadPcm16(sink.LastData(FirstLease).pcm, LastFrame(sink.LastData(FirstLease).pcm), 0);
            var firstNew = sink.FirstData(RenewedLease);
            short firstNewSample = ReadPcm16(firstNew.pcm, 0, 0);
            double resumeMs = (firstNew.ticks - renewedAt) * 1000.0 / Stopwatch.Frequency;
            TestContext.WriteLine($"last old sample {lastOld}, first renewed sample {firstNewSample}, " +
                                  $"renewal->DATA {resumeMs:F1} ms");
            Assert.AreEqual(1, sink.CountEnds(FirstLease), "the retired identity ends exactly once");
            Assert.Greater(firstNewSample, lastOld, "the renewed session must not restart the ramp at frame 0");
            Assert.LessOrEqual(firstNewSample - lastOld, 3, "the renewed session resumes at the next frame");
            Assert.Less(resumeMs, 100.0, "DATA resumes as soon as the renewed lease resolves");
        }

        private static float[] Ramp()
        {
            // 1 s, strictly increasing: one PCM16 step (~1 LSB) per frame.
            var samples = new float[16000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * (i + 1) / samples.Length;
            return samples;
        }

        private static int LastFrame(byte[] pcm) => pcm.Length / 4 - 1;

        private static short ReadPcm16(byte[] pcm, int frame, int channel)
        {
            int offset = (frame * 2 + channel) * 2;
            return (short)(pcm[offset] | (pcm[offset + 1] << 8));
        }

        private static void WaitFor(Func<bool> predicate)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, 1000), "Timed out waiting for recording mixer packets.");
        }
    }
}
