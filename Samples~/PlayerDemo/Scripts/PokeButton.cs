using UnityEngine;
using UnityEngine.Events;
using Hapbeat;

namespace Hapbeat.Samples
{
    /// <summary>
    /// ポークボタン。押下時に色変化 + AudioClip ストリーミング。
    /// </summary>
    public class PokeButton : MonoBehaviour
    {
        [Header("Visual")]
        [SerializeField] private Renderer _buttonRenderer;
        [SerializeField] private Color _normalColor = Color.white;
        [SerializeField] private Color _pressedColor = Color.cyan;
        [SerializeField] private float _pressDepth = 0.02f;

        [Header("Audio")]
        [SerializeField] private AudioSource _audioSource;
        [SerializeField] private AudioClip _pressClip;

        [Header("Haptic Streaming")]
        [Tooltip("ボタン押下時にストリーミング再生する AudioClip。未設定なら _pressClip を使用。")]
        [SerializeField] private AudioClip _hapticClip;
        [SerializeField] private float _hapticGain = 0.3f;

        [Header("Events")]
        public UnityEvent OnPressed;

        private Vector3 _originalPosition;
        private bool _isPressed;

        private void Awake()
        {
            _originalPosition = transform.localPosition;
            if (_buttonRenderer != null)
                _buttonRenderer.material.color = _normalColor;
        }

        public void Press()
        {
            if (_isPressed) return;
            _isPressed = true;

            if (_buttonRenderer != null)
                _buttonRenderer.material.color = _pressedColor;
            transform.localPosition = _originalPosition - transform.up * _pressDepth;

            if (_audioSource != null && _pressClip != null)
                _audioSource.PlayOneShot(_pressClip);

            // Haptic streaming
            var clip = _hapticClip != null ? _hapticClip : _pressClip;
            if (clip != null && HapbeatManager.Instance != null)
                HapbeatManager.Instance.StreamAudioClip(clip, _hapticGain);

            OnPressed?.Invoke();
        }

        public void Release()
        {
            _isPressed = false;
            if (_buttonRenderer != null)
                _buttonRenderer.material.color = _normalColor;
            transform.localPosition = _originalPosition;
        }
    }
}
