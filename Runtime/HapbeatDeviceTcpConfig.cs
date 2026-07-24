using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Fire-and-forget helper that pushes per-device config commands over the
    /// device's TCP config port (serial-config.md §4, `TCP_CONFIG_PORT` = 7701).
    /// Currently used only for <c>set_stream_buffer</c> (contracts §4.20-pre),
    /// which switches CLIP (Wi-Fi UDP stream) playback into the firmware's
    /// hold-decay + drift-corrected continuous mode instead of the default
    /// re-prime-on-underrun low-latency jitter buffer.
    ///
    /// <para>
    /// The device's TCP 7701 is a <b>single-client slot</b> — if Studio or
    /// hapbeat-helper already holds it, this connect attempt simply fails.
    /// That is expected and harmless: the caller (<see cref="HapbeatManager"/>)
    /// is told whether the send actually succeeded (see <c>onResult</c> below)
    /// so it can retry a failed device later instead of silently treating it
    /// as configured forever. All I/O runs on a thread-pool thread
    /// (<see cref="Task.Run(Action)"/>) so the per-frame UDP sends in
    /// <c>HapbeatManager.MixerCoroutine</c> are never blocked by a slow or
    /// absent TCP peer, and never retries in a tight loop by itself — pacing
    /// is the caller's responsibility.
    /// </para>
    /// </summary>
    internal static class HapbeatDeviceTcpConfig
    {
        private const int ConnectTimeoutMs = 400;
        private const int IoTimeoutMs = 400;
        private const int ResponseBufferSize = 256;

        // Per-IP in-flight guard — prevents overlapping calls (e.g. a PONG
        // arriving from the same device while a previous send for it hasn't
        // finished yet) from opening a second connection against the
        // device's single-client TCP slot.
        private static readonly HashSet<string> s_inFlight = new HashSet<string>();
        private static readonly object s_lock = new object();

        // Results are produced on a thread-pool thread but must only be
        // observed on the main thread (callers touch Unity API / non-thread-safe
        // collections from onResult). Queued here and drained by
        // DispatchMainThreadCallbacks(), mirroring HapbeatClient's own
        // _mainThreadQueue / DispatchMainThreadCallbacks pattern.
        private static readonly ConcurrentQueue<Action> s_mainThreadQueue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Sends <c>{"cmd":"set_stream_buffer","buffer_ms":N,"persist":true}\n</c>
        /// to <paramref name="ip"/>:<paramref name="tcpPort"/>. Never throws,
        /// never blocks the calling (main) thread.
        /// </summary>
        /// <param name="ip">Device IPv4/IPv6 address (no port).</param>
        /// <param name="tcpPort">Device config TCP port (7701).</param>
        /// <param name="bufferMs">`buffer_ms` value, 0-500 (see contracts §4.20-pre). Caller is expected to have already filtered out &lt;= 0.</param>
        /// <param name="verbose">Mirrors <see cref="HapbeatConfig.verboseLogging"/> — gates Debug.Log output.</param>
        /// <param name="onResult">
        /// Optional callback invoked exactly once, on the main thread (via
        /// <see cref="DispatchMainThreadCallbacks"/>), with <c>(ip, success)</c>.
        /// <c>success</c> is true only if the TCP connect completed and the
        /// JSON payload was written to the socket — a busy/refused config
        /// slot (the common case when Studio/Helper holds it) reports false
        /// so the caller can retry later instead of assuming the device is
        /// configured.
        /// </param>
        public static void SendSetStreamBufferAsync(string ip, int tcpPort, int bufferMs, bool verbose, Action<string, bool> onResult = null)
        {
            if (string.IsNullOrEmpty(ip)) return;

            lock (s_lock)
            {
                if (s_inFlight.Contains(ip)) return;
                s_inFlight.Add(ip);
            }

            Task.Run(() =>
            {
                bool success = false;
                try
                {
                    success = SendSetStreamBufferBlocking(ip, tcpPort, bufferMs, verbose);
                }
                catch (Exception e)
                {
                    success = false;
                    if (verbose)
                        Debug.Log($"[Hapbeat] set_stream_buffer -> {ip}:{tcpPort} failed: {e.Message}");
                }
                finally
                {
                    lock (s_lock) { s_inFlight.Remove(ip); }

                    if (onResult != null)
                    {
                        bool resultForClosure = success;
                        s_mainThreadQueue.Enqueue(() => onResult(ip, resultForClosure));
                    }
                }
            });
        }

        /// <summary>
        /// Drains result callbacks queued by <see cref="SendSetStreamBufferAsync"/>
        /// onto the calling (main) thread. Call this from a MonoBehaviour's
        /// <c>Update()</c> — see <see cref="HapbeatManager.Update"/>.
        /// </summary>
        public static void DispatchMainThreadCallbacks()
        {
            while (s_mainThreadQueue.TryDequeue(out Action action))
            {
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        // Runs entirely off the main thread. Blocking calls here are fine —
        // this method only ever executes inside the Task.Run above.
        // Returns true iff the connect succeeded and the payload was written
        // to the socket (the ack read below is best-effort and never affects
        // the return value — see its own comment).
        private static bool SendSetStreamBufferBlocking(string ip, int tcpPort, int bufferMs, bool verbose)
        {
            using (var client = new TcpClient())
            {
                IAsyncResult connectResult = client.BeginConnect(ip, tcpPort, null, null);
                bool signaled = connectResult.AsyncWaitHandle.WaitOne(ConnectTimeoutMs);
                if (!signaled || !client.Connected)
                {
                    if (verbose)
                    {
                        Debug.Log($"[Hapbeat] set_stream_buffer -> {ip}:{tcpPort}: connect timed out " +
                                  "(device's single TCP config slot is likely held by Studio/Helper — harmless, will retry after cooldown).");
                    }
                    return false;
                }
                client.EndConnect(connectResult);
                client.SendTimeout = IoTimeoutMs;
                client.ReceiveTimeout = IoTimeoutMs;

                using (NetworkStream stream = client.GetStream())
                {
                    // serial-config.md §4.20-pre wire shape. Newline-terminated —
                    // tcp_server.cpp reads JSON one line at a time.
                    string json = "{\"cmd\":\"set_stream_buffer\",\"buffer_ms\":" + bufferMs.ToString() + ",\"persist\":true}\n";
                    byte[] payload = Encoding.UTF8.GetBytes(json);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();

                    if (verbose)
                    {
                        // Best-effort read of the {"status":"ok",...} ack line.
                        // A read timeout here just means we didn't wait for the
                        // ack — the command itself was already written above,
                        // so this doesn't change the success result.
                        try
                        {
                            byte[] buf = new byte[ResponseBufferSize];
                            int n = stream.Read(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                string resp = Encoding.UTF8.GetString(buf, 0, n).TrimEnd('\r', '\n');
                                Debug.Log($"[Hapbeat] set_stream_buffer -> {ip}:{tcpPort} sent (buffer_ms={bufferMs}). Response: {resp}");
                            }
                            else
                            {
                                Debug.Log($"[Hapbeat] set_stream_buffer -> {ip}:{tcpPort} sent (buffer_ms={bufferMs}). No response (connection closed).");
                            }
                        }
                        catch (Exception)
                        {
                            Debug.Log($"[Hapbeat] set_stream_buffer -> {ip}:{tcpPort} sent (buffer_ms={bufferMs}). Response read timed out.");
                        }
                    }

                    return true;
                }
            }
        }
    }
}
