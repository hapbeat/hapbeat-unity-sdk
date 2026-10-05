using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    public sealed class StreamSessionIdentityLifecycleTests
    {
        private sealed class RecordingSink : IHapbeatEndpointStreamPacketSink
        {
            public readonly List<HapbeatProtocol.StreamSessionIdentity> Begins =
                new List<HapbeatProtocol.StreamSessionIdentity>();
            public readonly List<HapbeatProtocol.StreamSessionIdentity> Ends =
                new List<HapbeatProtocol.StreamSessionIdentity>();
            private readonly object _lock = new object();

            // The mixer repeats an identical v2 BEGIN for loss protection (StreamBeginRepeatTests);
            // this sink records each session's BEGIN once.
            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target)
            {
                lock (_lock)
                {
                    if (Begins.Exists(x => x.Lease.Equals(identity.Lease) && x.Generation == identity.Generation))
                        return;
                    Begins.Add(identity);
                }
            }

            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength) { }

            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            {
                lock (_lock) Ends.Add(identity);
            }

            public int BeginCount { get { lock (_lock) return Begins.Count; } }
            public int EndCount { get { lock (_lock) return Ends.Count; } }
            public HapbeatProtocol.StreamSessionIdentity BeginAt(int index) { lock (_lock) return Begins[index]; }
            public HapbeatProtocol.StreamSessionIdentity EndAt(int index) { lock (_lock) return Ends[index]; }
        }

        [Test]
        public void LeaseUpdate_EndsOldIdentityBeforeBeginningNewIdentity()
        {
            var sink = new RecordingSink();
            var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
            var firstLease = new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7);
            var renewedLease = new HapbeatProtocol.StreamLeaseIdentity(0x1112131415161718UL, 8);
            HapbeatClient.StreamEndpoint activeEndpoint = new HapbeatClient.StreamEndpoint(
                endpoint, "player_1/pos_l_arm/group_1", firstLease);

            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { activeEndpoint }, () => 0.01f, _ => { });
            mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            WaitFor(() => sink.BeginCount == 1);

            HapbeatProtocol.StreamSessionIdentity oldIdentity = sink.BeginAt(0);
            activeEndpoint = new HapbeatClient.StreamEndpoint(endpoint,
                "player_1/pos_l_arm/group_1", renewedLease);
            mixer.ReconcileEndpoints();

            WaitFor(() => sink.EndCount == 1 && sink.BeginCount == 2);
            HapbeatProtocol.StreamSessionIdentity newIdentity = sink.BeginAt(1);
            Assert.AreEqual(oldIdentity.Lease.DeviceBootId, sink.EndAt(0).Lease.DeviceBootId);
            Assert.AreEqual(oldIdentity.Lease.LeaseTicket, sink.EndAt(0).Lease.LeaseTicket,
                "the old END must retain its original lease token");
            Assert.AreEqual(renewedLease.DeviceBootId, newIdentity.Lease.DeviceBootId);
            Assert.AreEqual(renewedLease.LeaseTicket, newIdentity.Lease.LeaseTicket);
            Assert.AreNotEqual(oldIdentity.Lease.DeviceBootId, newIdentity.Lease.DeviceBootId);
        }

        private static void WaitFor(Func<bool> predicate)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, 1000), "Timed out waiting for recording mixer packets.");
        }
    }
}
