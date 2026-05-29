using System.Collections.Generic;
using UnityEngine;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Named AudioClip を Inspector で登録し、Animation Event / UnityEvent /
    /// script から <see cref="Play(string)"/> で再生する汎用プレイヤー。
    /// <para>
    /// Z2 では各 Animation Clip の frame 0 に Animation Event を追加して
    /// <c>Play("door_open")</c> 等を呼び出す想定。AudioSource 1 つを
    /// PlayOneShot で使い回すので、同時に複数 clip を重ねて鳴らせる
    /// (例: door_open の余韻中に slam 音が割り込む)。
    /// </para>
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class SoundPlayer : MonoBehaviour
    {
        [System.Serializable]
        public class NamedClip
        {
            [Tooltip("Play(name) で参照する識別子。Animation Event の String parameter に書く値。")]
            public string name;

            [Tooltip("再生する AudioClip。")]
            public AudioClip clip;

            [Range(0f, 1f)]
            [Tooltip("Per-clip volume scale (AudioSource の volume と乗算)。")]
            public float volume = 1f;
        }

        [SerializeField] private List<NamedClip> _clips = new List<NamedClip>();
        [SerializeField] private bool _verboseLog = false;

        private AudioSource _source;
        private Dictionary<string, NamedClip> _map;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            RebuildMap();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Inspector 編集中も dict を最新化 (Play モード入る前の確認用)
            RebuildMap();
        }
#endif

        private void RebuildMap()
        {
            _map = new Dictionary<string, NamedClip>();
            if (_clips == null) return;
            foreach (var c in _clips)
            {
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                _map[c.name] = c;
            }
        }

        /// <summary>
        /// 登録名で AudioClip を再生する。Animation Event や UnityEvent から呼ぶ。
        /// </summary>
        public void Play(string name)
        {
            if (_map == null) RebuildMap();
            if (string.IsNullOrEmpty(name)) return;
            if (!_map.TryGetValue(name, out var c) || c.clip == null)
            {
                if (_verboseLog) Debug.LogWarning($"[SoundPlayer] '{name}' not registered or clip null on {gameObject.name}");
                return;
            }
            _source.PlayOneShot(c.clip, c.volume);
            if (_verboseLog) Debug.Log($"[SoundPlayer] Played '{name}' volume={c.volume:F2}");
        }
    }
}
