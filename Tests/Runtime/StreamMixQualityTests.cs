using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Mix-bus quality (sdk-multi-stream.md §5.7 / §5.8, conformance cases 8–9): gain / pan
    /// changes ramp within a block (no zipper step at block edges), sums within full scale
    /// pass bit-identical, and overlapping full-gain sources are limited, not hard-clamped.
    /// Recording sink only: no network client, no audio, no hardware.
    /// </summary>
    public sealed class StreamMixQualityTests
    {
        private const int SampleRate = 16000;
        private const int BlockFrames = 160; // 10 ms mix block
        private const float FullScale = 32767f;

        private static readonly HapbeatClient.StreamEndpoint Endpoint =
            new HapbeatClient.StreamEndpoint(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700),
                "player_1/pos_l_arm/group_1");

        private sealed class PcmSink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            private readonly List<short> _samples = new List<short>();
            private int _packets;

            public int Packets { get { lock (_lock) return _packets; } }

            public short[] Samples() { lock (_lock) return _samples.ToArray(); }

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target) { }

            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
            {
                lock (_lock)
                {
                    for (int i = 0; i < dataLength; i += 2)
                        _samples.Add((short)(audioData[dataOffset + i] | (audioData[dataOffset + i + 1] << 8)));
                    _packets++;
                }
            }

            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity) { }
        }

        [Test]
        public void GainAndPanChange_RampsWithinTheBlock_WithoutZipperStep()
        {
            // A constant (DC) source makes any gain step visible as a jump.
            const float dc = 20000f / FullScale;
            var sink = new PcmSink();
            using (var mixer = Create(sink, () => true))
            {
                var playback = mixer.AddSamples(Constant(dc, SampleRate), SampleRate, 1, 1f, 1f,
                    Endpoint.Address, true);
                WaitFor(() => sink.Packets >= 3);
                playback.Gain = 0.2f; // applied at once this would be an 18000 step
                playback.Pan = 0.5f;
                int changedAt = sink.Packets;
                WaitFor(() => sink.Packets >= changedAt + 4);
            }

            short[] pcm = sink.Samples();
            short[] left = Channel(pcm, 0);
            short[] right = Channel(pcm, 1);
            Assert.GreaterOrEqual(left[0], 19999, "a new source starts at its own gain, without a ramp");
            // balance law: L = g (1 - pan) = 0.1, R = g = 0.2, each ramped over one block
            Assert.LessOrEqual(MaxStep(left), (20000 - 2000) / (float)BlockFrames + 2);
            Assert.LessOrEqual(MaxStep(right), (20000 - 4000) / (float)BlockFrames + 2);
            Assert.AreEqual(2000, left[left.Length - 1], 1, "left settles at the new channel gain");
            Assert.AreEqual(4000, right[right.Length - 1], 1, "right settles at the new channel gain");
        }

        [Test]
        public void SumWithinFullScale_PassesTheLimiterBitIdentical()
        {
            // Peaks reach 0.99 FS: above the soft knee (0.95 FS) yet within full scale.
            float[] a = Sine(140f, 0.50f, SampleRate);
            float[] b = Sine(140f, 0.49f, SampleRate);
            var sink = new PcmSink();
            bool resolved = false;
            using (var mixer = Create(sink, () => resolved))
            {
                mixer.AddSamples(a, SampleRate, 1, 1f, 1f, Endpoint.Address, true);
                mixer.AddSamples(b, SampleRate, 1, 1f, 1f, Endpoint.Address, true);
                // Both sources join the same first block from frame 0.
                resolved = true;
                mixer.ReconcileEndpoints();
                WaitFor(() => sink.Packets >= 20);
            }

            short[] pcm = sink.Samples();
            int peak = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                int frame = (i / 2) % a.Length;
                int expected = (int)((a[frame] + b[frame]) * FullScale);
                Assert.AreEqual(expected, pcm[i], $"sample {i} changed although the sum is within full scale");
                peak = Math.Max(peak, Math.Abs((int)pcm[i]));
            }
            Assert.Greater(peak, 0.95f * FullScale, "the fixture must reach the soft-knee range");
        }

        [Test]
        public void FourFullGainSines_AreLimitedWithoutPinnedSamplesOrBlockSteps()
        {
            const float frequency = 140f;
            float[] sine = Sine(frequency, 1f, SampleRate);
            var sink = new PcmSink();
            bool resolved = false;
            using (var mixer = Create(sink, () => resolved))
            {
                for (int i = 0; i < 4; i++)
                    mixer.AddSamples(sine, SampleRate, 1, 1f, 1f, Endpoint.Address, true);
                resolved = true;
                mixer.ReconcileEndpoints();
                WaitFor(() => sink.Packets >= 30);
            }

            short[] left = Channel(sink.Samples(), 0);
            // The first blocks carry the attack from unity gain; judge the settled output.
            var settled = new short[left.Length - BlockFrames * 2];
            Array.Copy(left, BlockFrames * 2, settled, 0, settled.Length);
            int peak = 0;
            int pinned = 0;
            for (int i = 0; i < settled.Length; i++)
            {
                int magnitude = Math.Abs((int)settled[i]);
                peak = Math.Max(peak, magnitude);
                if (magnitude >= 32767) pinned++;
            }
            Assert.AreEqual(0, pinned, "no settled sample may be pinned at full scale");
            Assert.GreaterOrEqual(peak, 0.85f * FullScale, "limited to the target, not over-attenuated");
            Assert.LessOrEqual(peak, 0.95f * FullScale, "limited to the 0.9 FS target");
            // The waveform stays a sine with no flat clipped tops and no block-edge steps.
            double clean = peak * 2 * Math.Sin(Math.PI * frequency / SampleRate);
            Assert.LessOrEqual(MaxStep(settled), 1.1 * clean);
        }

        private static HapbeatEndpointStreamMixer Create(PcmSink sink, Func<bool> resolved)
        {
            return new HapbeatEndpointStreamMixer(sink, target =>
            {
                var endpoints = new List<HapbeatClient.StreamEndpoint>();
                if (resolved() && HapbeatClient.AddressMatches(target, Endpoint.Address)) endpoints.Add(Endpoint);
                return endpoints;
            }, () => 0.01f, _ => { });
        }

        private static float[] Constant(float value, int frames)
        {
            var samples = new float[frames];
            for (int i = 0; i < samples.Length; i++) samples[i] = value;
            return samples;
        }

        private static float[] Sine(float frequency, float amplitude, int frames)
        {
            var samples = new float[frames];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / SampleRate);
            return samples;
        }

        private static short[] Channel(short[] interleaved, int channel)
        {
            var samples = new short[interleaved.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = interleaved[i * 2 + channel];
            return samples;
        }

        private static int MaxStep(short[] samples)
        {
            int max = 0;
            for (int i = 1; i < samples.Length; i++)
                max = Math.Max(max, Math.Abs(samples[i] - samples[i - 1]));
            return max;
        }

        private static void WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return;
                Thread.Sleep(1);
            }
            Assert.Fail("Timed out waiting for endpoint stream packets.");
        }
    }
}
