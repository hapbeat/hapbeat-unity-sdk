using System;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Legacy (pre-v2) stream packets must be byte-identical to what the SDK sent
    /// before stream-session-v2 (commit f6d859f, message-format.md "v1 stream"):
    /// header protocol_version=1, no 16-byte envelope, END with an empty payload.
    /// The literals are the v2 fixture bytes with the envelope removed and the
    /// header version set to 1.
    /// </summary>
    public sealed class StreamLegacyWireFormatTests
    {
        [Test]
        public void LegacyPackets_MatchPreV2WireBytes()
        {
            byte[] begin = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_STREAM_BEGIN, 0x1234,
                HapbeatProtocol.BuildLegacyStreamBeginPayload(16000, 2,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, "*"));
            byte[] data = HapbeatProtocol.BuildLegacyStreamDataPacket(0x1234, 0,
                new byte[] { 16, 0, 240, 255 }, 0, 4);
            byte[] end = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_STREAM_END, 0x1234,
                Array.Empty<byte>());

            CollectionAssert.AreEqual(Hex("4248013034120e00803e0200000000000000803f2a00"), begin);
            CollectionAssert.AreEqual(Hex("4248013134120800000000001000f0ff"), data);
            CollectionAssert.AreEqual(Hex("4248013234120000"), end);
        }

        [Test]
        public void LegacyBegin_WithoutTarget_OmitsTargetBytes()
        {
            byte[] payload = HapbeatProtocol.BuildLegacyStreamBeginPayload(16000, 2,
                HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, null);
            CollectionAssert.AreEqual(Hex("803e0200000000000000803f"), payload);
        }

        [Test]
        public void LegacyData_UsesByteOffsetAndSourceSlice()
        {
            byte[] packet = HapbeatProtocol.BuildLegacyStreamDataPacket(0x0001, 0x01020304,
                new byte[] { 9, 9, 0xAA, 0xBB, 9 }, 2, 2);
            CollectionAssert.AreEqual(Hex("424801310100060004030201aabb"), packet);
        }

        private static byte[] Hex(string value)
        {
            var bytes = new byte[value.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
