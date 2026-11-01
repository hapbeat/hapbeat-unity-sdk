using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// The value the SDK sends for a haptic's strength is
    /// <c>clamp(clip_gain × app_gain, 0, 1)</c> on every fire path (contracts
    /// kit-format.md "強さの式（正本）"): the PLAY <c>gain</c> on the wire and a stream
    /// source's PCM multiplier (<see cref="HapbeatStreamPlayback.Gain"/>). Only the final
    /// product is clamped, and a call-time gain multiplies clip_gain instead of replacing it.
    /// PLAYs go to a recording datagram sink and streams run on a No Network manager, so
    /// nothing leaves this PC.
    /// </summary>
    public sealed class ClipGainSendValueTests
    {
        private const float Eps = 1e-5f;

        // ── The formula ───────────────────────────────────────────────────

        [TestCase(0.5f, 1f, 0.5f, TestName = "ToSendGain_ClipHalf_AppOne_IsHalf")]
        [TestCase(0.5f, 2f, 1f, TestName = "ToSendGain_ClipHalf_AppTwo_IsOne")]
        [TestCase(0.8f, 2f, 1f, TestName = "ToSendGain_ClipPoint8_AppTwo_CapsAtOne")]
        [TestCase(0.5f, 0f, 0f, TestName = "ToSendGain_AppZero_IsZero")]
        [TestCase(1f, 1.5f, 1f, TestName = "ToSendGain_KitFree_AppOnePointFive_IsOne")]
        [TestCase(0.5f, -1f, 0f, TestName = "ToSendGain_NegativeApp_IsZero")]
        public void ToSendGain_IsClampedProduct(float clipGain, float appGain, float expected)
        {
            Assert.AreEqual(expected, HapbeatGain.ToSendGain(clipGain, appGain), Eps);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void ToSendGain_NonFinite_IsSilence(float product)
        {
            Assert.AreEqual(0f, HapbeatGain.ToSendGain(product));
        }

        [Test]
        public void PlayPayload_GainIsClampedOnTheWire()
        {
            Assert.AreEqual(1f, ReadPlayGain(HapbeatProtocol.BuildPlayPayload("k.e", 0L, 1.6f, "")), Eps);
            Assert.AreEqual(0f, ReadPlayGain(HapbeatProtocol.BuildPlayPayload("k.e", 0L, -0.5f, "")), Eps);
            Assert.AreEqual(0.6f, ReadPlayGain(HapbeatProtocol.BuildPlayPayload("k.e", 0L, 0.6f, "")), Eps);
        }

        [Test]
        public void StreamPlayback_ClampsOnlyTheProduct()
        {
            // Baseline above 1 is kept (not clamped) so a modulator below 1 still scales it.
            var playback = new HapbeatStreamPlayback(1.6f, 1.6f, false);
            Assert.AreEqual(1.6f, playback.BaselineGain, Eps);
            Assert.AreEqual(1f, playback.Gain, Eps, "initial gain caps at 1");
            playback.ApplyGainModulation(0.5f);
            Assert.AreEqual(0.8f, playback.Gain, Eps, "1.6 × 0.5, not min(1.6, 1) × 0.5");
            playback.ApplyGainModulation(2f);
            Assert.AreEqual(1f, playback.Gain, Eps);
            playback.Gain = -0.2f;
            Assert.AreEqual(0f, playback.Gain, Eps);
        }

        // ── Through the fire paths ────────────────────────────────────────

        private static readonly PropertyInfo InstanceProperty =
            typeof(HapbeatManager).GetProperty(nameof(HapbeatManager.Instance), BindingFlags.Static | BindingFlags.Public);
        private static readonly FieldInfo ClientField =
            typeof(HapbeatManager).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class RecordingSink : IHapbeatDatagramSink
        {
            public readonly List<byte[]> Datagrams = new List<byte[]>();

            public void Send(byte[] data, int length, IPEndPoint destination)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, 0, copy, 0, length);
                Datagrams.Add(copy);
            }
        }

        private GameObject _managerGo;
        private HapbeatManager _manager;
        private HapbeatConfig _config;
        private GameObject _triggerGo;
        private HapbeatEventMap _map;
        private AudioClip _clip;

        [SetUp]
        public void SetUp()
        {
            Assert.IsNull(HapbeatManager.Instance, "precondition: no manager left over from another test");

            _config = ScriptableObject.CreateInstance<HapbeatConfig>();
            _config.noNetwork = true;
            _config.enableLogging = false;

            // Awake does not run in Edit Mode: set the config, register the singleton
            // and initialize by hand (Initialize connects through a No Network client).
            _managerGo = new GameObject("HapbeatManager (test)");
            _manager = _managerGo.AddComponent<HapbeatManager>();
            typeof(HapbeatManager).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(_manager, _config);
            InstanceProperty.SetValue(null, _manager);
            typeof(HapbeatManager).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_manager, null);
            Assert.IsTrue(_manager.NoNetwork, "these tests must never send");
            Assert.IsFalse(_manager.Client.HasSocket);

            _map = ScriptableObject.CreateInstance<HapbeatEventMap>();
            _triggerGo = new GameObject("Trigger (test)");
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
            InstanceProperty.SetValue(null, null);
            if (_triggerGo != null) UnityEngine.Object.DestroyImmediate(_triggerGo);
            if (_managerGo != null) UnityEngine.Object.DestroyImmediate(_managerGo);
            if (_map != null) UnityEngine.Object.DestroyImmediate(_map);
            if (_config != null) UnityEngine.Object.DestroyImmediate(_config);
            if (_clip != null) UnityEngine.Object.DestroyImmediate(_clip);
        }

        /// <summary>
        /// Swap the manager's No Network client for one whose datagrams go to a recording
        /// sink (no socket is opened), so the PLAY payload can be read back.
        /// </summary>
        private RecordingSink CapturePlays()
        {
            var sink = new RecordingSink();
            var client = new HapbeatClient(noNetwork: false, datagramSink: sink);
            client.OpenBroadcast(7700);
            Assert.IsFalse(client.HasSocket, "a test sink stands in for the socket");
            _manager.Client.Dispose();
            ClientField.SetValue(_manager, client);
            sink.Datagrams.Clear();
            return sink;
        }

        private HapbeatUnityEventTrigger AddTrigger(HapbeatEventEntry entry)
        {
            _map.entries.Add(entry);
            var trigger = _triggerGo.AddComponent<HapbeatUnityEventTrigger>();
            trigger.EditorSetupEntry(_map, entry.id);
            return trigger;
        }

        // A negative offset cancels this device's AV offset (at most 300 ms), so the
        // entry is sent at once instead of from a coroutine.
        private static HapbeatEventEntry CommandEntry(float gain, float clipGain)
        {
            var entry = new HapbeatEventEntry
            {
                mode = HapticMode.Command, category = "test", eventName = "hit",
                gain = gain, delayOffsetSeconds = -1f,
            };
            entry.SetCachedManifestClipGain(clipGain);
            return entry;
        }

        private HapbeatEventEntry StreamEntry(float gain, float clipGain)
        {
            _clip = AudioClip.Create("hum", 1600, 1, 16000, false);
            var entry = new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip, streamClip = _clip, gain = gain, delayOffsetSeconds = -1f,
            };
            if (clipGain >= 0f) entry.SetCachedManifestClipGain(clipGain);
            return entry;
        }

        private static float SinglePlayGain(RecordingSink sink)
        {
            var plays = sink.Datagrams.FindAll(d => d.Length > HapbeatProtocol.HEADER_SIZE && d[3] == HapbeatProtocol.CMD_PLAY);
            Assert.AreEqual(1, plays.Count, "exactly one PLAY must have been sent");
            return ReadPlayGain(plays[0]);
        }

        [Test]
        public void FireWithGain_MultipliesClipGainAndEntryGain_InsteadOfReplacingThem()
        {
            var trigger = AddTrigger(CommandEntry(gain: 1.5f, clipGain: 0.8f));
            var sink = CapturePlays();

            trigger.FireWithGain(0.5f);

            Assert.AreEqual(0.6f, SinglePlayGain(sink), Eps, "0.8 × 1.5 × 0.5");
        }

        [Test]
        public void Fire_Command_EntryGainTimesTriggerMultiplierTimesClipGain()
        {
            var trigger = AddTrigger(CommandEntry(gain: 1.5f, clipGain: 0.8f));
            trigger.GainMultiplier = 0.5f;
            var sink = CapturePlays();

            trigger.Fire();

            Assert.AreEqual(0.6f, SinglePlayGain(sink), Eps, "0.8 × 1.5 × 0.5");
        }

        [Test]
        public void Fire_Command_ProductAboveOne_CapsAtOne()
        {
            var trigger = AddTrigger(CommandEntry(gain: 2f, clipGain: 0.8f));
            var sink = CapturePlays();

            trigger.Fire();

            Assert.AreEqual(1f, SinglePlayGain(sink), Eps, "0.8 × 2 = 1.6 is sent as 1");
        }

        [Test]
        public void DirectPlay_KnowsNoClipGain_SendsClampedGain()
        {
            var sink = CapturePlays();

            _manager.Play("test.hit", 1.5f);

            Assert.AreEqual(1f, SinglePlayGain(sink), Eps, "clip_gain 1 × 1.5");
        }

        [Test]
        public void Fire_StreamClip_SourceGainIsClampedProduct()
        {
            var trigger = AddTrigger(StreamEntry(gain: 1.5f, clipGain: 0.8f));
            trigger.GainMultiplier = 0.5f;

            trigger.Fire();

            Assert.IsNotNull(trigger.ActivePlayback);
            Assert.AreEqual(1.2f, trigger.ActivePlayback.BaselineGain, Eps, "baseline = clip_gain × entry.gain, unclamped");
            Assert.AreEqual(0.6f, trigger.ActivePlayback.Gain, Eps, "0.8 × 1.5 × 0.5");

            trigger.GainMultiplier = 2f;
            Assert.AreEqual(1f, trigger.ActivePlayback.Gain, Eps, "1.2 × 2 caps at 1");
        }

        [Test]
        public void Fire_KitFreeStreamClip_ClipGainOne_CapsAtOne()
        {
            var trigger = AddTrigger(StreamEntry(gain: 1.5f, clipGain: -1f));

            trigger.Fire();

            Assert.IsNotNull(trigger.ActivePlayback);
            Assert.AreEqual(1f, trigger.ActivePlayback.Gain, Eps, "Kit-free: clip_gain 1 × 1.5");
        }

        // The PLAY payload (and a full datagram, header + payload) ends with
        // gain (float32 LE) + pan (float32 LE).
        private static float ReadPlayGain(byte[] buf)
        {
            byte[] bytes = new byte[4];
            Buffer.BlockCopy(buf, buf.Length - 8, bytes, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
