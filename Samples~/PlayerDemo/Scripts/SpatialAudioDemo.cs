using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// Zone D: 定位感デモ。正弦波を出す音源がゾーン中心の周囲を周回する。
    /// プレイヤーが Zone D のトリガー領域に入ると再生開始、出ると停止。
    ///
    /// AudioSource の Spatial Blend = 1 により、Unity が自動でパンニング・距離減衰を処理。
    /// HapbeatAudioBridge が処理済みの音声をリアルタイムストリーミング。
    /// → デバイスの L/R チャンネルで定位感を体験できる。
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SpatialAudioDemo : MonoBehaviour
    {
        [Header("Orbit")]
        [Tooltip("周回の中心位置（ゾーン中心に固定）。未設定なら自身の初期位置を使用。")]
        [SerializeField] private Transform _orbitCenter;
        [SerializeField] private float _radius = 3f;
        [SerializeField] private float _orbitSpeed = 0.3f;
        [SerializeField] private float _heightAmplitude = 0.5f;
        [SerializeField] private float _heightOffset = 1.5f;

        [Header("Audio")]
        [Tooltip("再生する AudioClip（正弦波等）。")]
        [SerializeField] private AudioClip _audioClip;

        [Header("Activation")]
        [Tooltip("プレイヤーがこのトリガー内にいるときだけ再生する。未設定なら常時再生。")]
        [SerializeField] private Collider _activationZone;
        [SerializeField] private string _playerTag = "Player";

        [Header("Visual")]
        [SerializeField] private Renderer _visualRenderer;
        [SerializeField] private Color _activeColor = Color.cyan;
        [SerializeField] private Color _inactiveColor = Color.gray;

        private AudioSource _audioSource;
        private HapbeatAudioBridge _audioBridge;
        private float _angle;
        private Vector3 _centerPosition;
        private bool _isActive;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioBridge = GetComponent<HapbeatAudioBridge>();

            // 周回中心を固定（プレイヤーを追従しない）
            _centerPosition = _orbitCenter != null
                ? _orbitCenter.position
                : transform.position;

            if (_audioSource != null)
            {
                _audioSource.spatialBlend = 1f;
                _audioSource.loop = true;
                _audioSource.playOnAwake = false;
                _audioSource.rolloffMode = AudioRolloffMode.Linear;
                _audioSource.minDistance = 1f;
                _audioSource.maxDistance = _radius * 2f;

                if (_audioClip != null)
                    _audioSource.clip = _audioClip;
            }

            // ビジュアル初期状態
            if (_visualRenderer != null)
                _visualRenderer.material.color = _inactiveColor;
        }

        private void Update()
        {
            if (!_isActive) return;

            // 固定中心の周囲を周回
            _angle += _orbitSpeed * 360f * Time.deltaTime;
            float rad = _angle * Mathf.Deg2Rad;
            float x = Mathf.Cos(rad) * _radius;
            float z = Mathf.Sin(rad) * _radius;
            float y = _heightOffset + Mathf.Sin(rad * 2f) * _heightAmplitude;

            transform.position = _centerPosition + new Vector3(x, y, z);

            if (_visualRenderer != null)
                _visualRenderer.material.color = _activeColor;
        }

        /// <summary>プレイヤーが Zone D に入ったとき（外部の Trigger から呼ぶ）。</summary>
        public void Activate()
        {
            if (_isActive) return;
            _isActive = true;

            if (_audioSource != null && !_audioSource.isPlaying)
                _audioSource.Play();

            if (_audioBridge != null && !_audioBridge.IsStreaming)
                _audioBridge.StartStreaming();
        }

        /// <summary>プレイヤーが Zone D から出たとき。</summary>
        public void Deactivate()
        {
            if (!_isActive) return;
            _isActive = false;

            if (_audioSource != null)
                _audioSource.Stop();

            if (_audioBridge != null)
                _audioBridge.StopStreaming();

            if (_visualRenderer != null)
                _visualRenderer.material.color = _inactiveColor;
        }

        public void SetOrbitSpeed(float speed) => _orbitSpeed = speed;
        public void SetRadius(float radius)
        {
            _radius = radius;
            if (_audioSource != null)
                _audioSource.maxDistance = radius * 2f;
        }
    }
}
