using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// <see cref="Hapbeat.HapbeatTickEmitter"/> の audio 版 (Slider 専用)。
    /// Slider など <c>UnityEvent&lt;float&gt;</c> の入力を threshold 単位で間引いて
    /// AudioSource.PlayOneShot を発火する。
    /// <para>
    /// 同じ Slider に <c>HapbeatTickEmitter</c> と <c>TickAudioEmitter</c> を**両方 wire**し、
    /// 両者の <c>_tickMode</c> と <c>_tickThreshold</c> を同じ値にすれば、
    /// **haptic と audio が同じ瞬間に発火**する。
    /// </para>
    /// <para>
    /// <see cref="HapbeatTickEmitter.TickMode"/> と同じく <b>AbsolutePosition</b> (デフォルト)
    /// と <b>AccumulatedMotion</b> を選べる。詳細は SDK 本体の docstring を参照。
    /// </para>
    /// <para>
    /// 配線例 (Slider):
    /// <list type="bullet">
    ///   <item>Slider.onValueChanged の Dynamic float → <see cref="Fire(float)"/></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Vector2 入力 (ScrollRect / MinMaxSlider) には非対応。</b>
    /// Showcase は Slider しか使わないため、Vector2 overload と axis 選択は削除済み。
    /// 必要なら <see cref="Hapbeat.HapbeatTickEmitter"/> (SDK 本体) を参照。
    /// </para>
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class TickAudioEmitter : MonoBehaviour
    {
        /// <summary>Tick detection algorithm (HapbeatTickEmitter.TickMode と同等の意味)。</summary>
        public enum TickMode
        {
            /// <summary>Fixed marks at multiples of threshold anchored to zero.</summary>
            AbsolutePosition,
            /// <summary>Anchor moves ±threshold per tick (accumulated motion).</summary>
            AccumulatedMotion,
        }

        [Tooltip("再生する AudioClip。null なら無音 (ログのみ)。")]
        [SerializeField] private AudioClip _clip;

        [Range(0f, 1f)]
        [Tooltip("PlayOneShot に渡す volume scale (AudioSource の volume と乗算)。")]
        [SerializeField] private float _volume = 0.5f;

        [Tooltip("tick の検出方式。\n" +
                 "  ・AbsolutePosition (デフォルト): スライダー上の 0, threshold, 2×threshold, ... の\n" +
                 "    固定メモリを跨ぐたびに fire。\n" +
                 "  ・AccumulatedMotion: 累積移動が threshold を超えるたびに fire (anchor 移動式)。\n" +
                 "HapbeatTickEmitter と同じ mode + 同じ threshold にすれば haptic と完全同期発火する。")]
        [SerializeField]
        private TickMode _tickMode = TickMode.AbsolutePosition;

        [Tooltip("tick の間隔 (入力値の単位)。HapbeatTickEmitter と揃えること。\n" +
                 "0 にすると \"任意の変化で fire\" モード。")]
        [SerializeField, Min(0f)] private float _tickThreshold = 0.1f;

        [Tooltip("最初に値を受け取った時点で 1 回 fire する。" +
                 "通常は OFF (初期化時の誤発火を防ぐ)。")]
        [SerializeField] private bool _emitOnInitialValue = false;

        [Tooltip("発火を有効化するか。OFF にすると全部無視。")]
        [SerializeField] private bool _triggerEnabled = true;

        [SerializeField] private bool _verboseLog = false;

        private AudioSource _source;
        private float _lastValue;
        private bool _hasReference;

        public float TickThreshold
        {
            get => _tickThreshold;
            set => _tickThreshold = Mathf.Max(0f, value);
        }

        public bool TriggerEnabled
        {
            get => _triggerEnabled;
            set => _triggerEnabled = value;
        }

        private void Awake() => _source = GetComponent<AudioSource>();

        /// <summary>
        /// 内部 anchor をリセット。Slider が programmatic に jump した直後、
        /// その jump を tick 連発に変換したくない時に呼ぶ。
        /// </summary>
        public void ResetReference() => _hasReference = false;

        /// <summary>
        /// Slider 等の <c>UnityEvent&lt;float&gt;</c> から wire する発火ハンドラ。
        /// Inspector では <b>Dynamic float</b> セクションから選ぶこと
        /// (Static Parameters の Fire を選ぶと固定値 0 が毎回渡されて鳴らなくなる)。
        /// </summary>
        public void Fire(float value) => Process(value);

        /// <summary>
        /// Snap algorithm をバイパスして強制発火 (button click 等用)。
        /// Slider に wire してはいけない (値変化を無視するため threshold が効かない)。
        /// </summary>
        public void FireNow() => PlayTick();

        private void Process(float v)
        {
            if (!_triggerEnabled) return;

            if (!_hasReference)
            {
                _lastValue = v;
                _hasReference = true;
                if (_emitOnInitialValue) PlayTick();
                return;
            }

            if (_tickThreshold <= 0f)
            {
                if (!Mathf.Approximately(v, _lastValue))
                {
                    _lastValue = v;
                    PlayTick();
                }
                return;
            }

            int ticksToFire;
            if (_tickMode == TickMode.AbsolutePosition)
            {
                // 固定メモリ通過: FloorToInt の band index 差 = 通過数。
                int marksOld = Mathf.FloorToInt(_lastValue / _tickThreshold);
                int marksNew = Mathf.FloorToInt(v / _tickThreshold);
                ticksToFire = Mathf.Abs(marksNew - marksOld);
                _lastValue = v;
            }
            else // AccumulatedMotion
            {
                // 累積移動: anchor (_lastValue) を ±threshold ずつ進める。
                ticksToFire = 0;
                float delta = v - _lastValue;
                while (Mathf.Abs(delta) >= _tickThreshold)
                {
                    ticksToFire++;
                    _lastValue += Mathf.Sign(delta) * _tickThreshold;
                    delta = v - _lastValue;
                    if (ticksToFire >= 64) break;
                }
            }

            if (ticksToFire > 64)
            {
                Debug.LogWarning(
                    $"[TickAudioEmitter] {name}: 64-tick cap (would fire {ticksToFire}).", this);
                ticksToFire = 64;
            }

            for (int i = 0; i < ticksToFire; i++) PlayTick();
        }

        private void PlayTick()
        {
            if (_clip == null)
            {
                if (_verboseLog) Debug.Log($"[TickAudioEmitter] {name}: clip 未設定で skip");
                return;
            }
            _source.PlayOneShot(_clip, _volume);
            if (_verboseLog) Debug.Log($"[TickAudioEmitter] {name}: tick at v={_lastValue:F3}");
        }

        private void OnDisable() => _hasReference = false;
    }
}
