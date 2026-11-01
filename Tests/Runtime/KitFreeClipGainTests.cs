using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    /// <summary>
    /// A StreamClip entry that belongs to no Kit plays at clip_gain 1 and is not
    /// reported as "no cached manifest clip_gain"; Kit entries still are.
    /// </summary>
    public sealed class KitFreeClipGainTests
    {
        [Test]
        public void KitFreeStreamClip_UnresolvedCache_IsClipGainOneAndNotMissing()
        {
            var entry = new HapbeatEventEntry { mode = HapticMode.StreamClip, gain = 0.6f };
            Assert.Less(entry.CachedManifestClipGain, 0f, "precondition: the cache starts unresolved");

            Assert.IsFalse(entry.UsesKitClipGain);
            Assert.IsFalse(entry.IsMissingKitClipGain, "a Kit-free StreamClip must not warn");
            Assert.AreEqual(1f, entry.EffectiveClipGain);
            Assert.AreEqual(0.6f, entry.GetEffectiveGain(), 1e-6f);
        }

        [Test]
        public void CommandEntry_UnresolvedCache_IsMissing()
        {
            var entry = new HapbeatEventEntry { mode = HapticMode.Command, gain = 0.8f };
            Assert.IsTrue(entry.UsesKitClipGain);
            Assert.IsTrue(entry.IsMissingKitClipGain);
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
                Assert.IsTrue(entry.UsesKitClipGain);
                Assert.IsTrue(entry.IsMissingKitClipGain);

                entry.SetCachedManifestClipGain(0.4f);
                Assert.IsFalse(entry.IsMissingKitClipGain);
                Assert.AreEqual(0.4f, entry.EffectiveClipGain, 1e-6f);
                Assert.AreEqual(0.2f, entry.GetEffectiveGain(), 1e-6f);

                entry.SetCachedManifestClipGain(0f);
                Assert.IsFalse(entry.IsMissingKitClipGain, "an authored 0 is a resolved value");
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
            // keeps its authored clip_gain.
            var entry = new HapbeatEventEntry { mode = HapticMode.StreamClip, gain = 1f };
            entry.SetCachedManifestClipGain(0.7f);
            Assert.IsFalse(entry.IsMissingKitClipGain);
            Assert.AreEqual(0.7f, entry.GetEffectiveGain(), 1e-6f);
        }
    }
}
