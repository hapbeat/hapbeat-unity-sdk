using System.Threading;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Handle to an active StreamClip playback. Exposes thread-safe runtime
    /// controls (<see cref="Gain"/>, <see cref="Pan"/>) that
    /// <see cref="HapbeatParameterBinding"/> can write to each frame for
    /// continuous haptic modulation.
    ///
    /// <para>
    /// The streaming coroutine (in <see cref="HapbeatManager"/>) reads these
    /// values per-chunk, applies them to the PCM samples before they hit the
    /// wire, and sends STREAM_DATA with the modulated audio. No protocol
    /// extension is required — dynamic volume / panning are implemented
    /// entirely on the SDK side.
    /// </para>
    ///
    /// <para>
    /// Pan semantics: −1 = full left, 0 = centered, +1 = full right. For mono
    /// clips the pan value is ignored (there is only one channel to scale).
    /// An equal-power panning law is used for stereo so a center pan preserves
    /// perceived loudness.
    /// </para>
    /// </summary>
    public sealed class HapbeatStreamPlayback
    {
        private float _gain;
        private float _pan;
        private int _stopped; // 0 = running, 1 = stopped

        /// <summary>
        /// The gain the entry authored (entry.gain × manifest.intensity),
        /// captured at stream start. <see cref="HapbeatParameterBinding"/>
        /// multiplies this by its modulation output so a bound stream is
        /// <c>BaselineGain × bindingOutput</c> — author intent ("full press
        /// = authored strength") is preserved regardless of which bindings
        /// are attached.
        /// </summary>
        public float BaselineGain { get; }

        internal HapbeatStreamPlayback(float initialGain)
        {
            BaselineGain = initialGain;
            Volatile.Write(ref _gain, initialGain);
            Volatile.Write(ref _pan, 0f);
            Volatile.Write(ref _stopped, 0);
        }

        /// <summary>
        /// Overall gain multiplier applied to every sample before sending.
        /// Thread-safe; the streaming coroutine reads this per chunk.
        /// Clamped to <c>[0, 2]</c> on write.
        /// </summary>
        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, Mathf.Clamp(value, 0f, 2f));
        }

        /// <summary>
        /// Stereo pan, <c>−1</c> (full left) .. <c>+1</c> (full right).
        /// Ignored for mono clips. Thread-safe.
        /// </summary>
        public float Pan
        {
            get => Volatile.Read(ref _pan);
            set => Volatile.Write(ref _pan, Mathf.Clamp(value, -1f, 1f));
        }

        /// <summary>True once <see cref="Stop"/> has been called (or the clip
        /// has finished playing on its own for non-loop entries).</summary>
        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        /// <summary>True while the stream is still active.</summary>
        public bool IsActive => !IsStopped;

        /// <summary>
        /// Request the stream to stop. The streaming coroutine checks this
        /// between chunks and sends <c>STREAM_END</c> shortly after. Safe to
        /// call from any thread.
        /// </summary>
        public void Stop()
        {
            Volatile.Write(ref _stopped, 1);
        }

        /// <summary>Internal: mark the stream as finished after the coroutine exits.</summary>
        internal void MarkStopped()
        {
            Volatile.Write(ref _stopped, 1);
        }

        /// <summary>
        /// Equal-power per-channel gain coefficients derived from
        /// <see cref="Pan"/>. Returns <c>(gainL, gainR)</c>. For mono paths,
        /// the caller should just use the overall <see cref="Gain"/>.
        /// </summary>
        internal void GetStereoChannelGains(out float gainL, out float gainR)
        {
            // Equal-power panning: maps pan ∈ [-1,1] to angle θ ∈ [0, π/2].
            // gainL = cos(θ), gainR = sin(θ). At θ=π/4 (centered), both are √½
            // so a centered pan preserves total power.
            float pan = Volatile.Read(ref _pan);
            float theta = (pan + 1f) * (Mathf.PI * 0.25f); // 0..π/2
            gainL = Mathf.Cos(theta);
            gainR = Mathf.Sin(theta);
        }
    }
}
