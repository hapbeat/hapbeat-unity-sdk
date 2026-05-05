using UnityEngine;
using UnityEngine.UI;

namespace Hapbeat.Samples.Tutorial
{
    /// <summary>
    /// Z4 Stream Console: Space starts/stops streaming a selectable AudioClip.
    /// Gain and Pan sliders modulate the active stream in real time
    /// (HapbeatStreamPlayback.Gain / Pan). Demonstrates the script-driven
    /// equivalent of HapbeatParameterBinding — the binding is written by the
    /// user instead of evaluated by SDK.
    ///
    /// Slider haptic ticks come from a HapbeatTickEmitter component added by
    /// BatchSetup; this controller does not touch the tick path.
    /// </summary>
    public class StreamDemoController : MonoBehaviour
    {
        [SerializeField] private TutorialBridge _bridge;
        [SerializeField] private AudioClip[] _clips;
        [SerializeField] private Slider _gainSlider;
        [SerializeField] private Slider _panSlider;
        [SerializeField] private Dropdown _clipDropdown;
        [SerializeField] private Text _statusText;
        [SerializeField] private KeyCode _toggleKey = KeyCode.Space;

        private HapbeatStreamPlayback _playback;

        private void Start()
        {
            if (_clipDropdown != null && _clips != null && _clips.Length > 0)
            {
                _clipDropdown.ClearOptions();
                foreach (var clip in _clips)
                    _clipDropdown.options.Add(new Dropdown.OptionData(clip != null ? clip.name : "(null)"));
                _clipDropdown.RefreshShownValue();
            }
            UpdateStatus();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
                Toggle();

            if (_playback != null && !_playback.IsStopped)
            {
                if (_gainSlider != null) _playback.Gain = _gainSlider.value;
                if (_panSlider != null) _playback.Pan = _panSlider.value;
            }
            else if (_playback != null && _playback.IsStopped)
            {
                _playback = null;
                UpdateStatus();
            }
        }

        private void Toggle()
        {
            if (_bridge == null) return;
            if (_playback != null && !_playback.IsStopped)
            {
                _bridge.StopStream();
                _playback = null;
            }
            else
            {
                AudioClip clip = ResolveSelectedClip();
                if (clip == null) return;
                float gain = _gainSlider != null ? _gainSlider.value : 1f;
                _playback = _bridge.StreamWithPickerTarget(clip, gain, loop: true);
            }
            UpdateStatus();
        }

        private AudioClip ResolveSelectedClip()
        {
            if (_clips == null || _clips.Length == 0) return null;
            int idx = _clipDropdown != null ? _clipDropdown.value : 0;
            idx = Mathf.Clamp(idx, 0, _clips.Length - 1);
            return _clips[idx];
        }

        private void UpdateStatus()
        {
            if (_statusText == null) return;
            bool playing = _playback != null && !_playback.IsStopped;
            _statusText.text = playing ? "Streaming..." : "Stopped";
        }
    }
}
