using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// ドラムパッド。衝突時に AudioClip を速度連動で再生 + ストリーミング。
    /// </summary>
    public class DrumPad : MonoBehaviour
    {
        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _drumClip;
        [SerializeField] private float _maxVelocity = 5f;
        [SerializeField] private float _maxVolume = 1f;

        [Header("Haptic Streaming")]
        [Tooltip("ストリーミング再生する AudioClip。未設定なら _drumClip を使用。")]
        [SerializeField] private AudioClip _hapticClip;

        private void OnCollisionEnter(Collision collision)
        {
            float velocity = collision.relativeVelocity.magnitude;
            float normalizedVel = Mathf.Clamp01(velocity / _maxVelocity);

            // Audio
            if (_audioSource != null && _drumClip != null)
                _audioSource.PlayOneShot(_drumClip, normalizedVel * _maxVolume);

            // Haptic streaming
            var clip = _hapticClip != null ? _hapticClip : _drumClip;
            if (clip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(clip, normalizedVel);
        }
    }
}
