using System;

namespace Hapbeat
{
    /// <summary>
    /// Data class representing a Hapbeat device.
    /// This is a plain C# object (not a MonoBehaviour) used to store device information
    /// received from the Bridge.
    /// </summary>
    [Serializable]
    public class HapbeatDevice
    {
        /// <summary>Unique identifier of the device.</summary>
        public string deviceId;

        /// <summary>Human-readable name of the device.</summary>
        public string name;

        /// <summary>Group ID this device belongs to (0 = broadcast, 1-254 = group, 255 = reserved).</summary>
        public byte group;

        /// <summary>Firmware version string.</summary>
        public string firmwareVersion;

        /// <summary>Battery level as a normalized value (0.0 to 1.0).</summary>
        public float batteryLevel;

        /// <summary>Timestamp of the last communication with this device.</summary>
        public DateTime lastSeen;

        public HapbeatDevice()
        {
            deviceId = string.Empty;
            name = string.Empty;
            group = 0;
            firmwareVersion = string.Empty;
            batteryLevel = 0f;
            lastSeen = DateTime.MinValue;
        }

        public HapbeatDevice(string deviceId, string name, byte group)
        {
            this.deviceId = deviceId;
            this.name = name;
            this.group = group;
            firmwareVersion = string.Empty;
            batteryLevel = 0f;
            lastSeen = DateTime.UtcNow;
        }

        public override string ToString()
        {
            return $"HapbeatDevice(id={deviceId}, name={name}, group={group}, " +
                   $"fw={firmwareVersion}, battery={batteryLevel:P0}, lastSeen={lastSeen})";
        }
    }
}
