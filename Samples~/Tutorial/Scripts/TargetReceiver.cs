using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z5 Target Range hit detector. When a projectile collides, raises an
    /// OnHit UnityEvent. In the With version, that event is wired to a
    /// HapbeatUnityEventTrigger component (UnityEvent wiring example).
    /// </summary>
    public class TargetReceiver : MonoBehaviour
    {
        [SerializeField] private string _projectileTag = "Projectile";
        [SerializeField] private float _flashSeconds = 0.2f;
        [SerializeField] private MeshRenderer _flashRenderer;

        public UnityEvent OnHit;

        private Color _baseColor;
        private float _flashEnd;

        private void Awake()
        {
            if (_flashRenderer != null)
                _baseColor = _flashRenderer.material.color;
        }

        private void Update()
        {
            if (_flashRenderer != null && _flashEnd > 0f && Time.time >= _flashEnd)
            {
                _flashRenderer.material.color = _baseColor;
                _flashEnd = 0f;
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (!collision.collider.CompareTag(_projectileTag)) return;
            OnHit?.Invoke();
            if (_flashRenderer != null)
            {
                _flashRenderer.material.color = Color.yellow;
                _flashEnd = Time.time + _flashSeconds;
            }
        }
    }
}
