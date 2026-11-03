using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// ERROR from a receiver (message-format.md §0xFF "受ける規則（SDK）", stream-session.md
    /// Sender requirements 5): accepted only from a known device's address and port 7700,
    /// a stream error only for the current or previous session sent to that device, and
    /// handled per endpoint. The client hands datagrams to a recording sink (no socket);
    /// devices and ERRORs are injected through the private receive handlers; the mixer
    /// tests use a recording packet sink.
    /// </summary>
    public sealed class StreamErrorHandlingTests
    {
        private const string Address = "player_1/pos_chest/group_1";
        private const string OtherAddress = "player_1/pos_neck/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.90"), 7700);
        private static readonly IPEndPoint OtherDevice = new IPEndPoint(IPAddress.Parse("192.0.2.91"), 7700);
        private static readonly HapbeatProtocol.StreamLeaseIdentity Lease =
            new HapbeatProtocol.StreamLeaseIdentity(0xE001, 4);

        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo HandleErrorMethod =
            typeof(HapbeatClient).GetMethod("HandleError", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class DatagramSink : IHapbeatDatagramSink
        {
            public readonly List<(IPEndPoint destination, byte[] packet)> Sent = new List<(IPEndPoint, byte[])>();
            public void Send(byte[] data, int length, IPEndPoint destination) =>
                Sent.Add((destination, (byte[])data.Clone()));

            public int ExtendedPingsTo(IPEndPoint device)
            {
                int count = 0;
                foreach (var sent in Sent)
                    if (sent.packet[3] == HapbeatProtocol.CMD_PING && sent.packet[6] == 16 &&
                        sent.destination.Equals(device)) count++;
                return count;
            }
        }

        private sealed class Observed
        {
            public readonly List<ushort> Errors = new List<ushort>();
            public readonly List<(IPEndPoint, ushort, HapbeatProtocol.StreamSessionIdentity)> StreamErrors =
                new List<(IPEndPoint, ushort, HapbeatProtocol.StreamSessionIdentity)>();
            public int Renewed;
        }

        // ---- acceptance (client) ----------------------------------------------------

        [TestCase(0, TestName = "StreamError_FromUnknownAddress_IsIgnored")]
        [TestCase(1, TestName = "StreamError_FromAnotherPort_IsIgnored")]
        [TestCase(2, TestName = "StreamError_ForAnUntrackedEnvelope_IsIgnored")]
        [TestCase(3, TestName = "StreamError_WithDetailNot16Bytes_IsIgnored")]
        [TestCase(4, TestName = "StreamError_ForTheSessionBeforeLast_IsIgnored")]
        public void UnacceptedStreamError_IsIgnored(int failure)
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            var observed = Observe(client);
            HapbeatProtocol.StreamSessionIdentity first = Begin(client);
            HapbeatProtocol.StreamSessionIdentity current = first;
            if (failure == 4)
            {
                Begin(client);
                current = Begin(client); // first is now two sessions back
            }

            IPEndPoint from = failure == 0 ? new IPEndPoint(IPAddress.Parse("192.0.2.99"), 7700)
                : failure == 1 ? new IPEndPoint(Device.Address, 50000)
                : Device;
            byte[] detail = failure == 2 ? Envelope(new HapbeatProtocol.StreamSessionIdentity(Lease, 99))
                : failure == 3 ? new byte[15]
                : Envelope(first);
            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_STREAM_STOPPED, "stream stopped",
                HapbeatProtocol.CMD_STREAM_DATA, detail), from);
            client.DispatchMainThreadCallbacks();

            Assert.IsEmpty(observed.Errors, "a rejected ERROR raises nothing");
            Assert.IsEmpty(observed.StreamErrors);
            Assert.IsTrue(current.IsValid);
        }

        [Test]
        public void StreamError_ForTheCurrentOrPreviousSession_IsAccepted()
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            var observed = Observe(client);
            HapbeatProtocol.StreamSessionIdentity previous = Begin(client);
            HapbeatProtocol.StreamSessionIdentity current = Begin(client);

            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_STREAM_NOT_STARTED, "not started",
                HapbeatProtocol.CMD_STREAM_DATA, Envelope(current)), Device);
            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_STREAM_TIMED_OUT, "timed out",
                HapbeatProtocol.CMD_STREAM_DATA, Envelope(previous)), Device);
            client.DispatchMainThreadCallbacks();

            CollectionAssert.AreEqual(new[] { HapbeatProtocol.ERROR_STREAM_NOT_STARTED, HapbeatProtocol.ERROR_STREAM_TIMED_OUT },
                observed.Errors);
            Assert.AreEqual(2, observed.StreamErrors.Count);
            Assert.AreEqual(Device, observed.StreamErrors[0].Item1);
            Assert.AreEqual(current.Generation, observed.StreamErrors[0].Item3.Generation);
            Assert.AreEqual(previous.Generation, observed.StreamErrors[1].Item3.Generation);
        }

        [Test]
        public void StreamSuperseded_DefersOnlyThatEndpoint()
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            AddGen2Device(client, sink, OtherDevice, OtherAddress, new HapbeatProtocol.StreamLeaseIdentity(0xE002, 1));
            HapbeatProtocol.StreamSessionIdentity session = Begin(client);

            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_STREAM_SUPERSEDED, "superseded",
                HapbeatProtocol.CMD_STREAM_DATA, Envelope(session)), Device);
            client.DispatchMainThreadCallbacks();

            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address), "no automatic takeover");
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.Superseded, client.GetStreamDeferReason(Address));
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(OtherAddress).Count,
                "an ERROR from one device does not change sending to another");

            // Periodic lease replies do not take it back; a re-issued one does.
            client.SendLeasePingTo(Device);
            ReplyGen2(client, sink, Device, Address, new HapbeatProtocol.StreamLeaseIdentity(0xE001, 6));
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address));
            client.SendLeasePingTo(Device);
            ReplyGen2(client, sink, Device, Address, new HapbeatProtocol.StreamLeaseIdentity(0xE001, 7), reissued: true);
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(Address).Count);
        }

        [TestCase(HapbeatProtocol.ERROR_STREAM_GEN_UNSUPPORTED, TestName = "StreamGenUnsupported_SendsAUnicastLeasePing")]
        [TestCase(HapbeatProtocol.ERROR_STREAM_TARGET_MISMATCH, TestName = "StreamTargetMismatch_SendsAUnicastLeasePing")]
        [TestCase(HapbeatProtocol.ERROR_STREAM_LEASE_INVALID, TestName = "StreamLeaseInvalid_SendsAUnicastLeasePing")]
        public void ClassOrLeaseError_AsksThatEndpointAgain(ushort code)
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            HapbeatProtocol.StreamSessionIdentity session = Begin(client);
            int before = sink.ExtendedPingsTo(Device);
            byte[] detail = HapbeatProtocol.IsStreamSessionErrorCode(code) ? Envelope(session) : Array.Empty<byte>();
            InvokeError(client, ErrorPayload(code, "error", HapbeatProtocol.CMD_STREAM_BEGIN, detail), Device);
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(before + 1, sink.ExtendedPingsTo(Device));
        }

        [Test]
        public void StreamLeaseInvalid_SameLeaseInTheNextMatchedReply_BeginsANewGeneration()
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            var observed = Observe(client);
            HapbeatProtocol.StreamSessionIdentity session = Begin(client);
            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_STREAM_LEASE_INVALID, "lease invalid",
                HapbeatProtocol.CMD_STREAM_DATA, Envelope(session)), Device);
            client.DispatchMainThreadCallbacks();

            ReplyGen2(client, sink, Device, Address, Lease);
            Assert.AreEqual(1, observed.Renewed, "same lease confirmed: the session begins a new generation");

            // Once only: the next periodic reply is not a renewal.
            client.SendLeasePingTo(Device);
            ReplyGen2(client, sink, Device, Address, Lease);
            Assert.AreEqual(1, observed.Renewed);
        }

        [Test]
        public void UnknownCommandForCapsRequest_MeansNoCaps()
        {
            var sink = new DatagramSink();
            using var client = OpenWithGen2Device(sink);
            client.RefreshStreamLeases(null); // sends the first CAPS_REQ
            Assert.AreEqual(-1, client.GetStreamEndpointInfo(Device.Address).CapsGeneration);
            InvokeError(client, ErrorPayload(HapbeatProtocol.ERROR_UNKNOWN_COMMAND, "unknown command",
                HapbeatProtocol.CMD_CAPS_REQ, Array.Empty<byte>()), Device);
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(0, client.GetStreamEndpointInfo(Device.Address).CapsGeneration,
                "UNKNOWN_COMMAND for CAPS_REQ: the device has no CAPS");
        }

        // ---- handling (mixer) -------------------------------------------------------

        private enum Kind { Begin, Data, End }

        private sealed class PacketSink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            private readonly List<(Kind kind, string endpoint, HapbeatProtocol.StreamSessionIdentity identity)> _packets =
                new List<(Kind, string, HapbeatProtocol.StreamSessionIdentity)>();

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target)
            {
                lock (_lock) _packets.Add((Kind.Begin, endpoint.ToString(), identity));
            }
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
            {
                lock (_lock) _packets.Add((Kind.Data, endpoint.ToString(), identity));
            }
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            {
                lock (_lock) _packets.Add((Kind.End, endpoint.ToString(), identity));
            }

            public List<(Kind kind, string endpoint, HapbeatProtocol.StreamSessionIdentity identity)> Snapshot()
            {
                lock (_lock) return new List<(Kind, string, HapbeatProtocol.StreamSessionIdentity)>(_packets);
            }

            public int Count(Kind kind, IPEndPoint endpoint)
            {
                lock (_lock) return _packets.FindAll(p => p.kind == kind && p.endpoint == endpoint.ToString()).Count;
            }

            public HapbeatProtocol.StreamSessionIdentity LastIdentity(IPEndPoint endpoint)
            {
                lock (_lock) return _packets.FindLast(p => p.endpoint == endpoint.ToString()).identity;
            }
        }

        private static HapbeatEndpointStreamMixer TwoEndpointMixer(PacketSink sink) =>
            new HapbeatEndpointStreamMixer(sink, target =>
            {
                var all = new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(Device, Address, Lease),
                    new HapbeatClient.StreamEndpoint(OtherDevice, OtherAddress, new HapbeatProtocol.StreamLeaseIdentity(0xE002, 1)),
                };
                return all.FindAll(e => HapbeatClient.AddressMatches(target, e.Address));
            }, () => 0.01f, _ => { });

        [Test]
        public void StreamNotStarted_ResendsTheSameBeginAtOnce()
        {
            var sink = new PacketSink();
            using var mixer = TwoEndpointMixer(sink);
            var playback = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "*/pos_chest", true);
            WaitFor(() => sink.Count(Kind.Data, Device) > 5);
            HapbeatProtocol.StreamSessionIdentity identity = sink.LastIdentity(Device);
            int begins = sink.Count(Kind.Begin, Device);

            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_NOT_STARTED, identity);
            Assert.AreEqual(begins + 1, sink.Count(Kind.Begin, Device));
            Assert.AreEqual(identity.Generation, sink.LastIdentity(Device).Generation, "the identical BEGIN tuple");
            playback.Stop();
        }

        [Test]
        public void StreamTimedOut_EndsAndBeginsANewGeneration_OnlyOnThatEndpoint()
        {
            var sink = new PacketSink();
            using var mixer = TwoEndpointMixer(sink);
            var playback = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count(Kind.Data, Device) > 5 && sink.Count(Kind.Data, OtherDevice) > 5);
            HapbeatProtocol.StreamSessionIdentity timedOut = sink.LastIdentity(Device);
            HapbeatProtocol.StreamSessionIdentity other = sink.LastIdentity(OtherDevice);

            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_TIMED_OUT, timedOut);
            WaitFor(() => sink.LastIdentity(Device).Generation != timedOut.Generation);
            var packets = sink.Snapshot();
            int end = packets.FindIndex(p => p.kind == Kind.End && p.endpoint == Device.ToString());
            Assert.GreaterOrEqual(end, 0, "the timed-out generation is ENDed");
            Assert.AreEqual(timedOut.Generation, packets[end].identity.Generation);
            var next = packets.Find(p => p.endpoint == Device.ToString() && p.identity.Generation > timedOut.Generation);
            Assert.AreEqual(Kind.Begin, next.kind, "the new generation starts with BEGIN before its DATA");
            Assert.AreEqual(timedOut.Lease, next.identity.Lease, "same lease, new generation");

            Assert.AreEqual(0, sink.Count(Kind.End, OtherDevice), "the other endpoint is unaffected");
            Assert.AreEqual(other.Generation, sink.LastIdentity(OtherDevice).Generation);

            // An ERROR about the generation just replaced does not restart again.
            int ends = sink.Count(Kind.End, Device);
            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_TIMED_OUT, timedOut);
            Assert.AreEqual(ends, sink.Count(Kind.End, Device));
            playback.Stop();
        }

        [Test]
        public void StreamStopped_RemovesTheSourceFromThatEndpointOnly_AndItNeverRejoins()
        {
            var sink = new PacketSink();
            using var mixer = TwoEndpointMixer(sink);
            var playback = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count(Kind.Data, Device) > 5 && sink.Count(Kind.Data, OtherDevice) > 5);

            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_STOPPED, sink.LastIdentity(Device));
            Thread.Sleep(50);
            int stoppedData = sink.Count(Kind.Data, Device);
            int otherData = sink.Count(Kind.Data, OtherDevice);
            mixer.ReconcileEndpoints();
            Thread.Sleep(100);

            Assert.AreEqual(stoppedData, sink.Count(Kind.Data, Device), "no more DATA to the stopped device");
            Assert.Greater(sink.Count(Kind.Data, OtherDevice), otherData, "the other device keeps playing");
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, playback.Status);
            WaitFor(() => sink.Count(Kind.End, Device) == 1); // the empty session's END after the linger

            // A new Playback joins the stopped device as usual.
            var fresh = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "*/pos_chest", true);
            WaitFor(() => sink.Count(Kind.Data, Device) > stoppedData);
            fresh.Stop();
            playback.Stop();
        }

        [TestCase(false, TestName = "StreamStopped_OneShotOnItsOnlyEndpoint_IsStopped")]
        [TestCase(true, TestName = "StreamStopped_LoopOnItsOnlyEndpoint_IsStopped")]
        public void StreamStopped_SourceWithNoEndpointLeft_IsStopped(bool loop)
        {
            var sink = new PacketSink();
            using var mixer = TwoEndpointMixer(sink);
            var playback = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "*/pos_chest", loop);
            WaitFor(() => sink.Count(Kind.Data, Device) > 5);
            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_STOPPED, sink.LastIdentity(Device));
            WaitFor(() => playback.Status == HapbeatStreamPlaybackStatus.Stopped);
        }

        [Test]
        public void StreamEnded_IsLoggedOnly()
        {
            var sink = new PacketSink();
            var logs = new List<string>();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { new HapbeatClient.StreamEndpoint(Device, Address, Lease) },
                () => 0.01f, message => { lock (logs) logs.Add(message); });
            var playback = mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "", true);
            WaitFor(() => sink.Count(Kind.Data, Device) > 5);
            int begins = sink.Count(Kind.Begin, Device);
            mixer.HandleStreamError(Device, HapbeatProtocol.ERROR_STREAM_ENDED, sink.LastIdentity(Device));
            lock (logs) Assert.AreEqual(1, logs.FindAll(l => l.Contains("STREAM_ENDED")).Count);
            Assert.AreEqual(0, sink.Count(Kind.End, Device));
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, playback.Status);
            Assert.LessOrEqual(sink.Count(Kind.Begin, Device) - begins, 1, "only the periodic BEGIN repeat");
            playback.Stop();
        }

        // ---- helpers ----------------------------------------------------------------

        private static HapbeatClient OpenWithGen2Device(DatagramSink sink)
        {
            var client = new HapbeatClient(false, sink);
            client.OpenBroadcast(7700);
            client.DispatchMainThreadCallbacks();
            AddGen2Device(client, sink, Device, Address, Lease);
            return client;
        }

        private static void AddGen2Device(HapbeatClient client, DatagramSink sink, IPEndPoint device,
            string address, HapbeatProtocol.StreamLeaseIdentity lease)
        {
            client.SendLeasePingTo(device);
            ReplyGen2(client, sink, device, address, lease);
        }

        private static void ReplyGen2(HapbeatClient client, DatagramSink sink, IPEndPoint device, string address,
            HapbeatProtocol.StreamLeaseIdentity lease, bool reissued = false)
        {
            ushort seq = 0;
            bool found = false;
            for (int i = sink.Sent.Count - 1; i >= 0 && !found; i--)
            {
                byte[] packet = sink.Sent[i].packet;
                if (packet[3] == HapbeatProtocol.CMD_PING && packet[6] == 16 && sink.Sent[i].destination.Equals(device))
                {
                    seq = (ushort)(packet[4] | (packet[5] << 8));
                    found = true;
                }
            }
            Assert.IsTrue(found);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            byte[] pong = StreamLegacyClassificationTests.V2Pong(timestamp, incarnation, lease, true, false, reissued);
            pong = ReplaceAddress(pong, address);
            HandlePongMethod.Invoke(client, new object[] { seq, pong, device });
            client.DispatchMainThreadCallbacks();
        }

        private static HapbeatProtocol.StreamSessionIdentity Begin(HapbeatClient client)
        {
            Assert.IsTrue(client.TryAllocateStreamSessionIdentity(Device, Lease, out var identity));
            client.SendStreamBeginTo(Device, identity, 16000, 2, HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, Address);
            return identity;
        }

        private static Observed Observe(HapbeatClient client)
        {
            client.DispatchMainThreadCallbacks();
            var observed = new Observed();
            client.OnError += (code, _) => observed.Errors.Add(code);
            client.OnStreamError += (endpoint, code, identity) => observed.StreamErrors.Add((endpoint, code, identity));
            client.OnStreamLeaseRenewed += _ => observed.Renewed++;
            return observed;
        }

        private static void InvokeError(HapbeatClient client, byte[] payload, IPEndPoint from)
        {
            Assert.IsNotNull(HandleErrorMethod);
            HandleErrorMethod.Invoke(client, new object[] { payload, from });
        }

        private static byte[] ErrorPayload(ushort code, string message, byte refCommand, byte[] detail)
        {
            var payload = new List<byte> { (byte)code, (byte)(code >> 8) };
            payload.AddRange(Encoding.ASCII.GetBytes(message));
            payload.Add(0);
            payload.Add(refCommand);
            payload.Add(0x34);
            payload.Add(0x12);
            payload.Add((byte)detail.Length);
            payload.AddRange(detail);
            return payload.ToArray();
        }

        private static byte[] Envelope(HapbeatProtocol.StreamSessionIdentity identity) =>
            HapbeatProtocol.BuildStreamEndPayload(identity);

        private static byte[] ReplaceAddress(byte[] payload, string address)
        {
            const string helperAddress = "player_1/pos_chest/group_1";
            string text = Encoding.ASCII.GetString(payload);
            int at = text.IndexOf(helperAddress, StringComparison.Ordinal);
            if (at < 0 || address == helperAddress) return payload;
            var result = new List<byte>(payload.Length + address.Length);
            for (int i = 0; i < at; i++) result.Add(payload[i]);
            result.AddRange(Encoding.ASCII.GetBytes(address));
            for (int i = at + helperAddress.Length; i < payload.Length; i++) result.Add(payload[i]);
            return result.ToArray();
        }

        private static void WaitFor(Func<bool> predicate)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, 2000), "Timed out waiting for mixer packets.");
        }
    }
}
