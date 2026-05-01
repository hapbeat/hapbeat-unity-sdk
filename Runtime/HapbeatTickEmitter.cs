using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Haptic trigger that fires once per fixed-magnitude step of a continuous
    /// input value — a "detent" / "tick" trigger. Drop-in replacement for
    /// <see cref="HapbeatUnityEventTrigger"/> when the source is a
    /// <c>UnityEvent&lt;float&gt;</c> or <c>UnityEvent&lt;Vector2&gt;</c>
    /// (Slider, ScrollRect, MinMaxSlider, etc.).
    ///
    /// <para>
    /// <b>Why this exists.</b> Wiring "Slider.onValueChanged → Trigger.Fire"
    /// fires the haptic on every minute jiggle of the input — fine in concept
    /// but quickly turns into spam, leading users to bolt on a cooldown timer
    /// that is fundamentally fragile (rate is tied to wall clock instead of
    /// input motion). This component instead emits one haptic fire per
    /// <see cref="_tickThreshold"/> units of accumulated motion, so a slow
    /// drag emits few ticks and a fast drag emits many — exactly the
    /// scroll-wheel-detent feel most UI haptics want.
    /// </para>
    ///
    /// <para>
    /// <b>Wiring example (vertical ScrollRect).</b>
    /// <list type="bullet">
    ///   <item><c>ScrollRect.onValueChanged(Vector2)</c> → <see cref="Fire(Vector2)"/></item>
    ///   <item><see cref="_axis"/> = Y, <see cref="_tickThreshold"/> = 0.05</item>
    /// </list>
    /// 5% of normalized scroll position equals one haptic tick. No further
    /// wiring needed — the trigger fires its configured event directly.
    /// </para>
    ///
    /// <para>
    /// Snap algorithm: each call records the new value, computes the delta
    /// from the last tick anchor, and emits ticks while
    /// <c>|delta| ≥ threshold</c>, advancing the anchor by
    /// <c>sign(delta) * threshold</c> each iteration. One tick per threshold-
    /// sized step in either direction.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Tick Trigger")]
    public class HapbeatTickEmitter : HapbeatTriggerBase
    {
        /// <summary>Which component of a Vector2 input to track.</summary>
        public enum VectorAxis
        {
            /// <summary>Track the X component (horizontal scroll, MinMaxSlider min handle).</summary>
            X,
            /// <summary>Track the Y component (vertical scroll, MinMaxSlider max handle).</summary>
            Y,
            /// <summary>Track the magnitude (sqrt(x² + y²)).</summary>
            Magnitude,
        }

        [Tooltip("値が累積でこの量だけ変化するたびに 1 回 fire する。\n" +
                 "Slider なら slider 値の絶対量、ScrollRect の onValueChanged は " +
                 "0..1 正規化なので 0.05 で 5% ごと 1 tick になる。\n" +
                 "0 にすると \"任意の変化で fire\" モード。")]
        [SerializeField, Min(0f)]
        private float _tickThreshold = 0.1f;

        [Tooltip("Vector2 入力時にどの軸を追跡するか。float 入力時は無視される。")]
        [SerializeField]
        private VectorAxis _axis = VectorAxis.Y;

        [Tooltip("最初に値を受け取った時点で 1 回 fire する。\n" +
                 "通常は OFF — 初期化や enable 時の値で誤発火を防ぐ。")]
        [SerializeField]
        private bool _emitOnInitialValue = false;

        // Anchor value the next tick is measured against. Advanced by
        // ±threshold each time a tick fires so consecutive small motions
        // accumulate into the next tick instead of being lost.
        private float _lastTickValue;
        private bool _hasReference;

        /// <summary>
        /// Current threshold (units of accumulated change required for one tick).
        /// </summary>
        public float TickThreshold
        {
            get => _tickThreshold;
            set => _tickThreshold = Mathf.Max(0f, value);
        }

        /// <summary>
        /// Reset the internal tick anchor. Call when the input value is reset
        /// or jumps discontinuously (e.g. the slider is snapped programmatically)
        /// and you want subsequent change tracked from the new baseline rather
        /// than emitting a flurry of ticks for the jump.
        /// </summary>
        public void ResetReference() => _hasReference = false;

        /// <summary>
        /// Float-input fire handler. Wire <c>Slider.onValueChanged</c> here.
        /// Accumulates change; fires only when the threshold is crossed.
        /// </summary>
        public void Fire(float value) => Process(value);

        /// <summary>
        /// Vector2-input fire handler. Wire <c>ScrollRect.onValueChanged</c> or
        /// <c>MinMaxSlider.onValueChanged</c> here. Reads the axis configured
        /// by <see cref="_axis"/>.
        /// </summary>
        public void Fire(Vector2 value)
        {
            switch (_axis)
            {
                case VectorAxis.X:         Process(value.x); break;
                case VectorAxis.Y:         Process(value.y); break;
                case VectorAxis.Magnitude: Process(value.magnitude); break;
            }
        }

        /// <summary>
        /// Bypasses tick logic and fires unconditionally. Useful for "force
        /// fire" wiring (e.g. button click that should always tick once).
        /// </summary>
        public void FireNow() => FireHaptic();

        /// <summary>Stops any active streaming haptic. Mirrors HapbeatUnityEventTrigger.Stop().</summary>
        public void Stop() => StopHaptic();

        private void Process(float v)
        {
            if (!_triggerEnabled) return;

            if (!_hasReference)
            {
                _lastTickValue = v;
                _hasReference = true;
                if (_emitOnInitialValue) FireHaptic();
                return;
            }

            // Threshold = 0 → "fire on any change". Guard against the
            // infinite loop the snap algorithm would otherwise hit.
            if (_tickThreshold <= 0f)
            {
                if (!Mathf.Approximately(v, _lastTickValue))
                {
                    _lastTickValue = v;
                    FireHaptic();
                }
                return;
            }

            int ticks = 0;
            float delta = v - _lastTickValue;
            while (Mathf.Abs(delta) >= _tickThreshold)
            {
                FireHaptic();
                ticks++;
                _lastTickValue += Mathf.Sign(delta) * _tickThreshold;
                delta = v - _lastTickValue;

                // Safety cap — if a wired listener feeds back into the input
                // source (rare but possible) the loop could otherwise run
                // unbounded. 64 ticks/call is plenty for any UI scenario.
                if (ticks >= 64)
                {
                    Debug.LogWarning(
                        $"[HapbeatTickEmitter] {name}: 64-tick cap hit in one Process() call. " +
                        "Threshold may be too small for the input range, or a wired handler " +
                        "is feeding back into the input source.", this);
                    break;
                }
            }
        }

        private void OnDisable() => _hasReference = false;
    }
}
