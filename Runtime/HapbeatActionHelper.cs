using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Inspector-friendly wrapper over <see cref="HapbeatManager"/> methods
    /// that are typically singleton-only (Stop / StopAll / StopStream / Ping).
    /// Exposes them as instance methods so they can be wired as targets from
    /// Inspector UnityEvents (e.g. UI Buttons, key dispatchers, animation
    /// events) without writing a custom MonoBehaviour.
    ///
    /// <para>
    /// Place once on the <c>[Hapbeat Event Router]</c> GameObject alongside
    /// <see cref="HapbeatManager"/>. All methods are no-ops if the Manager
    /// instance is not yet ready.
    /// </para>
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Action Helper")]
    public class HapbeatActionHelper : MonoBehaviour
    {
        [Tooltip("Default group ID for Stop / StopAll. -1 = use HapbeatConfig default.")]
        [SerializeField] private int _group = -1;

        /// <summary>Stop the host-side audio stream (if any).</summary>
        public void StopStream()
        {
            HapbeatManager.Instance?.StopStream();
        }

        /// <summary>Tell the device to stop a specific event id.</summary>
        public void Stop(string eventId)
        {
            HapbeatManager.Instance?.Stop(eventId, _group);
        }

        /// <summary>Tell the device to stop every active event in the configured group.</summary>
        public void StopAll()
        {
            HapbeatManager.Instance?.StopAll(_group);
        }

        /// <summary>Stop both: host-side stream (if any) AND every device-side event.</summary>
        public void StopEverything()
        {
            var mgr = HapbeatManager.Instance;
            if (mgr == null) return;
            mgr.StopStream();
            mgr.StopAll(_group);
        }

        /// <summary>Send a Ping for keep-alive / latency measurement.</summary>
        public void Ping()
        {
            HapbeatManager.Instance?.Ping();
        }
    }
}
