using UnityEngine;
using Hapbeat;

/// <summary>
/// Simple demo script showing how to use the Hapbeat SDK.
/// Attach to any GameObject in the scene.
///
/// Controls:
///   Space  - Play "impact.hit"
///   S      - Stop "impact.hit"
///   X      - Stop all events
///   P      - Send Ping
/// </summary>
public class HapbeatDemo : MonoBehaviour
{
    [Header("Demo Settings")]
    [Tooltip("The event ID to play when Space is pressed.")]
    [SerializeField]
    private string _eventId = "impact.hit";

    [Tooltip("Gain for the haptic effect.")]
    [Range(0f, 2f)]
    [SerializeField]
    private float _gain = 1.0f;

    [Tooltip("Target group ID.")]
    [SerializeField]
    private byte _group = 0;

    private void Start()
    {
        // Ensure HapbeatManager exists in the scene
        if (HapbeatManager.Instance == null)
        {
            Debug.LogWarning("[HapbeatDemo] HapbeatManager が見つかりません。" +
                             "シーンに HapbeatManager を追加してください。");
            return;
        }

        // Subscribe to events
        HapbeatManager.Instance.OnConnected += () =>
        {
            Debug.Log("[HapbeatDemo] Bridge に接続しました。");
        };

        HapbeatManager.Instance.OnDisconnected += () =>
        {
            Debug.Log("[HapbeatDemo] Bridge から切断されました。");
        };

        HapbeatManager.Instance.OnPong += (rttUs) =>
        {
            Debug.Log($"[HapbeatDemo] Pong 受信: RTT = {rttUs} μs ({rttUs / 1000.0:F1} ms)");
        };

        HapbeatManager.Instance.OnError += (errorMessage) =>
        {
            Debug.LogWarning($"[HapbeatDemo] エラー: {errorMessage}");
        };
    }

    private void Update()
    {
        if (HapbeatManager.Instance == null)
            return;

        // Space: Play event
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log($"[HapbeatDemo] Play: {_eventId}");
            HapbeatManager.Instance.Play(_eventId, _gain, _group);
        }

        // S: Stop event
        if (Input.GetKeyDown(KeyCode.S))
        {
            Debug.Log($"[HapbeatDemo] Stop: {_eventId}");
            HapbeatManager.Instance.Stop(_eventId, _group);
        }

        // X: Stop all
        if (Input.GetKeyDown(KeyCode.X))
        {
            Debug.Log("[HapbeatDemo] Stop All");
            HapbeatManager.Instance.StopAll(_group);
        }

        // P: Ping
        if (Input.GetKeyDown(KeyCode.P))
        {
            Debug.Log("[HapbeatDemo] Ping");
            HapbeatManager.Instance.Ping();
        }
    }
}
