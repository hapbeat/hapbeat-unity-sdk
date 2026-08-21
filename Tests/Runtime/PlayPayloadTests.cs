using System;
using System.Text;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Wire-format tests for <see cref="HapbeatProtocol.BuildPlayPayload"/>.
    /// The layout is normative in hapbeat-contracts/specs/message-format.md §0x01:
    ///   event_id (null-term) + target (null-term) + target_time (int64 LE)
    ///   + gain (float32 LE) + pan (float32 LE)
    /// Pure byte math, so these run without entering Play mode.
    /// </summary>
    public class PlayPayloadTests
    {
        private const string EventId = "sample-kit.sine_100hz";
        private const string Target  = "player_1/pos_neck";

        /// <summary>Byte offset of the trailing pan field for the fixed test strings.</summary>
        private static int PanOffset =>
            Encoding.UTF8.GetByteCount(EventId) + 1 + Encoding.UTF8.GetByteCount(Target) + 1 + 8 + 4;

        [Test]
        public void Layout_HasTrailingPanField()
        {
            byte[] payload = HapbeatProtocol.BuildPlayPayload(EventId, 1234567L, 0.75f, Target, 0.5f);

            Assert.AreEqual(PanOffset + 4, payload.Length, "payload must end with a float32 pan");

            int offset = 0;
            Assert.AreEqual(EventId, ReadCString(payload, ref offset));
            Assert.AreEqual(Target,  ReadCString(payload, ref offset));
            Assert.AreEqual(1234567L, ReadInt64LE(payload, offset)); offset += 8;
            Assert.AreEqual(0.75f, ReadFloat32LE(payload, offset), 1e-6f); offset += 4;
            Assert.AreEqual(0.5f,  ReadFloat32LE(payload, offset), 1e-6f);
        }

        [Test]
        public void OmittedPan_IsCenter()
        {
            // Callers that predate DEC-055 keep compiling; the field still goes on
            // the wire, as 0.0 (center), because the sender always writes it.
            byte[] payload = HapbeatProtocol.BuildPlayPayload(EventId, 0L, 1.0f, Target);

            Assert.AreEqual(PanOffset + 4, payload.Length);
            Assert.AreEqual(0f, ReadFloat32LE(payload, PanOffset), 1e-6f);
        }

        [TestCase(-2.5f, -1f, "below range clamps to full left")]
        [TestCase(-1f,   -1f, "full left passes through")]
        [TestCase(0f,     0f, "center passes through")]
        [TestCase(1f,     1f, "full right passes through")]
        [TestCase(3f,     1f, "above range clamps to full right")]
        [TestCase(float.NaN, 0f, "NaN falls back to center")]
        public void Pan_IsClampedOnTheWire(float input, float expected, string reason)
        {
            byte[] payload = HapbeatProtocol.BuildPlayPayload(EventId, 0L, 1.0f, Target, input);
            Assert.AreEqual(expected, ReadFloat32LE(payload, PanOffset), 1e-6f, reason);
        }

        // The wire is little-endian regardless of host endianness (HapbeatProtocol
        // byte-swaps on BE hosts), so read it back the same way.
        private static float ReadFloat32LE(byte[] buf, int offset)
        {
            byte[] bytes = new byte[4];
            Buffer.BlockCopy(buf, offset, bytes, 0, 4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private static long ReadInt64LE(byte[] buf, int offset)
        {
            byte[] bytes = new byte[8];
            Buffer.BlockCopy(buf, offset, bytes, 0, 8);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToInt64(bytes, 0);
        }

        private static string ReadCString(byte[] buf, ref int offset)
        {
            int start = offset;
            while (offset < buf.Length && buf[offset] != 0) offset++;
            string s = Encoding.UTF8.GetString(buf, start, offset - start);
            offset++; // skip the terminator
            return s;
        }
    }
}
