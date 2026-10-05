using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// stream-session-v2.md "Legacy receiver fallback (SDK)": an endpoint is classified
    /// only from matched direct replies to this client's own extended PING. Drives the
    /// private PONG handler directly; no socket is opened and nothing is sent.
    /// </summary>
    public sealed class StreamLegacyClassificationTests
    {
        private const string Address = "player_1/pos_chest/group_1";

        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);

        public enum Tail
        {
            None,          // PONG ends right after volume_wiper
            BeforeWiper,   // PONG ends after the address strings (older firmware)
            OtherBytes,    // non-HBS2 bytes follow volume_wiper
        }

        [TestCase(Tail.None, TestName = "MatchedTaillessPong_ClassifiesLegacy")]
        [TestCase(Tail.BeforeWiper, TestName = "MatchedPongEndingBeforeVolumeWiper_ClassifiesLegacy")]
        [TestCase(Tail.OtherBytes, TestName = "MatchedPongWithNonHbs2Bytes_ClassifiesLegacy")]
        public void MatchedPongWithoutHbs2Marker_ClassifiesLegacy(Tail tail)
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.30");
            int leaseEvents = 0;
            client.OnStreamLeaseChanged += (_, __, ___, ____) => leaseEvents++;

            var ping = SendPing(client);
            InvokeHandlePong(client, ping.seq, LegacyPong(ping.timestamp, tail), sender);
            client.DispatchMainThreadCallbacks();

            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(StreamEndpointMode.Legacy, resolved[0].Mode);
            Assert.IsFalse(resolved[0].Lease.IsValid, "a legacy endpoint has no lease");
            Assert.AreEqual(sender, resolved[0].EndPoint);
            Assert.AreEqual(0, leaseEvents,
                "legacy classification must not raise lease events (no 'lease unavailable' warning)");
        }

        [TestCase(0, TestName = "UnsolicitedTaillessPong_DoesNotClassify")]
        [TestCase(1, TestName = "TaillessPongForUnknownSeq_DoesNotClassify")]
        [TestCase(2, TestName = "TaillessPongWithTimestampMismatch_DoesNotClassify")]
        [TestCase(3, TestName = "TaillessPongForRetiredIncarnation_DoesNotClassify")]
        public void UnmatchedTaillessPong_LeavesUnknownEndpointDeferred(int failure)
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.31");
            var ping = SendPing(client);
            ushort seq = ping.seq;
            long timestamp = ping.timestamp;
            if (failure == 0) timestamp = 0;
            else if (failure == 1) seq = unchecked((ushort)(seq + 1));
            else if (failure == 2) timestamp++;
            else client.ReacquireStreamLeases();

            InvokeHandlePong(client, seq, LegacyPong(timestamp, Tail.None), sender);

            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address),
                "only a matched direct reply may classify an unknown endpoint");
            Assert.IsFalse(client.IsLegacyStreamEndpoint(sender.Address));
        }

        [Test]
        public void UnsolicitedTaillessPong_DoesNotEraseV2Lease()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.32");
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x51, 5);
            var ping = SendPing(client);
            InvokeHandlePong(client, ping.seq, V2Pong(ping.timestamp, ping.incarnation, lease), sender);

            var next = SendPingAfter(client, ping.timestamp);
            InvokeHandlePong(client, next.seq, LegacyPong(0, Tail.None), sender);

            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(StreamEndpointMode.V2, resolved[0].Mode);
            Assert.AreEqual(lease, resolved[0].Lease);
        }

        [Test]
        public void LateTaillessReply_AfterNewerV2Reply_DoesNotFlipToLegacy()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.33");
            var older = SendPing(client);
            var newer = SendPingAfter(client, older.timestamp);
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x52, 6);
            InvokeHandlePong(client, newer.seq, V2Pong(newer.timestamp, newer.incarnation, lease), sender);

            InvokeHandlePong(client, older.seq, LegacyPong(older.timestamp, Tail.None), sender);

            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(StreamEndpointMode.V2, resolved[0].Mode, "a late reply must never change the class");
            Assert.AreEqual(lease, resolved[0].Lease);
        }

        [Test]
        public void LateV2Reply_AfterNewerLegacyReply_DoesNotFlipToV2()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.34");
            var older = SendPing(client);
            var newer = SendPingAfter(client, older.timestamp);
            InvokeHandlePong(client, newer.seq, LegacyPong(newer.timestamp, Tail.None), sender);

            InvokeHandlePong(client, older.seq, V2Pong(older.timestamp, older.incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0x53, 7)), sender);

            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(StreamEndpointMode.Legacy, resolved[0].Mode,
                "ordering is enforced across both classes");
        }

        [TestCase(0, TestName = "Hbs2TailWithWrongLength_IsIgnored")]
        [TestCase(1, TestName = "Hbs2TailWithWrongVersion_IsIgnored")]
        [TestCase(2, TestName = "Hbs2TailWithNonzeroReserved_IsIgnored")]
        [TestCase(3, TestName = "Hbs2TailWithUnknownFlag_IsIgnored")]
        public void MalformedHbs2Tail_ChangesNeitherClassNorLease(int defect)
        {
            // Unknown endpoint: stays unknown (deferred), NOT legacy.
            using (var client = new HapbeatClient())
            {
                var sender = Endpoint("192.0.2.35");
                var ping = SendPing(client);
                InvokeHandlePong(client, ping.seq, MalformedPong(ping.timestamp, ping.incarnation, defect), sender);
                Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address));
                Assert.IsFalse(client.IsLegacyStreamEndpoint(sender.Address));
            }

            // Established v2 endpoint: keeps its lease.
            using (var client = new HapbeatClient())
            {
                var sender = Endpoint("192.0.2.36");
                var lease = new HapbeatProtocol.StreamLeaseIdentity(0x54, 8);
                var ping = SendPing(client);
                InvokeHandlePong(client, ping.seq, V2Pong(ping.timestamp, ping.incarnation, lease), sender);
                var next = SendPingAfter(client, ping.timestamp);
                InvokeHandlePong(client, next.seq, MalformedPong(next.timestamp, next.incarnation, defect), sender);

                var resolved = client.GetResolvedStreamEndpoints(Address);
                Assert.AreEqual(1, resolved.Count);
                Assert.AreEqual(StreamEndpointMode.V2, resolved[0].Mode);
                Assert.AreEqual(lease, resolved[0].Lease);
            }

            // Established legacy endpoint: stays legacy.
            using (var client = new HapbeatClient())
            {
                var sender = Endpoint("192.0.2.37");
                var ping = SendPing(client);
                InvokeHandlePong(client, ping.seq, LegacyPong(ping.timestamp, Tail.None), sender);
                var next = SendPingAfter(client, ping.timestamp);
                InvokeHandlePong(client, next.seq, MalformedPong(next.timestamp, next.incarnation, defect), sender);
                Assert.IsTrue(client.IsLegacyStreamEndpoint(sender.Address));
            }
        }

        [Test]
        public void LegacyToV2_AndBack_FollowsLatestMatchedReply()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.38");
            var first = SendPing(client);
            InvokeHandlePong(client, first.seq, LegacyPong(first.timestamp, Tail.None), sender);
            Assert.AreEqual(StreamEndpointMode.Legacy, client.GetResolvedStreamEndpoints(Address)[0].Mode);

            // Firmware update: a matched valid HBS2 reply clears the legacy class.
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x55, 9);
            var second = SendPingAfter(client, first.timestamp);
            InvokeHandlePong(client, second.seq, V2Pong(second.timestamp, second.incarnation, lease), sender);
            var v2 = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, v2.Count);
            Assert.AreEqual(StreamEndpointMode.V2, v2[0].Mode);
            Assert.AreEqual(lease, v2[0].Lease);
            Assert.IsFalse(client.IsLegacyStreamEndpoint(sender.Address));

            // Rollback: a matched tailless reply clears the v2 lease.
            var third = SendPingAfter(client, second.timestamp);
            InvokeHandlePong(client, third.seq, LegacyPong(third.timestamp, Tail.None), sender);
            var legacy = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, legacy.Count);
            Assert.AreEqual(StreamEndpointMode.Legacy, legacy[0].Mode);
            Assert.IsFalse(legacy[0].Lease.IsValid);
        }

        [Test]
        public void V2DeferredLease_NeverFallsBackToLegacy()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.39");
            var ping = SendPing(client);
            InvokeHandlePong(client, ping.seq, V2Pong(ping.timestamp, ping.incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0x56, 0), leaseValid: false), sender);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address), "lease_valid=0 defers");
            Assert.IsFalse(client.IsLegacyStreamEndpoint(sender.Address));
        }

        [Test]
        public void ExplicitReacquire_KeepsLegacyClassification()
        {
            using var client = new HapbeatClient();
            var sender = Endpoint("192.0.2.40");
            var ping = SendPing(client);
            InvokeHandlePong(client, ping.seq, LegacyPong(ping.timestamp, Tail.None), sender);

            client.ReacquireStreamLeases();

            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count,
                "legacy firmware has no lease; focus/reacquire must not interrupt its stream");
            Assert.AreEqual(StreamEndpointMode.Legacy, resolved[0].Mode);
        }

        [Test]
        public void MalformedHbs2Tail_ParsesAsMalformedNotAbsent()
        {
            var ping = (seq: (ushort)1, timestamp: 42L, incarnation: 7UL);
            var info = HapbeatProtocol.ParsePongExtendedInfo(MalformedPong(ping.timestamp, ping.incarnation, 0));
            Assert.AreEqual(HapbeatProtocol.StreamLeaseTailStatus.Malformed, info.StreamLease.Status);
            Assert.IsFalse(info.StreamLease.IsPresent);
            Assert.AreEqual(Address, info.Address, "ordinary fields still parse");

            var legacy = HapbeatProtocol.ParsePongExtendedInfo(LegacyPong(42, Tail.OtherBytes));
            Assert.AreEqual(HapbeatProtocol.StreamLeaseTailStatus.Absent, legacy.StreamLease.Status);
            var valid = HapbeatProtocol.ParsePongExtendedInfo(V2Pong(42, 7, new HapbeatProtocol.StreamLeaseIdentity(1, 1)));
            Assert.AreEqual(HapbeatProtocol.StreamLeaseTailStatus.Valid, valid.StreamLease.Status);
        }

        // ---- helpers --------------------------------------------------------------

        private static IPEndPoint Endpoint(string ip) => new IPEndPoint(IPAddress.Parse(ip), 7700);

        private static void InvokeHandlePong(HapbeatClient client, ushort seq, byte[] payload, IPEndPoint sender)
        {
            Assert.IsNotNull(HandlePongMethod);
            HandlePongMethod.Invoke(client, new object[] { seq, payload, sender });
        }

        private static (ushort seq, long timestamp, ulong incarnation) SendPing(HapbeatClient client)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            return (seq, timestamp, incarnation);
        }

        private static (ushort seq, long timestamp, ulong incarnation) SendPingAfter(HapbeatClient client, long after)
        {
            (ushort seq, long timestamp, ulong incarnation) pending;
            do pending = SendPing(client);
            while (pending.timestamp <= after);
            return pending;
        }

        private static List<byte> OrdinaryFields(long timestamp, bool withVolume)
        {
            var payload = new List<byte>();
            AppendUInt64(payload, unchecked((ulong)timestamp));
            AppendUInt64(payload, 2);
            AppendCString(payload, "test-device");
            AppendCString(payload, Address);
            AppendCString(payload, "0.1.0");
            if (withVolume)
            {
                payload.Add(0); // volume_level
                payload.Add(0); // volume_wiper
            }
            return payload;
        }

        internal static byte[] LegacyPong(long timestamp, Tail tail)
        {
            var payload = OrdinaryFields(timestamp, tail != Tail.BeforeWiper);
            if (tail == Tail.OtherBytes) payload.AddRange(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
            return payload.ToArray();
        }

        internal static byte[] V2Pong(long timestamp, ulong incarnation,
            HapbeatProtocol.StreamLeaseIdentity lease, bool leaseValid = true, bool superseded = false)
        {
            var payload = OrdinaryFields(timestamp, true);
            payload.AddRange(new byte[] { (byte)'H', (byte)'B', (byte)'S', (byte)'2', 1,
                (byte)((leaseValid ? 1 : 0) | (superseded ? 2 : 0)), 0, 0 });
            AppendUInt64(payload, incarnation);
            AppendUInt64(payload, lease.DeviceBootId);
            AppendUInt32(payload, lease.LeaseTicket);
            AppendUInt32(payload, 0);
            return payload.ToArray();
        }

        private static byte[] MalformedPong(long timestamp, ulong incarnation, int defect)
        {
            var payload = new List<byte>(V2Pong(timestamp, incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0x99, 3)));
            int tail = payload.Count - 32;
            switch (defect)
            {
                case 0: payload.RemoveAt(payload.Count - 1); break; // 31 bytes
                case 1: payload[tail + 4] = 2; break;               // extension_version
                case 2: payload[tail + 6] = 1; break;               // reserved
                default: payload[tail + 5] |= 0x04; break;          // undefined flag bit
            }
            return payload.ToArray();
        }

        private static void AppendCString(List<byte> payload, string value)
        {
            payload.AddRange(Encoding.UTF8.GetBytes(value));
            payload.Add(0);
        }

        private static void AppendUInt32(List<byte> payload, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            payload.AddRange(bytes);
        }

        private static void AppendUInt64(List<byte> payload, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            payload.AddRange(bytes);
        }
    }
}
