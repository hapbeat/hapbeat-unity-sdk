using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Unit tests for <see cref="HapbeatManager.ResolveEffectiveOverride(int, int)"/> — the
    /// per-axis precedence between the build-wide value (HapbeatConfig.buildOverridePlayer /
    /// buildOverrideGroup) and the per-device value (PlayerPrefs / runtime panel).
    /// Pure and UnityEngine-independent, so these run without entering Play mode.
    /// </summary>
    public class AddressOverrideResolutionTests
    {
        // Config not forcing (-1, or anything outside 1..99): the per-device value decides.
        [TestCase(-1, -1, -1)]
        [TestCase(-1, 5, 5)]
        [TestCase(-1, 99, 99)]
        [TestCase(0, 5, 5)]
        [TestCase(100, 5, 5)]
        public void ConfigNotForced_UsesPerDeviceValue(int configValue, int perDeviceValue, int expected)
        {
            Assert.AreEqual(expected, HapbeatManager.ResolveEffectiveOverride(configValue, perDeviceValue));
        }

        // Config in 1..99 wins over whatever the device has, including "disabled".
        [TestCase(3, -1, 3)]
        [TestCase(3, 7, 3)]
        [TestCase(1, 99, 1)]
        [TestCase(99, 0, 99)]
        public void ConfigForced_OverridesPerDeviceValue(int configValue, int perDeviceValue, int expected)
        {
            Assert.AreEqual(expected, HapbeatManager.ResolveEffectiveOverride(configValue, perDeviceValue));
        }

        // Per-device values outside 1..99 normalize to -1 (disabled) the same way
        // HapbeatClient.NormalizeOverride does everywhere else.
        [TestCase(0)]
        [TestCase(100)]
        [TestCase(-42)]
        public void PerDeviceValueOutsideRange_NormalizesToDisabled(int perDeviceValue)
        {
            Assert.AreEqual(-1, HapbeatManager.ResolveEffectiveOverride(-1, perDeviceValue));
        }

        // Default config (-1/-1) with nothing persisted must stay byte-for-byte the
        // old behavior: the override is simply disabled.
        [Test]
        public void DefaultConfigAndNoPersistedValue_IsDisabled()
        {
            Assert.AreEqual(HapbeatManager.AddressOverrideDisabled,
                HapbeatManager.ResolveEffectiveOverride(-1, HapbeatManager.AddressOverrideDisabled));
        }
    }
}
