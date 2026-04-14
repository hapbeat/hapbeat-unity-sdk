using UnityEngine;
using UnityEngine.Events;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// シンプルな射撃スクリプト。チュートリアルで Hapbeat を後付けする対象。
    /// After シーンでは _hapticClip 設定によりストリーミング反動フィードバック付き。
    /// </summary>
    public class SimpleShooter : MonoBehaviour
    {
        [Header("Shooting")]
        [SerializeField] private Transform _muzzle;
        [SerializeField] private float _range = 30f;
        [SerializeField] private float _hitForce = 5f;
        [SerializeField] private LayerMask _targetLayer = ~0;

        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _shotClip;

        [Header("Haptic Streaming")]
        [Tooltip("射撃反動としてストリーミング再生する AudioClip（After シーンで設定）。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _hapticGain = 0.7f;

        [Header("VFX")]
        [SerializeField] private ParticleSystem _muzzleFlash;

        [Header("Events")]
        public UnityEvent OnShoot;

        public void Fire()
        {
            if (_audioSource != null && _shotClip != null)
                _audioSource.PlayOneShot(_shotClip);

            if (_muzzleFlash != null)
                _muzzleFlash.Play();

            // Haptic streaming (After シーンで _hapticClip が設定されていれば再生)
            if (_hapticClip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(_hapticClip, _hapticGain);

            OnShoot?.Invoke();

            Transform origin = _muzzle != null ? _muzzle : transform;
            if (Physics.Raycast(origin.position, origin.forward, out RaycastHit hit, _range, _targetLayer))
            {
                var target = hit.collider.GetComponent<Target>();
                if (target != null) target.OnHit();

                var rb = hit.collider.attachedRigidbody;
                if (rb != null)
                    rb.AddForceAtPosition(origin.forward * _hitForce, hit.point, ForceMode.Impulse);
            }
        }
    }
}
