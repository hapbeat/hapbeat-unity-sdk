using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Hapbeat.Tests
{
    public sealed class ParameterBindingMapScopeTests
    {
        [TestCase(BindingOutputParameter.StreamGain)]
        [TestCase(BindingOutputParameter.StreamPan)]
        public void DuplicatedEntryIds_OnlyModulatePlaybackFromTheLinkedMap(BindingOutputParameter output)
        {
            var go = new GameObject("Two hands");
            var left = ScriptableObject.CreateInstance<HapbeatEventMap>();
            HapbeatEventMap right = null;
            try
            {
                var entry = new HapbeatEventEntry { mode = HapticMode.StreamClip };
                var preset = new HapbeatBindingPreset
                {
                    outputParameter = output, outputMin = 0.75f, outputMax = 0.75f,
                };
                entry.bindings.Add(preset);
                left.entries.Add(entry);
                var entryId = entry.id;
                var presetId = preset.id;
                right = Object.Instantiate(left); // Duplication intentionally preserves both stable IDs.

                var leftTrigger = go.AddComponent<HapbeatUnityEventTrigger>();
                var rightTrigger = go.AddComponent<HapbeatUnityEventTrigger>();
                leftTrigger.EditorSetupEntry(left, entryId);
                rightTrigger.EditorSetupEntry(right, entryId);
                var leftPlayback = new HapbeatStreamPlayback(1, 0.25f, true);
                var rightPlayback = new HapbeatStreamPlayback(1, 0.5f, true);
                typeof(HapbeatTriggerBase).GetField("_activePlayback", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(leftTrigger, leftPlayback);
                typeof(HapbeatTriggerBase).GetField("_activePlayback", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(rightTrigger, rightPlayback);
                var leftBinding = AddBinding(go, left, presetId);
                var rightBinding = AddBinding(go, right, presetId);
                var resolve = typeof(HapbeatTriggerBase).GetMethod("FindBindingForEntry", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.AreSame(rightBinding, resolve.Invoke(rightTrigger, new object[] { entryId, output == BindingOutputParameter.StreamGain }));
                Assert.AreSame(leftBinding, resolve.Invoke(leftTrigger, new object[] { entryId, output == BindingOutputParameter.StreamGain }));

                typeof(HapbeatParameterBinding).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(rightBinding, null);
                Assert.That(output == BindingOutputParameter.StreamGain ? rightPlayback.Gain : rightPlayback.Pan, Is.EqualTo(0.75f));
                Assert.That(output == BindingOutputParameter.StreamGain ? leftPlayback.Gain : leftPlayback.Pan,
                    Is.EqualTo(output == BindingOutputParameter.StreamGain ? 0.25f : 0f), "the other hand must be untouched");
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(left);
                if (right != null) Object.DestroyImmediate(right);
            }
        }

        private static HapbeatParameterBinding AddBinding(GameObject go, HapbeatEventMap map, string presetId)
        {
            var binding = go.AddComponent<HapbeatParameterBinding>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(HapbeatParameterBinding).GetField("_linkedEventMap", flags).SetValue(binding, map);
            typeof(HapbeatParameterBinding).GetField("_linkedBindingId", flags).SetValue(binding, presetId);
            typeof(HapbeatParameterBinding).GetField("_sourceTransform", flags).SetValue(binding, go.transform);
            return binding;
        }
    }
}
