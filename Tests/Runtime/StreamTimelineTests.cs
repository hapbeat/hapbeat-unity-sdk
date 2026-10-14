using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// The stream timeline (DEC-093): a source starts at its start delay, sample-accurate, and a source due inside
    /// blocks already sent ahead re-splices the session (new generation from its start) instead of joining late.
    /// Records packets only: no network client, no hardware.
    /// </summary>
    public sealed class StreamTimelineTests
    {
        private sealed class Sink : IHapbeatEndpointStreamPacketSink
        {
            private readonly object _lock = new object();
            public readonly List<uint> BeginGenerations = new List<uint>();
            public readonly List<uint> EndGenerations = new List<uint>();
            public readonly List<(uint generation, uint offset, byte[] pcm)> Packets = new List<(uint, uint, byte[])>();
            public void Begin(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity, ushort rate, byte channels,
                byte format, uint total, float gain, string target)
            { lock (_lock) if (!BeginGenerations.Contains(identity.Generation)) BeginGenerations.Add(identity.Generation); }
            private void Record(uint g, uint o, byte[] p) { lock (_lock) Packets.Add((g, o, p)); }
            public void Data(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity, uint byteOffset,
                byte[] audioData, int offset, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(audioData, offset, copy, 0, length);
                Record(identity.Generation, byteOffset, copy);
            }
            public void End(IPEndPoint endpoint, HapbeatProtocol.StreamSessionIdentity identity)
            { lock (_lock) EndGenerations.Add(identity.Generation); }
            public List<(uint generation, uint offset, byte[] pcm)> Snapshot() { lock (_lock) return new List<(uint, uint, byte[])>(Packets); }
            public int Begins { get { lock (_lock) return BeginGenerations.Count; } }
        }

        private static readonly HapbeatClient.StreamEndpoint Endpoint =
            new HapbeatClient.StreamEndpoint(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700), "player_1/pos_l_arm/group_1");

        private static HapbeatEndpointStreamMixer Create(Sink sink, float sendAhead) =>
            new HapbeatEndpointStreamMixer(sink, _ => new List<HapbeatClient.StreamEndpoint> { Endpoint }, () => sendAhead, _ => { });

        [Test]
        public void StartDelay_PutsTheFirstFrameThatFarIntoAFreshStream()
        {
            var sink = new Sink();
            using var mixer = Create(sink, 0.05f);
            mixer.AddSamples(Constant(0.5f, 1600), 16000, 1, 1f, 1f, "*/pos_l_arm", false, startDelaySeconds: 0.03);
            WaitFor(() => sink.Snapshot().Count >= 6);
            int onset = FirstNonzeroFrame(sink.Snapshot(), 0);
            TestContext.WriteLine($"onset at frame {onset} (30 ms = 480)");
            Assert.That(onset, Is.InRange(480 - 32, 480 + 32));
        }

        [Test]
        public void OverlappingSource_ReSplicesFromItsStart_InsteadOfJoiningBehindTheQueue()
        {
            var sink = new Sink();
            using var mixer = Create(sink, 0.05f);
            var bed = mixer.AddSamples(Constant(0.25f, 160), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            Thread.Sleep(200);
            Assert.AreEqual(1, sink.Begins);
            mixer.AddSamples(Constant(0.5f, 1600), 16000, 1, 1f, 1f, "*/pos_l_arm", false);
            WaitFor(() => sink.Begins == 2 && sink.Snapshot().FindAll(d => d.generation == sink.BeginGenerations[1]).Count >= 3);
            Assert.AreEqual(new List<uint> { sink.BeginGenerations[0] }, sink.EndGenerations, "the old generation ENDs before the new BEGIN");
            var resent = sink.Snapshot().FindAll(d => d.generation == sink.BeginGenerations[1]);
            Assert.AreEqual(0u, resent[0].offset, "the new generation starts at byte 0");
            // The bed goes on from the rewound block and the one-shot is in from (about) the first frame.
            int onset = FirstLevelFrame(resent, 0.6f);
            TestContext.WriteLine($"one-shot onset at frame {onset} of the new generation");
            Assert.That(onset, Is.LessThan(160), "the one-shot starts within the first re-sent block, not behind the queued 50 ms");
            Assert.That(Read(resent[0].pcm, 0), Is.GreaterThan(0.2f), "the bed continues through the splice");
            Assert.IsTrue(bed.IsActive);
        }

        [Test]
        public void SourceAfterAnIdleGap_DoesNotReSplice()
        {
            var sink = new Sink();
            using var mixer = Create(sink, 0.05f);
            var first = mixer.AddSamples(Constant(0.25f, 160), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            Thread.Sleep(150);
            first.Stop();
            Thread.Sleep(120); // inside the 300 ms linger, nothing queued any more
            mixer.AddSamples(Constant(0.5f, 800), 16000, 1, 1f, 1f, "*/pos_l_arm", false);
            Thread.Sleep(120);
            Assert.AreEqual(1, sink.Begins);
            Assert.IsEmpty(sink.EndGenerations);
        }

        [Test]
        public void ShortSendAhead_IsNeverReSpliced()
        {
            var sink = new Sink();
            using var mixer = Create(sink, 0.01f);
            mixer.AddSamples(Constant(0.25f, 160), 16000, 1, 1f, 1f, "*/pos_l_arm", true);
            Thread.Sleep(100);
            mixer.AddSamples(Constant(0.5f, 800), 16000, 1, 1f, 1f, "*/pos_l_arm", false);
            Thread.Sleep(100);
            Assert.AreEqual(1, sink.Begins, "a queue the device would not flush is joined, not re-spliced");
        }

        private static float[] Constant(float value, int frames)
        {
            var samples = new float[frames];
            for (int i = 0; i < frames; i++) samples[i] = value;
            return samples;
        }

        private static float Read(byte[] pcm, int frame) => (short)(pcm[frame * 4] | (pcm[frame * 4 + 1] << 8)) / 32767f;

        private static int FirstNonzeroFrame(List<(uint generation, uint offset, byte[] pcm)> packets, int fromPacket) => FirstLevelFrame(packets, 0.01f, fromPacket);

        private static int FirstLevelFrame(List<(uint generation, uint offset, byte[] pcm)> packets, float level, int fromPacket = 0)
        {
            int frame = 0;
            for (int p = fromPacket; p < packets.Count; p++)
            {
                byte[] pcm = packets[p].pcm;
                for (int f = 0; f < pcm.Length / 4; f++, frame++) if (Math.Abs(Read(pcm, f)) >= level) return frame;
            }
            return -1;
        }

        private static void WaitFor(Func<bool> predicate, int timeoutMilliseconds = 1000)
        {
            for (int i = 0; i < timeoutMilliseconds / 5; i++)
            {
                if (predicate()) return;
                Thread.Sleep(5);
            }
            Assert.Fail("Timed out waiting for stream packets.");
        }
    }
}
