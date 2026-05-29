using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Hapbeat.Samples.Showcase
{
    /// <summary>
    /// Z4 Stream Console: <see cref="HapbeatUnityEventTrigger"/> (entry: stream_demo,
    /// StreamClip loop mode) を Space で開始/停止する。再生中は Gain / Pan slider
    /// で <see cref="HapbeatStreamPlayback.Gain"/> / <see cref="HapbeatStreamPlayback.Pan"/>
    /// をリアルタイム変調する (3 行 script。UI Slider → Stream params の最小例)。
    /// </summary>
    public class StreamDemoController : MonoBehaviour
    {
        [Tooltip("EventMap entry: stream_demo を持つ HapbeatUnityEventTrigger (StreamClip mode + loop)。")]
        [SerializeField] private HapbeatUnityEventTrigger _trigger;
        [SerializeField] private Slider _gainSlider;
        [SerializeField] private Slider _panSlider;
        [SerializeField] private Text _statusText;
        [SerializeField] private Key _toggleKey = Key.Space;
        [SerializeField] private bool _verboseLog = true;

        private bool _wasPlaying;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && _toggleKey != Key.None && kb[_toggleKey].wasPressedThisFrame)
            {
                if (_verboseLog) Debug.Log($"[Stream] {_toggleKey} → Toggle");
                Toggle();
            }

            // Slider → playback の Gain/Pan modulation。
            //
            // === Tutorial 設計選択 ===
            // 方式 A (declarative / 推奨): HapbeatParameterBinding を 2 個
            //   StreamPanelHud に attach (Source=SliderValue, Output=StreamGain/StreamPan)。
            //   Inspector で wiring 完結、EventMap の Bindings セクションにも表示。
            //   → Z4 builder のデフォルトはこれ。
            //
            // 方式 B (imperative / 下記コメントアウト部): script から直接
            //   pb.Gain = slider.value のように毎フレーム書込み。
            //   custom 計算や複雑な mapping に向く。EventMap には現れない。
            //
            // どちらでも同じ挙動。プロジェクト都合で選択してください。
            //
            // var pb = _trigger != null ? _trigger.ActivePlayback : null;
            // if (pb != null)
            // {
            //     if (_gainSlider != null) pb.Gain = _gainSlider.value;
            //     if (_panSlider != null)  pb.Pan  = _panSlider.value;
            // }

            // Status text 更新用に active playback を 1 度取得 (Toggle の状態反映だけに使用)
            var activePb = _trigger != null ? _trigger.ActivePlayback : null;
            bool playing = activePb != null && !activePb.IsStopped;
            if (playing != _wasPlaying)
            {
                _wasPlaying = playing;
                UpdateStatus(playing);
            }
        }

        public void Toggle()
        {
            if (_trigger == null)
            {
                Debug.LogWarning("[Stream] _trigger 未割当");
                return;
            }
            var pb = _trigger.ActivePlayback;
            bool playing = pb != null && !pb.IsStopped;
            if (playing)
            {
                _trigger.Stop();
                if (_verboseLog) Debug.Log("[Stream] Trigger.Stop");
            }
            else
            {
                _trigger.Fire();
                if (_verboseLog) Debug.Log("[Stream] Trigger.Fire (loop)");
            }
        }

        private void UpdateStatus(bool playing)
        {
            if (_statusText == null) return;
            _statusText.text = playing ? "Streaming..." : "Stopped";
        }
    }
}
