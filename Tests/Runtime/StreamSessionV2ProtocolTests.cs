using System;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    public sealed class StreamSessionV2ProtocolTests
    {
        // contracts/fixtures/stream-session-v2.json is the cross-SDK authority for
        // these bytes. Keep the literals here so the Unity test assembly stays
        // self-contained when imported as a UPM package.
        [Test]
        public void FixturePackets_HaveV2HeaderAndExactLittleEndianEnvelope()
        {
            var identity = new HapbeatProtocol.StreamSessionIdentity(
                new HapbeatProtocol.StreamLeaseIdentity(0x0102030405060708UL, 7), 42);

            byte[] begin = HapbeatProtocol.BuildStreamPacket(HapbeatProtocol.CMD_STREAM_BEGIN, 0x1234,
                HapbeatProtocol.BuildStreamBeginPayload(identity, 16000, 2,
                    HapbeatProtocol.AUDIO_FORMAT_PCM16, 0, 1f, "*"));
            byte[] data = HapbeatProtocol.BuildStreamPacket(HapbeatProtocol.CMD_STREAM_DATA, 0x1234,
                HapbeatProtocol.BuildStreamDataPayload(identity, 0, new byte[] { 16, 0, 240, 255 }, 0, 4));
            byte[] end = HapbeatProtocol.BuildStreamPacket(HapbeatProtocol.CMD_STREAM_END, 0x1234,
                HapbeatProtocol.BuildStreamEndPayload(identity));

            CollectionAssert.AreEqual(Hex("4248023034121e000807060504030201070000002a000000803e0200000000000000803f2a00"), begin);
            CollectionAssert.AreEqual(Hex("42480231341218000807060504030201070000002a000000000000001000f0ff"), data);
            CollectionAssert.AreEqual(Hex("42480232341210000807060504030201070000002a000000"), end);
        }

        [Test]
        public void ExtendedPing_RemainsV1AndAppendsNonzeroClientIncarnation()
        {
            byte[] payload = HapbeatProtocol.BuildPingPayload(0x0102030405060708L, 0x1112131415161718UL);
            CollectionAssert.AreEqual(Hex("08070605040302011817161514131211"), payload);
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
