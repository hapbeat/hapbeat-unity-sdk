using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// stream-session.md Sender requirements (1.x) and sdk-multi-stream.md §3 / §6.15 on the
    /// endpoint mixer: a new generation after the sender's own 3 s gap, a ticket change that
    /// continues a finite source from its cursor, repeated BEGINs carrying the device's
    /// current address, deferredReason, and no pan on a one-motor endpoint. Recording sink
    /// only; no socket is opened and nothing is sent. (Unity has no live push source, so
    /// "silence for idle live sources" has no Unity case.)
    /// </summary>
    public sealed class StreamGen2SenderTests
    {
        private const string Address = "player_1/pos_chest/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.110"), 7700);
        private static readonly HapbeatProtocol.StreamLeaseIdentity FirstLease =
            new HapbeatProtocol.StreamLeaseIdentity(0x5E1D, 3);
        private static readonly HapbeatProtocol.StreamLeaseIdentity NextTicket =
            new HapbeatProtocol.StreamLeaseIdentity(0x5E1D, 4);

        private enum Kind { Begin, Data, End }

        private readonly struct Packet
        {
            public readonly Kind Kind;
            public readonly HapbeatProtocol.StreamSessionIdentity Identity;
            public readonly byte[] Pcm;
            public readonly string Target;

            public Packet(Kind kind, HapbeatProtocol.StreamSessionIdentity identity, byte[] pcm = null, string target = null)
            {
                Kind = kind;
                Identity = identity;
                Pcm = pcm;
                Target = target;
            }
        }

        private sealed class Sink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            private readonly List<Packet> _packets = new List<Packet>();

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target)
            {
                lock (_lock) _packets.Add(new Packet(Kind.Begin, identity, null, target));
            }
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
            {
                var copy = new byte[dataLength];
                Buffer.BlockCopy(audioData, dataOffset, copy, 0, dataLength);
                lock (_lock) _packets.Add(new Packet(Kind.Data, identity, copy));
            }
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            {
                lock (_lock) _packets.Add(new Packet(Kind.End, identity));
            }

            public List<Packet> Snapshot()
            {
                lock (_lock) return new List<Packet>(_packets);
            }

            public int Count(Kind kind)
            {
                lock (_lock) return _packets.FindAll(p => p.Kind == kind).Count;
            }
        }

        [Test]
        public void OwnGapOfThreeSeconds_EndsAndBeginsANewGenerationBeforeMoreData_KeepingTheCursor()
        {
            var sink = new Sink();
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, Address, FirstLease));
            var playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, Address, false);
            WaitFor(() => sink.Count(Kind.Data) > 10);
            Assert.AreEqual(0, sink.Count(Kind.End));

            mixer.AdvanceClockForTest(HapbeatEndpointStreamMixer.OwnGapSeconds + 0.5);
            WaitFor(() =>
            {
                List<Packet> now = sink.Snapshot();
                int ended = IndexAfter(now, 0, Kind.End);
                return ended >= 0 && IndexAfter(now, ended, Kind.Data) >= 0;
            });

            List<Packet> after = sink.Snapshot();
            int end = IndexAfter(after, 0, Kind.End);
            Packet lastBefore = after.FindLast(p => p.Kind == Kind.Data && after.IndexOf(p) < end);
            Assert.AreEqual(lastBefore.Identity.Generation, after[end].Identity.Generation,
                "the gapped generation is ENDed");
            int begin = IndexAfter(after, end, Kind.Begin);
            int data = IndexAfter(after, end, Kind.Data);
            Assert.Greater(begin, end);
            Assert.Greater(data, begin, "END -> BEGIN of the new generation -> DATA");
            Assert.AreEqual(lastBefore.Identity.Generation + 1, after[begin].Identity.Generation);
            Assert.AreEqual(after[begin].Identity.Generation, after[data].Identity.Generation);
            Assert.AreEqual(after[begin].Identity.Lease, lastBefore.Identity.Lease, "same lease");

            short lastOld = ReadPcm16(lastBefore.Pcm, lastBefore.Pcm.Length / 4 - 1);
            short firstNew = ReadPcm16(after[data].Pcm, 0);
            Assert.Greater(firstNew, lastOld, "the source continues from its cursor (no frame 0)");
            Assert.LessOrEqual(firstNew - lastOld, 3);
            playback.Stop();
        }

        [Test]
        public void GapUnderThreeSeconds_KeepsTheGeneration()
        {
            var sink = new Sink();
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, Address, FirstLease));
            var playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, Address, true);
            WaitFor(() => sink.Count(Kind.Data) > 10);
            mixer.AdvanceClockForTest(HapbeatEndpointStreamMixer.OwnGapSeconds - 1.0);
            int data = sink.Count(Kind.Data);
            WaitFor(() => sink.Count(Kind.Data) > data + 10);
            Assert.AreEqual(0, sink.Count(Kind.End));
            var packets = sink.Snapshot();
            Assert.AreEqual(packets[0].Identity.Generation, packets[packets.Count - 1].Identity.Generation);
            playback.Stop();
        }

        [Test]
        public void TicketChangeForTheSameIncarnation_EndsTheOldIdentityOnce_AndContinuesFromTheCursor()
        {
            var sink = new Sink();
            HapbeatProtocol.StreamLeaseIdentity lease = FirstLease;
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, Address, lease));
            var playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, Address, false);
            WaitFor(() => sink.Count(Kind.Data) > 10);

            lease = NextTicket; // a matched reply carries a different ticket (same boot, same incarnation)
            mixer.ReconcileEndpoints();
            WaitFor(() => sink.Snapshot().Exists(p => p.Kind == Kind.Data && p.Identity.Lease.Equals(NextTicket)));

            List<Packet> packets = sink.Snapshot();
            Assert.AreEqual(1, packets.FindAll(p => p.Kind == Kind.End && p.Identity.Lease.Equals(FirstLease)).Count,
                "the session under the old ticket ENDs once, under its old identity");
            Packet lastOld = packets.FindLast(p => p.Kind == Kind.Data && p.Identity.Lease.Equals(FirstLease));
            int firstNewIndex = packets.FindIndex(p => p.Identity.Lease.Equals(NextTicket));
            Assert.AreEqual(Kind.Begin, packets[firstNewIndex].Kind, "the new ticket's session starts with BEGIN");
            Packet firstNew = packets.Find(p => p.Kind == Kind.Data && p.Identity.Lease.Equals(NextTicket));
            short last = ReadPcm16(lastOld.Pcm, lastOld.Pcm.Length / 4 - 1);
            short next = ReadPcm16(firstNew.Pcm, 0);
            Assert.Greater(next, last, "a finite source continues from its cursor, not frame 0");
            Assert.LessOrEqual(next - last, 3);
            playback.Stop();
        }

        [Test]
        public void RepeatedBegin_CarriesTheDevicesCurrentAddress()
        {
            var sink = new Sink();
            string address = Address;
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, address, FirstLease));
            var playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count(Kind.Data) > 3);

            address = "player_1/pos_chest/group_2"; // the device's address changed mid-session
            mixer.ReconcileEndpoints();
            int begins = sink.Count(Kind.Begin);
            WaitFor(() => sink.Count(Kind.Begin) > begins, 1500); // the next periodic repeat
            List<Packet> packets = sink.Snapshot();
            Packet repeat = packets.FindLast(p => p.Kind == Kind.Begin);
            Assert.AreEqual(address, repeat.Target, "otherwise the receiver answers STREAM_TARGET_MISMATCH");
            Assert.AreEqual(packets[0].Identity.Generation, repeat.Identity.Generation, "still the same session");
            Assert.AreEqual(0, sink.Count(Kind.End));
            playback.Stop();
        }

        [Test]
        public void DeferReason_ComesFromTheResolver_AndClearsWhenActive()
        {
            var sink = new Sink();
            HapbeatClient.StreamEndpoint? endpoint = null;
            var reason = HapbeatStreamPlaybackDeferReason.UnknownClass;
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => endpoint.HasValue
                    ? new List<HapbeatClient.StreamEndpoint> { endpoint.Value }
                    : new List<HapbeatClient.StreamEndpoint>(),
                () => 0.01f, _ => { }, resolveDeferReason: _ => reason);
            var playback = mixer.AddSamples(Ramp(), 16000, 1, 1f, 1f, Address, true);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, playback.Status);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.UnknownClass, playback.DeferReason);

            reason = HapbeatStreamPlaybackDeferReason.Superseded;
            mixer.ReconcileEndpoints();
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.Superseded, playback.DeferReason);

            reason = HapbeatStreamPlaybackDeferReason.LeaseUnavailable;
            mixer.ReconcileEndpoints();
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.LeaseUnavailable, playback.DeferReason);

            endpoint = new HapbeatClient.StreamEndpoint(Device, Address, FirstLease);
            mixer.ReconcileEndpoints();
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, playback.Status);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.None, playback.DeferReason);
            playback.Stop();
        }

        [Test]
        public void DeferReason_Priority_OverMatchingDevices()
        {
            // Superseded > LeaseUnavailable > UnknownClass > NoResolvedEndpoint (sdk-multi-stream.md §3).
            using var client = new HapbeatClient();
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint, client.GetStreamDeferReason("player_1"));
            var unknown = new IPEndPoint(IPAddress.Parse("192.0.2.111"), 7700);
            var unavailable = new IPEndPoint(IPAddress.Parse("192.0.2.112"), 7700);
            var superseded = new IPEndPoint(IPAddress.Parse("192.0.2.113"), 7700);

            Discover(client, unknown, "player_1/pos_l_arm/group_1");
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.UnknownClass, client.GetStreamDeferReason("player_1"));
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint, client.GetStreamDeferReason("player_2"));

            ReplyLease(client, unavailable, "player_1/pos_r_arm/group_1", leaseValid: false, superseded: false);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.LeaseUnavailable, client.GetStreamDeferReason("player_1"));
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.UnknownClass, client.GetStreamDeferReason("*/pos_l_arm"));

            ReplyLease(client, superseded, "player_1/pos_chest/group_1", leaseValid: true, superseded: true);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.Superseded, client.GetStreamDeferReason("player_1"));

            var infos = client.GetStreamEndpointInfos();
            Assert.AreEqual(3, infos.Count);
            Assert.AreEqual(HapbeatStreamClass.Unknown, client.GetStreamEndpointInfo(unknown.Address).StreamClass);
            Assert.AreEqual(HapbeatStreamClass.Gen2, client.GetStreamEndpointInfo(unavailable.Address).StreamClass);
            Assert.AreEqual(HapbeatStreamLeaseState.Unavailable, client.GetStreamEndpointInfo(unavailable.Address).LeaseState);
            Assert.AreEqual(HapbeatStreamLeaseState.Superseded, client.GetStreamEndpointInfo(superseded.Address).LeaseState);
            Assert.AreEqual(HapbeatStreamLeaseState.None, client.GetStreamEndpointInfo(unknown.Address).LeaseState);
        }

        [Test]
        public void Gen1EndpointWaitingForItsGuard_CountsAsActive()
        {
            var sink = new LegacySink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(Device, Address, default, StreamEndpointMode.Legacy),
                },
                () => 0.01f, _ => { });
            var first = mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, Address, true);
            WaitFor(() => sink.Begins == 1);
            first.Stop();
            WaitFor(() => sink.Ends == 1);

            var second = mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, Address, true);
            Assert.AreEqual(1, sink.Begins, "the 300 ms guard still holds the next BEGIN");
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, second.Status,
                "waiting out the Gen 1 guard is not Deferred (sdk-multi-stream.md §3)");
            WaitFor(() => sink.Begins == 2);
            second.Stop();
        }

        [Test]
        public void OneMotorEndpoint_IgnoresPan_LeftAndRightAreEqual()
        {
            var sink = new Sink();
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, Address, FirstLease,
                StreamEndpointMode.V2, hapticChannels: 1));
            var playback = mixer.AddSamples(Constant(0.5f), 16000, 1, 1f, 1f, Address, true);
            playback.Pan = -1f; // full left on a two-motor device
            WaitFor(() => sink.Count(Kind.Data) > 10);
            Packet last = sink.Snapshot().FindLast(p => p.Kind == Kind.Data);
            for (int frame = 0; frame < last.Pcm.Length / 4; frame++)
                Assert.AreEqual(ReadPcm16(last.Pcm, frame, 0), ReadPcm16(last.Pcm, frame, 1), $"frame {frame}");
            Assert.Greater(ReadPcm16(last.Pcm, 0, 1), 0, "the right channel is not silenced by the pan");
            playback.Stop();
        }

        [Test]
        public void TwoMotorOrUnknownEndpoint_AppliesPan()
        {
            var sink = new Sink();
            using var mixer = Mixer(sink, () => new HapbeatClient.StreamEndpoint(Device, Address, FirstLease));
            var playback = mixer.AddSamples(Constant(0.5f), 16000, 1, 1f, 1f, Address, true);
            playback.Pan = -1f;
            WaitFor(() => sink.Count(Kind.Data) > 10);
            Packet last = sink.Snapshot().FindLast(p => p.Kind == Kind.Data);
            Assert.Greater(ReadPcm16(last.Pcm, 0, 0), 0);
            Assert.AreEqual(0, ReadPcm16(last.Pcm, 0, 1), "no CAPS (0.x): pan applies as before");
            playback.Stop();
        }

        // ---- helpers ----------------------------------------------------------------

        private sealed class LegacySink : IHapbeatEndpointStreamPacketSink, IHapbeatLegacyEndpointStreamPacketSink
        {
            private int _begins, _ends;
            public int Begins => Volatile.Read(ref _begins);
            public int Ends => Volatile.Read(ref _ends);
            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity, ushort sampleRate,
                byte channels, byte format, uint totalSamples, float gain, string target) { }
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity, uint byteOffset,
                byte[] audioData, int dataOffset, int dataLength) { }
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity) { }
            public void LegacyBegin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format,
                uint totalSamples, float gain, string target) => Interlocked.Increment(ref _begins);
            public void LegacyData(IPEndPoint endpoint, uint byteOffset, byte[] audioData, int dataOffset, int dataLength) { }
            public void LegacyEnd(IPEndPoint endpoint) => Interlocked.Increment(ref _ends);
        }

        private static HapbeatEndpointStreamMixer Mixer(Sink sink, Func<HapbeatClient.StreamEndpoint> endpoint) =>
            new HapbeatEndpointStreamMixer(sink, target =>
            {
                HapbeatClient.StreamEndpoint current = endpoint();
                return HapbeatClient.AddressMatches(target, current.Address)
                    ? new List<HapbeatClient.StreamEndpoint> { current }
                    : new List<HapbeatClient.StreamEndpoint>();
            }, () => 0.01f, _ => { });

        private static void Discover(HapbeatClient client, IPEndPoint device, string address)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out _));
            Invoke(client, seq, Rewrite(StreamLegacyClassificationTests.LegacyPong(timestamp,
                StreamLegacyClassificationTests.Tail.None), address), device);
        }

        private static void ReplyLease(HapbeatClient client, IPEndPoint device, string address,
            bool leaseValid, bool superseded)
        {
            ushort seq = client.SendLeasePingTo(device);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            Invoke(client, seq, Rewrite(StreamLegacyClassificationTests.V2Pong(timestamp, incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0xDEF, leaseValid ? 2u : 0u), leaseValid, superseded,
                highWaterTicket: superseded ? 3u : 0u), address), device);
        }

        private static void Invoke(HapbeatClient client, ushort seq, byte[] payload, IPEndPoint sender) =>
            typeof(HapbeatClient).GetMethod("HandlePong",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(client, new object[] { seq, payload, sender });

        private static byte[] Rewrite(byte[] payload, string address)
        {
            const string helperAddress = "player_1/pos_chest/group_1";
            string text = System.Text.Encoding.ASCII.GetString(payload);
            int at = text.IndexOf(helperAddress, StringComparison.Ordinal);
            if (at < 0 || address == helperAddress) return payload;
            var result = new List<byte>();
            for (int i = 0; i < at; i++) result.Add(payload[i]);
            result.AddRange(System.Text.Encoding.ASCII.GetBytes(address));
            for (int i = at + helperAddress.Length; i < payload.Length; i++) result.Add(payload[i]);
            return result.ToArray();
        }

        private static int IndexAfter(List<Packet> packets, int from, Kind kind)
        {
            for (int i = Math.Max(0, from); i < packets.Count; i++)
                if (packets[i].Kind == kind) return i;
            return -1;
        }

        private static float[] Ramp()
        {
            // 1 s, strictly increasing: one PCM16 step (~1 LSB) per frame.
            var samples = new float[16000];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * (i + 1) / samples.Length;
            return samples;
        }

        private static float[] Constant(float value)
        {
            var samples = new float[1600];
            for (int i = 0; i < samples.Length; i++) samples[i] = value;
            return samples;
        }

        private static short ReadPcm16(byte[] pcm, int frame, int channel = 0)
        {
            int offset = (frame * 2 + channel) * 2;
            return (short)(pcm[offset] | (pcm[offset + 1] << 8));
        }

        private static void WaitFor(Func<bool> predicate, int timeoutMilliseconds = 2000)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, timeoutMilliseconds), "Timed out waiting for mixer packets.");
        }
    }
}
