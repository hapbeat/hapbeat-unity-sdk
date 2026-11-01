using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// The value the SDK sends for a haptic's strength (contracts kit-format.md
    /// "強さの式（正本）"):
    /// <code>
    /// output = clip × app_gain × device_volume
    /// clip   = WAV × clip_gain
    /// sent   = clamp(clip_gain × app_gain, 0, 1)
    /// </code>
    /// <c>clip_gain</c> is the clip's strength from the Kit manifest
    /// (<see cref="HapbeatEventEntry.EffectiveClipGain"/>, 1 when unknown); <c>app_gain</c>
    /// is the product of every multiplier the app applies (the entry's <c>gain</c>, the
    /// trigger multiplier, a binding output, a call-time <c>gain</c>). Only the final
    /// product is clamped: the PLAY <c>gain</c> (<see cref="HapbeatProtocol.BuildPlayPayload"/>)
    /// and a stream source's PCM multiplier (<see cref="HapbeatStreamPlayback.Gain"/>) both
    /// pass through <see cref="ToSendGain(float)"/>.
    /// </summary>
    public static class HapbeatGain
    {
        /// <summary><c>clamp(clipGain × appGain, 0, 1)</c>; NaN / infinity is sent as 0.</summary>
        public static float ToSendGain(float clipGain, float appGain) => ToSendGain(clipGain * appGain);

        /// <summary>
        /// Clamp an already multiplied <c>clip_gain × app_gain</c> to <c>[0, 1]</c>.
        /// NaN / infinity (e.g. a bound parameter divided by zero) is sent as 0 (silence).
        /// </summary>
        public static float ToSendGain(float product) =>
            float.IsNaN(product) || float.IsInfinity(product) ? 0f : Mathf.Clamp01(product);
    }
}
