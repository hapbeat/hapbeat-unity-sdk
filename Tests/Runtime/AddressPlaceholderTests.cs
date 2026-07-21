using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Unit tests for <see cref="HapbeatManager.ApplyAddressPlaceholders(string, int, int)"/>.
    /// Pure, UnityEngine-independent function, so these run without entering Play mode.
    /// </summary>
    public class AddressPlaceholderTests
    {
        [TestCase("Booth <p>", 3, -1, "Booth 3")]
        [TestCase("Booth <p>/<g>", 3, 7, "Booth 3/7")]
        [TestCase("<p>-<g>", -1, -1, "---")]
        [TestCase("<g> only", -1, 42, "42 only")]
        [TestCase("no placeholders here", 3, 7, "no placeholders here")]
        [TestCase("<p><p>", 5, -1, "55")]
        public void ReplacesPlaceholdersWithCurrentOverrideValues(string input, int overridePlayer, int overrideGroup, string expected)
        {
            string result = HapbeatManager.ApplyAddressPlaceholders(input, overridePlayer, overrideGroup);
            Assert.AreEqual(expected, result);
        }

        [TestCase(null)]
        [TestCase("")]
        public void NullOrEmpty_PassesThroughUnchanged(string input)
        {
            string result = HapbeatManager.ApplyAddressPlaceholders(input, overridePlayer: 3, overrideGroup: 7);
            Assert.AreEqual(input, result);
        }

        [Test]
        public void ZeroOrNegativeOverride_RendersAsDash()
        {
            string result = HapbeatManager.ApplyAddressPlaceholders("<p>/<g>", overridePlayer: 0, overrideGroup: -5);
            Assert.AreEqual("-/-", result);
        }
    }
}
