using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// ScriptableObject holding Hapbeat SDK connection and behavior settings.
    /// Create via Assets > Create > Hapbeat > Config.
    /// </summary>
    [CreateAssetMenu(fileName = "HapbeatConfig", menuName = "Hapbeat/Config", order = 1)]
    public class HapbeatConfig : ScriptableObject
    {
        [Header("Bridge Connection")]
        [Tooltip("Hostname or IP address of the Hapbeat Bridge server.")]
        public string bridgeHost = "127.0.0.1";

        [Tooltip("UDP port of the Hapbeat Bridge server.")]
        public int bridgePort = 7700;

        [Header("Behavior")]
        [Tooltip("Automatically connect to the Bridge on startup.")]
        public bool autoConnect = true;

        [Tooltip("Interval in seconds between keep-alive ping messages.")]
        [Range(1f, 60f)]
        public float pingInterval = 5.0f;

        [Header("Debugging")]
        [Tooltip("Enable detailed logging to the Unity console.")]
        public bool enableLogging = true;
    }
}
