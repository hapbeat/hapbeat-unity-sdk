using System.Collections.Generic;
using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// stream-session.md "How SDKs send extended PINGs" (1.x): discovery broadcasts the
    /// 8-byte PING; the 16-byte extended PING goes by unicast only to the endpoints the SDK
    /// streams to or expects to stream to, at once and then every second until a matched
    /// reply, every TTL/3 (5 s) after that, and for one TTL (15 s) after the last session.
    /// The client hands every datagram to a recording sink (no socket); devices are
    /// injected through the private PONG handler, and the schedule is driven with explicit
    /// timestamps.
    /// </summary>
    public sealed class StreamLeaseKeepAliveTests
    {
        private const string LeftAddress = "player_1/pos_l_arm/group_1";
        private const string RightAddress = "player_1/pos_r_arm/group_1";
        private static readonly IPEndPoint Left = new IPEndPoint(IPAddress.Parse("192.0.2.80"), 7700);
        private static readonly IPEndPoint Right = new IPEndPoint(IPAddress.Parse("192.0.2.81"), 7700);
        private const long Second = 1_000_000;

        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class Sink : IHapbeatDatagramSink
        {
            public readonly List<(IPEndPoint destination, byte[] packet)> Sent = new List<(IPEndPoint, byte[])>();

            public void Send(byte[] data, int length, IPEndPoint destination) =>
                Sent.Add((destination, (byte[])data.Clone()));

            /// <summary>PINGs sent since <paramref name="from"/>, as (destination, payload length).</summary>
            public List<(IPEndPoint destination, int length)> Pings(int from = 0)
            {
                var pings = new List<(IPEndPoint, int)>();
                for (int i = from; i < Sent.Count; i++)
                    if (Sent[i].packet[3] == HapbeatProtocol.CMD_PING)
                        pings.Add((Sent[i].destination, Sent[i].packet[6] | (Sent[i].packet[7] << 8)));
                return pings;
            }
        }

        [Test]
        public void DiscoveryBroadcast_IsTheEightBytePing()
        {
            var sink = new Sink();
            using var client = Open(sink);
            client.SendPing();
            var pings = sink.Pings();
            Assert.AreEqual(1, pings.Count);
            Assert.AreEqual(8, pings[0].length, "an 8-byte PING never allocates a stream lease");
            Assert.AreEqual(IPAddress.Broadcast, pings[0].destination.Address);
        }

        [Test]
        public void ExtendedPing_GoesByUnicastOnlyToEndpointsMatchingAPlayback()
        {
            var sink = new Sink();
            using var client = Open(sink);
            Discover(client);
            long t0 = client.GetLocalTimestampUs();

            int mark = sink.Sent.Count;
            client.RefreshStreamLeases(new[] { "*/pos_l_arm" }, false, t0);
            var pings = sink.Pings(mark);
            Assert.AreEqual(1, pings.Count, "only the endpoint a Playback targets gets a lease request");
            Assert.AreEqual(Left, pings[0].destination);
            Assert.AreEqual(16, pings[0].length);

            mark = sink.Sent.Count;
            client.RefreshStreamLeases(null, false, t0 + 20 * Second);
            Assert.IsEmpty(sink.Pings(mark), "no Playback, no recent session: no lease request");
            foreach (var sent in sink.Sent)
            {
                bool extendedPing = sent.packet[3] == HapbeatProtocol.CMD_PING && sent.packet[6] == 16;
                if (extendedPing)
                    Assert.AreNotEqual(IPAddress.Broadcast, sent.destination.Address, "extended PINGs are never broadcast");
            }
        }

        [Test]
        public void UnmatchedEndpoint_IsAskedEverySecond_ThenEveryFiveSecondsOnceMatched()
        {
            var sink = new Sink();
            using var client = Open(sink);
            Discover(client);
            string[] targets = { LeftAddress };
            long t0 = client.GetLocalTimestampUs();

            Assert.AreEqual(1, PingsAt(client, sink, targets, t0), "a new lease target is asked at once");
            Assert.AreEqual(0, PingsAt(client, sink, targets, t0 + Second / 2));
            Assert.AreEqual(1, PingsAt(client, sink, targets, t0 + Second), "no matched reply yet: again after 1 s");

            ReplyGen2(client, sink, Left, LeftAddress, 7);
            long matched = t0 + Second;
            Assert.AreEqual(0, PingsAt(client, sink, targets, matched + 4 * Second));
            Assert.AreEqual(1, PingsAt(client, sink, targets, matched + 5 * Second),
                "a held lease is refreshed at TTL/3");
            Assert.LessOrEqual(HapbeatClient.LeaseRefreshIntervalUs, HapbeatClient.StreamLeaseTtlUs / 3);
        }

        [Test]
        public void Gen1Endpoint_IsAlsoRefreshedEveryFiveSeconds()
        {
            var sink = new Sink();
            using var client = Open(sink);
            Discover(client);
            string[] targets = { LeftAddress };
            long t0 = client.GetLocalTimestampUs();
            Assert.AreEqual(1, PingsAt(client, sink, targets, t0));
            ReplyGen1(client, sink, Left);
            Assert.AreEqual(0, PingsAt(client, sink, targets, t0 + 4 * Second));
            Assert.AreEqual(1, PingsAt(client, sink, targets, t0 + 5 * Second));
        }

        [Test]
        public void LeaseIsRefreshedForOneTtlAfterTheLastSession_ThenStopsBeingUsable()
        {
            var sink = new Sink();
            using var client = Open(sink);
            Discover(client);
            long t0 = client.GetLocalTimestampUs();
            Assert.AreEqual(1, PingsAt(client, sink, new[] { LeftAddress }, t0));
            ReplyGen2(client, sink, Left, LeftAddress, 7);
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0xB007, 7);
            Assert.IsTrue(client.TryAllocateStreamSessionIdentity(Left, lease, out var identity));
            client.SendStreamEndTo(Left, identity); // the last session ends now
            long ended = client.GetLocalTimestampUs();

            // No Playback targets the device any more; the lease is kept for one TTL.
            Assert.AreEqual(1, PingsAt(client, sink, null, ended + 6 * Second));
            Assert.AreEqual(1, PingsAt(client, sink, null, ended + 12 * Second));
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(LeftAddress).Count);

            int stateChanges = 0;
            client.OnStreamEndpointStateChanged += _ => stateChanges++;
            Assert.AreEqual(0, PingsAt(client, sink, null, ended + 16 * Second));
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(LeftAddress),
                "an unrefreshed lease expires on the receiver; it must not be used for the next session");
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(1, stateChanges);
            Assert.AreEqual(HapbeatStreamLeaseState.Unavailable, client.GetStreamEndpointInfo(Left.Address).LeaseState);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.LeaseUnavailable, client.GetStreamDeferReason(LeftAddress));

            // A new Playback asks again at once.
            Assert.AreEqual(1, PingsAt(client, sink, new[] { LeftAddress }, ended + 16 * Second + 1));
        }

        // ---- helpers --------------------------------------------------------------

        private static HapbeatClient Open(Sink sink)
        {
            var client = new HapbeatClient(false, sink);
            client.SetCommandUnicast(true, 120f); // device liveness spans the simulated time
            client.OpenBroadcast(7700);
            client.DispatchMainThreadCallbacks();
            return client;
        }

        // Both devices answer the discovery broadcast (liveness and address only).
        private static void Discover(HapbeatClient client)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out _));
            Invoke(client, seq, StreamLegacyClassificationTests.LegacyPong(timestamp,
                StreamLegacyClassificationTests.Tail.None), Left, LeftAddress);
            Invoke(client, seq, StreamLegacyClassificationTests.LegacyPong(timestamp,
                StreamLegacyClassificationTests.Tail.None), Right, RightAddress);
            client.DispatchMainThreadCallbacks();
        }

        private static int PingsAt(HapbeatClient client, Sink sink, string[] targets, long nowUs)
        {
            int mark = sink.Sent.Count;
            client.RefreshStreamLeases(targets, false, nowUs);
            return sink.Pings(mark).Count;
        }

        // Answers the last extended PING sent to that endpoint.
        private static void ReplyGen2(HapbeatClient client, Sink sink, IPEndPoint device, string address, uint ticket)
        {
            ushort seq = LastLeasePingSeq(sink, device);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            byte[] pong = StreamLegacyClassificationTests.V2Pong(timestamp, incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0xB007, ticket));
            Invoke(client, seq, pong, device, address);
            client.DispatchMainThreadCallbacks();
        }

        private static void ReplyGen1(HapbeatClient client, Sink sink, IPEndPoint device)
        {
            ushort seq = LastLeasePingSeq(sink, device);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out _));
            Invoke(client, seq, StreamLegacyClassificationTests.LegacyPong(timestamp,
                StreamLegacyClassificationTests.Tail.None), device, LeftAddress);
            client.DispatchMainThreadCallbacks();
        }

        private static ushort LastLeasePingSeq(Sink sink, IPEndPoint device)
        {
            for (int i = sink.Sent.Count - 1; i >= 0; i--)
            {
                byte[] packet = sink.Sent[i].packet;
                if (packet[3] == HapbeatProtocol.CMD_PING && packet[6] == 16 && sink.Sent[i].destination.Equals(device))
                    return (ushort)(packet[4] | (packet[5] << 8));
            }
            Assert.Fail("no extended PING was sent to " + device);
            return 0;
        }

        // The ordinary PONG fields carry the address the test helpers use; rewrite it for this device.
        private static void Invoke(HapbeatClient client, ushort seq, byte[] payload, IPEndPoint sender, string address)
        {
            Assert.IsNotNull(HandlePongMethod);
            byte[] patched = ReplaceAddress(payload, address);
            HandlePongMethod.Invoke(client, new object[] { seq, patched, sender });
        }

        private static byte[] ReplaceAddress(byte[] payload, string address)
        {
            const string helperAddress = "player_1/pos_chest/group_1";
            string text = System.Text.Encoding.ASCII.GetString(payload);
            int at = text.IndexOf(helperAddress, System.StringComparison.Ordinal);
            if (at < 0 || address == helperAddress) return payload;
            var result = new List<byte>(payload.Length + address.Length);
            for (int i = 0; i < at; i++) result.Add(payload[i]);
            result.AddRange(System.Text.Encoding.ASCII.GetBytes(address));
            for (int i = at + helperAddress.Length; i < payload.Length; i++) result.Add(payload[i]);
            return result.ToArray();
        }
    }
}
