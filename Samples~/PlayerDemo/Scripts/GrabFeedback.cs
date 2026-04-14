using UnityEngine;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// グラブフィードバック。掴む/離す時に AudioClip をストリーミング。
    /// </summary>
    public class GrabFeedback : MonoBehaviour
    {
        [Header("Haptic Streaming")]
        [Tooltip("掴んだ時にストリーミング再生する AudioClip。")]
        [SerializeField] private AudioClip _grabClip;
        [SerializeField] private float _grabGain = 0.4f;

        [Tooltip("離した時にストリーミング再生する AudioClip。")]
        [SerializeField] private AudioClip _releaseClip;
        [SerializeField] private float _releaseGain = 0.2f;

        public void OnGrab()
        {
            if (_grabClip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(_grabClip, _grabGain);
        }

        public void OnRelease()
        {
            if (_releaseClip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(_releaseClip, _releaseGain);
        }
    }
}
