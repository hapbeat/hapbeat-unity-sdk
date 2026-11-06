using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// <see cref="HapbeatManager.StopAll"/> and StreamClip (sdk-multi-stream.md §6.13): the STOP_ALL ends this app's
    /// sessions on the matching devices at once, so a StreamClip started right after begins a new session (a new
    /// generation) there instead of joining the one the device stopped, where every DATA is rejected. The manager
    /// runs No Network and its stream mixer is a test mixer whose packets go to a recording sink: nothing is sent.
    /// </summary>
    public sealed class StreamStopAllTests
    {
        private const string Address = "player_1/pos_chest/group_1";
        private const string OtherAddress = "player_1/pos_neck/group_1";
        private static readonly IPEndPoint Device = new IPEndPoint(IPAddress.Parse("192.0.2.95"), 7700);
        private static readonly IPEndPoint OtherDevice = new IPEndPoint(IPAddress.Parse("192.0.2.96"), 7700);
        private static readonly HapbeatProtocol.StreamLeaseIdentity Lease =
            new HapbeatProtocol.StreamLeaseIdentity(0xE101, 2);
        private static readonly HapbeatProtocol.StreamLeaseIdentity OtherLease =
            new HapbeatProtocol.StreamLeaseIdentity(0xE102, 1);

        private static readonly PropertyInfo InstanceProperty =
            typeof(HapbeatManager).GetProperty(nameof(HapbeatManager.Instance), BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo MixerField =
            typeof(HapbeatManager).GetField("_endpointStreamMixer", BindingFlags.Instance | BindingFlags.NonPublic);

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

            public List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> To(IPEndPoint endpoint)
            {
                var result = new List<(Kind, HapbeatProtocol.StreamSessionIdentity)>();
                lock (_lock)
                    foreach (var packet in _packets)
                        if (packet.endpoint == endpoint.ToString()) result.Add((packet.kind, packet.identity));
                return result;
            }

            public int Count(Kind kind, IPEndPoint endpoint)
            {
                int count = 0;
                foreach (var packet in To(endpoint)) if (packet.kind == kind) count++;
                return count;
            }

            public HapbeatProtocol.StreamSessionIdentity LastIdentity(IPEndPoint endpoint)
            {
                List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> packets = To(endpoint);
                return packets.Count > 0 ? packets[packets.Count - 1].identity : default;
            }
        }

        private GameObject _managerGo;
        private HapbeatManager _manager;
        private HapbeatConfig _config;
        private PacketSink _sink;
        private HapbeatEndpointStreamMixer _mixer;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNull(HapbeatManager.Instance, "precondition: no manager left over from another test");
            _config = ScriptableObject.CreateInstance<HapbeatConfig>();
            _config.noNetwork = true;
            _config.enableLogging = false;

            // Awake does not run in Edit Mode: set the config, register the singleton and initialize by hand
            // (Initialize connects through a No Network client).
            _managerGo = new GameObject("HapbeatManager (test)");
            _manager = _managerGo.AddComponent<HapbeatManager>();
            typeof(HapbeatManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_manager, _config);
            InstanceProperty.SetValue(null, _manager);
            typeof(HapbeatManager).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_manager, null);
            Assert.IsTrue(_manager.NoNetwork, "these tests must never send");
            Assert.IsFalse(_manager.Client.HasSocket);
            Assert.IsTrue(_manager.IsConnected, "StopAll runs on a connected manager");

            // The manager's stream mixer, with two resolved Stream Gen 2 devices and a recording sink.
            _sink = new PacketSink();
            _mixer = new HapbeatEndpointStreamMixer(_sink, target =>
            {
                var all = new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(Device, Address, Lease),
                    new HapbeatClient.StreamEndpoint(OtherDevice, OtherAddress, OtherLease),
                };
                return all.FindAll(e => HapbeatClient.AddressMatches(target, e.Address));
            }, () => 0.01f, _ => { });
            Assert.IsNotNull(MixerField);
            Assert.IsNull(MixerField.GetValue(_manager), "precondition: no StreamClip has created the mixer yet");
            MixerField.SetValue(_manager, _mixer);
        }

        [TearDown]
        public void TearDown()
        {
            if (_manager != null)
            {
                Assert.IsFalse(_manager.Client == null || _manager.Client.HasSocket, "no test may open a socket");
                typeof(HapbeatManager).GetMethod("Cleanup", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_manager, null);
            }
            _mixer?.Dispose();
            InstanceProperty.SetValue(null, null);
            if (_managerGo != null) Object.DestroyImmediate(_managerGo);
            if (_config != null) Object.DestroyImmediate(_config);
        }

        [Test]
        public void StopAll_EndsTheSessionOnTheMatchingDevice_AndTheNextStreamBeginsANewGeneration()
        {
            HapbeatStreamPlayback both = _mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => _sink.Count(Kind.Data, Device) > 5 && _sink.Count(Kind.Data, OtherDevice) > 5);
            HapbeatProtocol.StreamSessionIdentity stopped = _sink.LastIdentity(Device);
            HapbeatProtocol.StreamSessionIdentity other = _sink.LastIdentity(OtherDevice);

            _manager.StopAll("*/pos_chest");

            Assert.AreEqual(1, _sink.Count(Kind.End, Device), "StopAll ENDs the session on the matching device at once");
            Assert.AreEqual(stopped.Generation, _sink.LastIdentity(Device).Generation, "... the generation it played");
            int stoppedData = _sink.Count(Kind.Data, Device);
            Thread.Sleep(50);
            Assert.AreEqual(stoppedData, _sink.Count(Kind.Data, Device), "no more DATA to the stopped device");
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, both.Status, "the Playback keeps playing on the other device");
            Assert.AreEqual(0, _sink.Count(Kind.End, OtherDevice), "the other device's session is untouched");
            Assert.AreEqual(other.Generation, _sink.LastIdentity(OtherDevice).Generation);

            // A StreamClip started right after plays on the stopped device, under a new generation.
            HapbeatStreamPlayback fresh = _mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "*/pos_chest", true);
            WaitFor(() => _sink.Count(Kind.Data, Device) > stoppedData);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, fresh.Status);
            List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> toDevice = _sink.To(Device);
            int end = toDevice.FindIndex(p => p.kind == Kind.End);
            List<(Kind kind, HapbeatProtocol.StreamSessionIdentity identity)> after =
                toDevice.GetRange(end + 1, toDevice.Count - end - 1);
            Assert.AreEqual(Kind.Begin, after[0].kind, "the new session starts with a BEGIN");
            foreach (var packet in after)
            {
                Assert.AreEqual(Lease, packet.identity.Lease, "same lease");
                Assert.Greater(packet.identity.Generation, stopped.Generation, "a new generation, not the stopped one");
            }

            // StopAll() for every device: no device is left for either Playback.
            _manager.StopAll();
            Assert.AreEqual(2, _sink.Count(Kind.End, Device));
            Assert.AreEqual(1, _sink.Count(Kind.End, OtherDevice));
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Stopped, both.Status);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Stopped, fresh.Status);
            Assert.IsFalse(_mixer.IsStreaming);
        }

        [Test]
        public void StopAll_OnAnotherDevice_LeavesTheSessionAlone()
        {
            HapbeatStreamPlayback playback = _mixer.AddSamples(new float[16000], 16000, 1, 1f, 1f, "*/pos_chest", true);
            WaitFor(() => _sink.Count(Kind.Data, Device) > 5);
            HapbeatProtocol.StreamSessionIdentity running = _sink.LastIdentity(Device);

            _manager.StopAll("*/pos_neck");

            Assert.AreEqual(0, _sink.Count(Kind.End, Device));
            int data = _sink.Count(Kind.Data, Device);
            WaitFor(() => _sink.Count(Kind.Data, Device) > data);
            Assert.AreEqual(running.Generation, _sink.LastIdentity(Device).Generation);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, playback.Status);
            playback.Stop();
        }

        private static void WaitFor(System.Func<bool> predicate)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, 2000), "Timed out waiting for mixer packets.");
        }
    }
}
