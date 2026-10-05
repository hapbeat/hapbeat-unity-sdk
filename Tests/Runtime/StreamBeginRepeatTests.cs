using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// A single lost v2 BEGIN must not silence a whole session: the identical BEGIN is
    /// repeated before the first DATA blocks and then periodically (equal BEGIN is
    /// idempotent on the receiver). Legacy firmware restarts on BEGIN, so it gets none.
    /// Recording sinks only; no socket is opened and nothing is sent.
    /// </summary>
    public sealed class StreamBeginRepeatTests
    {
        private enum Kind { Begin, Data, End, LegacyBegin, LegacyData, LegacyEnd }

        private sealed class Sink : IHapbeatEndpointStreamPacketSink, IHapbeatLegacyEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            private readonly List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> _packets =
                new List<(Kind, HapbeatProtocol.StreamSessionIdentity)>();

            private void Record(Kind kind, HapbeatProtocol.StreamSessionIdentity identity = default)
            {
                lock (_lock) _packets.Add((kind, identity));
            }

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target) =>
                Record(Kind.Begin, identity);
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength) => Record(Kind.Data, identity);
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity) =>
                Record(Kind.End, identity);
            public void LegacyBegin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format,
                uint totalSamples, float gain, string target) => Record(Kind.LegacyBegin);
            public void LegacyData(IPEndPoint endpoint, uint byteOffset, byte[] audioData,
                int dataOffset, int dataLength) => Record(Kind.LegacyData);
            public void LegacyEnd(IPEndPoint endpoint) => Record(Kind.LegacyEnd);

            public List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> Snapshot()
            {
                lock (_lock) return new List<(Kind, HapbeatProtocol.StreamSessionIdentity)>(_packets);
            }

            public int Count(Kind kind)
            {
                lock (_lock) return _packets.FindAll(x => x.kind == kind).Count;
            }
        }

        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.60"), 7700);
        private const string Address = "player_1/pos_chest/group_1";

        [Test]
        public void ShouldRepeatBegin_CoversFirstBlocksThenEveryFiveHundredMilliseconds()
        {
            var repeated = new List<int>();
            for (int block = 0; block <= 150; block++)
                if (HapbeatEndpointStreamMixer.ShouldRepeatBegin(block)) repeated.Add(block);
            CollectionAssert.AreEqual(new[] { 1, 2, 50, 100, 150 }, repeated);
        }

        [Test]
        public void V2Session_RepeatsIdenticalBegin_AndNeverSendsDataBeforeItsBegin()
        {
            var sink = new Sink();
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x71, 4);
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { new HapbeatClient.StreamEndpoint(Device, Address, lease) },
                () => 0.01f, _ => { });
            var playback = mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count(Kind.Data) > 105, 3000);
            playback.Stop();
            WaitFor(() => sink.Count(Kind.End) == 1, 1000);

            var packets = sink.Snapshot();
            HapbeatProtocol.StreamSessionIdentity identity = packets[0].identity;
            Assert.AreEqual(Kind.Begin, packets[0].kind, "the session starts with BEGIN");
            int dataBlock = 0;
            var repeatedBefore = new List<int>();
            foreach (var packet in packets)
            {
                Assert.AreEqual(identity.Lease, packet.identity.Lease);
                Assert.AreEqual(identity.Generation, packet.identity.Generation,
                    "every repeat is the identical BEGIN tuple; DATA uses only that tuple");
                if (packet.kind == Kind.Begin)
                {
                    if (dataBlock > 0) repeatedBefore.Add(dataBlock);
                }
                else if (packet.kind == Kind.Data)
                {
                    dataBlock++;
                }
            }
            Assert.Greater(dataBlock, 105);
            CollectionAssert.IsSubsetOf(new[] { 1, 2, 50, 100 }, repeatedBefore,
                "BEGIN precedes DATA blocks 1, 2 (with the original before 0) and then every 50 blocks");
            foreach (int block in repeatedBefore)
                Assert.IsTrue(block < 3 || block % 50 == 0, $"unexpected BEGIN repeat before block {block}");
        }

        [Test]
        public void LegacySession_SendsSingleBegin()
        {
            var sink = new Sink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(Device, Address, default, StreamEndpointMode.Legacy),
                },
                () => 0.01f, _ => { });
            var playback = mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count(Kind.LegacyData) > 55, 3000);
            playback.Stop();
            WaitFor(() => sink.Count(Kind.LegacyEnd) == 1, 1000);
            Assert.AreEqual(1, sink.Count(Kind.LegacyBegin), "pre-v2 firmware restarts on every BEGIN");
            Assert.AreEqual(0, sink.Count(Kind.Begin));
        }

        [Test]
        public void Client_AnnouncesEachSessionOnce_DespiteRepeatedBegin()
        {
            // Unopened client: the send path returns before any socket write.
            using var client = new HapbeatClient();
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x72, 9);
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(client, new object[] { seq, StreamLegacyClassificationTests.V2Pong(timestamp, incarnation, lease), Device });
            client.DispatchMainThreadCallbacks();
            int announced = 0;
            client.OnStreamSessionBegan += (_, __) => announced++;

            Assert.IsTrue(client.TryAllocateStreamSessionIdentity(Device, lease, out var first));
            for (int i = 0; i < 3; i++)
                client.SendStreamBeginTo(Device, first, 16000, 2, HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, Address);
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(1, announced, "a repeated identical BEGIN is not a new session");

            Assert.IsTrue(client.TryAllocateStreamSessionIdentity(Device, lease, out var second));
            client.SendStreamBeginTo(Device, second, 16000, 2, HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, Address);
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(2, announced, "a new generation is announced");
        }

        private static void WaitFor(Func<bool> predicate, int timeoutMilliseconds)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, timeoutMilliseconds), "Timed out waiting for recording mixer packets.");
        }
    }
}
