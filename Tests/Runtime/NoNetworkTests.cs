using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// HapbeatConfig.noNetwork / -HapbeatNoNetwork: no socket, no datagram, while the
    /// client's calls and events keep running. Every client here uses a recording
    /// datagram sink instead of a socket, so these tests never put a packet on the LAN.
    /// </summary>
    public sealed class NoNetworkTests
    {
        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class RecordingSink : IHapbeatDatagramSink
        {
            public readonly List<IPEndPoint> Destinations = new List<IPEndPoint>();

            public void Send(byte[] data, int length, IPEndPoint destination) =>
                Destinations.Add(destination);
        }

        private sealed class Observed
        {
            public readonly List<bool> ConnectionStates = new List<bool>();
            public int PongFromCount;
            public bool HadSocket;
        }

        [Test]
        public void NoNetwork_SendsNothingOnAnyPath_WhileCallsAndEventsRunAsUsual()
        {
            var sink = new RecordingSink();
            var client = new HapbeatClient(noNetwork: true, datagramSink: sink);
            Observed observed = DriveEverySendPath(client);

            Assert.AreEqual(0, sink.Destinations.Count, "No Network must not hand a single datagram to the transport");
            Assert.IsFalse(observed.HadSocket, "No Network must not open a socket");
            Assert.IsTrue(client.NoNetwork);
            CollectionAssert.AreEqual(new[] { true, false }, observed.ConnectionStates,
                "the client must still report connect and disconnect");
            Assert.AreEqual(1, observed.PongFromCount, "PONG callbacks must still be raised");
            Assert.AreEqual(ExpectedDatagrams, client.SuppressedDatagramCount,
                "every send path must have reached the drop point (and nothing past it)");
        }

        [Test]
        public void ControlRun_SameCallsWithNetworkOn_ReachTheSink()
        {
            // Proves the sink observes every send path that the no-network test drives:
            // without No Network the same calls produce ExpectedDatagrams datagrams.
            var sink = new RecordingSink();
            var client = new HapbeatClient(noNetwork: false, datagramSink: sink);
            Observed observed = DriveEverySendPath(client);

            Assert.AreEqual(ExpectedDatagrams, sink.Destinations.Count);
            Assert.AreEqual(0, client.SuppressedDatagramCount);
            Assert.IsFalse(observed.HadSocket, "a test sink stands in for the socket");
            CollectionAssert.AreEqual(new[] { true, false }, observed.ConnectionStates);
        }

        // ConnectStatus, discovery PING, PLAY/STOP/STOP_ALL broadcast, lease PING, CAPS_REQ,
        // PLAY unicast, Gen 2 BEGIN/DATA/END, Gen 1 BEGIN/DATA/END, ConnectStatus(false).
        private const int ExpectedDatagrams = 15;

        private static Observed DriveEverySendPath(HapbeatClient client)
        {
            var observed = new Observed();
            client.OnConnectionStateChanged += connected => observed.ConnectionStates.Add(connected);
            client.OnPongFrom += (_, __) => observed.PongFromCount++;

            client.OpenBroadcast(7700);
            observed.HadSocket |= client.HasSocket;
            client.DispatchMainThreadCallbacks();
            Assert.IsTrue(client.IsConnected);

            // Discovery / keep-alive, then commands while no device is known (broadcast).
            client.SendConnectStatus(true, 0, "app", "pc");
            client.SendPing();
            Assert.AreEqual(CommandSendResult.Broadcast, client.SendPlay("kit.hit", 0, 1f));
            client.SendStop("kit.hit");
            client.SendStopAll();

            // A device answers its lease request (injected; TEST-NET address): unicast
            // commands, a stream lease and a CAPS_REQ.
            var device = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
            const string address = "player_1/pos_chest/group_1";
            ushort seq = client.SendLeasePingTo(device);
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out ulong incarnation));
            var lease = new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7);
            HandlePongMethod.Invoke(client, new object[]
            {
                seq, BuildPongPayload(timestamp, address, incarnation, lease), device,
            });
            client.DispatchMainThreadCallbacks();
            client.RefreshStreamLeases(new[] { "player_1" });
            Assert.AreEqual(CommandSendResult.Unicast, client.SendPlay("kit.hit", 0, 1f, "player_1"));

            List<HapbeatClient.StreamEndpoint> endpoints = client.GetResolvedStreamEndpoints("player_1");
            Assert.AreEqual(1, endpoints.Count);
            Assert.IsTrue(client.TryAllocateStreamSessionIdentity(endpoints[0].EndPoint, endpoints[0].Lease,
                out HapbeatProtocol.StreamSessionIdentity identity));
            var pcm = new byte[64];
            client.SendStreamBeginTo(device, identity, 16000, 1, 0, 0, 1f, address);
            client.SendStreamDataTo(device, identity, 0, pcm, 0, pcm.Length);
            client.SendStreamEndTo(device, identity);

            client.SendLegacyStreamBeginTo(device, 16000, 1, 0, 0, 1f, address);
            client.SendLegacyStreamDataTo(device, 0, pcm, 0, pcm.Length);
            client.SendLegacyStreamEndTo(device);

            client.SendConnectStatus(false, 0, "app", "pc");
            observed.HadSocket |= client.HasSocket;

            client.Disconnect();
            client.DispatchMainThreadCallbacks();
            Assert.IsFalse(client.IsConnected);
            client.Dispose();
            return observed;
        }

        [Test]
        public void Discovery_NoNetwork_CompletesWithNoDevicesWithoutScanning()
        {
            var discovery = new HapbeatDiscovery { NoNetwork = true };
            List<HapbeatDevice> result = null;
            discovery.OnDiscoveryComplete += devices => result = devices;

            discovery.Discover(3000, 7700);
            Assert.IsFalse(discovery.IsDiscovering, "no scan thread must be started");
            discovery.DispatchCallbacks();

            Assert.IsNotNull(result, "OnDiscoveryComplete must still be raised");
            Assert.AreEqual(0, result.Count);
            discovery.Dispose();
        }

        [Test]
        public void CommandLineFlag_IsRecognizedCaseInsensitively()
        {
            Assert.IsTrue(HapbeatConfig.HasNoNetworkFlag(new[] { "Unity.exe", "-batchmode", "-HapbeatNoNetwork" }));
            Assert.IsTrue(HapbeatConfig.HasNoNetworkFlag(new[] { "Game.exe", "-hapbeatnonetwork" }));
            Assert.IsFalse(HapbeatConfig.HasNoNetworkFlag(new[] { "Game.exe", "-HapbeatNoNetworkX" }));
            Assert.IsFalse(HapbeatConfig.HasNoNetworkFlag(null));
        }

        [Test]
        public void ResolveNoNetwork_FollowsTheConfigField()
        {
            var config = ScriptableObject.CreateInstance<HapbeatConfig>();
            try
            {
                Assert.IsFalse(config.noNetwork, "sending stays on by default");
                config.noNetwork = true;
                Assert.IsTrue(HapbeatConfig.ResolveNoNetwork(config));
                config.noNetwork = false;
                Assert.AreEqual(HapbeatConfig.IsNoNetworkCommandLine, HapbeatConfig.ResolveNoNetwork(config));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        private static byte[] BuildPongPayload(long timestamp, string address, ulong incarnation,
            HapbeatProtocol.StreamLeaseIdentity lease)
        {
            var payload = new List<byte>();
            AppendLE(payload, BitConverter.GetBytes(timestamp));
            AppendLE(payload, BitConverter.GetBytes(2L));
            AppendCString(payload, "test-device");
            AppendCString(payload, address);
            AppendCString(payload, "0.5.0");
            payload.Add(0); // volume level
            payload.Add(0); // volume wiper
            payload.AddRange(new byte[] { (byte)'H', (byte)'B', (byte)'S', (byte)'2', 1, 1, 0, 0 });
            AppendLE(payload, BitConverter.GetBytes(incarnation));
            AppendLE(payload, BitConverter.GetBytes(lease.DeviceBootId));
            AppendLE(payload, BitConverter.GetBytes(lease.LeaseTicket));
            AppendLE(payload, BitConverter.GetBytes(0u));
            return payload.ToArray();
        }

        private static void AppendCString(List<byte> payload, string value)
        {
            payload.AddRange(Encoding.UTF8.GetBytes(value));
            payload.Add(0);
        }

        private static void AppendLE(List<byte> payload, byte[] bytes)
        {
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            payload.AddRange(bytes);
        }
    }
}
