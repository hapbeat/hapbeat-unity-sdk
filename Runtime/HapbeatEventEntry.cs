using System;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// A single haptic event definition within a HapbeatEventMap.
    /// Centralizes event ID, gain, and group so all triggers reference one source of truth.
    /// </summary>
    [Serializable]
    public class HapbeatEventEntry
    {
        [Tooltip("Human-readable label for this event (e.g. \"Landing Impact\").")]
        public string displayName = "";

        [Tooltip("Hapbeat event ID sent to devices (e.g. \"impact.landing\").")]
        public string eventId = "";

        [Tooltip("Gain multiplier. 0.0 to 2.0.")]
        [Range(0f, 2f)]
        public float gain = 1.0f;

        [Tooltip("Target group ID. -1 = use config default, 0 = all devices.")]
        public int group = -1;

        [Tooltip("Designer notes (not sent to devices).")]
        [TextArea(1, 3)]
        public string notes = "";
    }
}
