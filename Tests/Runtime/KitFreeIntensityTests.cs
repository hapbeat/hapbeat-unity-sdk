using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// A StreamClip entry that belongs to no Kit plays at intensity 1 and is not
    /// reported as "no cached manifest intensity"; Kit entries still are.
    /// </summary>
    public sealed class KitFreeIntensityTests
    {
        [Test]
        public void KitFreeStreamClip_UnresolvedCache_IsIntensityOneAndNotMissing()
        {
            var entry = new HapbeatEventEntry { mode = HapticMode.StreamClip, gain = 0.6f };
            Assert.Less(entry.CachedManifestIntensity, 0f, "precondition: the cache starts unresolved");

            Assert.IsFalse(entry.UsesKitIntensity);
            Assert.IsFalse(entry.IsMissingKitIntensity, "a Kit-free StreamClip must not warn");
            Assert.AreEqual(1f, entry.EffectiveIntensity);
            Assert.AreEqual(0.6f, entry.GetEffectiveGain(), 1e-6f);
        }

        [Test]
        public void CommandEntry_UnresolvedCache_IsMissing()
        {
            var entry = new HapbeatEventEntry { mode = HapticMode.Command, gain = 0.8f };
            Assert.IsTrue(entry.UsesKitIntensity);
            Assert.IsTrue(entry.IsMissingKitIntensity);
            Assert.AreEqual(0.8f, entry.GetEffectiveGain(), 1e-6f, "unresolved still fires at plain gain");
        }

        [Test]
        public void KitStreamClip_WithManifest_WarnsUntilResolved()
        {
            var manifest = new TextAsset("{}");
            try
            {
                var entry = new HapbeatEventEntry
                {
                    mode = HapticMode.StreamClip, gain = 0.5f, manifestOverride = manifest,
                };
                Assert.IsTrue(entry.UsesKitIntensity);
                Assert.IsTrue(entry.IsMissingKitIntensity);

                entry.SetCachedManifestIntensity(0.4f);
                Assert.IsFalse(entry.IsMissingKitIntensity);
                Assert.AreEqual(0.4f, entry.EffectiveIntensity, 1e-6f);
                Assert.AreEqual(0.2f, entry.GetEffectiveGain(), 1e-6f);

                entry.SetCachedManifestIntensity(0f);
                Assert.IsFalse(entry.IsMissingKitIntensity, "an authored 0 is a resolved value");
                Assert.AreEqual(0f, entry.GetEffectiveGain());
            }
            finally
            {
                Object.DestroyImmediate(manifest);
            }
        }

        [Test]
        public void KitFreeStreamClip_ResolvedCache_StillApplied()
        {
            // A Kit clip resolved by the global manifest scan (no manifestOverride)
            // keeps its authored intensity.
            var entry = new HapbeatEventEntry { mode = HapticMode.StreamClip, gain = 1f };
            entry.SetCachedManifestIntensity(0.7f);
            Assert.IsFalse(entry.IsMissingKitIntensity);
            Assert.AreEqual(0.7f, entry.GetEffectiveGain(), 1e-6f);
        }
    }
}
