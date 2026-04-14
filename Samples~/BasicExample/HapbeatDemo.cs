using UnityEngine;
using Hapbeat;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Basic demo: ストリーミング再生とイベントコマンド再生の両方を試せるデモ。
///
/// Controls:
///   Space  - AudioClip をストリーミング再生（デバイスに音声を直接送信）
///   E      - Event ID でコマンド再生（デバイス側の clip を再生）
///   S      - 停止（ストリーミング + イベント）
///   P      - Ping
///
/// Gain スライダーを Play 中に変更すると、ストリーミングが自動的に再開されて反映される。
/// </summary>
public class HapbeatDemo : MonoBehaviour
{
    [Header("Streaming Mode")]
    [Tooltip("ストリーミング再生する AudioClip。Space キーで送信。")]
    [SerializeField]
    private AudioClip _audioClip;

    [Header("Event Command Mode")]
    [Tooltip("コマンド再生する Event ID。E キーで送信。デバイス側に対応 clip が必要。")]
    [SerializeField]
    private string _eventId = "impact.hit";

    [Header("Common")]
    [Tooltip("Gain (0.0 - 2.0)")]
    [Range(0f, 2f)]
    [SerializeField]
    private float _gain = 0.5f;

    [Tooltip("Target group ID. -1 = config default, 0 = all devices.")]
    [SerializeField]
    private int _group = -1;

    private float _lastGain;
    private bool _isStreamingFromThisDemo;

    private void Start()
    {
        _lastGain = _gain;

        if (HapbeatManager.Instance == null)
        {
            Debug.LogWarning("[HapbeatDemo] HapbeatManager が見つかりません。");
            return;
        }

        HapbeatManager.Instance.OnConnected += () =>
        {
            string mode = HapbeatManager.Instance.IsBroadcast ? "broadcast" : "unicast";
            Debug.Log($"[HapbeatDemo] 送信準備完了 ({mode}, group={HapbeatManager.Instance.DefaultGroup})");
        };

        HapbeatManager.Instance.OnDisconnected += () =>
            Debug.Log("[HapbeatDemo] 切断されました。");

        HapbeatManager.Instance.OnPong += (rttUs) =>
            Debug.Log($"[HapbeatDemo] Pong: RTT={rttUs / 1000.0:F1}ms");

        HapbeatManager.Instance.OnError += (msg) =>
            Debug.LogWarning($"[HapbeatDemo] エラー: {msg}");
    }

    private void Update()
    {
        if (HapbeatManager.Instance == null) return;

        // Gain が変更されたらストリーミングを再開して反映
        if (_isStreamingFromThisDemo && !Mathf.Approximately(_gain, _lastGain))
        {
            _lastGain = _gain;
            if (_audioClip != null && HapbeatManager.Instance.IsStreaming)
            {
                Debug.Log($"[HapbeatDemo] Gain changed → restart stream (gain={_gain:F2})");
                HapbeatManager.Instance.StreamAudioClip(_audioClip, _gain);
            }
        }

        // ストリーミング終了を検知
        if (_isStreamingFromThisDemo && !HapbeatManager.Instance.IsStreaming)
            _isStreamingFromThisDemo = false;

        // Space: ストリーミング再生
        if (WasPressedThisFrame(KeySpace))
        {
            if (_audioClip != null)
            {
                Debug.Log($"[HapbeatDemo] Stream: {_audioClip.name} (gain={_gain:F2})");
                HapbeatManager.Instance.StreamAudioClip(_audioClip, _gain);
                _isStreamingFromThisDemo = true;
                _lastGain = _gain;
            }
            else
            {
                Debug.LogWarning("[HapbeatDemo] AudioClip が未設定です。Inspector で設定してください。");
            }
        }

        // E: イベントコマンド再生
        if (WasPressedThisFrame(KeyE))
        {
            Debug.Log($"[HapbeatDemo] Play event: {_eventId} (gain={_gain:F2})");
            HapbeatManager.Instance.Play(_eventId, _gain, _group);
        }

        // S: 停止
        if (WasPressedThisFrame(KeyS))
        {
            Debug.Log("[HapbeatDemo] Stop");
            HapbeatManager.Instance.StopStream();
            HapbeatManager.Instance.Stop(_eventId, _group);
            _isStreamingFromThisDemo = false;
        }

        // P: Ping
        if (WasPressedThisFrame(KeyP))
        {
            Debug.Log("[HapbeatDemo] Ping");
            HapbeatManager.Instance.Ping();
        }
    }

    // ========== Input 両対応 ==========

#if ENABLE_INPUT_SYSTEM
    static readonly Key KeySpace = Key.Space;
    static readonly Key KeyE = Key.E;
    static readonly Key KeyS = Key.S;
    static readonly Key KeyP = Key.P;
    static bool WasPressedThisFrame(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
#else
    const KeyCode KeySpace = KeyCode.Space;
    const KeyCode KeyE = KeyCode.E;
    const KeyCode KeyS = KeyCode.S;
    const KeyCode KeyP = KeyCode.P;
    static bool WasPressedThisFrame(KeyCode key) => Input.GetKeyDown(key);
#endif
}
