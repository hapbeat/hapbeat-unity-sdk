using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Mixer behavior for endpoints classified Legacy (stream-session-v2.md "Legacy
    /// receiver fallback"): pre-v2 packets, a 300 ms END->BEGIN guard for that endpoint
    /// only, and an immediate old-format END on a class change. Recording sinks only.
    /// </summary>
    public sealed class StreamLegacyMixerTests
    {
        private const string LeftAddress = "player_1/pos_l_arm/group_1";
        private const string RightAddress = "player_1/pos_r_arm/group_1";

        private enum Kind { Begin, Data, End, LegacyBegin, LegacyData, LegacyEnd }

        private readonly struct Packet
        {
            public readonly Kind Kind;
            public readonly string Endpoint;
            public readonly long Ticks;
            public readonly HapbeatProtocol.StreamSessionIdentity Identity;

            public Packet(Kind kind, IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            {
                Kind = kind;
                Endpoint = endpoint.ToString();
                Ticks = Stopwatch.GetTimestamp();
                Identity = identity;
            }
        }

        private class V2OnlySink : IHapbeatEndpointStreamPacketSink
        {
            protected readonly object Gate = new object();
            protected readonly List<Packet> Packets = new List<Packet>();

            protected void Record(Kind kind, IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity id = default)
            {
                lock (Gate) Packets.Add(new Packet(kind, ep, id));
            }

            public void Begin(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity,
                ushort rate, byte channels, byte format, uint samples, float gain, string target) =>
                Record(Kind.Begin, ep, identity);
            public void Data(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity,
                uint offset, byte[] pcm, int start, int length) => Record(Kind.Data, ep, identity);
            public void End(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity) =>
                Record(Kind.End, ep, identity);

            public List<Packet> Snapshot()
            {
                lock (Gate) return new List<Packet>(Packets);
            }

            public int Count(Kind kind, string endpoint)
            {
                lock (Gate)
                {
                    int count = 0;
                    foreach (var p in Packets) if (p.Kind == kind && p.Endpoint == endpoint) count++;
                    return count;
                }
            }

            public long TicksOf(Kind kind, string endpoint, int occurrence)
            {
                lock (Gate)
                {
                    int seen = 0;
                    foreach (var p in Packets)
                        if (p.Kind == kind && p.Endpoint == endpoint && seen++ == occurrence) return p.Ticks;
                    return -1;
                }
            }

            public int IndexOf(Kind kind, string endpoint, int occurrence)
            {
                lock (Gate)
                {
                    int seen = 0;
                    for (int i = 0; i < Packets.Count; i++)
                        if (Packets[i].Kind == kind && Packets[i].Endpoint == endpoint && seen++ == occurrence) return i;
                    return -1;
                }
            }
        }

        private sealed class DualSink : V2OnlySink, IHapbeatLegacyEndpointStreamPacketSink
        {
            public void LegacyBegin(IPEndPoint ep, ushort rate, byte channels, byte format,
                uint samples, float gain, string target) => Record(Kind.LegacyBegin, ep);
            public void LegacyData(IPEndPoint ep, uint offset, byte[] pcm, int start, int length) =>
                Record(Kind.LegacyData, ep);
            public void LegacyEnd(IPEndPoint ep) => Record(Kind.LegacyEnd, ep);
        }

        private static readonly IPEndPoint LeftEp = new IPEndPoint(IPAddress.Parse("192.0.2.50"), 7700);
        private static readonly IPEndPoint RightEp = new IPEndPoint(IPAddress.Parse("192.0.2.51"), 7700);
        private static readonly string Left = LeftEp.ToString();
        private static readonly string Right = RightEp.ToString();

        private static HapbeatClient.StreamEndpoint LegacyLeft() =>
            new HapbeatClient.StreamEndpoint(LeftEp, LeftAddress, default, StreamEndpointMode.Legacy);

        [Test]
        public void LegacyEndpoint_UsesOnlyLegacyPackets()
        {
            var sink = new DualSink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { LegacyLeft() }, () => 0.01f, _ => { });
            var playback = mixer.AddSamples(new float[1600], 16000, 1, 1f, 0f, "player_1", true);
            WaitFor(() => sink.Count(Kind.LegacyData, Left) > 1);
            playback.Stop();
            WaitFor(() => sink.Count(Kind.LegacyEnd, Left) == 1);

            Assert.AreEqual(1, sink.Count(Kind.LegacyBegin, Left));
            Assert.AreEqual(0, sink.Count(Kind.Begin, Left) + sink.Count(Kind.Data, Left) + sink.Count(Kind.End, Left),
                "a legacy endpoint must never receive v2 packets");
        }

        [Test]
        public void LegacyGuard_DelaysOnlyTheLegacyEndpoint_WhileV2RestartsImmediately()
        {
            var sink = new DualSink();
            // The right endpoint is a lease-less V2 mock (zero cooldown).
            var endpoints = new List<HapbeatClient.StreamEndpoint>
            {
                LegacyLeft(),
                new HapbeatClient.StreamEndpoint(RightEp, RightAddress),
            };
            using var mixer = new HapbeatEndpointStreamMixer(sink, _ => endpoints, () => 0.01f, _ => { });
            var first = mixer.AddSamples(new float[160], 16000, 1, 1f, 0f, "player_1", true);
            WaitFor(() => sink.Count(Kind.LegacyBegin, Left) == 1 && sink.Count(Kind.Begin, Right) == 1);
            first.Stop();
            WaitFor(() => sink.Count(Kind.LegacyEnd, Left) == 1 && sink.Count(Kind.End, Right) == 1);

            long requested = Stopwatch.GetTimestamp();
            var second = mixer.AddSamples(new float[160], 16000, 1, 1f, 0f, "player_1", true);
            WaitFor(() => sink.Count(Kind.Begin, Right) == 2);
            double v2RestartMs = Ms(sink.TicksOf(Kind.Begin, Right, 1) - requested);
            Assert.Less(v2RestartMs, 100, "a v2 endpoint must not inherit the legacy guard");
            Assert.AreEqual(1, sink.Count(Kind.LegacyBegin, Left),
                "the legacy endpoint stays deferred during its guard");

            WaitFor(() => sink.Count(Kind.LegacyBegin, Left) == 2);
            double legacyGapMs = Ms(sink.TicksOf(Kind.LegacyBegin, Left, 1) - sink.TicksOf(Kind.LegacyEnd, Left, 0));
            TestContext.WriteLine($"v2 restart {v2RestartMs:F1} ms, legacy END->BEGIN {legacyGapMs:F1} ms (recording sink)");
            Assert.GreaterOrEqual(legacyGapMs, 299, "legacy END->BEGIN must wait at least 300 ms");
            second.Stop();
        }

        [Test]
        public void ClassChange_EndsOldSessionImmediatelyInOldFormat()
        {
            var sink = new DualSink();
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x0A0B, 3);
            var current = new List<HapbeatClient.StreamEndpoint> { LegacyLeft() };
            using var mixer = new HapbeatEndpointStreamMixer(sink, _ => current, () => 0.01f, _ => { });
            var playback = mixer.AddSamples(new float[1600], 16000, 1, 1f, 0f, "player_1", true);
            WaitFor(() => sink.Count(Kind.LegacyData, Left) > 0);

            // Firmware update: legacy -> v2. END must be legacy and precede the v2 BEGIN,
            // within the same reconcile (no 300 ms idle linger, no cooldown for v2).
            current = new List<HapbeatClient.StreamEndpoint>
            {
                new HapbeatClient.StreamEndpoint(LeftEp, LeftAddress, lease),
            };
            long flipped = Stopwatch.GetTimestamp();
            mixer.ReconcileEndpoints();
            Assert.AreEqual(1, sink.Count(Kind.LegacyEnd, Left), "old legacy session ENDs immediately");
            Assert.AreEqual(1, sink.Count(Kind.Begin, Left), "the v2 session begins without a guard");
            Assert.Less(sink.IndexOf(Kind.LegacyEnd, Left, 0), sink.IndexOf(Kind.Begin, Left, 0));
            Assert.AreEqual(0, sink.Count(Kind.End, Left), "the old END is not v2-formatted");
            Assert.Less(Ms(sink.TicksOf(Kind.LegacyEnd, Left, 0) - flipped), 100);
            WaitFor(() => sink.Count(Kind.Data, Left) > 0);

            // Rollback: v2 -> legacy. END must be v2 (with the session identity) and
            // immediate; the next legacy BEGIN honors the guard of the last legacy END.
            current = new List<HapbeatClient.StreamEndpoint> { LegacyLeft() };
            flipped = Stopwatch.GetTimestamp();
            mixer.ReconcileEndpoints();
            Assert.AreEqual(1, sink.Count(Kind.End, Left), "old v2 session ENDs immediately");
            Assert.Less(Ms(sink.TicksOf(Kind.End, Left, 0) - flipped), 100);
            var snapshot = sink.Snapshot();
            var v2End = snapshot[sink.IndexOf(Kind.End, Left, 0)];
            Assert.AreEqual(lease, v2End.Identity.Lease, "v2 END keeps the identity it began under");

            WaitFor(() => sink.Count(Kind.LegacyBegin, Left) == 2);
            Assert.Less(sink.IndexOf(Kind.End, Left, 0), sink.IndexOf(Kind.LegacyBegin, Left, 1));
            Assert.GreaterOrEqual(
                Ms(sink.TicksOf(Kind.LegacyBegin, Left, 1) - sink.TicksOf(Kind.LegacyEnd, Left, 0)), 299);
            WaitFor(() => sink.Count(Kind.LegacyData, Left) > 1);
            playback.Stop();
        }

        [Test]
        public void SinkWithoutLegacySupport_LeavesLegacyEndpointUnresolved()
        {
            var sink = new V2OnlySink();
            var logs = new List<string>();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { LegacyLeft() }, () => 0.01f,
                message => { lock (logs) logs.Add(message); });
            var playback = mixer.AddSamples(new float[160], 16000, 1, 1f, 0f, "player_1", true);
            mixer.ReconcileEndpoints();
            Thread.Sleep(30);

            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, playback.Status);
            Assert.IsEmpty(sink.Snapshot());
            lock (logs) Assert.AreEqual(1, logs.Count, "unsupported legacy sink is logged once");
            playback.Stop();
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void WaitFor(System.Func<bool> condition)
        {
            Assert.That(SpinWait.SpinUntil(condition, 2000), "timed out waiting for mixer packets");
        }
    }
}
