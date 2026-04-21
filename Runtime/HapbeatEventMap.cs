using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Central registry of all haptic events for a project.
    /// Triggers reference entries by <b>stable GUID</b> (<see cref="HapbeatEventEntry.id"/>),
    /// so reordering, inserting, or duplicating entries cannot silently break
    /// existing trigger wiring.
    /// <para>
    /// List-index (<c>GetEntry(int)</c>) lookup is still supported for legacy
    /// triggers and for migration, but new code should prefer
    /// <see cref="FindById"/>.
    /// </para>
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
        /// Get an entry by list index, or null if out of range.
        /// Prefer <see cref="FindById"/> for trigger lookups — index-based access
        /// is fragile against reorder / insert / delete.
        /// </summary>
        public HapbeatEventEntry GetEntry(int index)
        {
            if (index < 0 || index >= entries.Count)
                return null;
            return entries[index];
        }

        /// <summary>
        /// Look up an entry by its stable <see cref="HapbeatEventEntry.id"/>.
        /// Returns null for empty / unknown ids.
        /// </summary>
        public HapbeatEventEntry FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && e.HasId && e.id == id)
                    return e;
            }
            return null;
        }

        /// <summary>Index of the entry with the given stable id, or -1 if not found.</summary>
        public int IndexOfId(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && e.HasId && e.id == id)
                    return i;
            }
            return -1;
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
        /// is prefixed with the entry's mode icon so Command / StreamClip
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
