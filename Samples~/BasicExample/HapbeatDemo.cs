using UnityEngine;
using Hapbeat;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Basic demo: Hapbeat Unity SDK の Stream / Command 両モードを EventMap 経由で
/// 動作確認するサンプル。すべての操作は EventMap 側のエントリ設定 (mode, loop,
/// streamClip, target …) を honor する。
///
/// Controls:
///   Space  - Stream 1 ショット再生 (entry: demo_stream_sine_100hz, loop=false)
///   L      - Stream ループ再生     (entry: demo_stream_loop_100hz, loop=true)
///   E      - Command 送信          (entry: demo_command_sine_200hz, mode=Command)
///   S      - すべて停止 (Stream + 上記 Command の Stop)
///   P      - Ping (応答時間を log に出力)
///
/// EventMap が未割当の場合は警告ログのみで no-op。BasicExampleSceneBuilder が
/// シーン生成時に EventMap と各エントリ名を自動 wire する。
/// </summary>
public class HapbeatDemo : MonoBehaviour
{
    [Header("EventMap")]
    [Tooltip("シーン生成時に BasicExampleSceneBuilder が自動 wire する EventMap。")]
    [SerializeField] private HapbeatEventMap _eventMap;

    [Header("Entry names (must match displayName in EventMap)")]
    [SerializeField] private string _oneshotStreamEntry = "demo_stream_sine_100hz";
    [SerializeField] private string _loopStreamEntry = "demo_stream_loop_100hz";
    [SerializeField] private string _commandEntry = "demo_command_sine_200hz";

    [Header("Common")]
    [Tooltip("Manual gain multiplier on top of entry.gain. 0.0 - 2.0")]
    [Range(0f, 2f)]
    [SerializeField] private float _gain = 0.5f;

    [Tooltip("Target group ID. -1 = config default, 0 = all devices.")]
    [SerializeField] private int _group = -1;

    [SerializeField, Tooltip("On-screen log (optional). When wired, key actions are surfaced visually.")]
    private Hapbeat.Samples.HapbeatDemoUI _ui;

    private void Start()
    {
        if (HapbeatManager.Instance == null)
        {
            Debug.LogWarning("[HapbeatDemo] HapbeatManager が見つかりません。");
            return;
        }

        HapbeatManager.Instance.OnConnected += () =>
        {
            string mode = HapbeatManager.Instance.IsBroadcast ? "broadcast" : "unicast";
            Debug.Log($"[HapbeatDemo] Ready ({mode}, group={HapbeatManager.Instance.DefaultGroup})");
        };
        HapbeatManager.Instance.OnDisconnected += () => Debug.Log("[HapbeatDemo] Disconnected");
        HapbeatManager.Instance.OnPong += rttUs => Debug.Log($"[HapbeatDemo] Pong: RTT={rttUs / 1000.0:F1}ms");
        HapbeatManager.Instance.OnError += msg => Debug.LogWarning($"[HapbeatDemo] Error: {msg}");
    }

    private void Update()
    {
        if (HapbeatManager.Instance == null) return;

        if (WasPressedThisFrame(KeySpace))
            FireStream(_oneshotStreamEntry, "Stream 1-shot");

        if (WasPressedThisFrame(KeyL))
            FireStream(_loopStreamEntry, "Stream loop");

        if (WasPressedThisFrame(KeyE))
            FireCommand(_commandEntry);

        if (WasPressedThisFrame(KeyS))
            StopAll();

        if (WasPressedThisFrame(KeyP))
        {
            Debug.Log("[HapbeatDemo] Ping");
            _ui?.Log("Ping sent");
            HapbeatManager.Instance.Ping();
        }
    }

    // ========== Action helpers ==========

    private void FireStream(string entryName, string logLabel)
    {
        var entry = ResolveEntry(entryName);
        if (entry == null) return;
        if (entry.streamClip == null)
        {
            Debug.LogWarning($"[HapbeatDemo] Entry '{entryName}' has no streamClip.");
            return;
        }
        float gain = _gain * entry.gain;
        Debug.Log($"[HapbeatDemo] {logLabel}: {entry.streamClip.name} (gain={gain:F2}, loop={entry.loop})");
        _ui?.Log($"{logLabel}: {entry.streamClip.name} (loop={entry.loop})");
        HapbeatManager.Instance.StreamAudioClip(entry.streamClip, gain, entry.target, entry.loop);
    }

    private void FireCommand(string entryName)
    {
        var entry = ResolveEntry(entryName);
        if (entry == null) return;
        if (string.IsNullOrEmpty(entry.eventId))
        {
            Debug.LogWarning($"[HapbeatDemo] Entry '{entryName}' has no event id.");
            return;
        }
        float gain = _gain * entry.gain;
        Debug.Log($"[HapbeatDemo] Fire command: {entry.eventId} (gain={gain:F2})");
        _ui?.Log($"Fire command: {entry.eventId}");
        HapbeatManager.Instance.Play(entry.eventId, gain, entry.group, entry.displayName, entry.target);
    }

    private void StopAll()
    {
        Debug.Log("[HapbeatDemo] Stop all");
        _ui?.Log("Stop all");

        // Stop any active stream (loop or one-shot).
        HapbeatManager.Instance.StopStream();

        // Also send a Stop command for the Command-mode entry so the device
        // can halt a long-tail clip if it's still playing.
        var cmdEntry = ResolveEntry(_commandEntry);
        if (cmdEntry != null && !string.IsNullOrEmpty(cmdEntry.eventId))
            HapbeatManager.Instance.Stop(cmdEntry.eventId, cmdEntry.group);
    }

    private HapbeatEventEntry ResolveEntry(string entryName)
    {
        if (_eventMap == null)
        {
            Debug.LogWarning("[HapbeatDemo] EventMap が未割当です。Inspector で設定してください。");
            return null;
        }
        var entry = _eventMap.FindByName(entryName);
        if (entry == null)
            Debug.LogWarning($"[HapbeatDemo] EventMap に '{entryName}' が見つかりません。");
        return entry;
    }

    // ========== Input 両対応 ==========

#if ENABLE_INPUT_SYSTEM
    static readonly Key KeySpace = Key.Space;
    static readonly Key KeyL = Key.L;
    static readonly Key KeyE = Key.E;
    static readonly Key KeyS = Key.S;
    static readonly Key KeyP = Key.P;
    static bool WasPressedThisFrame(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
#else
    const KeyCode KeySpace = KeyCode.Space;
    const KeyCode KeyL = KeyCode.L;
    const KeyCode KeyE = KeyCode.E;
    const KeyCode KeyS = KeyCode.S;
    const KeyCode KeyP = KeyCode.P;
    static bool WasPressedThisFrame(KeyCode key) => Input.GetKeyDown(key);
#endif
}
