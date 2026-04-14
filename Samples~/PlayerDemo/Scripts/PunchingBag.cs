using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// パンチングバッグ。衝突時に音を鳴らし、物理復元を行う。
    /// デフォルト: AudioClip をストリーミング（速度連動 gain）。
    /// Event Command モード: 同じ GO の HapbeatCollisionTrigger が処理。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PunchingBag : MonoBehaviour
    {
        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _impactClip;
        [SerializeField] private float _maxAudioVolume = 1f;

        [Header("Haptic Streaming")]
        [Tooltip("衝突時にストリーミング再生する AudioClip。未設定なら _impactClip を使用。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _maxVelocity = 10f;

        [Header("Physics")]
        [SerializeField] private float _restoreForce = 50f;

        private Vector3 _restPosition;
        private Rigidbody _rb;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _restPosition = transform.position;
        }

        private void FixedUpdate()
        {
            Vector3 displacement = _restPosition - transform.position;
            _rb.AddForce(displacement * _restoreForce);
        }

        private void OnCollisionEnter(Collision collision)
        {
            float velocity = collision.relativeVelocity.magnitude;
            float normalizedVel = Mathf.Clamp01(velocity / _maxVelocity);

            // Audio
            if (_audioSource != null && _impactClip != null)
                _audioSource.PlayOneShot(_impactClip, normalizedVel * _maxAudioVolume);

            // Haptic streaming
            var clip = _hapticClip != null ? _hapticClip : _impactClip;
            if (clip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(clip, normalizedVel);
        }
    }
}
