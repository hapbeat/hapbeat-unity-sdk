using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Unit tests for <see cref="HapbeatClient.AddressMatches(string, string)"/>.
    /// Pure, UnityEngine-independent function, so these run without entering Play
    /// mode. Cases are transcribed from the example table in
    /// hapbeat-contracts/specs/device-addressing.md §4.2, plus the group-omitted /
    /// group-mismatch cases used by stream-unicast target filtering.
    /// </summary>
    public class AddressMatchesTests
    {
        [TestCase("", "player_1/pos_neck", true, "empty target = all devices")]
        [TestCase("player_1", "player_1/pos_neck", true, "front-match")]
        [TestCase("player_1", "player_2/pos_neck", false, "player mismatch")]
        [TestCase("player_1/pos_neck", "player_1/pos_neck", true, "exact match")]
        [TestCase("player_1/pos_neck", "player_1/pos_r_wrist", false, "position mismatch")]
        [TestCase("*/pos_neck", "player_1/pos_neck", true, "wildcard player")]
        [TestCase("*/pos_neck", "player_2/pos_neck", true, "wildcard player, different player")]
        // Firmware only treats a segment that is *entirely* "*" as a wildcard
        // (address_match.cpp: `t_len == 1 && *tp == '*'`), so "pos_*" is compared
        // as a literal and does NOT match. The §4.2 table used to claim ✓ here;
        // the spec has been corrected to match the implementation.
        [TestCase("player_1/pos_*", "player_1/pos_neck", false, "partial wildcard is literal, not a wildcard")]
        [TestCase("player_1/*", "player_1/pos_neck", true, "whole-segment wildcard covers any position")]
        [TestCase("red", "red/player_1/pos_neck", true, "prefix front-match")]
        [TestCase("red/*/player_1", "red/alpha/player_1/pos_neck", true, "wildcard + front-match")]
        [TestCase("player_1/pos_neck/group_1", "player_1/pos_neck/group_1", true, "group exact match")]
        [TestCase("player_1/pos_neck/group_1", "player_1/pos_neck/group_2", false, "group mismatch")]
        [TestCase("player_1/pos_neck", "player_1/pos_neck/group_1", true, "group omitted in target = all groups")]
        [TestCase("*/*/group_1", "player_2/pos_chest/group_1", true, "player/position wildcard, group pinned")]
        [TestCase("player_1/pos_neck/group_1", "player_1/pos_neck", false, "target longer than (non-canonical) address")]
        public void MatchesSpecExampleTable(string target, string deviceAddress, bool expected, string reason)
        {
            bool result = HapbeatClient.AddressMatches(target, deviceAddress);
            Assert.AreEqual(expected, result, reason);
        }

        [Test]
        public void NullTarget_MatchesEverything()
        {
            Assert.IsTrue(HapbeatClient.AddressMatches(null, "player_1/pos_neck/group_1"));
        }

        [Test]
        public void NullDeviceAddress_TreatedAsEmptyString()
        {
            // Defensive: a null address (shouldn't normally happen — PONG always
            // carries a canonical address) behaves like "" rather than throwing.
            Assert.IsFalse(HapbeatClient.AddressMatches("player_1", null));
            Assert.IsTrue(HapbeatClient.AddressMatches("", null));
        }

        /// <summary>
        /// Firmware (address_match.cpp) walks pointers rather than splitting, so a
        /// single trailing '/' terminates the target instead of adding an empty
        /// segment. These pin SDK filtering to firmware behaviour — a mismatch here
        /// means the SDK drops a device whose firmware would have played the stream.
        /// </summary>
        [TestCase("player_1/", "player_1/pos_neck/group_1", true, "trailing slash = same as 'player_1'")]
        [TestCase("player_1/pos_neck/", "player_1/pos_neck/group_1", true, "trailing slash after position")]
        [TestCase("player_1/", "player_1", true, "trailing slash, exact-length address")]
        [TestCase("player_2/", "player_1/pos_neck/group_1", false, "trailing slash does not weaken mismatch")]
        [TestCase("player_1//", "player_1/pos_neck", false, "double slash: one real empty segment remains")]
        [TestCase("/", "player_1/pos_neck", false, "bare slash = one empty segment")]
        public void TrailingSlashMatchesFirmwarePointerWalk(
            string target, string deviceAddress, bool expected, string reason)
        {
            Assert.AreEqual(expected, HapbeatClient.AddressMatches(target, deviceAddress), reason);
        }

        [TestCase("*/*/group_2", "player_1/pos_chest/group_1", false, "group-only target, different group")]
        [TestCase("*/*/group_2", "player_1/pos_chest/group_2", true, "group-only target, matching group")]
        public void GroupSeparationCases(string target, string deviceAddress, bool expected, string reason)
        {
            Assert.AreEqual(expected, HapbeatClient.AddressMatches(target, deviceAddress), reason);
        }
    }
}
