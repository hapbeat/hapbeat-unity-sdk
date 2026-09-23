using System;
using System.Text;

namespace Hapbeat
{
    /// <summary>
    /// Static utility class for building and parsing Hapbeat WifiUdp protocol packets.
    /// All multi-byte values are little-endian. Max packet size is 512 bytes.
    /// </summary>
    public static class HapbeatProtocol
    {
        /// <summary>Magic bytes "HB" as uint16 little-endian (0x4842).</summary>
        public const ushort MAGIC = 0x4842;

        /// <summary>Current protocol version.</summary>
        public const byte PROTOCOL_VERSION = 0x01;

        /// <summary>
        /// Protocol version used exclusively by STREAM_BEGIN/DATA/END. All other
        /// commands, including PING/PONG, remain on <see cref="PROTOCOL_VERSION"/>.
        /// </summary>
        public const byte STREAM_PROTOCOL_VERSION = 0x02;

        /// <summary>Size of the common packet header in bytes.</summary>
        public const int HEADER_SIZE = 8;

        /// <summary>Maximum allowed packet size for command packets in bytes.</summary>
        public const int MAX_PACKET_SIZE = 512;

        /// <summary>Maximum allowed packet size for streaming data packets (MTU-safe).</summary>
        public const int MAX_STREAM_PACKET_SIZE = 1472; // 1500 MTU - 20 IP - 8 UDP

        // Command types (SDK → Device)
        public const byte CMD_PLAY = 0x01;
        public const byte CMD_STOP = 0x02;
        public const byte CMD_STOP_ALL = 0x03;
        public const byte CMD_PING = 0x10;
        public const byte CMD_CONNECT_STATUS = 0x20;

        // Streaming commands (SDK → Device)
        public const byte CMD_STREAM_BEGIN = 0x30;
        public const byte CMD_STREAM_DATA = 0x31;
        public const byte CMD_STREAM_END = 0x32;

        // Audio format constants
        public const byte AUDIO_FORMAT_PCM16 = 0;
        public const byte AUDIO_FORMAT_IMA_ADPCM = 1;

        /// <summary>Max payload for STREAM_DATA to stay within typical MTU (1500 - IP/UDP headers - protocol header).</summary>
        public const int STREAM_DATA_MAX_PAYLOAD = 1400;

        /// <summary>Size of the v2 identity envelope at the head of every stream payload.</summary>
        public const int STREAM_SESSION_ENVELOPE_SIZE = 16;

        /// <summary>
        /// Device-issued stream lease. It is intentionally separate from a logical
        /// source or Event ID: one endpoint mixer session owns one lease identity.
        /// </summary>
        internal readonly struct StreamLeaseIdentity : IEquatable<StreamLeaseIdentity>
        {
            public readonly ulong DeviceBootId;
            public readonly uint LeaseTicket;

            public StreamLeaseIdentity(ulong deviceBootId, uint leaseTicket)
            {
                DeviceBootId = deviceBootId;
                LeaseTicket = leaseTicket;
            }

            public bool IsValid => DeviceBootId != 0 && LeaseTicket != 0;

            public bool Equals(StreamLeaseIdentity other) =>
                DeviceBootId == other.DeviceBootId && LeaseTicket == other.LeaseTicket;

            public override bool Equals(object obj) =>
                obj is StreamLeaseIdentity other && Equals(other);

            public override int GetHashCode() =>
                DeviceBootId.GetHashCode() * 397 ^ (int)LeaseTicket;

            public override string ToString() => $"boot={DeviceBootId:X16}, ticket={LeaseTicket}";
        }

        /// <summary>Exact v2 identity stamped on a BEGIN/DATA/END packet.</summary>
        internal readonly struct StreamSessionIdentity
        {
            public readonly StreamLeaseIdentity Lease;
            public readonly uint Generation;

            public StreamSessionIdentity(StreamLeaseIdentity lease, uint generation)
            {
                Lease = lease;
                Generation = generation;
            }

            public bool IsValid => Lease.IsValid && Generation != 0;

            public override string ToString() => $"{Lease}, generation={Generation}";
        }

        /// <summary>
        /// What follows the ordinary PONG fields (stream-session-v2.md "Legacy receiver
        /// fallback"): nothing or non-HBS2 bytes (<see cref="Absent"/>; pre-v2 firmware
        /// on a matched reply), an exact valid 32-byte tail (<see cref="Valid"/>), or an
        /// HBS2 marker with a wrong length/version/flags/reserved field
        /// (<see cref="Malformed"/>; ignored for classification and leases).
        /// </summary>
        internal enum StreamLeaseTailStatus
        {
            Absent = 0,
            Valid = 1,
            Malformed = 2,
        }

        /// <summary>Validated HBS2 tail from a PONG, or <c>IsPresent=false</c>.</summary>
        internal readonly struct StreamLeasePongTail
        {
            public readonly StreamLeaseTailStatus Status;
            public readonly bool IsPresent;
            public readonly bool IsLeaseValid;
            public readonly bool IsLeaseSuperseded;
            public readonly ulong EchoedClientIncarnation;
            public readonly StreamLeaseIdentity Lease;
            public readonly uint HighWaterTicket;

            public StreamLeasePongTail(bool isPresent, bool isLeaseValid, bool isLeaseSuperseded,
                ulong echoedClientIncarnation, StreamLeaseIdentity lease, uint highWaterTicket)
            {
                Status = isPresent ? StreamLeaseTailStatus.Valid : StreamLeaseTailStatus.Absent;
                IsPresent = isPresent;
                IsLeaseValid = isLeaseValid;
                IsLeaseSuperseded = isLeaseSuperseded;
                EchoedClientIncarnation = echoedClientIncarnation;
                Lease = lease;
                HighWaterTicket = highWaterTicket;
            }

            private StreamLeasePongTail(StreamLeaseTailStatus status)
            {
                Status = status;
                IsPresent = false;
                IsLeaseValid = false;
                IsLeaseSuperseded = false;
                EchoedClientIncarnation = 0;
                Lease = default;
                HighWaterTicket = 0;
            }

            /// <summary>An HBS2 marker whose tail is not a valid version-1 extension.</summary>
            public static StreamLeasePongTail Malformed =>
                new StreamLeasePongTail(StreamLeaseTailStatus.Malformed);
        }

        /// <summary>Parsed PONG fields plus its optional HBS2 lease tail.</summary>
        internal readonly struct PongExtendedInfo
        {
            public readonly long Timestamp;
            public readonly long ServerTime;
            public readonly string DeviceName;
            public readonly string Address;
            public readonly string FirmwareVersion;
            public readonly int VolumeLevel;
            public readonly int VolumeWiper;
            public readonly StreamLeasePongTail StreamLease;

            public PongExtendedInfo(long timestamp, long serverTime, string deviceName, string address,
                string firmwareVersion, int volumeLevel, int volumeWiper, StreamLeasePongTail streamLease)
            {
                Timestamp = timestamp;
                ServerTime = serverTime;
                DeviceName = deviceName;
                Address = address;
                FirmwareVersion = firmwareVersion;
                VolumeLevel = volumeLevel;
                VolumeWiper = volumeWiper;
                StreamLease = streamLease;
            }
        }

        // Response types (Device → SDK)
        public const byte CMD_PONG = 0x11;
        public const byte CMD_ERROR = 0xFF;

        // Group ID constants
        public const byte GROUP_BROADCAST = 0;
        public const byte GROUP_RESERVED = 255;

        #region Packet Building

        /// <summary>
        /// Build a complete packet with header and payload.
        /// </summary>
        /// <param name="commandType">The command type byte.</param>
        /// <param name="seq">Sequence number for this packet.</param>
        /// <param name="payload">Payload bytes (may be empty).</param>
        /// <returns>Complete packet as byte array.</returns>
        public static byte[] BuildPacket(byte commandType, ushort seq, byte[] payload)
        {
            if (payload == null)
                payload = Array.Empty<byte>();

            int totalSize = HEADER_SIZE + payload.Length;
            if (totalSize > MAX_PACKET_SIZE)
                throw new ArgumentException(
                    $"Packet size {totalSize} exceeds maximum {MAX_PACKET_SIZE} bytes.");

            byte[] packet = new byte[totalSize];
            ushort payloadLength = (ushort)payload.Length;

            // Offset 0: magic (uint16, little-endian)
            WriteUInt16(packet, 0, MAGIC);
            // Offset 2: protocol_version (uint8)
            packet[2] = PROTOCOL_VERSION;
            // Offset 3: command_type (uint8)
            packet[3] = commandType;
            // Offset 4: seq (uint16, little-endian)
            WriteUInt16(packet, 4, seq);
            // Offset 6: payload_length (uint16, little-endian)
            WriteUInt16(packet, 6, payloadLength);
            // Offset 8+: payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, packet, HEADER_SIZE, payload.Length);

            return packet;
        }

        /// <summary>
        /// Build payload for PLAY command.
        /// </summary>
        /// <param name="eventId">Event identifier (null-terminated UTF-8 string).</param>
        /// <param name="targetTimeUs">Target time in microseconds.</param>
        /// <param name="gain">Gain multiplier.</param>
        /// <param name="target">Target address string; "" for broadcast.</param>
        /// <param name="pan">Stereo balance (-1 = left only / 0 = center / +1 = right only).
        /// Clamped to [-1, +1] before it goes on the wire.</param>
        /// <returns>Payload bytes.</returns>
        public static byte[] BuildPlayPayload(string eventId, long targetTimeUs, float gain,
            string target = null, float pan = 0f)
        {
            // Per contracts/specs/message-format.md §0x01 PLAY, the wire format is:
            //   event_id (null-term) + target (null-term) + target_time (int64)
            //   + gain (float32) + pan (float32)
            // target = "" means broadcast. pan is a trailing optional field on the
            // receiving side (absent = 0.0 / center) but the sender always writes it.
            byte[] eventIdBytes = Encoding.UTF8.GetBytes(eventId ?? "");
            byte[] targetBytes  = Encoding.UTF8.GetBytes(target ?? "");

            // Clamp here rather than at each call site so every PLAY on the wire is
            // spec-conformant regardless of who built it. NaN falls through to 0.
            if (float.IsNaN(pan))   pan = 0f;
            else if (pan < -1f)     pan = -1f;
            else if (pan >  1f)     pan =  1f;

            int size = eventIdBytes.Length + 1 + targetBytes.Length + 1 + 8 + 4 + 4;
            byte[] payload = new byte[size];

            int offset = 0;
            // event_id
            Buffer.BlockCopy(eventIdBytes, 0, payload, offset, eventIdBytes.Length);
            offset += eventIdBytes.Length;
            payload[offset++] = 0;
            // target
            Buffer.BlockCopy(targetBytes, 0, payload, offset, targetBytes.Length);
            offset += targetBytes.Length;
            payload[offset++] = 0;
            // target_time
            WriteInt64(payload, offset, targetTimeUs);
            offset += 8;
            // gain
            WriteFloat32(payload, offset, gain);
            offset += 4;
            // pan
            WriteFloat32(payload, offset, pan);

            return payload;
        }

        /// <summary>
        /// Build payload for STOP command.
        /// Wire format: event_id (null-term) + target (null-term).
        /// </summary>
        /// <param name="eventId">Event identifier.</param>
        /// <param name="target">Target address; "" for broadcast.</param>
        public static byte[] BuildStopPayload(string eventId, string target = null)
        {
            byte[] eventIdBytes = Encoding.UTF8.GetBytes(eventId ?? "");
            byte[] targetBytes  = Encoding.UTF8.GetBytes(target ?? "");

            int size = eventIdBytes.Length + 1 + targetBytes.Length + 1;
            byte[] payload = new byte[size];

            int offset = 0;
            Buffer.BlockCopy(eventIdBytes, 0, payload, offset, eventIdBytes.Length);
            offset += eventIdBytes.Length;
            payload[offset++] = 0;
            Buffer.BlockCopy(targetBytes, 0, payload, offset, targetBytes.Length);
            offset += targetBytes.Length;
            payload[offset] = 0;

            return payload;
        }

        /// <summary>
        /// Build payload for STOP_ALL command.
        /// Wire format: target (null-term).
        /// </summary>
        /// <param name="target">Target address; "" for broadcast.</param>
        public static byte[] BuildStopAllPayload(string target = null)
        {
            byte[] targetBytes = Encoding.UTF8.GetBytes(target ?? "");
            byte[] payload = new byte[targetBytes.Length + 1];
            Buffer.BlockCopy(targetBytes, 0, payload, 0, targetBytes.Length);
            payload[targetBytes.Length] = 0;
            return payload;
        }

        /// <summary>
        /// Build payload for PING command.
        /// </summary>
        /// <param name="timestampUs">Local timestamp in microseconds.</param>
        /// <returns>Payload bytes.</returns>
        public static byte[] BuildPingPayload(long timestampUs)
        {
            byte[] payload = new byte[8];
            WriteInt64(payload, 0, timestampUs);
            return payload;
        }

        /// <summary>
        /// Build an extended PING that asks a receiver for a stream lease. This is
        /// still a version-1 PING; a zero incarnation is invalid and never emitted.
        /// </summary>
        public static byte[] BuildPingPayload(long timestampUs, ulong clientIncarnation)
        {
            if (clientIncarnation == 0)
                throw new ArgumentOutOfRangeException(nameof(clientIncarnation),
                    "A stream lease request requires a nonzero client incarnation.");

            byte[] payload = new byte[16];
            WriteInt64(payload, 0, timestampUs);
            WriteUInt64(payload, 8, clientIncarnation);
            return payload;
        }

        /// <summary>
        /// Build payload for CONNECT_STATUS command.
        /// Sent periodically so the device can show connection state on its display/LED.
        /// Payload: connected(1) + group(1) + appName(null-term) + deviceName(null-term)
        /// </summary>
        /// <param name="connected">True if app is connected, false if disconnecting.</param>
        /// <param name="group">The group this sender is targeting.</param>
        /// <param name="appName">App name for OLED display (e.g. "MyVRGame").</param>
        /// <param name="deviceName">Host/device name for OLED display (e.g. "Quest3-Player1").</param>
        public static byte[] BuildConnectStatusPayload(bool connected, byte group,
            string appName = "", string deviceName = "")
        {
            // Belt-and-suspenders: HapbeatConfig.OnValidate already truncates the
            // user-edited appName to MaxAppNameLength, but the runtime fallback
            // (Application.productName) is not constrained. Truncate here so the
            // device never receives a string wider than its display grid.
            string clippedApp = appName ?? "";
            if (clippedApp.Length > HapbeatConfig.MaxAppNameLength)
                clippedApp = clippedApp.Substring(0, HapbeatConfig.MaxAppNameLength);

            byte[] appBytes = Encoding.UTF8.GetBytes(clippedApp);
            byte[] devBytes = Encoding.UTF8.GetBytes(deviceName ?? "");
            // connected(1) + group(1) + appName(null-term) + deviceName(null-term)
            int size = 1 + 1 + appBytes.Length + 1 + devBytes.Length + 1;
            byte[] payload = new byte[size];

            int offset = 0;
            payload[offset] = connected ? (byte)1 : (byte)0;
            offset += 1;
            payload[offset] = group;
            offset += 1;
            Buffer.BlockCopy(appBytes, 0, payload, offset, appBytes.Length);
            offset += appBytes.Length;
            payload[offset] = 0; // null terminator
            offset += 1;
            Buffer.BlockCopy(devBytes, 0, payload, offset, devBytes.Length);
            offset += devBytes.Length;
            payload[offset] = 0; // null terminator

            return payload;
        }

        /// <summary>
        /// Build payload for STREAM_BEGIN command.
        /// </summary>
        internal static byte[] BuildStreamBeginPayload(StreamSessionIdentity identity,
            ushort sampleRate, byte channels, byte format, uint totalSamples, float gain, string target = null)
        {
            ValidateStreamIdentity(identity);
            // Base: sample_rate(2) + channels(1) + format(1) + total_samples(4) + gain(4) = 12
            byte[] targetBytes = string.IsNullOrEmpty(target) ? null : Encoding.UTF8.GetBytes(target);
            int size = STREAM_SESSION_ENVELOPE_SIZE + 12 + (targetBytes != null ? targetBytes.Length + 1 : 0);
            byte[] payload = new byte[size];

            int offset = WriteStreamIdentity(payload, identity);
            WriteUInt16(payload, offset, sampleRate); offset += 2;
            payload[offset] = channels; offset += 1;
            payload[offset] = format; offset += 1;
            WriteUInt32(payload, offset, totalSamples); offset += 4;
            WriteFloat32(payload, offset, gain); offset += 4;

            // Optional: target (null-terminated UTF-8)
            if (targetBytes != null)
            {
                Buffer.BlockCopy(targetBytes, 0, payload, offset, targetBytes.Length);
                offset += targetBytes.Length;
                payload[offset] = 0;
            }

            return payload;
        }

        /// <summary>
        /// Build payload for STREAM_DATA command.
        /// </summary>
        internal static byte[] BuildStreamDataPayload(StreamSessionIdentity identity,
            uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
        {
            ValidateStreamIdentity(identity);
            if (audioData == null) throw new ArgumentNullException(nameof(audioData));
            if (dataOffset < 0 || dataLength < 0 || dataOffset > audioData.Length - dataLength)
                throw new ArgumentOutOfRangeException(nameof(dataLength));

            byte[] payload = new byte[STREAM_SESSION_ENVELOPE_SIZE + 4 + dataLength];
            int offset = WriteStreamIdentity(payload, identity);
            WriteUInt32(payload, offset, byteOffset);
            Buffer.BlockCopy(audioData, dataOffset, payload, offset + 4, dataLength);
            return payload;
        }

        /// <summary>Build the v2 END payload, which is precisely its identity envelope.</summary>
        internal static byte[] BuildStreamEndPayload(StreamSessionIdentity identity)
        {
            ValidateStreamIdentity(identity);
            byte[] payload = new byte[STREAM_SESSION_ENVELOPE_SIZE];
            WriteStreamIdentity(payload, identity);
            return payload;
        }

        /// <summary>
        /// Build a pre-v2 (legacy) STREAM_BEGIN payload for receivers that predate
        /// stream-session-v2 (message-format.md "v1 stream"): no identity envelope.
        /// Byte-identical to the SDK's pre-v2 builder. Sent with <see cref="BuildPacket"/>
        /// (header protocol_version=1).
        /// </summary>
        internal static byte[] BuildLegacyStreamBeginPayload(ushort sampleRate, byte channels, byte format,
            uint totalSamples, float gain, string target = null)
        {
            // Base: sample_rate(2) + channels(1) + format(1) + total_samples(4) + gain(4) = 12
            byte[] targetBytes = string.IsNullOrEmpty(target) ? null : Encoding.UTF8.GetBytes(target);
            int size = 12 + (targetBytes != null ? targetBytes.Length + 1 : 0);
            byte[] payload = new byte[size];

            int offset = 0;
            WriteUInt16(payload, offset, sampleRate); offset += 2;
            payload[offset] = channels; offset += 1;
            payload[offset] = format; offset += 1;
            WriteUInt32(payload, offset, totalSamples); offset += 4;
            WriteFloat32(payload, offset, gain); offset += 4;

            // Optional: target (null-terminated UTF-8)
            if (targetBytes != null)
            {
                Buffer.BlockCopy(targetBytes, 0, payload, offset, targetBytes.Length);
                offset += targetBytes.Length;
                payload[offset] = 0;
            }

            return payload;
        }

        /// <summary>Build a pre-v2 (legacy) STREAM_DATA payload: offset(4) + data, no envelope.</summary>
        internal static byte[] BuildLegacyStreamDataPayload(uint byteOffset, byte[] audioData,
            int dataOffset, int dataLength)
        {
            byte[] payload = new byte[4 + dataLength]; // offset(4) + data
            WriteUInt32(payload, 0, byteOffset);
            Buffer.BlockCopy(audioData, dataOffset, payload, 4, dataLength);
            return payload;
        }

        /// <summary>
        /// Build a pre-v2 (legacy) STREAM_DATA packet stamped protocol_version=1. Uses
        /// the stream MTU limit instead of the command packet limit.
        /// </summary>
        internal static byte[] BuildLegacyStreamDataPacket(ushort seq, uint byteOffset, byte[] audioData,
            int dataOffset, int dataLength)
        {
            byte[] payload = BuildLegacyStreamDataPayload(byteOffset, audioData, dataOffset, dataLength);
            int totalSize = HEADER_SIZE + payload.Length;
            if (totalSize > MAX_STREAM_PACKET_SIZE)
                throw new ArgumentException(
                    $"Stream packet size {totalSize} exceeds MTU limit {MAX_STREAM_PACKET_SIZE} bytes.");

            byte[] packet = new byte[totalSize];
            WriteUInt16(packet, 0, MAGIC);
            packet[2] = PROTOCOL_VERSION;
            packet[3] = CMD_STREAM_DATA;
            WriteUInt16(packet, 4, seq);
            WriteUInt16(packet, 6, (ushort)payload.Length);
            Buffer.BlockCopy(payload, 0, packet, HEADER_SIZE, payload.Length);
            return packet;
        }

        /// <summary>
        /// Build a STREAM_DATA packet. Uses larger size limit than command packets.
        /// </summary>
        internal static byte[] BuildStreamPacket(byte commandType, ushort seq, byte[] payload)
        {
            if (commandType != CMD_STREAM_BEGIN && commandType != CMD_STREAM_DATA && commandType != CMD_STREAM_END)
                throw new ArgumentOutOfRangeException(nameof(commandType), "Only stream commands use protocol version 2.");
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            int totalSize = HEADER_SIZE + payload.Length;
            if (totalSize > MAX_STREAM_PACKET_SIZE)
                throw new ArgumentException(
                    $"Stream packet size {totalSize} exceeds MTU limit {MAX_STREAM_PACKET_SIZE} bytes.");

            byte[] packet = new byte[totalSize];
            WriteUInt16(packet, 0, MAGIC);
            packet[2] = STREAM_PROTOCOL_VERSION;
            packet[3] = commandType;
            WriteUInt16(packet, 4, seq);
            WriteUInt16(packet, 6, (ushort)payload.Length);
            Buffer.BlockCopy(payload, 0, packet, HEADER_SIZE, payload.Length);
            return packet;
        }

        #endregion

        #region Packet Parsing

        /// <summary>
        /// Parse a received packet into its components.
        /// </summary>
        /// <param name="data">Raw packet bytes.</param>
        /// <returns>Tuple of (commandType, seq, payload).</returns>
        /// <exception cref="ArgumentException">Thrown if packet is malformed.</exception>
        public static (byte commandType, ushort seq, byte[] payload) ParsePacket(byte[] data)
        {
            if (data == null || data.Length < HEADER_SIZE)
                throw new ArgumentException("Packet too short to contain header.");

            ushort magic = ReadUInt16(data, 0);
            if (magic != MAGIC)
                throw new ArgumentException(
                    $"Invalid magic bytes: 0x{magic:X4}, expected 0x{MAGIC:X4}.");

            byte version = data[2];
            bool isV2Stream = version == STREAM_PROTOCOL_VERSION &&
                (data[3] == CMD_STREAM_BEGIN || data[3] == CMD_STREAM_DATA || data[3] == CMD_STREAM_END);
            if (version != PROTOCOL_VERSION && !isV2Stream)
                throw new ArgumentException(
                    $"Unsupported protocol version: {version}.");

            byte commandType = data[3];
            ushort seq = ReadUInt16(data, 4);
            ushort payloadLength = ReadUInt16(data, 6);

            if (data.Length < HEADER_SIZE + payloadLength)
                throw new ArgumentException(
                    $"Packet truncated: expected {HEADER_SIZE + payloadLength} bytes, got {data.Length}.");

            byte[] payload = new byte[payloadLength];
            if (payloadLength > 0)
                Buffer.BlockCopy(data, HEADER_SIZE, payload, 0, payloadLength);

            return (commandType, seq, payload);
        }

        /// <summary>
        /// Parse a PONG response payload.
        /// </summary>
        /// <param name="payload">PONG payload bytes.</param>
        /// <returns>Tuple of (timestamp, serverTime) in microseconds.</returns>
        public static (long timestamp, long serverTime) ParsePong(byte[] payload)
        {
            if (payload == null || payload.Length < 16)
                throw new ArgumentException("PONG payload too short, expected at least 16 bytes.");

            long timestamp = ReadInt64(payload, 0);
            long serverTime = ReadInt64(payload, 8);
            return (timestamp, serverTime);
        }

        /// <summary>
        /// Parse a PONG response payload including the device-addressing extension
        /// fields (device-addressing.md §5.4 / message-format.md §0x11): device_name,
        /// address, firmware_version, volume_level, volume_wiper — appended after the
        /// base timestamp/server_time pair in that order, each string null-terminated.
        /// <para>
        /// The extension is best-effort: a legacy/short/truncated payload (or one from
        /// a future/older firmware that packs fewer fields) yields <c>null</c> strings
        /// and <c>-1</c> byte fields for whatever wasn't present, rather than throwing —
        /// only the base 16-byte timestamp/server_time pair is required (same
        /// precondition as <see cref="ParsePong"/>). Callers that need the device's
        /// address for target matching (see <see cref="HapbeatClient.AddressMatches"/>)
        /// must treat a null <c>address</c> as "unknown, assume it matches" rather than
        /// as a hard failure.
        /// </para>
        /// </summary>
        public static (long timestamp, long serverTime, string deviceName, string address,
            string firmwareVersion, int volumeLevel, int volumeWiper) ParsePongExtended(byte[] payload)
        {
            PongExtendedInfo parsed = ParsePongExtendedInfo(payload);
            return (parsed.Timestamp, parsed.ServerTime, parsed.DeviceName, parsed.Address,
                parsed.FirmwareVersion, parsed.VolumeLevel, parsed.VolumeWiper);
        }

        /// <summary>
        /// Parse the ordinary PONG fields and, when present, the exact 32-byte HBS2
        /// stream-lease tail. A malformed or absent tail is not a lease response.
        /// </summary>
        internal static PongExtendedInfo ParsePongExtendedInfo(byte[] payload)
        {
            var (timestamp, serverTime) = ParsePong(payload);

            string deviceName = null;
            string address = null;
            string firmwareVersion = null;
            int volumeLevel = -1;
            int volumeWiper = -1;

            // No try/catch here: ReadNullTerminatedString and the volumeLevel/
            // volumeWiper reads below are all bounds-checked against payload.Length
            // before indexing (see ReadNullTerminatedString), so a malformed/
            // truncated extension tail simply yields null/-1 for the missing
            // fields rather than throwing — there is no exception this could catch.
            int offset = 16;
            deviceName = ReadNullTerminatedString(payload, ref offset);
            address = ReadNullTerminatedString(payload, ref offset);
            firmwareVersion = ReadNullTerminatedString(payload, ref offset);
            if (offset < payload.Length) volumeLevel = payload[offset++];
            if (offset < payload.Length) volumeWiper = payload[offset++];

            StreamLeasePongTail leaseTail = ParseStreamLeasePongTail(payload, offset);
            return new PongExtendedInfo(timestamp, serverTime, deviceName, address,
                firmwareVersion, volumeLevel, volumeWiper, leaseTail);
        }

        private static StreamLeasePongTail ParseStreamLeasePongTail(byte[] payload, int offset)
        {
            const int tailSize = 32;
            int remaining = payload.Length - offset;
            bool hasMarker = remaining >= 4 &&
                payload[offset] == (byte)'H' && payload[offset + 1] == (byte)'B' &&
                payload[offset + 2] == (byte)'S' && payload[offset + 3] == (byte)'2';
            // No marker after the ordinary fields (nothing follows, or other bytes
            // do) is how pre-v2 firmware answers an extended PING.
            if (!hasMarker)
                return default;
            if (remaining != tailSize ||
                payload[offset + 4] != 1 || (payload[offset + 5] & ~0x03) != 0 ||
                ReadUInt16(payload, offset + 6) != 0)
            {
                return StreamLeasePongTail.Malformed;
            }

            byte flags = payload[offset + 5];
            ulong incarnation = ReadUInt64(payload, offset + 8);
            var lease = new StreamLeaseIdentity(ReadUInt64(payload, offset + 16),
                ReadUInt32(payload, offset + 24));
            uint highWaterTicket = ReadUInt32(payload, offset + 28);
            bool leaseValid = (flags & 0x01) != 0 && lease.IsValid;
            bool superseded = (flags & 0x02) != 0;
            return new StreamLeasePongTail(true, leaseValid, superseded,
                incarnation, lease, highWaterTicket);
        }

        /// <summary>
        /// Read a null-terminated UTF-8 string starting at <paramref name="offset"/>,
        /// advancing <paramref name="offset"/> past the terminator. Returns null
        /// (without advancing) if <paramref name="offset"/> is already at or past the
        /// end of the buffer — i.e. this field simply wasn't present in the payload.
        /// </summary>
        private static string ReadNullTerminatedString(byte[] buffer, ref int offset)
        {
            if (offset >= buffer.Length) return null;

            int start = offset;
            int end = start;
            while (end < buffer.Length && buffer[end] != 0) end++;

            string value = Encoding.UTF8.GetString(buffer, start, end - start);
            offset = (end < buffer.Length) ? end + 1 : end; // skip null terminator if present
            return value;
        }

        /// <summary>
        /// Parse an ERROR response payload.
        /// </summary>
        /// <param name="payload">ERROR payload bytes.</param>
        /// <returns>Tuple of (errorCode, message).</returns>
        public static (ushort errorCode, string message) ParseError(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
                throw new ArgumentException("ERROR payload too short, expected at least 2 bytes.");

            ushort errorCode = ReadUInt16(payload, 0);

            string message = string.Empty;
            if (payload.Length > 2)
            {
                // Find null terminator or use remaining length
                int messageLength = payload.Length - 2;
                for (int i = 2; i < payload.Length; i++)
                {
                    if (payload[i] == 0)
                    {
                        messageLength = i - 2;
                        break;
                    }
                }
                message = Encoding.UTF8.GetString(payload, 2, messageLength);
            }

            return (errorCode, message);
        }

        #endregion

        #region Endian-Safe Read/Write Helpers

        private static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 2);
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 8);
        }

        private static void WriteInt64(byte[] buffer, int offset, long value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 8);
        }

        private static void WriteFloat32(byte[] buffer, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToUInt16(buffer, offset);
            }
            else
            {
                byte[] temp = new byte[2];
                Buffer.BlockCopy(buffer, offset, temp, 0, 2);
                Array.Reverse(temp);
                return BitConverter.ToUInt16(temp, 0);
            }
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt32(buffer, offset);
            byte[] temp = new byte[4];
            Buffer.BlockCopy(buffer, offset, temp, 0, 4);
            Array.Reverse(temp);
            return BitConverter.ToUInt32(temp, 0);
        }

        private static ulong ReadUInt64(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
                return BitConverter.ToUInt64(buffer, offset);
            byte[] temp = new byte[8];
            Buffer.BlockCopy(buffer, offset, temp, 0, 8);
            Array.Reverse(temp);
            return BitConverter.ToUInt64(temp, 0);
        }

        private static int WriteStreamIdentity(byte[] payload, StreamSessionIdentity identity)
        {
            WriteUInt64(payload, 0, identity.Lease.DeviceBootId);
            WriteUInt32(payload, 8, identity.Lease.LeaseTicket);
            WriteUInt32(payload, 12, identity.Generation);
            return STREAM_SESSION_ENVELOPE_SIZE;
        }

        private static void ValidateStreamIdentity(StreamSessionIdentity identity)
        {
            if (!identity.IsValid)
                throw new ArgumentException("A v2 stream packet requires a valid boot ID, lease ticket, and generation.",
                    nameof(identity));
        }

        private static long ReadInt64(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToInt64(buffer, offset);
            }
            else
            {
                byte[] temp = new byte[8];
                Buffer.BlockCopy(buffer, offset, temp, 0, 8);
                Array.Reverse(temp);
                return BitConverter.ToInt64(temp, 0);
            }
        }

        #endregion
    }
}
