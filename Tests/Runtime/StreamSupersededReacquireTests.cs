using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// A superseded lease is never taken back automatically, but an explicit user stream
    /// start (Editor Test Play) reacquires it. Drives the private PONG handler directly;
    /// no socket is opened and nothing is sent.
    /// </summary>
    public sealed class StreamSupersededReacquireTests
    {
        private const string Address = "player_1/pos_chest/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.70"), 7700);
        private static readonly HapbeatProtocol.StreamLeaseIdentity Lease =
            new HapbeatProtocol.StreamLeaseIdentity(0x81, 2);

        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);

        [Test]
        public void ExplicitStart_ReacquiresOnlyWhenSuperseded()
        {
            using var client = new HapbeatClient();
            ulong incarnation = ReplyWithLease(client, superseded: true);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address), "a superseded lease is not usable");

            // Periodic discovery keeps the superseded latch: no implicit takeover.
            ReplyWithLease(client, superseded: false);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address), "discovery must not seize the device");

            Assert.IsTrue(client.ReacquireStreamLeasesIfSuperseded(), "an explicit start reacquires");
            ulong renewed = ReplyWithLease(client, superseded: false, ticket: 3);
            Assert.AreNotEqual(incarnation, renewed, "reacquisition uses a fresh client incarnation");
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(Address).Count);

            Assert.IsFalse(client.ReacquireStreamLeasesIfSuperseded(),
                "a usable lease is kept; an explicit start must not churn the incarnation");
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(Address).Count);
        }

        private static ulong ReplyWithLease(HapbeatClient client, bool superseded, uint ticket = 2)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            Assert.IsNotNull(HandlePongMethod);
            HandlePongMethod.Invoke(client, new object[]
            {
                seq,
                StreamLegacyClassificationTests.V2Pong(timestamp, incarnation,
                    new HapbeatProtocol.StreamLeaseIdentity(Lease.DeviceBootId, ticket), true, superseded),
                Device,
            });
            return incarnation;
        }
    }
}
