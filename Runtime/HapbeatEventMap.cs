using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Central registry of all haptic events for a project.
    /// Triggers reference entries in this map, so changing an event ID or gain here
    /// propagates to every trigger automatically.
    /// Create via Assets > Create > Hapbeat > Event Map.
    /// </summary>
    [CreateAssetMenu(fileName = "HapbeatEventMap", menuName = "Hapbeat/Event Map", order = 2)]
    public class HapbeatEventMap : ScriptableObject
    {
        [Tooltip("All haptic event definitions for this project.")]
        public List<HapbeatEventEntry> entries = new List<HapbeatEventEntry>();

        [Tooltip("Editor-only. If true, any modifications made to this EventMap while in " +
                 "Play mode are reverted on Play exit. Useful for exploratory tuning " +
                 "without accidentally persisting values. Leave off to keep Unity's " +
                 "default behaviour (Play-mode edits to ScriptableObjects persist).")]
        public bool revertPlayModeChanges = false;

        /// <summary>
        /// Get an entry by index, or null if out of range.
        /// </summary>
        public HapbeatEventEntry GetEntry(int index)
        {
            if (index < 0 || index >= entries.Count)
                return null;
            return entries[index];
        }

        /// <summary>
        /// Get an entry by display name, or null if not found.
        /// </summary>
        public HapbeatEventEntry FindByName(string displayName)
        {
            return entries.Find(e => e.displayName == displayName);
        }

        /// <summary>
        /// Get an entry by event ID, or null if not found.
        /// </summary>
        public HapbeatEventEntry FindByEventId(string eventId)
        {
            return entries.Find(e => e.eventId == eventId);
        }

        /// <summary>
        /// Get display names for all entries (useful for editor dropdowns). Each line
        /// is prefixed with the entry's mode icon so Command / StreamClip / StreamSource
        /// are distinguishable at a glance.
        /// </summary>
        public string[] GetDisplayNames()
        {
            var names = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                string label = string.IsNullOrEmpty(e.displayName) ? e.eventId : e.displayName;
                string icon = e.GetModeIcon();
                names[i] = string.IsNullOrEmpty(icon)
                    ? $"[{i}] {label}"
                    : $"[{i}] {icon} {label}";
            }
            return names;
        }
    }
}
