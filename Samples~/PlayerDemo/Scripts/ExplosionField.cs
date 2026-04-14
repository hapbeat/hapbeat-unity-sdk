using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// 爆発フィールド。ランダム間隔で爆発を発生させ、
    /// プレイヤーとの距離に応じた gain で AudioClip をストリーミング。
    /// </summary>
    public class ExplosionField : MonoBehaviour
    {
        [Header("Explosion Settings")]
        [SerializeField] private Transform[] _explosionPoints;
        [SerializeField] private float _minInterval = 5f;
        [SerializeField] private float _maxInterval = 8f;
        [SerializeField] private float _maxRange = 20f;

        [Header("Haptic Streaming")]
        [Tooltip("爆発の触覚としてストリーミング再生する AudioClip。")]
        [SerializeField] private AudioClip _hapticClip;

        [Header("Effects")]
        [SerializeField] private ParticleSystem _explosionVFX;
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _explosionClip;

        private float _nextExplosionTime;
        private Transform _player;

        private void Start()
        {
            ScheduleNext();
            _player = Camera.main != null ? Camera.main.transform : null;
        }

        private void Update()
        {
            if (Time.time < _nextExplosionTime) return;
            Explode();
            ScheduleNext();
        }

        private void Explode()
        {
            if (_explosionPoints == null || _explosionPoints.Length == 0) return;

            var point = _explosionPoints[Random.Range(0, _explosionPoints.Length)];

            if (_explosionVFX != null)
            {
                _explosionVFX.transform.position = point.position;
                _explosionVFX.Play();
            }

            if (_audioSource != null && _explosionClip != null)
                _audioSource.PlayOneShot(_explosionClip);

            if (_player == null) return;
            float distance = Vector3.Distance(point.position, _player.position);
            if (distance > _maxRange) return;

            float gain = Mathf.Clamp01(1f - distance / _maxRange);

            // Haptic streaming
            var clip = _hapticClip != null ? _hapticClip : _explosionClip;
            if (clip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(clip, gain);
        }

        private void ScheduleNext()
        {
            _nextExplosionTime = Time.time + Random.Range(_minInterval, _maxInterval);
        }
    }
}
