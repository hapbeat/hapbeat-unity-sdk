using System;
using System.Collections.Concurrent;
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
        private static readonly FieldInfo PendingPingsField =
            typeof(HapbeatClient).GetField(
                "_pendingPings", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void UnsolicitedIdentityPong_UpdatesEndpointAndLivenessWithoutRttSample()
        {
            using var client = new HapbeatClient();
            var sender = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
            const string oldAddress = "player_1/pos_chest/group_1";
            const string newAddress = "player_2/pos_neck/group_2";

            InvokeHandlePong(client, 17, BuildPongPayload(1, 2, oldAddress), sender);
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

            ushort pendingSeq = client.SendPing();
            Assert.AreEqual(0, pendingSeq,
                "the first PING must exercise the valid sequence-zero case");
            InvokeHandlePong(client, 0, BuildPongPayload(0, 123456789, newAddress), sender);
            client.DispatchMainThreadCallbacks();

            Assert.AreEqual(0, rttSampleCount,
                "an unsolicited PONG must not publish an RTT/time-sync sample");
            Assert.AreEqual(1, livenessUpdateCount,
                "an unsolicited PONG must still update per-device liveness");
            Assert.AreEqual(0, livenessRttUs,
                "the liveness callback must not receive a fabricated RTT");
            Assert.IsNotNull(PendingPingsField,
                "the client must retain its pending-PING bookkeeping");
            var pendingPings = PendingPingsField.GetValue(client)
                as ConcurrentDictionary<ushort, long>;
            Assert.IsNotNull(pendingPings);
            Assert.IsTrue(pendingPings.ContainsKey(pendingSeq),
                "an unsolicited PONG must not consume a real pending PING with seq=0");

            Assert.IsEmpty(client.GetResolvedStreamEndpoints(oldAddress),
                "the old identity must no longer resolve to this endpoint");
            var endpoints = client.GetResolvedStreamEndpoints(newAddress);
            Assert.AreEqual(1, endpoints.Count,
                "the unsolicited PONG must refresh the endpoint registry identity");
            Assert.AreEqual(sender, endpoints[0].EndPoint);
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

        private static byte[] BuildPongPayload(long timestamp, long serverTime, string address)
        {
            var payload = new List<byte>();
            AppendInt64(payload, timestamp);
            AppendInt64(payload, serverTime);
            AppendCString(payload, "test-device");
            AppendCString(payload, address);
            AppendCString(payload, "0.1.0");
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
    }
}
