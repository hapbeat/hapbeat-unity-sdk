using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// 雨ゾーン。プレイヤーがトリガー領域に入ると AudioClip をストリーミングループ再生。
    /// 退出時に停止。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class RainZone : MonoBehaviour
    {
        [Header("Haptic Streaming")]
        [Tooltip("雨の触覚としてストリーミング再生する AudioClip（ループ再生される）。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _gain = 0.2f;

        [Header("Audio")]
        [SerializeField] private AudioSource _rainAudio;

        [Header("VFX")]
        [SerializeField] private ParticleSystem _rainVFX;

        [Header("Filter")]
        [SerializeField] private string _playerTag = "Player";

        private void OnTriggerEnter(Collider other)
        {
            if (!string.IsNullOrEmpty(_playerTag) && !other.CompareTag(_playerTag)) return;

            if (_hapticClip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(_hapticClip, _gain);

            if (_rainAudio != null && !_rainAudio.isPlaying) _rainAudio.Play();
            if (_rainVFX != null && !_rainVFX.isPlaying) _rainVFX.Play();
        }

        private void OnTriggerExit(Collider other)
        {
            if (!string.IsNullOrEmpty(_playerTag) && !other.CompareTag(_playerTag)) return;

            if (HapbeatManager.Instance != null)
                HapbeatManager.Instance.StopStream();

            if (_rainAudio != null) _rainAudio.Stop();
            if (_rainVFX != null) _rainVFX.Stop();
        }
    }
}
