using System;
using System.Text;

namespace Hapbeat
{
    /// <summary>
    /// Static utility class for building and parsing Hapbeat Bridge protocol packets.
    /// All multi-byte values are little-endian. Max packet size is 512 bytes.
    /// </summary>
    public static class HapbeatProtocol
    {
        /// <summary>Magic bytes "HB" as uint16 little-endian (0x4842).</summary>
        public const ushort MAGIC = 0x4842;

        /// <summary>Current protocol version.</summary>
        public const byte PROTOCOL_VERSION = 0x01;

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
        /// <param name="group">Target group ID.</param>
        /// <param name="gain">Gain multiplier.</param>
        /// <returns>Payload bytes.</returns>
        public static byte[] BuildPlayPayload(string eventId, long targetTimeUs, byte group, float gain,
            string target = null)
        {
            byte[] eventIdBytes = Encoding.UTF8.GetBytes(eventId);
            byte[] targetBytes = string.IsNullOrEmpty(target) ? null : Encoding.UTF8.GetBytes(target);

            // event_id (null-terminated) + target_time (int64) + group (uint8) + gain (float32)
            // + optional: target (null-terminated)
            int size = eventIdBytes.Length + 1 + 8 + 1 + 4;
            if (targetBytes != null) size += targetBytes.Length + 1;
            byte[] payload = new byte[size];

            int offset = 0;

            // event_id (null-terminated UTF-8)
            Buffer.BlockCopy(eventIdBytes, 0, payload, offset, eventIdBytes.Length);
            offset += eventIdBytes.Length;
            payload[offset] = 0;
            offset += 1;

            // target_time (int64, little-endian)
            WriteInt64(payload, offset, targetTimeUs);
            offset += 8;

            // target_group (uint8) — legacy, ignored by device if target is present
            payload[offset] = group;
            offset += 1;

            // gain (float32, little-endian)
            WriteFloat32(payload, offset, gain);
            offset += 4;

            // target (null-terminated UTF-8, optional)
            if (targetBytes != null)
            {
                Buffer.BlockCopy(targetBytes, 0, payload, offset, targetBytes.Length);
                offset += targetBytes.Length;
                payload[offset] = 0;
            }

            return payload;
        }

        /// <summary>
        /// Build payload for STOP command.
        /// </summary>
        /// <param name="eventId">Event identifier (null-terminated UTF-8 string).</param>
        /// <param name="group">Target group ID.</param>
        /// <returns>Payload bytes.</returns>
        public static byte[] BuildStopPayload(string eventId, byte group)
        {
            byte[] eventIdBytes = Encoding.UTF8.GetBytes(eventId);
            // event_id (null-terminated) + group (uint8)
            int size = eventIdBytes.Length + 1 + 1;
            byte[] payload = new byte[size];

            int offset = 0;

            // event_id (null-terminated UTF-8)
            Buffer.BlockCopy(eventIdBytes, 0, payload, offset, eventIdBytes.Length);
            offset += eventIdBytes.Length;
            payload[offset] = 0; // null terminator
            offset += 1;

            // target_group (uint8)
            payload[offset] = group;

            return payload;
        }

        /// <summary>
        /// Build payload for STOP_ALL command.
        /// </summary>
        /// <param name="group">Target group ID.</param>
        /// <returns>Payload bytes.</returns>
        public static byte[] BuildStopAllPayload(byte group)
        {
            return new byte[] { group };
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
            byte[] appBytes = Encoding.UTF8.GetBytes(appName ?? "");
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
        public static byte[] BuildStreamBeginPayload(ushort sampleRate, byte channels, byte format,
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

        /// <summary>
        /// Build payload for STREAM_DATA command.
        /// </summary>
        public static byte[] BuildStreamDataPayload(uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
        {
            byte[] payload = new byte[4 + dataLength]; // offset(4) + data
            WriteUInt32(payload, 0, byteOffset);
            Buffer.BlockCopy(audioData, dataOffset, payload, 4, dataLength);
            return payload;
        }

        /// <summary>
        /// Build a STREAM_DATA packet. Uses larger size limit than command packets.
        /// </summary>
        public static byte[] BuildStreamDataPacket(ushort seq, uint byteOffset, byte[] audioData, int dataOffset, int dataLength)
        {
            byte[] payload = BuildStreamDataPayload(byteOffset, audioData, dataOffset, dataLength);
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
            if (version != PROTOCOL_VERSION)
                throw new ArgumentException(
                    $"Unsupported protocol version: {version}, expected {PROTOCOL_VERSION}.");

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
