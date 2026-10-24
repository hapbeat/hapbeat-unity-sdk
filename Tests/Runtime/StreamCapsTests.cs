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
    /// CAPS_REQ / CAPS (message-format.md §0x12, §0x13; versioning.md §3.3): requested by
    /// exact unicast after a device's first matched PONG and after its firmware or boot
    /// changes, repeated 500 ms apart three times in total, then "no CAPS". A device
    /// whose generation is above this SDK's major draws one warning. The client hands
    /// datagrams to a recording sink (no socket); replies are injected through the
    /// private receive handlers and the schedule is driven with explicit timestamps.
    /// </summary>
    public sealed class StreamCapsTests
    {
        private const string Address = "player_1/pos_chest/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.100"), 7700);
        private const long Ms = 1000;

        private static readonly MethodInfo HandlePongMethod =
            typeof(HapbeatClient).GetMethod("HandlePong", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo HandleCapsMethod =
            typeof(HapbeatClient).GetMethod("HandleCaps", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WarnedNewerDevice =
            typeof(HapbeatClient).GetField("s_warnedNewerDevice", BindingFlags.Static | BindingFlags.NonPublic);

        private sealed class Sink : IHapbeatDatagramSink
        {
            public readonly List<(IPEndPoint destination, byte[] packet)> Sent = new List<(IPEndPoint, byte[])>();
            public void Send(byte[] data, int length, IPEndPoint destination) =>
                Sent.Add((destination, (byte[])data.Clone()));

            public List<(IPEndPoint destination, ushort seq, byte[] packet)> CapsRequests()
            {
                var requests = new List<(IPEndPoint, ushort, byte[])>();
                foreach (var sent in Sent)
                    if (sent.packet[3] == HapbeatProtocol.CMD_CAPS_REQ)
                        requests.Add((sent.destination, (ushort)(sent.packet[4] | (sent.packet[5] << 8)), sent.packet));
                return requests;
            }
        }

        [Test]
        public void FirstMatchedPong_SendsOneUnicastCapsRequest()
        {
            var sink = new Sink();
            using var client = Open(sink);
            Assert.IsEmpty(sink.CapsRequests(), "no CAPS_REQ before a matched PONG");
            MatchedPong(client, "1.0.0");
            long t0 = client.GetLocalTimestampUs();
            client.RefreshStreamLeases(null, false, t0);

            var requests = sink.CapsRequests();
            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual(Device, requests[0].destination, "exact unicast, never broadcast");
            Assert.AreEqual(9, requests[0].packet.Length);
            Assert.AreEqual(HapbeatProtocol.CAPS_VERSION, requests[0].packet[8]);
        }

        [Test]
        public void NoAnswerToThreeRequests_MeansNoCaps_UntilTheFirmwareChanges()
        {
            var sink = new Sink();
            using var client = Open(sink);
            MatchedPong(client, "0.5.0");
            long t0 = client.GetLocalTimestampUs();
            client.RefreshStreamLeases(null, false, t0);
            client.RefreshStreamLeases(null, false, t0 + 400 * Ms);
            Assert.AreEqual(1, sink.CapsRequests().Count, "500 ms apart");
            client.RefreshStreamLeases(null, false, t0 + 500 * Ms);
            client.RefreshStreamLeases(null, false, t0 + 1000 * Ms);
            Assert.AreEqual(3, sink.CapsRequests().Count);
            Assert.AreEqual(-1, client.GetStreamEndpointInfo(Device.Address).CapsGeneration, "still waiting");
            client.RefreshStreamLeases(null, false, t0 + 1500 * Ms);
            Assert.AreEqual(3, sink.CapsRequests().Count, "three in total");
            Assert.AreEqual(0, client.GetStreamEndpointInfo(Device.Address).CapsGeneration, "no CAPS: firmware 0.x");

            // The same boot keeps that answer; a firmware change asks again.
            MatchedPong(client, "0.5.0");
            client.RefreshStreamLeases(null, false, t0 + 5000 * Ms);
            Assert.AreEqual(3, sink.CapsRequests().Count);
            MatchedPong(client, "1.0.0");
            client.RefreshStreamLeases(null, false, t0 + 5000 * Ms);
            Assert.AreEqual(4, sink.CapsRequests().Count);
        }

        [Test]
        public void Caps_IsAcceptedOnlyForARequestSeqFromThatDevice()
        {
            var sink = new Sink();
            using var client = Open(sink);
            MatchedPong(client, "1.0.0");
            client.RefreshStreamLeases(null);
            ushort seq = sink.CapsRequests()[0].seq;

            InvokeCaps(client, unchecked((ushort)(seq + 1)), Caps(generation: 1, hapticChannels: 1), Device);
            InvokeCaps(client, seq, Caps(generation: 1, hapticChannels: 1), new IPEndPoint(IPAddress.Parse("192.0.2.101"), 7700));
            Assert.AreEqual(-1, client.GetStreamEndpointInfo(Device.Address).CapsGeneration);

            int resolved = 0;
            client.OnCapsResolved += _ => resolved++;
            InvokeCaps(client, seq, Caps(generation: 1, hapticChannels: 1), Device);
            client.DispatchMainThreadCallbacks();
            Assert.AreEqual(1, resolved);
            Assert.AreEqual(1, client.GetStreamEndpointInfo(Device.Address).CapsGeneration);
            Assert.IsTrue(client.TryGetCaps(Device.Address, out HapbeatProtocol.CapsInfo caps));
            Assert.AreEqual(1, caps.HapticChannels);
        }

        [Test]
        public void HapticChannels_ReachesTheResolvedStreamEndpoint()
        {
            var sink = new Sink();
            using var client = Open(sink);
            ushort ping = client.SendLeasePingTo(Device);
            Assert.IsTrue(client.TryGetPendingPing(ping, out long timestamp, out ulong incarnation));
            HandlePongMethod.Invoke(client, new object[] { ping, StreamLegacyClassificationTests.V2Pong(timestamp,
                incarnation, new HapbeatProtocol.StreamLeaseIdentity(0xCA, 1), firmware: "1.0.0"), Device });
            Assert.AreEqual(-1, client.GetResolvedStreamEndpoints(Address)[0].HapticChannels, "unknown before CAPS");
            client.RefreshStreamLeases(new[] { Address }); // still a lease target, so the lease stays usable
            InvokeCaps(client, sink.CapsRequests()[0].seq, Caps(generation: 1, hapticChannels: 1), Device);
            Assert.AreEqual(1, client.GetResolvedStreamEndpoints(Address)[0].HapticChannels);
        }

        [Test]
        public void NewerGenerationThanThisSdk_WarnsOncePerRun()
        {
            WarnedNewerDevice.SetValue(null, 0);
            var sink = new Sink();
            using var client = Open(sink);
            int warnings = 0;
            Application.LogCallback count = (message, _, type) =>
            {
                if (type == LogType.Warning && message.Contains("newer than this SDK")) warnings++;
            };
            Application.logMessageReceived += count;
            try
            {
                MatchedPong(client, "2.0.0");
                client.RefreshStreamLeases(null);
                InvokeCaps(client, sink.CapsRequests()[0].seq,
                    Caps(generation: HapbeatProtocol.SdkMajorVersion + 1, hapticChannels: 2), Device);
                Assert.AreEqual(1, warnings);

                MatchedPong(client, "2.0.1"); // firmware change: asked again, answered again
                client.RefreshStreamLeases(null);
                var requests = sink.CapsRequests();
                InvokeCaps(client, requests[requests.Count - 1].seq,
                    Caps(generation: HapbeatProtocol.SdkMajorVersion + 1, hapticChannels: 2), Device);
                Assert.AreEqual(1, warnings, "once per application run");
                Assert.AreEqual(HapbeatProtocol.SdkMajorVersion + 1,
                    client.GetStreamEndpointInfo(Device.Address).CapsGeneration);
            }
            finally
            {
                Application.logMessageReceived -= count;
            }
        }

        [Test]
        public void SdkMajorVersion_MatchesThePackageVersion()
        {
            string[] guids = UnityEditor.AssetDatabase.FindAssets("package", new[] { "Packages/com.hapbeat.sdk" });
            string json = null;
            foreach (string guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (path == "Packages/com.hapbeat.sdk/package.json")
                    json = System.IO.File.ReadAllText(System.IO.Path.GetFullPath(path));
            }
            Assume.That(json, Is.Not.Null, "package.json is reachable through the package path");
            int at = json.IndexOf("\"version\"", StringComparison.Ordinal);
            int quote = json.IndexOf('"', json.IndexOf(':', at) + 1);
            string version = json.Substring(quote + 1, json.IndexOf('"', quote + 1) - quote - 1);
            Assert.AreEqual(HapbeatProtocol.SdkMajorVersion.ToString(), version.Split('.')[0]);
        }

        // ---- helpers ----------------------------------------------------------------

        private static HapbeatClient Open(Sink sink)
        {
            var client = new HapbeatClient(false, sink);
            client.OpenBroadcast(7700);
            client.DispatchMainThreadCallbacks();
            return client;
        }

        // A matched reply to an 8-byte discovery PING (that counts for CAPS too).
        private static void MatchedPong(HapbeatClient client, string firmware)
        {
            ushort seq = client.SendPing();
            Assert.IsTrue(client.TryGetPendingPing(seq, out long timestamp, out _));
            var payload = new List<byte>();
            payload.AddRange(BitConverter.GetBytes(timestamp));
            payload.AddRange(BitConverter.GetBytes(2L));
            AppendCString(payload, "test-device");
            AppendCString(payload, Address);
            AppendCString(payload, firmware);
            payload.Add(0);
            payload.Add(0);
            Assert.IsNotNull(HandlePongMethod);
            HandlePongMethod.Invoke(client, new object[] { seq, payload.ToArray(), Device });
            client.DispatchMainThreadCallbacks();
        }

        private static byte[] Caps(int generation, int hapticChannels) => new byte[]
        {
            1, 0,                      // caps_version, reserved
            0x01, 1, (byte)generation, // generation
            0x06, 1, (byte)hapticChannels,
        };

        private static void InvokeCaps(HapbeatClient client, ushort seq, byte[] payload, IPEndPoint from)
        {
            Assert.IsNotNull(HandleCapsMethod);
            HandleCapsMethod.Invoke(client, new object[] { seq, payload, from });
        }

        private static void AppendCString(List<byte> payload, string value)
        {
            payload.AddRange(Encoding.UTF8.GetBytes(value));
            payload.Add(0);
        }
    }
}
