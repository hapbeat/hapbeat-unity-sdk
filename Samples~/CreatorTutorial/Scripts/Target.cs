using UnityEngine;
using UnityEngine.Events;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// 射撃ターゲット。命中するとスコア加算 + 倒れて数秒後に復活。
    /// _hapticClip が設定されていれば命中時にストリーミング再生。
    /// </summary>
    public class Target : MonoBehaviour
    {
        [SerializeField] private int _scoreValue = 100;
        [SerializeField] private float _respawnTime = 3f;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _hitClip;

        [Header("Haptic Streaming")]
        [Tooltip("命中時にストリーミング再生する AudioClip（After シーンで設定）。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _hapticGain = 0.5f;

        public UnityEvent OnTargetHit;

        private Vector3 _originalPosition;
        private Quaternion _originalRotation;
        private Rigidbody _rb;
        private bool _isDown;

        private void Awake()
        {
            _originalPosition = transform.position;
            _originalRotation = transform.rotation;
            _rb = GetComponent<Rigidbody>();
            if (_rb != null) _rb.isKinematic = true;
        }

        public void OnHit()
        {
            if (_isDown) return;
            _isDown = true;

            var scoreUI = FindObjectOfType<ScoreUI>();
            if (scoreUI != null) scoreUI.AddScore(_scoreValue);

            if (_audioSource != null && _hitClip != null)
                _audioSource.PlayOneShot(_hitClip);

            // Haptic streaming
            if (_hapticClip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(_hapticClip, _hapticGain);

            if (_rb != null)
            {
                _rb.isKinematic = false;
                _rb.AddForce(Vector3.back * 3f, ForceMode.Impulse);
            }

            OnTargetHit?.Invoke();
            Invoke(nameof(Respawn), _respawnTime);
        }

        private void Respawn()
        {
            _isDown = false;
            if (_rb != null) _rb.isKinematic = true;
            transform.position = _originalPosition;
            transform.rotation = _originalRotation;
        }
    }
}
