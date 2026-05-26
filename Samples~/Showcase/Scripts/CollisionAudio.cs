using UnityEngine;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// OnCollisionEnter で AudioSource.PlayOneShot を発火する collision-driven 音再生。
    /// 衝突速度に応じた volume scaling + cooldown でフラッタ抑制。
    /// <para>
    /// Z1 の Pin (10 本) や Z5 の Target Board に attach する想定。
    /// Tag フィルタを設定すれば「Projectile 衝突時のみ鳴る」等を実現。
    /// </para>
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class CollisionAudio : MonoBehaviour
    {
        [Tooltip("再生する AudioClip。複数登録時はランダム選択。")]
        [SerializeField] private AudioClip[] _clips;

        [Tooltip("これより遅い衝突では再生しない (誤発火防止)。")]
        [Range(0f, 10f)]
        [SerializeField] private float _minVelocity = 0.5f;

        [Tooltip("このスピードで volume = 1.0、それ以下は線形減衰、それ以上は cap。")]
        [Range(0.5f, 20f)]
        [SerializeField] private float _maxVelocity = 5f;

        [Tooltip("音量最小値 (衝突しても一定以上は鳴らす floor)。")]
        [Range(0f, 1f)]
        [SerializeField] private float _minVolume = 0.2f;

        [Tooltip("空なら任意の object との衝突で鳴る。非空なら指定 tag の collider のみ。")]
        [SerializeField] private string _projectileTag = "";

        [Tooltip("同一 collider との連続衝突を抑制 (秒)。")]
        [Range(0f, 0.5f)]
        [SerializeField] private float _cooldown = 0.05f;

        [SerializeField] private bool _verboseLog = false;

        private AudioSource _source;
        private float _lastPlayTime = float.NegativeInfinity;

        private void Awake() => _source = GetComponent<AudioSource>();

        private void OnCollisionEnter(Collision collision)
        {
            if (!string.IsNullOrEmpty(_projectileTag) && !collision.collider.CompareTag(_projectileTag))
                return;
            if (Time.time - _lastPlayTime < _cooldown) return;
            if (_clips == null || _clips.Length == 0) return;

            float v = collision.relativeVelocity.magnitude;
            if (v < _minVelocity)
            {
                if (_verboseLog)
                    Debug.Log($"[CollisionAudio] {name}: ignored (v={v:F2} < {_minVelocity:F2})");
                return;
            }

            float t = Mathf.InverseLerp(_minVelocity, _maxVelocity, v);
            float volume = Mathf.Lerp(_minVolume, 1f, t);

            // Random pick (variation 軽減)
            AudioClip clip = _clips[Random.Range(0, _clips.Length)];
            if (clip == null) return;

            _source.PlayOneShot(clip, volume);
            _lastPlayTime = Time.time;

            if (_verboseLog)
                Debug.Log($"[CollisionAudio] {name}: v={v:F2} vol={volume:F2} clip={clip.name}");
        }
    }
}
