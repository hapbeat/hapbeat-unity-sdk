using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    public sealed class StreamRestartLatencyTests
    {
        private sealed class Sink : IHapbeatEndpointStreamPacketSink
        {
            public int Begins;
            public int Ends;
            public long LastBegin;
            public void Begin(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity,
                ushort rate, byte channels, byte format, uint samples, float gain, string target)
            {
                Interlocked.Exchange(ref LastBegin, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref Begins);
            }
            public void Data(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity,
                uint offset, byte[] pcm, int start, int length) { }
            public void End(IPEndPoint ep, HapbeatProtocol.StreamSessionIdentity identity) => Interlocked.Increment(ref Ends);
        }

        [Test]
        public void NewSourceImmediatelyAfterEnd_HasNoThreeHundredMillisecondGate()
        {
            var sink = new Sink();
            var ep = new HapbeatClient.StreamEndpoint(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 7700), "player_1/pos_l_wrist/group_1");
            using var mixer = new HapbeatEndpointStreamMixer(sink,
                _ => new List<HapbeatClient.StreamEndpoint> { ep }, () => 0.01f, _ => { });
            var first = mixer.AddSamples(new float[160], 16000, 1, 1f, 0f, "*/pos_l_wrist", true);
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref sink.Begins) == 1, 1000));
            first.Stop();
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref sink.Ends) == 1, 1500));
            long requested = Stopwatch.GetTimestamp();
            mixer.AddSamples(new float[160], 16000, 1, 1f, 0f, "*/pos_l_wrist", true);
            Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref sink.Begins) == 2, 1000));
            double elapsedMs = (Interlocked.Read(ref sink.LastBegin) - requested) * 1000.0 / Stopwatch.Frequency;
            TestContext.WriteLine($"END-boundary re-grab: request to BEGIN = {elapsedMs:F2} ms (recording sink, no hardware)");
            Assert.Less(elapsedMs, 100, "Session identity, not a fixed wait, must protect the restarted stream.");
        }
    }
}
