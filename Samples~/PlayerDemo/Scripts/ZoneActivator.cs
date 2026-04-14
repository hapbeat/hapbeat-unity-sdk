using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Samples
{
    /// <summary>
    /// プレイヤーがトリガー領域に入ると OnEnterZone、出ると OnExitZone を発火。
    /// Zone D の SpatialAudioDemo.Activate/Deactivate に接続して使う。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ZoneActivator : MonoBehaviour
    {
        [SerializeField] private string _playerTag = "Player";

        public UnityEvent OnEnterZone;
        public UnityEvent OnExitZone;

        private void OnTriggerEnter(Collider other)
        {
            if (!string.IsNullOrEmpty(_playerTag) && !other.CompareTag(_playerTag)) return;
            OnEnterZone?.Invoke();
        }

        private void OnTriggerExit(Collider other)
        {
            if (!string.IsNullOrEmpty(_playerTag) && !other.CompareTag(_playerTag)) return;
            OnExitZone?.Invoke();
        }
    }
}
