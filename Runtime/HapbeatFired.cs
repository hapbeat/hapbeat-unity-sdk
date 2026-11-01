using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// One haptic fire, as reported by <see cref="HapbeatManager.Fired"/>. Raised at
    /// the moment the game fires (after cooldown / enable / "already playing" checks,
    /// before any delayed send), so <see cref="frame"/> is the frame the game fired on.
    /// Intended for recording a scene (contracts <c>scene-recording.md</c> §5: write
    /// <c>source = "unity:" + &lt;guid of the map asset&gt; + "/" + entryId</c>; a duplicated Event Map
    /// keeps its entries' ids, so the map guid tells them apart) without changing game code.
    /// </summary>
    public sealed class HapbeatFired
    {
        /// <summary>Stable id (<see cref="HapbeatEventEntry.id"/>) of the Event Map entry that fired,
        /// or null for a direct <see cref="HapbeatManager.Play"/> / <see cref="HapbeatManager.StreamAudioClip(AudioClip, float, string, bool)"/>
        /// call that did not come from an entry.</summary>
        public readonly string entryId;

        /// <summary>Event id (Command mode, <c>category.name</c>). For a StreamClip entry, the entry's
        /// event id if it has one; null for a direct <see cref="HapbeatManager.StreamAudioClip(AudioClip, float, string, bool)"/> call.</summary>
        public readonly string eventId;

        /// <summary>The entry's display name; for a direct call, the <c>displayName</c> passed to
        /// <see cref="HapbeatManager.Play"/> or the clip name for a stream. May be null or empty.</summary>
        public readonly string displayName;

        /// <summary>Command (device plays its Kit clip) or StreamClip (the SDK streams an AudioClip).</summary>
        public readonly HapticMode mode;

        /// <summary>
        /// The gain multiplier the game applied for this fire. For an entry: the factor on
        /// top of the entry's own gain (clip_gain × entry gain), e.g. the trigger's
        /// gain multiplier, a velocity curve value, a StreamGain binding's initial value
        /// or <c>FireWithStartGain</c>'s argument; 1 = the entry as authored. For a direct
        /// call (<see cref="entryId"/> null): the gain passed to the call.
        /// </summary>
        public readonly float gain;

        /// <summary>Device-addressing target string as authored (before the address override), or null = all devices.</summary>
        public readonly string target;

        /// <summary>True when the fire starts a looping stream (StreamClip with loop on).</summary>
        public readonly bool loop;

        /// <summary>Event Map the entry belongs to, or null for a direct call.</summary>
        public readonly HapbeatEventMap map;

        /// <summary>The component that fired (the trigger or the state behaviour), or null for a direct call.</summary>
        public readonly Object sender;

        /// <summary><see cref="Time.frameCount"/> when the game fired.</summary>
        public readonly int frame;

        /// <summary><see cref="Time.timeAsDouble"/> when the game fired (seconds, scaled game time).</summary>
        public readonly double time;

        internal HapbeatFired(string entryId, string eventId, string displayName, HapticMode mode, float gain,
            string target, bool loop, HapbeatEventMap map, Object sender)
        {
            this.entryId = entryId;
            this.eventId = eventId;
            this.displayName = displayName;
            this.mode = mode;
            this.gain = gain;
            this.target = target;
            this.loop = loop;
            this.map = map;
            this.sender = sender;
            frame = Time.frameCount;
            time = Time.timeAsDouble;
        }

        /// <summary>
        /// Report of an Event Map entry firing with <paramref name="gain"/> on top of the
        /// entry's own gain. Null when the entry cannot play (Command without an event id,
        /// StreamClip without a clip): such a fire is rejected when it is sent.
        /// </summary>
        internal static HapbeatFired FromEntry(HapbeatEventEntry entry, HapbeatEventMap map, float gain, bool loop,
            Object sender)
        {
            if (!CanPlay(entry)) return null;
            return new HapbeatFired(entry.id, entry.eventId, entry.displayName, entry.mode, gain,
                entry.HasTarget ? entry.target : null, loop, map, sender);
        }

        /// <summary>True when <paramref name="entry"/> has what its mode needs to play.</summary>
        internal static bool CanPlay(HapbeatEventEntry entry)
        {
            if (entry == null) return false;
            return entry.mode == HapticMode.StreamClip
                ? entry.streamClip != null
                : !string.IsNullOrEmpty(entry.eventId);
        }
    }
}
