using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// <see cref="HapbeatManager.Fired"/>: one report per fire, raised when the game
    /// fires (before any delayed send), with the entry id for trigger fires and null for
    /// direct calls. The manager runs in No Network mode, so nothing leaves this PC.
    /// </summary>
    public sealed class FiredEventTests
    {
        private static readonly PropertyInfo InstanceProperty =
            typeof(HapbeatManager).GetProperty(nameof(HapbeatManager.Instance), BindingFlags.Static | BindingFlags.Public);

        private GameObject _managerGo;
        private HapbeatManager _manager;
        private HapbeatConfig _config;
        private GameObject _triggerGo;
        private HapbeatEventMap _map;
        private AudioClip _clip;
        private readonly List<HapbeatFired> _fired = new List<HapbeatFired>();

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
            Assert.IsTrue(_manager.Client.NoNetwork);
            Assert.IsFalse(_manager.Client.HasSocket, "No Network must not open a socket");
            Assert.IsTrue(_manager.IsConnected);

            _map = ScriptableObject.CreateInstance<HapbeatEventMap>();
            _triggerGo = new GameObject("Trigger (test)");
            _fired.Clear();
            HapbeatManager.Fired += OnFired;
        }

        [TearDown]
        public void TearDown()
        {
            HapbeatManager.Fired -= OnFired;
            if (_manager != null)
            {
                Assert.IsFalse(_manager.Client == null || _manager.Client.HasSocket, "No Network must not open a socket");
                typeof(HapbeatManager).GetMethod("Cleanup", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_manager, null);
            }
            InstanceProperty.SetValue(null, null);
            if (_triggerGo != null) Object.DestroyImmediate(_triggerGo);
            if (_managerGo != null) Object.DestroyImmediate(_managerGo);
            if (_map != null) Object.DestroyImmediate(_map);
            if (_config != null) Object.DestroyImmediate(_config);
            if (_clip != null) Object.DestroyImmediate(_clip);
        }

        private void OnFired(HapbeatFired fired) => _fired.Add(fired);

        private HapbeatEventEntry AddCommandEntry(float delayOffsetSeconds)
        {
            var entry = new HapbeatEventEntry
            {
                mode = HapticMode.Command, category = "test", eventName = "hit", displayName = "Hit",
                gain = 0.5f, target = "player_1/pos_neck", loop = false,
                // A negative offset cancels this device's AV offset (at most 300 ms), so
                // the entry is sent at once; a positive one defers it.
                delayOffsetSeconds = delayOffsetSeconds,
            };
            _map.entries.Add(entry);
            return entry;
        }

        private HapbeatUnityEventTrigger AddTrigger(HapbeatEventEntry entry)
        {
            var trigger = _triggerGo.AddComponent<HapbeatUnityEventTrigger>();
            trigger.EditorSetupEntry(_map, entry.id);
            return trigger;
        }

        [Test]
        public void TriggerFire_Command_RaisesOnceWithTheEntry_AndIsNotReportedAgainByPlay()
        {
            var entry = AddCommandEntry(-1f);
            var trigger = AddTrigger(entry);
            trigger.GainMultiplier = 0.8f;
            long datagramsBefore = _manager.Client.SuppressedDatagramCount;

            trigger.Fire();

            Assert.AreEqual(1, _fired.Count, "one fire through trigger → manager must be reported once");
            HapbeatFired f = _fired[0];
            Assert.AreEqual(entry.id, f.entryId);
            Assert.AreEqual(entry.eventId, f.eventId);
            Assert.AreEqual("Hit", f.displayName);
            Assert.AreEqual(HapticMode.Command, f.mode);
            Assert.AreEqual(0.8f, f.gain, 1e-6f, "gain = the multiplier the trigger applied on top of the entry");
            Assert.AreEqual("player_1/pos_neck", f.target);
            Assert.IsFalse(f.loop);
            Assert.AreSame(_map, f.map);
            Assert.AreSame(trigger, f.sender);
            Assert.AreEqual(Time.frameCount, f.frame);
            Assert.Greater(_manager.Client.SuppressedDatagramCount, datagramsBefore,
                "the PLAY must have reached the (No Network) drop point, i.e. Play really ran");
        }

        [Test]
        public void TriggerFire_StreamClipLoop_RaisesOnceWithLoopAndTarget()
        {
            _clip = AudioClip.Create("loop", 1600, 1, 16000, false);
            var entry = new HapbeatEventEntry
            {
                mode = HapticMode.StreamClip, streamClip = _clip, displayName = "Hum",
                gain = 0.6f, target = "*/pos_chest", loop = true, delayOffsetSeconds = -1f,
            };
            _map.entries.Add(entry);
            var trigger = AddTrigger(entry);
            trigger.GainMultiplier = 1.5f;

            trigger.Fire();

            Assert.AreEqual(1, _fired.Count);
            HapbeatFired f = _fired[0];
            Assert.AreEqual(entry.id, f.entryId);
            Assert.AreEqual(HapticMode.StreamClip, f.mode);
            Assert.AreEqual(1.5f, f.gain, 1e-6f);
            Assert.AreEqual("*/pos_chest", f.target);
            Assert.IsTrue(f.loop);
            Assert.IsNotNull(trigger.ActivePlayback, "the stream must have been started (unreported) by the trigger");
        }

        [Test]
        public void TriggerFire_WithEntryDelay_RaisesAtCallTime_BeforeTheSend()
        {
            var entry = AddCommandEntry(0.5f);
            var trigger = AddTrigger(entry);
            long datagramsBefore = _manager.Client.SuppressedDatagramCount;
            int frame = Time.frameCount;

            trigger.Fire();

            Assert.AreEqual(1, _fired.Count, "reported when the game fired, not when the delayed send runs");
            Assert.AreEqual(entry.id, _fired[0].entryId);
            Assert.AreEqual(frame, _fired[0].frame);
            Assert.AreEqual(datagramsBefore, _manager.Client.SuppressedDatagramCount,
                "the PLAY itself is still waiting on the delay");
        }

        [Test]
        public void TriggerFireAfter_RaisesAtCallTime()
        {
            var entry = AddCommandEntry(0f);
            var trigger = AddTrigger(entry);
            long datagramsBefore = _manager.Client.SuppressedDatagramCount;

            trigger.FireAfter(0.5f);

            Assert.AreEqual(1, _fired.Count);
            Assert.AreEqual(entry.id, _fired[0].entryId);
            Assert.AreEqual(Time.frameCount, _fired[0].frame);
            Assert.AreEqual(datagramsBefore, _manager.Client.SuppressedDatagramCount);
        }

        [Test]
        public void TriggerFire_Rejected_RaisesNothing()
        {
            var entry = AddCommandEntry(-1f);
            var trigger = AddTrigger(entry);
            trigger.TriggerEnabled = false;

            trigger.Fire();

            Assert.AreEqual(0, _fired.Count, "a fire the trigger rejects is not reported");
        }

        [Test]
        public void DirectPlay_RaisesOnceWithoutEntry()
        {
            long datagramsBefore = _manager.Client.SuppressedDatagramCount;

            _manager.Play("test.hit", 0.7f, "Direct", "player_2");

            Assert.AreEqual(1, _fired.Count);
            HapbeatFired f = _fired[0];
            Assert.IsNull(f.entryId);
            Assert.AreEqual("test.hit", f.eventId);
            Assert.AreEqual("Direct", f.displayName);
            Assert.AreEqual(HapticMode.Command, f.mode);
            Assert.AreEqual(0.7f, f.gain, 1e-6f);
            Assert.AreEqual("player_2", f.target);
            Assert.IsNull(f.map);
            Assert.IsNull(f.sender);
            Assert.Greater(_manager.Client.SuppressedDatagramCount, datagramsBefore);
        }

        [Test]
        public void DirectStreamAudioClip_RaisesOnceWithoutEntry()
        {
            _clip = AudioClip.Create("direct", 1600, 1, 16000, false);

            var playback = _manager.StreamAudioClip(_clip, 0.4f, "player_1", loop: true);

            Assert.IsNotNull(playback);
            Assert.AreEqual(1, _fired.Count);
            HapbeatFired f = _fired[0];
            Assert.IsNull(f.entryId);
            Assert.AreEqual(HapticMode.StreamClip, f.mode);
            Assert.AreEqual("direct", f.displayName);
            Assert.AreEqual(0.4f, f.gain, 1e-6f);
            Assert.AreEqual("player_1", f.target);
            Assert.IsTrue(f.loop);
        }

        [Test]
        public void ThrowingSubscriber_DoesNotBreakTheFire()
        {
            System.Action<HapbeatFired> thrower = _ => throw new System.InvalidOperationException("recorder bug");
            HapbeatManager.Fired += thrower;
            try
            {
                UnityEngine.TestTools.LogAssert.Expect(LogType.Exception,
                    new System.Text.RegularExpressions.Regex("recorder bug"));
                long datagramsBefore = _manager.Client.SuppressedDatagramCount;

                _manager.Play("test.hit");

                Assert.AreEqual(1, _fired.Count, "the other subscribers still receive the fire");
                Assert.Greater(_manager.Client.SuppressedDatagramCount, datagramsBefore, "the PLAY still ran");
            }
            finally
            {
                HapbeatManager.Fired -= thrower;
            }
        }
    }
}
