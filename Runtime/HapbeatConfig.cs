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
        [Header("Connection")]
        [Tooltip("UDP port for communication with Hapbeat devices.")]
        public int port = 7700;

        [Tooltip("Target group ID. -1 = no group filter (default). 0 = broadcast to all. 1-254 = specific group.")]
        [Range(-1, 254)]
        public int group = -1;

        [Header("App Info")]
        [Tooltip("デバイスの OLED に表示されるアプリ名（最大8文字）。空欄の場合は Application.productName を使用。")]
        public string appName = "";

        [Header("Bridge (ESP-NOW)")]
        [Tooltip("Use Bridge for ESP-NOW multi-device transmission. When disabled (default), connects directly to devices via Wi-Fi UDP.")]
        public bool useBridge = false;

        [Tooltip("Hostname or IP address of the Hapbeat Bridge server. Only used when useBridge is enabled.")]
        public string bridgeHost = "127.0.0.1";

        [Header("Discovery")]
        [Tooltip("Discovery timeout in milliseconds.")]
        [Range(1000, 10000)]
        public int discoveryTimeoutMs = 3000;

        [Header("Behavior")]
        [Tooltip("Interval in seconds between keep-alive ping messages.")]
        [Range(1f, 60f)]
        public float pingInterval = 5.0f;

        [Header("Debugging")]
        [Tooltip("Enable detailed logging to the Unity console.")]
        public bool enableLogging = true;
    }
}
