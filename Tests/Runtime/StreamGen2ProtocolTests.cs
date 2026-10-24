using System;
using NUnit.Framework;

namespace Hapbeat.Tests
{
    /// <summary>
    /// Byte-level fixtures from hapbeat-contracts (commit 46f246e, the 1.0 specification):
    /// fixtures/stream-session-v2.json (packets), fixtures/stream-session-cases.json
    /// (pong_classification, expected_1x) and fixtures/wire-extension-cases.json (caps_req,
    /// caps, error, ping_lengths). The literals are copied here so the Unity test assembly
    /// stays self-contained when imported as a UPM package. Each hex string except the
    /// stream-session-v2.json payloads is a whole UDP datagram (8-byte header + payload).
    /// </summary>
    public sealed class StreamGen2ProtocolTests
    {
        // contracts/fixtures/stream-session-v2.json is the cross-SDK authority for
        // these bytes.
        [Test]
        public void FixturePackets_HaveGen2HeaderAndExactLittleEndianEnvelope()
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

        // stream-session-cases.json pong_classification.cases[*].expected_1x. Each PONG is a
        // matched reply to ping_hex (seq 257, client_incarnation 0x1122334455667788); the
        // client-level matching is covered by StreamLegacyClassificationTests.
        [TestCase("no_tail", "4248011101013d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c8", "Absent", false, false, 0u, 0u, false)]
        [TestCase("tail_32", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201010000887766554433221108070605040302010700000007000000", "Valid", true, false, 7u, 7u, false)]
        [TestCase("tail_superseded", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201030000887766554433221108070605040302010500000007000000", "Valid", true, true, 5u, 7u, false)]
        [TestCase("tail_40", "424801110101650000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c848425332010100008877665544332211080706050403020107000000070000000001020304050607", "Valid", true, false, 7u, 7u, false)]
        [TestCase("tail_reissued", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201050000887766554433221108070605040302010900000007000000", "Valid", true, false, 9u, 7u, true)]
        [TestCase("tail_unknown_flag", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201090000887766554433221108070605040302010700000007000000", "Valid", true, false, 7u, 7u, false)]
        [TestCase("tail_reserved_nonzero", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201010201887766554433221108070605040302010700000007000000", "Valid", true, false, 7u, 7u, false)]
        [TestCase("tail_ext_version_2", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533202010000887766554433221108070605040302010700000007000000", "Ignored", false, false, 0u, 0u, false)]
        [TestCase("tail_short", "424801110101550000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c8484253320101000088776655443322110807060504030201", "Ignored", false, false, 0u, 0u, false)]
        [TestCase("other_bytes", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c85854524100000000000000000000000000000000000000000000000000000000", "Ignored", false, false, 0u, 0u, false)]
        [TestCase("truncated_marker", "424801110101400000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c8484253", "Ignored", false, false, 0u, 0u, false)]
        [TestCase("non_streaming_node", "4248011101015d0000401e18240a060088531e18240a0600486170626561742d4100706c617965725f312f706f735f63686573742f67726f75705f3100312e302e300008c84842533201000000887766554433221108070605040302010000000000000000", "Valid", false, false, 0u, 0u, false)]
        public void PongClassification_FollowsExpected1x(string name, string hex,
            string expectedStatus, bool leaseValid, bool superseded,
            uint ticket, uint highWaterTicket, bool reissued)
        {
            // StreamLeaseTailStatus is internal, so a public test method takes its name.
            var status = (HapbeatProtocol.StreamLeaseTailStatus)Enum.Parse(
                typeof(HapbeatProtocol.StreamLeaseTailStatus), expectedStatus);
            var (command, seq, payload) = HapbeatProtocol.ParsePacket(Hex(hex));
            Assert.AreEqual(HapbeatProtocol.CMD_PONG, command);
            Assert.AreEqual(257, seq);
            HapbeatProtocol.StreamLeasePongTail tail = HapbeatProtocol.ParsePongExtendedInfo(payload).StreamLease;
            Assert.AreEqual(status, tail.Status, name);
            if (status != HapbeatProtocol.StreamLeaseTailStatus.Valid) return;
            Assert.AreEqual(0x1122334455667788UL, tail.EchoedClientIncarnation, name);
            Assert.AreEqual(leaseValid, tail.IsLeaseValid, name);
            Assert.AreEqual(superseded, tail.IsLeaseSuperseded, name);
            Assert.AreEqual(ticket, tail.Lease.LeaseTicket, name);
            Assert.AreEqual(highWaterTicket, tail.HighWaterTicket, name);
            Assert.AreEqual(reissued, tail.IsLeaseReissued, name);
        }

        // wire-extension-cases.json ping_lengths: 8 bytes = discovery, 16 bytes = lease request.
        [Test]
        public void PingFixtures_DiscoveryIsEightBytes_LeaseRequestIsSixteen()
        {
            byte[] ping8 = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_PING, 1,
                HapbeatProtocol.BuildPingPayload(1700000000000000L));
            CollectionAssert.AreEqual(Hex("424801100100080000401e18240a0600"), ping8);
            byte[] ping16 = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_PING, 257,
                HapbeatProtocol.BuildPingPayload(1700000000000000L, 0x1122334455667788UL));
            CollectionAssert.AreEqual(Hex("424801100101100000401e18240a06008877665544332211"), ping16);
        }

        // wire-extension-cases.json caps_req.
        [Test]
        public void CapsRequestFixture_IsCapsVersionOne()
        {
            byte[] packet = HapbeatProtocol.BuildPacket(HapbeatProtocol.CMD_CAPS_REQ, 0x0202,
                HapbeatProtocol.BuildCapsRequestPayload());
            CollectionAssert.AreEqual(Hex("424801120202010001"), packet);
        }

        // wire-extension-cases.json caps[0] band_1x.
        [Test]
        public void CapsFixture_Band1x_ReadsEveryEntry()
        {
            var (command, seq, payload) = HapbeatProtocol.ParsePacket(Hex("424801130202240001000101010205312e302e3003010204010705040f20050006010207013f080403000000"));
            Assert.AreEqual(HapbeatProtocol.CMD_CAPS, command);
            Assert.AreEqual(0x0202, seq, "CAPS echoes the CAPS_REQ seq");
            HapbeatProtocol.CapsInfo caps = HapbeatProtocol.ParseCaps(payload);
            Assert.AreEqual(1, caps.CapsVersion);
            Assert.AreEqual(1, caps.Generation);
            Assert.AreEqual("1.0.0", caps.FirmwareVersion);
            Assert.AreEqual(0b10, caps.StreamGens, "stream_gens [2]");
            Assert.AreEqual(0b111, caps.StreamFormats, "pcm16, adpcm, opus");
            Assert.AreEqual(15, caps.LeaseTtlSeconds);
            Assert.AreEqual(32, caps.LeaseSlots);
            Assert.AreEqual(5, caps.InactivityTimeoutSeconds);
            Assert.AreEqual(2, caps.HapticChannels);
            Assert.AreEqual(63, caps.AddressMaxLength);
            Assert.AreEqual(0b11L, caps.Features, "play_pan, error_reply");
        }

        // wire-extension-cases.json caps[1] future_entries: unknown 0x09 and experimental 0x80
        // skipped, a longer stream_limits read by its prefix, an empty haptic_channels ignored,
        // the duplicate generation's first entry wins.
        [Test]
        public void CapsFixture_FutureEntries_FollowTheTlvRules()
        {
            var (_, _, payload) = HapbeatProtocol.ParsePacket(Hex("4248011302021c0001000903010203010101800365787005060f20050009090600010107"));
            HapbeatProtocol.CapsInfo caps = HapbeatProtocol.ParseCaps(payload);
            Assert.AreEqual(1, caps.CapsVersion);
            Assert.AreEqual(1, caps.Generation);
            Assert.AreEqual(15, caps.LeaseTtlSeconds);
            Assert.AreEqual(32, caps.LeaseSlots);
            Assert.AreEqual(5, caps.InactivityTimeoutSeconds);
            Assert.IsNull(caps.FirmwareVersion);
            Assert.AreEqual(-1, caps.StreamGens);
            Assert.AreEqual(-1, caps.StreamFormats);
            Assert.AreEqual(-1, caps.HapticChannels, "an empty haptic_channels entry is ignored");
            Assert.AreEqual(-1, caps.AddressMaxLength);
            Assert.AreEqual(-1L, caps.Features);
        }

        [Test]
        public void Caps_EntryCutOffByThePayloadEnd_IsIgnored()
        {
            // caps_version 1, reserved, generation 1, then haptic_channels claiming 2 bytes with 1 left.
            HapbeatProtocol.CapsInfo caps = HapbeatProtocol.ParseCaps(new byte[] { 1, 0, 0x01, 1, 1, 0x06, 2, 1 });
            Assert.AreEqual(1, caps.Generation);
            Assert.AreEqual(-1, caps.HapticChannels);
        }

        // wire-extension-cases.json error[0] stream_timed_out.
        [Test]
        public void ErrorFixture_WithReferenceFields()
        {
            var (command, _, payload) = HapbeatProtocol.ParsePacket(Hex("424801ff03032700140073747265616d2074696d6564206f7574003134121008070605040302010700000029000000"));
            Assert.AreEqual(HapbeatProtocol.CMD_ERROR, command);
            HapbeatProtocol.ErrorInfo error = HapbeatProtocol.ParseErrorExtended(payload);
            Assert.AreEqual(HapbeatProtocol.ERROR_STREAM_TIMED_OUT, error.Code);
            Assert.AreEqual("stream timed out", error.Message);
            Assert.IsTrue(error.HasReference);
            Assert.AreEqual(49, error.RefCommand);
            Assert.AreEqual(4660, error.RefSeq);
            CollectionAssert.AreEqual(Hex("08070605040302010700000029000000"), error.Detail);
            Assert.IsTrue(error.TryGetStreamIdentity(out HapbeatProtocol.StreamSessionIdentity identity));
            Assert.AreEqual(0x0102030405060708UL, identity.Lease.DeviceBootId);
            Assert.AreEqual(7u, identity.Lease.LeaseTicket);
            Assert.AreEqual(41u, identity.Generation);
            Assert.AreEqual(20, HapbeatProtocol.ParseError(payload).errorCode, "0.x readers still read code and message");
        }

        // wire-extension-cases.json error[1] code_and_message_only (the form 0.x readers parse).
        [Test]
        public void ErrorFixture_CodeAndMessageOnly()
        {
            var (_, _, payload) = HapbeatProtocol.ParsePacket(Hex("424801ff03031800100073747265616d2067656e203220726571756972656400"));
            HapbeatProtocol.ErrorInfo error = HapbeatProtocol.ParseErrorExtended(payload);
            Assert.AreEqual(HapbeatProtocol.ERROR_STREAM_GEN_UNSUPPORTED, error.Code);
            Assert.AreEqual("stream gen 2 required", error.Message);
            Assert.IsFalse(error.HasReference);
            Assert.IsEmpty(error.Detail);
            Assert.IsFalse(error.TryGetStreamIdentity(out _));
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
