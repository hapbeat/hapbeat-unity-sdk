using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z5 Target Range hit detector. Projectile の tag に応じて
    /// OnHitLight / OnHitHeavy UnityEvent を発火する。
    /// 視覚 flash は MeshRenderer の sharedMaterial を Light/Heavy 用に差し替え、
    /// _flashSeconds 後に Base に戻す。
    /// </summary>
    public class TargetReceiver : MonoBehaviour
    {
        [SerializeField] private string _lightTag = "ProjectileLight";
        [SerializeField] private string _heavyTag = "ProjectileHeavy";
        [SerializeField] private float _flashSeconds = 0.2f;

        [Header("Visual flash (Material swap)")]
        [Tooltip("Flash する MeshRenderer (TargetBoard の見た目)。null なら flash 無効。")]
        [SerializeField] private MeshRenderer _flashRenderer;
        [Tooltip("常時表示するマテリアル (ColormapTargetBase)。")]
        [SerializeField] private Material _baseMaterial;
        [Tooltip("Light hit 時の flash マテリアル (ColormapTargetLight)。")]
        [SerializeField] private Material _lightFlashMaterial;
        [Tooltip("Heavy hit 時の flash マテリアル (ColormapTargetHeavy)。")]
        [SerializeField] private Material _heavyFlashMaterial;

        public UnityEvent OnHitLight;
        public UnityEvent OnHitHeavy;

        private float _flashEnd;

        private void Awake()
        {
            ApplyMaterial(_baseMaterial);
        }

        private void Update()
        {
            if (_flashEnd > 0f && Time.time >= _flashEnd)
            {
                ApplyMaterial(_baseMaterial);
                _flashEnd = 0f;
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            bool isLight = collision.collider.CompareTag(_lightTag);
            bool isHeavy = !isLight && collision.collider.CompareTag(_heavyTag);
            if (!isLight && !isHeavy) return;

            if (isHeavy) OnHitHeavy?.Invoke();
            else OnHitLight?.Invoke();

            ApplyMaterial(isHeavy ? _heavyFlashMaterial : _lightFlashMaterial);
            _flashEnd = Time.time + _flashSeconds;
        }

        private void ApplyMaterial(Material mat)
        {
            if (_flashRenderer == null || mat == null) return;
            // sharedMaterial を使うことで material instance を作らない (= leak しない)
            _flashRenderer.sharedMaterial = mat;
        }
    }
}
