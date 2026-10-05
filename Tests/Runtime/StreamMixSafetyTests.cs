using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Non-finite gains / samples and zero-length loops must not reach the wire as
    /// full-scale PCM or throw on the mixer thread. Recording sink only.
    /// </summary>
    public sealed class StreamMixSafetyTests
    {
        private sealed class Sink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            public readonly List<byte[]> Packets = new List<byte[]>();
            public int Ends;

            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target) { }

            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity,
                uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
            {
                var copy = new byte[dataLength];
                Buffer.BlockCopy(audioData, dataOffset, copy, 0, dataLength);
                lock (_lock) Packets.Add(copy);
            }

            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity) =>
                Interlocked.Increment(ref Ends);

            public int Count { get { lock (_lock) return Packets.Count; } }
            public List<byte[]> Snapshot() { lock (_lock) return new List<byte[]>(Packets); }
        }

        private static readonly HapbeatClient.StreamEndpoint Endpoint = new HapbeatClient.StreamEndpoint(
            new IPEndPoint(IPAddress.Parse("192.0.2.80"), 7700), "player_1/pos_chest/group_1");

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonFiniteGainAndPan_AreWrittenAsZero(float value)
        {
            var playback = new HapbeatStreamPlayback(1f, value, false);
            Assert.AreEqual(0f, playback.Gain, "initial gain");
            playback.Gain = 1f;
            playback.Gain = value;
            Assert.AreEqual(0f, playback.Gain, "Gain setter");
            playback.Gain = 1f;
            playback.ApplyGainModulation(value);
            Assert.AreEqual(0f, playback.Gain, "gain modulation");
            playback.Pan = value;
            Assert.AreEqual(0f, playback.Pan, "Pan setter");
        }

        [Test]
        public void NaNSamples_MixToSilence_NotFullScaleNegative()
        {
            var sink = new Sink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { Endpoint }, () => 0.01f, _ => { });
            var samples = new float[160];
            for (int i = 0; i < samples.Length; i++) samples[i] = float.NaN;
            mixer.AddSamples(samples, 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count > 3);
            foreach (byte[] pcm in sink.Snapshot())
                for (int i = 0; i < pcm.Length; i++)
                    Assert.AreEqual(0, pcm[i], "a NaN sample must be emitted as silence");
        }

        [Test]
        public void ZeroLengthLoop_KeepsMixerRunning_AndStopsCleanly()
        {
            var sink = new Sink();
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { Endpoint }, () => 0.01f, _ => { });
            var empty = mixer.AddSamples(new float[0], 16000, 1, 1f, 1f, "player_1", true);
            var tone = mixer.AddSamples(new float[160], 16000, 1, 1f, 1f, "player_1", true);
            WaitFor(() => sink.Count > 10);
            int before = sink.Count;
            WaitFor(() => sink.Count > before + 5);
            Assert.AreEqual(HapbeatStreamPlaybackStatus.Active, empty.Status);
            Assert.AreEqual(0, Volatile.Read(ref sink.Ends), "the session must not restart");
            empty.Stop();
            tone.Stop();
            WaitFor(() => Volatile.Read(ref sink.Ends) == 1);
        }

        private static void WaitFor(Func<bool> predicate)
        {
            Assert.IsTrue(SpinWait.SpinUntil(predicate, 1000), "Timed out waiting for recording mixer packets.");
        }
    }
}
