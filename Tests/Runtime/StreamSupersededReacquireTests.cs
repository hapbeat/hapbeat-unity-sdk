using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// A superseded lease is never taken back automatically, but an explicit user stream
    /// start (Editor Test Play) reacquires it, and a reply whose ticket the receiver
    /// re-issued (lease_reissued, stream-session.md "Incarnation and takeover") clears it.
    /// Drives the private PONG handler directly; no socket is opened and nothing is sent.
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

        [Test]
        public void NewTicketWithoutReissued_KeepsTheSupersededDeferral()
        {
            // For example after this application's own lease entry expired: the device
            // hands out a fresh ticket, but another application may still be present.
            using var client = new HapbeatClient();
            ReplyWithLease(client, superseded: true);
            ReplyWithLease(client, superseded: false, ticket: 9);

            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address));
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.Superseded, client.GetStreamDeferReason(Address));
            Assert.AreEqual(HapbeatStreamLeaseState.Superseded, client.GetStreamEndpointInfo(Device.Address).LeaseState);
        }

        [Test]
        public void ReissuedReply_ClearsTheSupersededDeferral()
        {
            using var client = new HapbeatClient();
            int stateChanges = 0;
            client.OnStreamEndpointStateChanged += _ => stateChanges++;
            ReplyWithLease(client, superseded: true);
            client.DispatchMainThreadCallbacks();
            stateChanges = 0;

            // A re-issued ticket that is still superseded (or not valid) does not clear it.
            ReplyWithLease(client, superseded: true, ticket: 9, reissued: true);
            Assert.IsEmpty(client.GetResolvedStreamEndpoints(Address));

            ReplyWithLease(client, superseded: false, ticket: 10, reissued: true);
            var resolved = client.GetResolvedStreamEndpoints(Address);
            Assert.AreEqual(1, resolved.Count, "lease_valid, not superseded and lease_reissued clears the deferral");
            Assert.AreEqual(10u, resolved[0].Lease.LeaseTicket, "the re-issued ticket is adopted");
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(1, stateChanges, "the endpoint's lease state changed once (Superseded -> Valid)");
            Assert.AreEqual(HapbeatStreamLeaseState.Valid, client.GetStreamEndpointInfo(Device.Address).LeaseState);
        }

        private static ulong ReplyWithLease(HapbeatClient client, bool superseded, uint ticket = 2,
            bool reissued = false)
        {
            ushort seq = client.SendLeasePingTo(Device);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            Assert.IsNotNull(HandlePongMethod);
            HandlePongMethod.Invoke(client, new object[]
            {
                seq,
                StreamLegacyClassificationTests.V2Pong(timestamp, incarnation,
                    new HapbeatProtocol.StreamLeaseIdentity(Lease.DeviceBootId, ticket), true, superseded, reissued),
                Device,
            });
            return incarnation;
        }
    }
}
