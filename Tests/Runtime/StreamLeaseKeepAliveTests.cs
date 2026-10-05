using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// v2 firmware expires an idle stream lease 15 s after the last PING, so the keep-alive
    /// interval is capped while a v2 device is known. Drives the private PONG handler
    /// directly; no socket is opened and nothing is sent.
    /// </summary>
    public sealed class StreamLeaseKeepAliveTests
    {
        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);

        [TestCase(30f, true, 10f)]
        [TestCase(60f, true, 10f)]
        [TestCase(5f, true, 5f)]
        [TestCase(30f, false, 30f)]
        public void EffectivePingInterval_IsCappedOnlyWhileV2DevicesExist(float configured, bool hasV2, float expected)
        {
            Assert.AreEqual(expected, HapbeatManager.EffectivePingInterval(configured, hasV2));
            Assert.Less(HapbeatManager.MaxV2LeasePingIntervalSeconds, 15f,
                "the cap must stay inside the firmware's 15 s idle-lease TTL");
        }

        [Test]
        public void V2Pong_MarksClientAsHoldingStreamLeases_LegacyPongDoesNot()
        {
            using var client = new HapbeatClient();
            Assert.IsFalse(client.HasV2StreamEndpoints);

            ushort legacySeq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(legacySeq, out long legacyTimestamp, out _));
            Invoke(client, legacySeq, StreamLegacyClassificationTests.LegacyPong(
                legacyTimestamp, StreamLegacyClassificationTests.Tail.None), "192.0.2.40");
            Assert.IsFalse(client.HasV2StreamEndpoints, "a legacy device holds no lease to keep alive");

            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            Invoke(client, seq, StreamLegacyClassificationTests.V2Pong(timestamp, incarnation,
                new HapbeatProtocol.StreamLeaseIdentity(0x61, 3)), "192.0.2.41");
            Assert.IsTrue(client.HasV2StreamEndpoints);
        }

        private static void Invoke(HapbeatClient client, ushort seq, byte[] payload, string ip)
        {
            Assert.IsNotNull(HandlePongMethod);
            HandlePongMethod.Invoke(client, new object[] { seq, payload, new IPEndPoint(IPAddress.Parse(ip), 7700) });
        }
    }
}
