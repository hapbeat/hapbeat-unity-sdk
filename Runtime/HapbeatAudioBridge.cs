using System;
using System.Threading;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// AudioSource にアタッチすると、Unity のオーディオパイプライン出力を
    /// リアルタイムで Hapbeat デバイスに UDP ストリーミングする。
    ///
    /// Unity が適用した Volume, Spatial Blend, Pan, Effects 等が全て反映される。
    /// ステレオ出力の場合、L/R チャンネルがデバイスの L/R に対応し、定位感が出る。
    ///
    /// 使い方:
    ///   1. AudioSource と同じ GameObject に HapbeatAudioBridge を追加
    ///   2. Play すると自動的にストリーミングが開始される
    ///   3. AudioSource を Stop するとストリーミングも停止する
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("Hapbeat/Hapbeat Audio Bridge")]
    public class HapbeatAudioBridge : MonoBehaviour
    {
        [Header("Streaming Settings")]
        [Tooltip("ストリーミングの gain。1.0 = AudioSource の出力そのまま。")]
        [Range(0f, 2f)]
        [SerializeField]
        private float _gain = 1.0f;

        [Tooltip("有効にすると AudioSource の再生開始時に自動でストリーミングを開始する。")]
        [SerializeField]
        private bool _autoStart = true;

        [Tooltip("送信バッファサイズ（サンプル数）。大きいほど安定するがレイテンシが増える。")]
        [SerializeField]
        private int _bufferSamples = 1024;

        /// <summary>現在ストリーミング中かどうか。</summary>
        public bool IsStreaming { get; private set; }

        [Tooltip("Target filter for device addressing. Empty = broadcast to all.")]
        [SerializeField]
        private string _target = "";

        [Tooltip("Mute speaker output. Audio is still captured and streamed to devices.\n\n" +
                 "NOTE: When triggered via EventMap StreamSource entry, this value is\n" +
                 "overwritten by entry.silentMode at Fire time. Edit EventMap to change\n" +
                 "permanently; Inspector changes are temporary.")]
        [SerializeField]
        private bool _silentMode = false;

        // Internal marker: true if Batch Setup added the paired AudioSource along with this bridge.
        // Used by Cleanup to know whether to also remove the AudioSource.
        [SerializeField, HideInInspector]
        private bool _audioSourceAutoAdded = false;

        /// <summary>True if the AudioSource was auto-added by Batch Setup.</summary>
        public bool AudioSourceAutoAdded
        {
            get => _audioSourceAutoAdded;
            set => _audioSourceAutoAdded = value;
        }

        /// <summary>Gain multiplier (0-2). Changes apply immediately to streaming.</summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Mathf.Clamp(value, 0f, 2f);
        }

        /// <summary>Target filter for device addressing. Set before StartStreaming().</summary>
        public string Target
        {
            get => _target;
            set => _target = value ?? "";
        }

        /// <summary>
        /// When true, speaker output is muted but audio is still captured for haptic streaming.
        /// OnAudioFilterRead captures data first, then zeros the output buffer.
        /// </summary>
        public bool SilentMode
        {
            get => _silentMode;
            set => _silentMode = value;
        }

        private HapbeatClient _client;
        private bool _streamStarted;
        private int _sampleRate;
        private int _channels;
        private uint _byteOffset;

        // Cached AudioSource reference. Used from the audio thread in
        // OnAudioFilterRead to apply the source's volume/mute — Unity's audio
        // pipeline applies those AFTER OnAudioFilterRead, so data arriving at
        // the filter stage has NOT had volume applied yet. Without the manual
        // multiplication below, HapbeatParameterBinding → AudioSource.volume
        // would silently have no effect on the haptic stream.
        private AudioSource _audioSource;

        // Audio thread → send buffer (lock-free ring)
        private float[] _ringBuffer;
        private int _ringSize;
        private volatile int _writePos;
        private volatile int _readPos;

        // Send thread
        private Thread _sendThread;
        private volatile bool _sendRunning;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
        }

        private void OnEnable()
        {
            if (_autoStart)
                StartStreaming();
        }

        private void OnDisable()
        {
            StopStreaming();
        }

        /// <summary>ストリーミングを開始する。</summary>
        public void StartStreaming()
        {
            if (IsStreaming) return;
            if (HapbeatManager.Instance == null || !HapbeatManager.Instance.IsConnected) return;

            _sampleRate = AudioSettings.outputSampleRate;
            _channels = GetChannelCount();
            _byteOffset = 0;
            _streamStarted = false;

            // Ring buffer: 0.5 秒分
            _ringSize = _sampleRate * _channels;
            _ringBuffer = new float[_ringSize];
            _writePos = 0;
            _readPos = 0;

            // Send thread
            _sendRunning = true;
            _sendThread = new Thread(SendLoop)
            {
                Name = "HapbeatAudioBridgeSend",
                IsBackground = true
            };
            _sendThread.Start();

            IsStreaming = true;
        }

        /// <summary>ストリーミングを停止する。</summary>
        public void StopStreaming()
        {
            if (!IsStreaming) return;

            _sendRunning = false;
            _sendThread?.Join(500);
            _sendThread = null;

            // STREAM_END を送信
            var client = GetClient();
            if (client != null && client.IsConnected)
                client.SendStreamEnd();

            IsStreaming = false;
            _streamStarted = false;
        }

        /// <summary>
        /// Unity のオーディオスレッドから呼ばれる。
        /// <para>
        /// <b>重要</b>: Unity の OnAudioFilterRead はフィルタ段であり、
        /// <c>AudioSource.volume</c> / <c>mute</c> / <c>panStereo</c> などのプロパティは
        /// このコールバックが返った<i>後</i>に適用される。したがって <c>data</c> には
        /// ボリューム適用前の生サンプルが入っている。
        /// </para>
        /// <para>
        /// <c>HapbeatParameterBinding</c> が <c>Volume</c> 出力で <c>audioSource.volume</c>
        /// を書き換えても、そのままではキャプチャ後の ring buffer に反映されない。
        /// ここで手動で <c>audioSource.volume</c>（および <c>mute</c>）を乗算することで、
        /// binding からの連続制御が触覚ストリームに正しく届くようにしている。
        /// </para>
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!IsStreaming || _ringBuffer == null)
            {
                // Still mute if silentMode even when not streaming
                if (_silentMode)
                    System.Array.Clear(data, 0, data.Length);
                return;
            }

            _channels = channels;

            // Manually apply AudioSource.volume / mute so HapbeatParameterBinding's
            // Volume output actually reaches the haptic stream. See xmldoc above.
            float sourceVolume = 1f;
            if (_audioSource != null)
                sourceVolume = _audioSource.mute ? 0f : _audioSource.volume;

            // Capture: write to ring buffer (volume-scaled, pre-mute)
            if (sourceVolume >= 0.9999f)
            {
                // Fast path — avoid per-sample multiply when volume is effectively 1.0
                for (int i = 0; i < data.Length; i++)
                {
                    int nextWrite = (_writePos + 1) % _ringSize;
                    if (nextWrite == _readPos) break; // buffer full, drop samples
                    _ringBuffer[_writePos] = data[i];
                    _writePos = nextWrite;
                }
            }
            else
            {
                for (int i = 0; i < data.Length; i++)
                {
                    int nextWrite = (_writePos + 1) % _ringSize;
                    if (nextWrite == _readPos) break;
                    _ringBuffer[_writePos] = data[i] * sourceVolume;
                    _writePos = nextWrite;
                }
            }

            // Mute speaker output (data is zeroed AFTER capture so muting the
            // speaker doesn't also silence the haptic stream).
            if (_silentMode)
                System.Array.Clear(data, 0, data.Length);
        }

        /// <summary>送信スレッド: ring buffer から読み出して UDP 送信。</summary>
        private void SendLoop()
        {
            // 送信チャンクサイズ（バイト単位、PCM16）
            int chunkSamples = _bufferSamples * _channels;
            byte[] pcmChunk = new byte[chunkSamples * 2];

            while (_sendRunning)
            {
                // Ring buffer に十分なデータがあるか確認
                int available = (_writePos - _readPos + _ringSize) % _ringSize;
                if (available < chunkSamples)
                {
                    Thread.Sleep(1);
                    continue;
                }

                var client = GetClient();
                if (client == null || !client.IsConnected)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // STREAM_BEGIN を初回だけ送信
                if (!_streamStarted)
                {
                    client.SendStreamBegin(
                        (ushort)_sampleRate,
                        (byte)_channels,
                        HapbeatProtocol.AUDIO_FORMAT_PCM16,
                        0, // total_samples unknown (continuous)
                        _gain,
                        string.IsNullOrEmpty(_target) ? null : _target);
                    _streamStarted = true;
                }

                // Ring buffer → PCM16 変換
                float currentGain = _gain;
                for (int i = 0; i < chunkSamples; i++)
                {
                    float sample = _ringBuffer[_readPos] * currentGain;
                    short pcm16 = (short)Mathf.Clamp(sample * 32767f, -32768f, 32767f);
                    pcmChunk[i * 2] = (byte)(pcm16 & 0xFF);
                    pcmChunk[i * 2 + 1] = (byte)((pcm16 >> 8) & 0xFF);
                    _readPos = (_readPos + 1) % _ringSize;
                }

                // MTU に収まるよう分割して送信
                int maxPayload = HapbeatProtocol.STREAM_DATA_MAX_PAYLOAD;
                int offset = 0;
                int remaining = pcmChunk.Length;
                while (remaining > 0)
                {
                    int sendSize = Math.Min(remaining, maxPayload);
                    client.SendStreamData(_byteOffset, pcmChunk, offset, sendSize);
                    _byteOffset += (uint)sendSize;
                    offset += sendSize;
                    remaining -= sendSize;
                }
            }
        }

        private HapbeatClient GetClient()
        {
            // HapbeatManager の内部 client を取得するために reflection は使わず、
            // 公開 API 経由で送信する。ただし SendStreamData は client に直接アクセスが必要。
            // → HapbeatManager に GetClient() を追加するか、Manager 経由で送信する。
            // ここでは Manager のストリーミング API を使わず、直接 client にアクセスするため
            // HapbeatManager.Client プロパティを使用する。
            return HapbeatManager.Instance?.Client;
        }

        private static int GetChannelCount()
        {
            switch (AudioSettings.speakerMode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Stereo: return 2;
                default: return 2; // それ以上はステレオにダウンミックスされる
            }
        }
    }
}
