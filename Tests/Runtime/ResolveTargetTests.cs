using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Unit tests for <see cref="HapbeatClient.ResolveTarget(string, int, int)"/> and
    /// <see cref="HapbeatClient.NormalizeOverride"/>. Both are pure, UnityEngine-independent
    /// functions, so these run without entering Play mode.
    /// <para>
    /// Cases are transcribed from the expected-value tables in
    /// subrepo-devs/unity-sdk/instructions/instructions-address-override-202607211200.md.
    /// </para>
    /// </summary>
    public class ResolveTargetTests
    {
        // ---- overridePlayer=3, overrideGroup=-1 ----
        [TestCase("", "player_3")]
        [TestCase("player_1", "player_3")]
        [TestCase("player_1/pos_chest", "player_3/pos_chest")]
        [TestCase("*/pos_neck", "player_3/pos_neck")]
        [TestCase("player_1/pos_chest/group_5", "player_3/pos_chest/group_5")]
        [TestCase("red/player_1/pos_neck", "red/player_3/pos_neck")]
        public void PlayerOverrideOnly_ReplacesOrInsertsPlayerSegment(string input, string expected)
        {
            string result = HapbeatClient.ResolveTarget(input, overridePlayer: 3, overrideGroup: -1);
            Assert.AreEqual(expected, result);
        }

        // ---- overridePlayer=-1, overrideGroup=7 ----
        [TestCase("", "group_7")]
        [TestCase("player_1/pos_chest", "player_1/pos_chest/group_7")]
        [TestCase("player_1/pos_chest/group_5", "player_1/pos_chest/group_7")]
        public void GroupOverrideOnly_AppendsOrReplacesGroupSegment(string input, string expected)
        {
            string result = HapbeatClient.ResolveTarget(input, overridePlayer: -1, overrideGroup: 7);
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void BothOverridesActive_ReplacesBothSegments()
        {
            string result = HapbeatClient.ResolveTarget("player_1/pos_chest/group_5", overridePlayer: 3, overrideGroup: 7);
            Assert.AreEqual("player_3/pos_chest/group_7", result);
        }

        // ---- both disabled (-1/-1): exact passthrough, including null. This is
        // the backward-compatibility guarantee — existing projects that never
        // touch overridePlayer/overrideGroup must see byte-for-byte identical
        // target strings. ----
        [TestCase("")]
        [TestCase("player_1/pos_chest")]
        [TestCase("player_1/pos_chest/group_5")]
        [TestCase("red/player_1/pos_neck")]
        [TestCase(null)]
        public void BothDisabled_PassesThroughUnchanged(string input)
        {
            string result = HapbeatClient.ResolveTarget(input, overridePlayer: -1, overrideGroup: -1);
            Assert.AreEqual(input, result);
        }

        [Test]
        public void NullTarget_WithActiveOverride_DoesNotThrowAndInsertsSegment()
        {
            // null is only a guaranteed no-op passthrough when BOTH overrides are
            // disabled (see BothDisabled_PassesThroughUnchanged). With an active
            // override, null must still be handled gracefully (treated the same
            // as "" — matching BuildXxxPayload's own null handling).
            string result = null;
            Assert.DoesNotThrow(() => result = HapbeatClient.ResolveTarget(null, overridePlayer: 3, overrideGroup: -1));
            Assert.AreEqual("player_3", result);
        }

        [Test]
        public void MultiWildcard_DoesNotThrow()
        {
            // "*/*/group_1" is outside the documented grammar (double wildcard
            // ahead of a position-less group segment). The only contract here is
            // "does not throw" — see instructions §注意点.
            Assert.DoesNotThrow(() => HapbeatClient.ResolveTarget("*/*/group_1", overridePlayer: 3, overrideGroup: -1));
            Assert.DoesNotThrow(() => HapbeatClient.ResolveTarget("*/*/group_1", overridePlayer: -1, overrideGroup: 7));
            Assert.DoesNotThrow(() => HapbeatClient.ResolveTarget("*/*/group_1", overridePlayer: 3, overrideGroup: 7));
        }

        // ---- NormalizeOverride: the entry-point clamp used by
        // HapbeatClient.SetAddressOverride / HapbeatManager.SetAddressOverride.
        // 0 and 100+ are out of the device-addressing 1..99 range and must
        // normalize to -1 ("disabled"), same as an explicit -1. ----
        [TestCase(-1, -1)]
        [TestCase(0, -1)]
        [TestCase(1, 1)]
        [TestCase(50, 50)]
        [TestCase(99, 99)]
        [TestCase(100, -1)]
        [TestCase(254, -1)]
        [TestCase(int.MinValue, -1)]
        [TestCase(int.MaxValue, -1)]
        public void NormalizeOverride_ClampsToValidRange(int input, int expected)
        {
            Assert.AreEqual(expected, HapbeatClient.NormalizeOverride(input));
        }
    }
}
