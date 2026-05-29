using UnityEngine;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Animator state Enter で <see cref="SoundPlayer.Play"/> を呼ぶ StateMachineBehaviour。
    /// Animation Event では「どの previous state から来たか」を判別できないため、
    /// door_close のように「特定の transition (Open → Closed) のみ鳴らしたい」音用。
    /// <para>
    /// 通常の clip-bound 音 (door_open, door_slam 等) は Animation Event の方が
    /// frame-precise でシンプルなので、そちらを推奨。本 behaviour は
    /// 「Required Previous State でフィルタしたい場面」専用。
    /// </para>
    /// </summary>
    public class StateAudioBehaviour : StateMachineBehaviour
    {
        [Tooltip("SoundPlayer.Play(name) の引数に渡す登録名。空ならスキップ。")]
        [SerializeField] private string _soundName = "";

        [Tooltip("非空ならこの state からの遷移時のみ鳴らす (例: door_close は Open からの戻りのみ)。" +
                 "空なら任意の遷移で鳴る。")]
        [SerializeField] private string _requiredPreviousState = "";

        [Tooltip("詳細ログ出力。")]
        [SerializeField] private bool _verboseLog = false;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
        {
            if (string.IsNullOrEmpty(_soundName)) return;

            if (!string.IsNullOrEmpty(_requiredPreviousState))
            {
                // transition 中でないと previous state を確認できない (= 初期 state 入場時 skip)
                if (!animator.IsInTransition(layerIndex))
                {
                    if (_verboseLog)
                        Debug.Log($"[StateAudio] {animator.gameObject.name}: skipped — no active transition");
                    return;
                }
                var prev = animator.GetCurrentAnimatorStateInfo(layerIndex);
                if (!prev.IsName(_requiredPreviousState))
                {
                    if (_verboseLog)
                        Debug.Log($"[StateAudio] {animator.gameObject.name}: skipped — previous state != '{_requiredPreviousState}'");
                    return;
                }
            }

            var player = animator.GetComponent<SoundPlayer>();
            if (player == null)
            {
                if (_verboseLog)
                    Debug.LogWarning($"[StateAudio] {animator.gameObject.name}: SoundPlayer not found on Animator's GameObject");
                return;
            }
            player.Play(_soundName);
        }
    }
}
