using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    public sealed class EndpointStreamMixerTests
    {
        private sealed class RecordingSink : IHapbeatEndpointStreamPacketSink
        {
            public readonly List<string> Begins = new List<string>();
            public readonly List<string> DataEndpoints = new List<string>();
            public readonly List<string> Ends = new List<string>();
            public readonly List<string> BeginTargets = new List<string>();
            public readonly List<(string endpoint, byte[] pcm)> Packets = new List<(string endpoint, byte[] pcm)>();
            public readonly List<(string endpoint, ushort rate, byte channels)> BeginFormats =
                new List<(string endpoint, ushort rate, byte channels)>();
            private readonly object _lock = new object();

            public void Begin(IPEndPoint endpoint, ushort rate, byte channels, byte _, uint __, float ___, string ____)
            {
                lock (_lock)
                {
                    Begins.Add(endpoint.ToString());
                    BeginTargets.Add(____);
                    BeginFormats.Add((endpoint.ToString(), rate, channels));
                }
            }
            public void Data(IPEndPoint endpoint, uint _, byte[] audioData, int offset, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(audioData, offset, copy, 0, length);
                lock (_lock)
                {
                    DataEndpoints.Add(endpoint.ToString());
                    Packets.Add((endpoint.ToString(), copy));
                }
            }
            public void End(IPEndPoint endpoint)
            { lock (_lock) Ends.Add(endpoint.ToString()); }
            public int CountData(string endpoint) { lock (_lock) return DataEndpoints.FindAll(x => x == endpoint).Count; }
            public int CountEnds(string endpoint) { lock (_lock) return Ends.FindAll(x => x == endpoint).Count; }
            public int CountBegins(string endpoint) { lock (_lock) return Begins.FindAll(x => x == endpoint).Count; }
        }

        private sealed class CountingSink : IHapbeatEndpointStreamPacketSink
        {
            private int _dataPackets;
            public int DataPackets => Volatile.Read(ref _dataPackets);

            public void Begin(IPEndPoint endpoint, ushort sampleRate, byte channels, byte format,
                uint totalSamples, float gain, string target) { }

            public void Data(IPEndPoint endpoint, uint byteOffset, byte[] audioData, int dataOffset, int dataLength) =>
                Interlocked.Increment(ref _dataPackets);

            public void End(IPEndPoint endpoint) { }
        }

        private static readonly HapbeatClient.StreamEndpoint[] Endpoints =
        {
            new HapbeatClient.StreamEndpoint(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700), "player_1/pos_l_arm/group_1"),
            new HapbeatClient.StreamEndpoint(new IPEndPoint(IPAddress.Parse("192.0.2.11"), 7700), "player_1/pos_r_arm/group_1"),
        };

        [Test]
        public void DifferentTargets_StartOneWireSessionPerEndpoint_AndBothHandlesAreActive()
        {
            using var mixer = Create(out var sink);
            var left = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            var right = mixer.AddSamples(LoopSamples(), 8000, 2, 1f, 1f, "*/pos_r_arm", true);

            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 && sink.CountData("192.0.2.11:7700") > 0);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, left.Status);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, right.Status);
            CollectionAssert.AreEquivalent(new[] { "192.0.2.10:7700", "192.0.2.11:7700" }, sink.Begins);
        }

        [Test]
        public void EndpointPackets_ContainOnlyTheirMatchingSource()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(ConstantSamples(0.25f), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            mixer.AddSamples(ConstantSamples(0.75f), 16000, 1, 1f, 1f, "*/pos_r_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 && sink.CountData("192.0.2.11:7700") > 0);

            byte left = sink.Packets.Find(x => x.endpoint == "192.0.2.10:7700").pcm[1];
            byte right = sink.Packets.Find(x => x.endpoint == "192.0.2.11:7700").pcm[1];
            Assert.Less(left, right, "right endpoint must not receive left-only PCM");
        }

        [Test]
        public void WildcardJoinsBothEndpoints_AndStoppingLeftDoesNotEndRight()
        {
            using var mixer = Create(out var sink);
            var left = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_r_arm", true);
            mixer.AddSamples(LoopSamples(), 44100, 1, 1f, 1f, "*/pos_*", true); // literal, intentionally unmatched
            mixer.AddSamples(LoopSamples(), 44100, 1, 1f, 1f, "*/*/group_1", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 1 && sink.CountData("192.0.2.11:7700") > 1);

            left.Stop();
            int rightDataBefore = sink.CountData("192.0.2.11:7700");
            WaitFor(() => sink.CountData("192.0.2.11:7700") > rightDataBefore);
            Assert.AreEqual(0, sink.CountEnds("192.0.2.10:7700"),
                "the wildcard source still owns the left endpoint session");
            Assert.AreEqual(0, sink.CountEnds("192.0.2.11:7700"));
        }

        [Test]
        public void StoppingLastLeftSource_EndsOnlyLeftWhileRightContinues()
        {
            using var mixer = Create(out var sink);
            var left = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_r_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 && sink.CountData("192.0.2.11:7700") > 0);

            left.Stop();
            WaitFor(() => sink.CountEnds("192.0.2.10:7700") == 1);
            int rightDataBefore = sink.CountData("192.0.2.11:7700");
            WaitFor(() => sink.CountData("192.0.2.11:7700") > rightDataBefore);
            Assert.AreEqual(0, sink.CountEnds("192.0.2.11:7700"));
        }

        [Test]
        public void UnresolvedTarget_IsDeferredAndDoesNotCreatePackets()
        {
            var sink = new RecordingSink();
            using var mixer = new HapbeatEndpointStreamMixer(sink, _ => new List<HapbeatClient.StreamEndpoint>(), () => 0.01f, _ => { });
            var playback = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "player_9/pos_l_arm", true);

            Thread.Sleep(25);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, playback.Status);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint, playback.DeferReason);
            Assert.IsEmpty(sink.Begins);
            Assert.IsEmpty(sink.DataEndpoints);
        }

        [Test]
        public void UnresolvedTarget_StopRemovesDeferredSourceWithoutScheduler()
        {
            var sink = new RecordingSink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint>(), () => 0.01f, _ => { });
            var playback = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                "player_9/pos_l_arm", true);

            playback.Stop();

            Assert.IsFalse(mixer.IsStreaming);
            Assert.IsTrue(playback.IsStopped);
            Assert.IsEmpty(sink.Begins);
        }

        [Test]
        public void UnresolvedConcreteTarget_TargetPatternStopPreventsLaterJoin()
        {
            var sink = new RecordingSink();
            bool endpointKnown = false;
            using var mixer = new HapbeatEndpointStreamMixer(sink, target =>
            {
                return endpointKnown && HapbeatClient.AddressMatches(target, Endpoints[0].Address)
                    ? new List<HapbeatClient.StreamEndpoint> { Endpoints[0] }
                    : new List<HapbeatClient.StreamEndpoint>();
            }, () => 0.01f, _ => { });
            var playback = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                "player_1/pos_l_arm", true);

            mixer.StopTarget("*/pos_l_arm", flush: true);
            endpointKnown = true;
            mixer.ReconcileEndpoints();

            Assert.IsTrue(playback.IsStopped);
            Assert.IsFalse(mixer.IsStreaming);
            Assert.IsEmpty(sink.Begins);
            Assert.IsEmpty(sink.DataEndpoints);
        }

        [Test]
        public void DeferredOneShot_WaitsWhileOtherEndpointRuns_ThenStartsOnJoin()
        {
            var sink = new RecordingSink();
            bool rightKnown = false;
            using var mixer = new HapbeatEndpointStreamMixer(sink, target =>
            {
                var result = new List<HapbeatClient.StreamEndpoint>();
                if (HapbeatClient.AddressMatches(target, Endpoints[0].Address)) result.Add(Endpoints[0]);
                if (rightKnown && HapbeatClient.AddressMatches(target, Endpoints[1].Address)) result.Add(Endpoints[1]);
                return result;
            }, () => 0.01f, _ => { });
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            var deferred = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_r_arm", false);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0);
            Thread.Sleep(25);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, deferred.Status);
            Assert.IsFalse(deferred.IsStopped);

            rightKnown = true;
            mixer.ReconcileEndpoints();
            WaitFor(() => sink.CountData("192.0.2.11:7700") > 0);
            Assert.AreNotEqual(HapbeatStreamPlaybackStatus.Deferred, deferred.Status);
        }

        [Test]
        public void OneHundredTwentyEightSources_HaveNoFixedAdmissionLimit()
        {
            var sink = new CountingSink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { Endpoints[0] },
                () => 0.01f, _ => { });
            for (int i = 0; i < 128; i++)
                Assert.NotNull(mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true));

            WaitFor(() => sink.DataPackets >= 3);
            mixer.ResetDiagnostics();
            var watch = Stopwatch.StartNew();
            WaitFor(() => mixer.Diagnostics.MixedChunkCount >= 20);
            watch.Stop();
            HapbeatEndpointStreamMixerDiagnostics diagnostics = mixer.Diagnostics;
            double bytesPerSecond = diagnostics.SentPcmBytes / watch.Elapsed.TotalSeconds;
            TestContext.WriteLine($"128-source steady mix: chunks={diagnostics.MixedChunkCount}, " +
                                  $"deadlineMisses={diagnostics.DeadlineMissCount}, " +
                                  $"maxMixMs={diagnostics.MaxMixMilliseconds:F3}, " +
                                  $"schedulerAllocatedBytes={diagnostics.SchedulerAllocatedBytes}, " +
                                  $"pcmBytesPerSecond={bytesPerSecond:F0}");
            Assert.GreaterOrEqual(diagnostics.SentPcmBytes, 20 * 640);
        }

        [Test]
        public void SameEndpoint_HasOneBegin_WhenSeveralSourcesMatch()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(LoopSamples(), 8000, 1, 1f, 1f, "*/pos_l_arm", true);
            mixer.AddSamples(LoopSamples(), 44100, 2, 1f, 1f, "*/pos_l_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0);

            Assert.AreEqual(1, sink.Begins.FindAll(x => x == "192.0.2.10:7700").Count);
            Assert.AreEqual((ushort)16000, sink.BeginFormats[0].rate);
            Assert.AreEqual((byte)2, sink.BeginFormats[0].channels);
        }

        [Test]
        public void StopAll_SendsExactlyOneEndPerEndpoint()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/*/group_1", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 && sink.CountData("192.0.2.11:7700") > 0);

            mixer.StopAll();
            Assert.AreEqual(1, sink.CountEnds("192.0.2.10:7700"));
            Assert.AreEqual(1, sink.CountEnds("192.0.2.11:7700"));
        }

        [Test]
        public void StopAllWithFlush_SendsReplacementBeginAndExactlyOneEndPerEndpoint()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/*/group_1", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 &&
                          sink.CountData("192.0.2.11:7700") > 0);

            mixer.StopAll(flush: true);
            Assert.AreEqual(2, sink.CountBegins("192.0.2.10:7700"));
            Assert.AreEqual(2, sink.CountBegins("192.0.2.11:7700"));
            Assert.AreEqual(1, sink.CountEnds("192.0.2.10:7700"));
            Assert.AreEqual(1, sink.CountEnds("192.0.2.11:7700"));
        }

        [Test]
        public void StopAllTimeout_DoesNotSendDelayedEndIntoRestartedSession()
        {
            var sink = new RecordingSink();
            using var stopFinalizeEntered = new ManualResetEventSlim(false);
            using var allowStopFinalize = new ManualResetEventSlim(false);
            using var mixer = new HapbeatEndpointStreamMixer(sink, target =>
            {
                return HapbeatClient.AddressMatches(target, Endpoints[0].Address)
                    ? new List<HapbeatClient.StreamEndpoint> { Endpoints[0] }
                    : new List<HapbeatClient.StreamEndpoint>();
            }, () => 0.01f, _ => { }, beforeStopFinalize: () =>
            {
                stopFinalizeEntered.Set();
                allowStopFinalize.Wait(1500);
            });
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0);

            mixer.StopAll(); // deliberately reaches the bounded 500 ms join timeout
            Assert.IsTrue(stopFinalizeEntered.IsSet);
            Assert.AreEqual(1, sink.CountEnds("192.0.2.10:7700"));
            int dataBeforeRestart = sink.CountData("192.0.2.10:7700");
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            allowStopFinalize.Set();

            WaitFor(() => sink.CountBegins("192.0.2.10:7700") == 2 &&
                          sink.CountData("192.0.2.10:7700") > dataBeforeRestart);
            Assert.AreEqual(1, sink.CountEnds("192.0.2.10:7700"),
                "the timed-out scheduler must suppress its delayed END");
        }

        [Test]
        public void NaturalCompletion_EndsOnce_AndRestartDoesNotReceiveStaleEnd()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(new[] { 0.25f }, 16000, 1, 1f, 1f, "*/pos_l_arm", false);
            WaitFor(() => sink.CountEnds("192.0.2.10:7700") == 1);

            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 1);
            Assert.AreEqual(1, sink.CountEnds("192.0.2.10:7700"));
        }

        [Test]
        public void AddDuringNaturalFinalize_StartsFreshSchedulerWithoutStaleEnd()
        {
            var sink = new RecordingSink();
            using var finalizeEntered = new ManualResetEventSlim(false);
            using var allowFinalize = new ManualResetEventSlim(false);
            using var mixer = new HapbeatEndpointStreamMixer(sink, target =>
            {
                return HapbeatClient.AddressMatches(target, Endpoints[0].Address)
                    ? new List<HapbeatClient.StreamEndpoint> { Endpoints[0] }
                    : new List<HapbeatClient.StreamEndpoint>();
            }, () => 0.01f, _ => { }, () =>
            {
                finalizeEntered.Set();
                allowFinalize.Wait(500);
            });

            mixer.AddSamples(new[] { 0.25f }, 16000, 1, 1f, 1f, "*/pos_l_arm", false);
            Assert.IsTrue(finalizeEntered.Wait(500), "old scheduler did not reach natural-finalize barrier");
            int beginsBefore = sink.CountBegins("192.0.2.10:7700");
            int endsBefore = sink.CountEnds("192.0.2.10:7700");
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            allowFinalize.Set();

            WaitFor(() => sink.CountBegins("192.0.2.10:7700") >= beginsBefore &&
                          sink.CountData("192.0.2.10:7700") > 1);
            Assert.AreEqual(endsBefore, sink.CountEnds("192.0.2.10:7700"),
                "the old scheduler must not send END after the replacement source was admitted");
        }

        [Test]
        public void AddressChangeAtSameEndpoint_ReplacesSessionWithEndThenBegin()
        {
            var sink = new RecordingSink();
            var endpoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700);
            string address = "player_1/pos_l_arm/group_1";
            using var mixer = new HapbeatEndpointStreamMixer(sink, target =>
            {
                return HapbeatClient.AddressMatches(target, address)
                    ? new List<HapbeatClient.StreamEndpoint> { new HapbeatClient.StreamEndpoint(endpoint, address) }
                    : new List<HapbeatClient.StreamEndpoint>();
            }, () => 0.01f, _ => { });
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/*/group_1", true);
            WaitFor(() => sink.Begins.Count == 1);

            address = "player_1/pos_r_arm/group_1";
            mixer.ReconcileEndpoints();
            WaitFor(() => sink.Begins.Count == 2 && sink.CountEnds("192.0.2.10:7700") == 1);
        }

        [Test]
        public void BridgeEndpoint_KeepsFirstTargetAndDefersSecond()
        {
            var sink = new RecordingSink();
            var bridge = new IPEndPoint(IPAddress.Loopback, 7700);
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                target => new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(bridge, target, false)
                }, () => 0.01f, _ => { });
            var first = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "player_1/pos_l_arm", true);
            var second = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "player_1/pos_r_arm", true);

            WaitFor(() => sink.CountData("127.0.0.1:7700") > 0);
            Assert.IsTrue(first.IsActive);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, second.Status);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.TransportTargetConflict, second.DeferReason);
            Assert.AreEqual(1, sink.Begins.Count);
            Assert.AreEqual("player_1/pos_l_arm", sink.BeginTargets[0]);
        }

        [TestCase("player_1")]
        [TestCase("*")]
        public void BridgeEndpoint_DoesNotMixPrefixOrWildcardTargetIntoActiveWireStream(string secondTarget)
        {
            var sink = new RecordingSink();
            var bridge = new IPEndPoint(IPAddress.Loopback, 7700);
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                target => new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(bridge, target, false)
                }, () => 0.01f, _ => { });
            var first = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                "player_1/pos_l_arm", true);
            var second = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                secondTarget, true);

            WaitFor(() => sink.CountData("127.0.0.1:7700") > 0);
            Assert.IsTrue(first.IsActive);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, second.Status);
            Assert.AreEqual(HapbeatStreamPlaybackDeferReason.TransportTargetConflict, second.DeferReason);
            Assert.AreEqual(1, sink.CountBegins("127.0.0.1:7700"));
        }

        [Test]
        public void BridgeEndpoint_StartsDeferredTargetImmediatelyAfterFirstTargetCompletes()
        {
            var sink = new RecordingSink();
            var bridge = new IPEndPoint(IPAddress.Loopback, 7700);
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                target => new List<HapbeatClient.StreamEndpoint>
                {
                    new HapbeatClient.StreamEndpoint(bridge, target, false)
                }, () => 0.01f, _ => { });
            var first = mixer.AddSamples(new float[1600], 16000, 1, 1f, 1f,
                "player_1/pos_l_arm", false);
            var second = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                "player_1/pos_r_arm", true);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Deferred, second.Status);

            WaitFor(() => sink.CountBegins("127.0.0.1:7700") == 2);
            WaitFor(() => second.IsActive && sink.CountData("127.0.0.1:7700") > 10);
            Assert.IsTrue(first.IsStopped);
            Assert.AreEqual(1, sink.CountEnds("127.0.0.1:7700"));
            Assert.AreEqual("player_1/pos_r_arm", sink.BeginTargets[1]);
        }

        [Test]
        public void TargetFlush_ResetsAndEndsOnlyMatchingEndpoint()
        {
            using var mixer = Create(out var sink);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "player_1/pos_l_arm", true);
            mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f, "*/pos_r_arm", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 && sink.CountData("192.0.2.11:7700") > 0);

            mixer.StopTarget("*/pos_l_arm", flush: true);
            WaitFor(() => sink.CountEnds("192.0.2.10:7700") == 1);
            Assert.AreEqual(2, sink.CountBegins("192.0.2.10:7700"), "initial BEGIN + flush BEGIN");
            Assert.AreEqual(1, sink.CountBegins("192.0.2.11:7700"));
            Assert.AreEqual(0, sink.CountEnds("192.0.2.11:7700"));
        }

        [Test]
        public void TargetFlush_DetachesWildcardSourceOnlyFromMatchingEndpoint()
        {
            using var mixer = Create(out var sink);
            var wildcard = mixer.AddSamples(LoopSamples(), 16000, 1, 1f, 1f,
                "*/*/group_1", true);
            WaitFor(() => sink.CountData("192.0.2.10:7700") > 0 &&
                          sink.CountData("192.0.2.11:7700") > 0);

            mixer.StopTarget("*/pos_l_arm", flush: true);

            WaitFor(() => sink.CountEnds("192.0.2.10:7700") == 1);
            int rightDataBefore = sink.CountData("192.0.2.11:7700");
            WaitFor(() => sink.CountData("192.0.2.11:7700") > rightDataBefore);
            Assert.IsTrue(wildcard.IsActive);
            Assert.AreEqual(2, sink.CountBegins("192.0.2.10:7700"));
            Assert.AreEqual(0, sink.CountEnds("192.0.2.11:7700"));
        }

        private static HapbeatEndpointStreamMixer Create(out RecordingSink sink)
        {
            sink = new RecordingSink();
            return new HapbeatEndpointStreamMixer(sink, target =>
            {
                var result = new List<HapbeatClient.StreamEndpoint>();
                foreach (var endpoint in Endpoints)
                    if (HapbeatClient.AddressMatches(target, endpoint.Address)) result.Add(endpoint);
                return result;
            }, () => 0.01f, _ => { });
        }

        private static float[] LoopSamples() => new float[160];
        private static float[] ConstantSamples(float value)
        {
            var samples = new float[160];
            for (int i = 0; i < samples.Length; i++) samples[i] = value;
            return samples;
        }

        private static void WaitFor(Func<bool> predicate)
        {
            for (int i = 0; i < 100; i++)
            {
                if (predicate()) return;
                Thread.Sleep(5);
            }
            Assert.Fail("Timed out waiting for endpoint stream packets.");
        }
    }
}
