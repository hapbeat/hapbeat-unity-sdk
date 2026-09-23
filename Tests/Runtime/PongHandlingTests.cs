using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    public sealed class PongHandlingTests
    {
        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod(
                "HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);
        [Test]
        public void TaillessUnsolicitedIdentityPong_UpdatesAddressWithoutErasingLeaseOrRttState()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
            const string oldAddress = "player_1/pos_chest/group_1";
            const string newAddress = "player_2/pos_neck/group_2";

            var initial = SendPendingLeasePing(client);
            var expectedLease = new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7);
            InvokeHandlePong(client, initial.seq, BuildPongPayload(initial.timestamp, 2, oldAddress,
                initial.incarnation, expectedLease, 0, leaseValid: true, superseded: false), sender);
            client.DispatchMainThreadCallbacks();

            int rttSampleCount = 0;
            int livenessUpdateCount = 0;
            long livenessRttUs = long.MinValue;
            client.OnPong += (_, __) => rttSampleCount++;
            client.OnPongFrom += (_, rttUs) =>
            {
                livenessUpdateCount++;
                livenessRttUs = rttUs;
            };

            var pending = SendPendingLeasePing(client);
            InvokeHandlePong(client, pending.seq, BuildPongPayload(0, 123456789, newAddress), sender);
            client.DispatchMainThreadCallbacks();

            Assert.AreEqual(0, rttSampleCount,
                "an unsolicited PONG must not publish an RTT/time-sync sample");
            Assert.AreEqual(1, livenessUpdateCount,
                "an unsolicited PONG must still update per-device liveness");
            Assert.AreEqual(0, livenessRttUs,
                "the liveness callback must not receive a fabricated RTT");
            Assert.IsTrue(client.TryGetPendingPing(pending.seq, out _, out _),
                "an unsolicited PONG must not consume a real pending PING with seq=0");

            Assert.IsEmpty(client.GetResolvedStreamEndpoints(oldAddress),
                "the old identity must no longer resolve to this endpoint");
            var endpoints = client.GetResolvedStreamEndpoints(newAddress);
            Assert.AreEqual(1, endpoints.Count,
                "the unsolicited PONG must refresh the endpoint registry identity");
            Assert.AreEqual(sender, endpoints[0].EndPoint);
            Assert.AreEqual(expectedLease.DeviceBootId, endpoints[0].Lease.DeviceBootId);
            Assert.AreEqual(expectedLease.LeaseTicket, endpoints[0].Lease.LeaseTicket,
                "a PONG without HBS2 must retain, not erase, the established lease");
        }

        [Test]
        public void NewerLeasePongWins_WhenAnOlderBootPongArrivesLate()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.11"), 7700);
            const string oldAddress = "player_1/pos_chest/group_1";
            const string newAddress = "player_2/pos_neck/group_2";

            var oldRequest = SendPendingLeasePing(client);
            var newRequest = SendPendingLeasePingAfter(client, oldRequest.timestamp);
            var newLease = new HapbeatProtocol.StreamLeaseIdentity(0xAABBCCDDEEFF0011UL, 21);
            InvokeHandlePong(client, newRequest.seq, BuildPongPayload(newRequest.timestamp, 2, newAddress,
                newRequest.incarnation, newLease, 0, leaseValid: true, superseded: false), sender);
            client.DispatchMainThreadCallbacks();
            int staleLivenessCallbacks = 0;
            client.OnPongFrom += (_, __) => staleLivenessCallbacks++;

            var oldLease = new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7);
            InvokeHandlePong(client, oldRequest.seq, BuildPongPayload(oldRequest.timestamp, 2, oldAddress,
                oldRequest.incarnation, oldLease, 0, leaseValid: true, superseded: false), sender);
            client.DispatchMainThreadCallbacks();

            var endpoints = client.GetResolvedStreamEndpoints(newAddress);
            Assert.AreEqual(1, endpoints.Count);
            Assert.AreEqual(newLease.DeviceBootId, endpoints[0].Lease.DeviceBootId);
            Assert.AreEqual(newLease.LeaseTicket, endpoints[0].Lease.LeaseTicket,
                "a delayed older PONG must not roll an endpoint back to its retired identity");
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(oldAddress),
                "a delayed older PONG must not roll the PONG address back to its old route");
            Assert.AreEqual(0, staleLivenessCallbacks,
                "a stale correlated PONG must not publish a liveness callback");
        }

        [Test]
        public void OneBroadcastPendingPing_CorrelatesLeasePongsFromMultipleDevices()
        {
            using var client = new HapbeatClient();
            var request = SendPendingLeasePing(client);
            InvokeHandlePong(client, request.seq, BuildPongPayload(request.timestamp, 2,
                "player_1/pos_l_arm/group_1", request.incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(11, 1), 0, true, false),
                new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700));
            InvokeHandlePong(client, request.seq, BuildPongPayload(request.timestamp, 2,
                "player_1/pos_r_arm/group_1", request.incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(12, 2), 0, true, false),
                new IPEndPoint(IPAddress.Parse("192.0.2.11"), 7700));

            Assert.AreEqual(2, client.GetResolvedStreamEndpoints("").Count,
                "the first PONG to a broadcast PING must not consume the pending correlation");
        }

        [Test]
        public void LeaseRouteMovesToNewIp_OnlyLatestCorrelatedPongRouteResolves()
        {
            using var client = new HapbeatClient();
            var oldSender = new IPEndPoint(IPAddress.Parse("192.0.2.20"), 7700);
            var newSender = new IPEndPoint(IPAddress.Parse("192.0.2.21"), 7700);
            const string address = "player_1/pos_l_arm/group_1";
            var lease = new HapbeatProtocol.StreamLeaseIdentity(51, 12);

            var oldRequest = SendPendingLeasePing(client);
            var newRequest = SendPendingLeasePingAfter(client, oldRequest.timestamp);
            InvokeHandlePong(client, oldRequest.seq, BuildPongPayload(oldRequest.timestamp, 2, address,
                oldRequest.incarnation, lease, 0, leaseValid: true, superseded: false), oldSender);
            InvokeHandlePong(client, newRequest.seq, BuildPongPayload(newRequest.timestamp, 2, address,
                newRequest.incarnation, lease, 0, leaseValid: true, superseded: false), newSender);

            var resolved = client.GetResolvedStreamEndpoints(address);
            Assert.AreEqual(1, resolved.Count,
                "a DHCP/IP migration for one lease must not expose both routes to the mixer");
            Assert.AreEqual(newSender, resolved[0].EndPoint,
                "the newest correlated PONG route must replace the prior IP for its lease");

            InvokeHandlePong(client, oldRequest.seq, BuildPongPayload(oldRequest.timestamp, 2, address,
                oldRequest.incarnation, lease, 0, leaseValid: true, superseded: false), oldSender);

            resolved = client.GetResolvedStreamEndpoints(address);
            Assert.AreEqual(1, resolved.Count);
            Assert.AreEqual(newSender, resolved[0].EndPoint,
                "a delayed old-IP PONG must not make the stale route selectable again");
        }

        [Test]
        public void SupersededLease_RemainsDeferredUntilExplicitReacquireUsesFreshIncarnation()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.12"), 7700);
            const string address = "player_1/pos_chest/group_1";
            var superseded = SendPendingLeasePing(client);
            InvokeHandlePong(client, superseded.seq, BuildPongPayload(superseded.timestamp, 2, address,
                superseded.incarnation, new HapbeatProtocol.StreamLeaseIdentity(21, 7), 8,
                leaseValid: true, superseded: true), sender);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(address));

            var periodic = SendPendingLeasePingAfter(client, superseded.timestamp);
            InvokeHandlePong(client, periodic.seq, BuildPongPayload(periodic.timestamp, 2, address,
                periodic.incarnation, new HapbeatProtocol.StreamLeaseIdentity(21, 7), 8,
                leaseValid: true, superseded: false), sender);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(address),
                "periodic discovery must not automatically reacquire a superseded lease");

            client.ReacquireStreamLeases();
            var reacquired = SendPendingLeasePing(client);
            InvokeHandlePong(client, reacquired.seq, BuildPongPayload(reacquired.timestamp, 2, address,
                reacquired.incarnation, new HapbeatProtocol.StreamLeaseIdentity(22, 9), 0,
                leaseValid: true, superseded: false), sender);
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(address).Count);
        }

        [Test]
        public void PriorIncarnationPong_AfterExplicitReacquireCannotChangeEndpointIdentity()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.13"), 7700);
            const string oldAddress = "player_1/pos_chest/group_1";
            const string newAddress = "player_3/pos_back/group_3";
            var oldRequest = SendPendingLeasePing(client);

            client.ReacquireStreamLeases();
            var newRequest = SendPendingLeasePing(client);
            var newLease = new HapbeatProtocol.StreamLeaseIdentity(31, 10);
            InvokeHandlePong(client, newRequest.seq, BuildPongPayload(newRequest.timestamp, 2, newAddress,
                newRequest.incarnation, newLease, 0, leaseValid: true, superseded: false), sender);

            InvokeHandlePong(client, oldRequest.seq, BuildPongPayload(oldRequest.timestamp, 2, oldAddress,
                oldRequest.incarnation, new HapbeatProtocol.StreamLeaseIdentity(30, 9), 0,
                leaseValid: true, superseded: false), sender);

            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(newAddress).Count);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(oldAddress),
                "a PONG for a retired client incarnation must be ignored before address mutation");
        }

        [TestCase(0, TestName = "Hbs2PongMissingPendingCannotChangeEndpointIdentity")]
        [TestCase(1, TestName = "Hbs2PongWithTimestampMismatchCannotChangeEndpointIdentity")]
        [TestCase(2, TestName = "Hbs2PongWithZeroTimestampCannotChangeEndpointIdentity")]
        public void Hbs2CorrelationFailure_DoesNotChangeAddressOrPublishLiveness(int failure)
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.14"), 7700);
            const string newAddress = "player_4/pos_l_arm/group_4";
            const string oldAddress = "player_1/pos_chest/group_1";
            var accepted = SendPendingLeasePing(client);
            var validLease = new HapbeatProtocol.StreamLeaseIdentity(41, 11);
            InvokeHandlePong(client, accepted.seq, BuildPongPayload(accepted.timestamp, 2, newAddress,
                accepted.incarnation, validLease, 0, leaseValid: true, superseded: false), sender);
            client.DispatchMainThreadCallbacks();
            int livenessCallbacks = 0;
            client.OnPongFrom += (_, __) => livenessCallbacks++;

            ushort seq = accepted.seq;
            long timestamp = accepted.timestamp;
            if (failure == 0)
            {
                seq = accepted.seq == ushort.MaxValue ? (ushort)(accepted.seq - 1) : (ushort)(accepted.seq + 1);
                timestamp++;
            }
            else if (failure == 1)
            {
                timestamp++;
            }
            else
            {
                timestamp = 0;
            }

            InvokeHandlePong(client, seq, BuildPongPayload(timestamp, 2, oldAddress,
                accepted.incarnation, new HapbeatProtocol.StreamLeaseIdentity(40, 10), 0,
                leaseValid: true, superseded: false), sender);
            client.DispatchMainThreadCallbacks();

            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(newAddress).Count);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(oldAddress));
            Assert.AreEqual(0, livenessCallbacks,
                "an HBS2 PONG with failed correlation must be discarded before liveness callbacks");
        }

        [Test]
        public void NormalPong_StillPublishesRttSample()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.11"), 7700);
            long rttUs = long.MinValue;
            int livenessUpdateCount = 0;
            client.OnPong += (sample, _) => rttUs = sample;
            client.OnPongFrom += (_, __) => livenessUpdateCount++;

            long pingTimestamp = client.GetLocalTimestampUs() - 1000;
            InvokeHandlePong(client, 18, BuildPongPayload(pingTimestamp, 2,
                "player_1/pos_chest/group_1"), sender);
            client.DispatchMainThreadCallbacks();

            Assert.GreaterOrEqual(rttUs, 0,
                "a normal PING response must continue to publish RTT");
            Assert.AreEqual(1, livenessUpdateCount);
        }

        private static void InvokeHandlePong(
            HapbeatClient client, ushort seq, byte[] payload, IPEndPoint sender)
        {
            Assert.IsNotNull(HandlePongMethod,
                "the receive path must expose the private PONG handler");
            HandlePongMethod.Invoke(client, new object[]
            {
                seq,
                payload,
                sender,
            });
        }

        private static (ushort seq, long timestamp, ulong incarnation) SendPendingLeasePing(HapbeatClient client)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            return (seq, timestamp, incarnation);
        }

        private static (ushort seq, long timestamp, ulong incarnation) SendPendingLeasePingAfter(
            HapbeatClient client, long timestamp)
        {
            (ushort seq, long nextTimestamp, ulong incarnation) pending;
            do
            {
                pending = SendPendingLeasePing(client);
            }
            while (pending.nextTimestamp <= timestamp);
            return pending;
        }

        private static byte[] BuildPongPayload(long timestamp, long serverTime, string address,
            ulong incarnation = 0, HapbeatProtocol.StreamLeaseIdentity lease = default,
            uint highWaterTicket = 0, bool leaseValid = false, bool superseded = false)
        {
            var payload = new List<byte>();
            AppendInt64(payload, timestamp);
            AppendInt64(payload, serverTime);
            AppendCString(payload, "test-device");
            AppendCString(payload, address);
            AppendCString(payload, "0.1.0");
            if (incarnation != 0)
            {
                payload.Add(0); // volume level
                payload.Add(0); // volume wiper
                payload.AddRange(new byte[] { (byte)'H', (byte)'B', (byte)'S', (byte)'2', 1,
                    (byte)((leaseValid ? 1 : 0) | (superseded ? 2 : 0)), 0, 0 });
                AppendUInt64(payload, incarnation);
                AppendUInt64(payload, lease.DeviceBootId);
                AppendUInt32(payload, lease.LeaseTicket);
                AppendUInt32(payload, highWaterTicket);
            }
            return payload.ToArray();
        }

        private static void AppendCString(List<byte> payload, string value)
        {
            payload.AddRange(Encoding.UTF8.GetBytes(value));
            payload.Add(0);
        }

        private static void AppendInt64(List<byte> payload, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            payload.AddRange(bytes);
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
