using System;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Sound / haptics timing (DEC-093). The haptic of one firing goes
    /// <c>M + E</c> after the app's sound, where
    /// <list type="bullet">
    /// <item><c>M</c> = sound sync point − haptic sync point: the materials' own timing, from the cue table
    /// (<c>sounds.&lt;name&gt;.syncMs</c> / <c>clips.&lt;clip&gt;.syncMs</c>, ms from each file's start; 0 when absent);</item>
    /// <item><c>E</c> = <see cref="OffsetMs"/>: this device's audio output latency minus the haptic's arrival latency,
    /// set by a person feeling both (an in-app slider), never by the cue table.</item>
    /// </list>
    /// When <c>M + E</c> is negative the sound waits instead (nothing is cut); see <see cref="Split"/>.
    /// Triggers apply the positive part of <c>E</c> by themselves; an app that plays its own sound calls
    /// <see cref="Split"/> and fires with the returned delays (sound: <c>AudioSource.PlayScheduled</c>, haptic:
    /// <see cref="HapbeatUnityEventTrigger.FireAfter"/>).
    /// </summary>
    public static class HapbeatAvSync
    {
        /// <summary>PlayerPrefs key of the saved offset (per installed app = per device).</summary>
        public const string PrefsKey = "Hapbeat.AvOffsetMs";
        public const float MinOffsetMs = -300f;
        public const float MaxOffsetMs = 300f;

        private static float _defaultOffsetMs;
        private static float? _cached;

        /// <summary>
        /// The offset used when none was saved on this device (e.g. a per-model value an app ships), ms.
        /// </summary>
        public static float DefaultOffsetMs
        {
            get => _defaultOffsetMs;
            set
            {
                float before = OffsetMs;
                _defaultOffsetMs = Clamp(value);
                if (!HasSavedOffset && !Mathf.Approximately(before, OffsetMs)) Notify();
            }
        }

        /// <summary>True when this device saved its own offset (it then wins over <see cref="DefaultOffsetMs"/>).</summary>
        public static bool HasSavedOffset => Saved.HasValue;

        /// <summary>
        /// This device's offset E (ms, <see cref="MinOffsetMs"/>..<see cref="MaxOffsetMs"/>): + = the haptic that
        /// much later than the sound, − = the sound that much later. Setting it saves it on the device
        /// (PlayerPrefs) and raises <see cref="Changed"/>; read on the main thread.
        /// </summary>
        public static float OffsetMs
        {
            get => Saved ?? _defaultOffsetMs;
            set
            {
                float next = Clamp(value);
                if (Saved.HasValue && Mathf.Approximately(Saved.Value, next)) return;
                _cached = next;
                PlayerPrefs.SetFloat(PrefsKey, next);
                PlayerPrefs.Save();
                Notify();
            }
        }

        /// <summary>Forgets the saved offset: <see cref="DefaultOffsetMs"/> applies again.</summary>
        public static void ClearSavedOffset()
        {
            if (!HasSavedOffset) return;
            PlayerPrefs.DeleteKey(PrefsKey);
            PlayerPrefs.Save();
            _cached = null;
            Notify();
        }

        /// <summary>Raised on the main thread when <see cref="OffsetMs"/> changes (value in ms).</summary>
        public static event Action<float> Changed;

        /// <summary>The haptic-only part of the offset (s, ≥ 0) that triggers wait before sending.</summary>
        public static float HapticDelaySeconds => Mathf.Max(0f, OffsetMs) / 1000f;

        /// <summary>
        /// Delays (s, ≥ 0, one of them 0) for one firing of a sound and a haptic: the haptic goes
        /// <c>(soundSyncMs − hapticSyncMs) + OffsetMs</c> after the sound, or the sound waits when that is negative.
        /// <paramref name="includeOffset"/> false = the materials' timing only (M).
        /// </summary>
        public static void Split(double soundSyncMs, double hapticSyncMs, out double soundDelaySeconds,
            out double hapticDelaySeconds, bool includeOffset = true)
        {
            double totalMs = soundSyncMs - hapticSyncMs + (includeOffset ? OffsetMs : 0f);
            soundDelaySeconds = totalMs < 0 ? -totalMs / 1000.0 : 0;
            hapticDelaySeconds = totalMs > 0 ? totalMs / 1000.0 : 0;
        }

        private static float? Saved
        {
            get
            {
                if (_cached.HasValue) return _cached;
                if (!PlayerPrefs.HasKey(PrefsKey)) return null;
                _cached = Clamp(PlayerPrefs.GetFloat(PrefsKey));
                return _cached;
            }
        }

        private static float Clamp(float ms) => float.IsNaN(ms) ? 0f : Mathf.Clamp(ms, MinOffsetMs, MaxOffsetMs);

        private static void Notify()
        {
            HapbeatManager.NotifyHapticDelayChanged();
            Changed?.Invoke(OffsetMs);
        }

        // Domain reload off (Enter Play Mode options): drop the cache so a PlayerPrefs edit between plays is read again.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _cached = null; _defaultOffsetMs = 0f; Changed = null; }
    }
}
