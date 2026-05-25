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
    /// Two tick algorithms are available via <see cref="_tickMode"/>:
    /// <list type="bullet">
    ///   <item><b>AbsolutePosition</b> (default): fixed marks at
    ///     <c>0, threshold, 2×threshold, ...</c> anchored to zero. Fires once
    ///     per mark crossing in either direction. Mark positions are
    ///     independent of the slider's initial value.</item>
    ///   <item><b>AccumulatedMotion</b>: anchor moves ±threshold per tick.
    ///     Fires once per threshold of accumulated motion from the last
    ///     tick. Useful for "wheel detent" feel where the marks should
    ///     follow the drag rather than stay fixed on the slider track.</item>
    /// </list>
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

        /// <summary>Tick detection algorithm.</summary>
        public enum TickMode
        {
            /// <summary>Fixed marks at multiples of threshold anchored to zero.
            /// One tick per mark crossing. Marks do NOT move with the input.</summary>
            AbsolutePosition,
            /// <summary>Anchor moves ±threshold per tick (relative to last fire).
            /// One tick per threshold of accumulated motion. Marks effectively
            /// "follow" the drag — slow wiggles inside one threshold span do
            /// not fire, but a sustained drag of N×threshold fires N times.</summary>
            AccumulatedMotion,
        }

        [Tooltip("tick の検出方式。\n" +
                 "  ・AbsolutePosition (デフォルト): スライダー上に 0, threshold, 2×threshold, ... の\n" +
                 "    固定メモリがあり、その線を跨ぐたびに 1 tick fire (位置基準)。\n" +
                 "    例: threshold=0.1, 0.05→0.18 で 1 tick (0.1 を跨ぐ)。逆方向も対称。\n" +
                 "  ・AccumulatedMotion: 前回発火位置から累積移動 ±threshold 動くたびに 1 tick (相対基準)。\n" +
                 "    例: threshold=0.1, 0.05→0.15 で 1 tick (anchor が 0.15 に移動)。\n" +
                 "    細かい往復で累積を増やさない wheel detent 的感触に向く。")]
        [SerializeField]
        private TickMode _tickMode = TickMode.AbsolutePosition;

        [Tooltip("tick の間隔 (入力値の単位)。\n" +
                 "  AbsolutePosition: 固定メモリの間隔 (0, threshold, 2×threshold, ...)\n" +
                 "  AccumulatedMotion: anchor が動く累積量\n" +
                 "0 にすると \"任意の変化で fire\" モード (mode 共通)。")]
        [SerializeField, Min(0f)]
        private float _tickThreshold = 0.1f;

        [Tooltip("Vector2 入力時にどの軸を追跡するか。float 入力時は無視される。")]
        [SerializeField]
        private VectorAxis _axis = VectorAxis.Y;

        [Tooltip("最初に値を受け取った時点で 1 回 fire する。\n" +
                 "通常は OFF — 初期化や enable 時の値で誤発火を防ぐ。")]
        [SerializeField]
        private bool _emitOnInitialValue = false;

        // Last observed input value. Used to determine which fixed-position
        // marks (multiples of threshold) were crossed between the previous
        // call and the current value. Anchored to absolute zero (NOT to the
        // initial value), so all wired sliders / scrollrects share the same
        // global mark positions regardless of where they started.
        private float _lastValue;
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
                _lastValue = v;
                _hasReference = true;
                if (_emitOnInitialValue) FireHaptic();
                return;
            }

            // Threshold = 0 → "fire on any change" (mode 共通の早期 return)。
            if (_tickThreshold <= 0f)
            {
                if (!Mathf.Approximately(v, _lastValue))
                {
                    _lastValue = v;
                    FireHaptic();
                }
                return;
            }

            int ticksToFire;
            if (_tickMode == TickMode.AbsolutePosition)
            {
                // 固定メモリ通過判定: FloorToInt で band 化し、index 差 = 通過数。
                // 例: threshold=0.1, 0.05 (band 0) → 0.18 (band 1) で 1 tick。
                //     marks anchored to zero, slider 初期値に依存しない。
                int marksOld = Mathf.FloorToInt(_lastValue / _tickThreshold);
                int marksNew = Mathf.FloorToInt(v / _tickThreshold);
                ticksToFire = Mathf.Abs(marksNew - marksOld);
                _lastValue = v;
            }
            else // AccumulatedMotion
            {
                // 累積移動判定: anchor (=_lastValue) を ±threshold ずつ進めながら
                // tick を発火。anchor は最後に fire した位置 + 各 tick ステップで進む。
                ticksToFire = 0;
                float delta = v - _lastValue;
                while (Mathf.Abs(delta) >= _tickThreshold)
                {
                    ticksToFire++;
                    _lastValue += Mathf.Sign(delta) * _tickThreshold;
                    delta = v - _lastValue;
                    if (ticksToFire >= 64) break;  // cap (warn 下で)
                }
            }

            // Safety cap — wired listener が input にフィードバックする等で
            // 値が大ジャンプした場合の暴走防止。64/call で十分。
            if (ticksToFire > 64)
            {
                Debug.LogWarning(
                    $"[HapbeatTickEmitter] {name}: 64-tick cap hit in one Process() call " +
                    $"(would have fired {ticksToFire}). Threshold may be too small for the " +
                    "input range, or a wired handler is feeding back into the input source.", this);
                ticksToFire = 64;
            }

            for (int i = 0; i < ticksToFire; i++)
                FireHaptic();
        }

        protected override void OnDisable()
        {
            base.OnDisable();  // flush pending haptic-delay coroutines + event unsubscribe
            _hasReference = false;
        }
    }
}
