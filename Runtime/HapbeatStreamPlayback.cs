using System;
using System.Threading;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>Observable outcome for a logical StreamClip source.</summary>
    public enum HapbeatStreamPlaybackStatus
    {
        Deferred,
        Active,
        Stopped,
    }

    /// <summary>Why a logical StreamClip has not started a wire stream yet.</summary>
    public enum HapbeatStreamPlaybackDeferReason
    {
        None,
        NoResolvedEndpoint,
        TransportTargetConflict,
    }

    /// <summary>
    /// Handle to an active StreamClip playback. Exposes thread-safe runtime
    /// controls (<see cref="Gain"/>, <see cref="Pan"/>) that
    /// <see cref="HapbeatParameterBinding"/> can write to each frame for
    /// continuous haptic modulation.
    ///
    /// <para>
    /// The streaming scheduler (owned by <see cref="HapbeatManager"/>) reads these
    /// values per-chunk, applies them to the PCM samples before they hit the
    /// wire, and sends STREAM_DATA with the modulated audio. No protocol
    /// extension is required — dynamic volume / panning are implemented
    /// entirely on the SDK side.
    /// </para>
    ///
    /// <para>
    /// Pan semantics: −1 = full left, 0 = centered, +1 = full right. Mono clips
    /// are upmixed to the stream's stereo output before this balance is applied.
    /// <b>Stereo balance (linear)</b> is used — center (pan = 0) is
    /// passthrough (gainL = gainR = 1.0), full pan attenuates the opposite
    /// channel to zero. This matches haptic intent: left / right actuators
    /// are physically separate on the body, so the audio-mixing
    /// "equal-power compensates for binaural summation" assumption doesn't
    /// apply — equal-power would silently attenuate every centered stereo
    /// clip by √½ ≈ 0.71 (~3 dB) versus the same clip played as mono.
    /// </para>
    /// </summary>
    public sealed class HapbeatStreamPlayback
    {
        private float _gain;
        private float _pan;
        private int _stopped; // 0 = running, 1 = stopped
        private int _status = (int)HapbeatStreamPlaybackStatus.Deferred;
        private int _deferReason = (int)HapbeatStreamPlaybackDeferReason.NoResolvedEndpoint;
        private Action _onStopRequested;

        /// <summary>
        /// The gain the entry authored (entry.gain × manifest.intensity),
        /// captured at stream start. <see cref="HapbeatParameterBinding"/>
        /// multiplies this by its modulation output so a bound stream is
        /// <c>BaselineGain × bindingOutput</c> — author intent ("full press
        /// = authored strength") is preserved regardless of which bindings
        /// are attached.
        /// </summary>
        public float BaselineGain { get; }

        internal HapbeatStreamPlayback(float baselineGain, Action onStopRequested = null)
            : this(baselineGain, baselineGain, onStopRequested) { }

        internal HapbeatStreamPlayback(float baselineGain, float initialGain,
            Action onStopRequested = null)
        {
            BaselineGain = baselineGain;
            _onStopRequested = onStopRequested;
            Volatile.Write(ref _gain, initialGain);
            Volatile.Write(ref _pan, 0f);
            Volatile.Write(ref _stopped, 0);
        }

        /// <summary>
        /// Overall gain multiplier applied to every sample before sending.
        /// Thread-safe; the streaming scheduler reads this per chunk.
        /// Clamped to <c>[0, 2]</c> on write.
        /// </summary>
        public float Gain
        {
            get => Volatile.Read(ref _gain);
            set => Volatile.Write(ref _gain, Mathf.Clamp(value, 0f, 2f));
        }

        /// <summary>
        /// Apply an external gain modulator as <c>Gain = BaselineGain × modulator</c>.
        /// Single shared entry point: <see cref="HapbeatTriggerBase.GainMultiplier"/>
        /// (imperative) and <see cref="HapbeatParameterBinding"/> Update (declarative)
        /// <b>both call this</b> so the formula lives in one place.
        /// Typical modulator range is [0, 1] (= 0..authored); up to [0, 2] is allowed for boost.
        /// </summary>
        public void ApplyGainModulation(float modulator)
        {
            Gain = BaselineGain * modulator;  // Gain setter clamps the result.
        }

        /// <summary>
        /// Stereo pan, <c>−1</c> (full left) .. <c>+1</c> (full right).
        /// Mono sources are upmixed before this balance is applied. Thread-safe.
        /// </summary>
        public float Pan
        {
            get => Volatile.Read(ref _pan);
            set => Volatile.Write(ref _pan, Mathf.Clamp(value, -1f, 1f));
        }

        /// <summary>True once <see cref="Stop"/> has been called (or the clip
        /// has finished playing on its own for non-loop entries).</summary>
        public bool IsStopped => Volatile.Read(ref _stopped) != 0;

        /// <summary>Whether this source is deferred, active on a transport endpoint, or stopped.</summary>
        public HapbeatStreamPlaybackStatus Status => (HapbeatStreamPlaybackStatus)Volatile.Read(ref _status);

        /// <summary>Machine-readable reason when <see cref="Status"/> is Deferred.</summary>
        public HapbeatStreamPlaybackDeferReason DeferReason =>
            (HapbeatStreamPlaybackDeferReason)Volatile.Read(ref _deferReason);

        /// <summary>True only after a matching transport endpoint has begun.</summary>
        public bool IsActive => Status == HapbeatStreamPlaybackStatus.Active;

        /// <summary>
        /// Request the stream to stop. The streaming scheduler checks this
        /// between chunks and sends <c>STREAM_END</c> shortly after. Safe to
        /// call from any thread.
        /// </summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            Volatile.Write(ref _status, (int)HapbeatStreamPlaybackStatus.Stopped);
            Volatile.Write(ref _deferReason, (int)HapbeatStreamPlaybackDeferReason.None);
            Interlocked.Exchange(ref _onStopRequested, null)?.Invoke();
        }

        /// <summary>Internal: mark the stream as finished after the scheduler removes it.</summary>
        internal void MarkStopped()
        {
            Volatile.Write(ref _stopped, 1);
            Volatile.Write(ref _status, (int)HapbeatStreamPlaybackStatus.Stopped);
            Volatile.Write(ref _deferReason, (int)HapbeatStreamPlaybackDeferReason.None);
            Interlocked.Exchange(ref _onStopRequested, null);
        }

        internal void MarkActive()
        {
            if (IsStopped) return;
            Volatile.Write(ref _deferReason, (int)HapbeatStreamPlaybackDeferReason.None);
            Volatile.Write(ref _status, (int)HapbeatStreamPlaybackStatus.Active);
        }

        internal void MarkDeferred(HapbeatStreamPlaybackDeferReason reason)
        {
            if (IsStopped) return;
            Volatile.Write(ref _deferReason, (int)reason);
            Volatile.Write(ref _status, (int)HapbeatStreamPlaybackStatus.Deferred);
        }

        /// <summary>
        /// Stereo-balance per-channel gain coefficients derived from
        /// <see cref="Pan"/>. Returns <c>(gainL, gainR)</c>. For mono paths,
        /// the caller should just use the overall <see cref="Gain"/>.
        ///
        /// <para><b>Linear balance, not equal-power</b>:
        /// <list type="bullet">
        ///   <item><c>pan = 0</c> (default): <c>gainL = gainR = 1.0</c> — passthrough.
        ///         A stereo clip plays at the same per-channel amplitude as an
        ///         equivalent mono clip; no hidden attenuation by √½.</item>
        ///   <item><c>pan = −1</c>: <c>gainL = 1.0, gainR = 0</c> — full left.</item>
        ///   <item><c>pan = +1</c>: <c>gainL = 0, gainR = 1.0</c> — full right.</item>
        /// </list>
        /// Audio mixers favour equal-power because binaural summation in one ear
        /// would otherwise add up. Hapbeat's left / right actuators are
        /// physically separate on the body — they don't sum into a single
        /// perception — so equal-power's compensation only hurts, attenuating
        /// every centered clip by ~3 dB relative to the mono equivalent.
        /// </para>
        /// </summary>
        internal void GetStereoChannelGains(out float gainL, out float gainR)
        {
            float pan = Volatile.Read(ref _pan);
            gainL = pan <= 0f ? 1f : 1f - pan;
            gainR = pan >= 0f ? 1f : 1f + pan;
        }
    }
}
