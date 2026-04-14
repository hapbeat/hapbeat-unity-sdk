using UnityEngine;
using UnityEngine.Events;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// シューティングレンジ。Activate イベントで射撃し、Raycast でターゲットを判定。
    /// 射撃時に AudioClip をストリーミング（反動フィードバック）。
    /// </summary>
    public class ShootingRange : MonoBehaviour
    {
        [Header("Shooting")]
        [SerializeField] private Transform _muzzle;
        [SerializeField] private float _range = 50f;
        [SerializeField] private float _hitForce = 5f;
        [SerializeField] private LayerMask _targetLayer = ~0;

        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _shotClip;

        [Header("Haptic Streaming")]
        [Tooltip("射撃反動としてストリーミング再生する AudioClip。未設定なら _shotClip を使用。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _hapticGain = 0.7f;

        [Header("VFX")]
        [SerializeField] private ParticleSystem _muzzleFlash;

        [Header("Events")]
        public UnityEvent OnShoot;
        public UnityEvent OnTargetHit;

        /// <summary>XR Grab Interactable の Activate や UnityEvent から呼ぶ。</summary>
        public void Fire()
        {
            // Audio
            if (_audioSource != null && _shotClip != null)
                _audioSource.PlayOneShot(_shotClip);

            // VFX
            if (_muzzleFlash != null)
                _muzzleFlash.Play();

            // Haptic streaming
            var clip = _hapticClip != null ? _hapticClip : _shotClip;
            if (clip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(clip, _hapticGain);

            OnShoot?.Invoke();

            // Raycast
            Transform origin = _muzzle != null ? _muzzle : transform;
            if (Physics.Raycast(origin.position, origin.forward, out RaycastHit hit, _range, _targetLayer))
            {
                var rb = hit.collider.attachedRigidbody;
                if (rb != null)
                    rb.AddForceAtPosition(origin.forward * _hitForce, hit.point, ForceMode.Impulse);

                var target = hit.collider.GetComponent<Target>();
                if (target != null)
                    target.OnHit();

                OnTargetHit?.Invoke();
            }
        }
    }
}
