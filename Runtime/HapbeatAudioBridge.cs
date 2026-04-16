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

        /// <summary>Gain を動的に変更可能。</summary>
        public float Gain
        {
            get => _gain;
            set => _gain = Mathf.Clamp(value, 0f, 2f);
        }

        private HapbeatClient _client;
        private bool _streamStarted;
        private int _sampleRate;
        private int _channels;
        private uint _byteOffset;

        // Audio thread → send buffer (lock-free ring)
        private float[] _ringBuffer;
        private int _ringSize;
        private volatile int _writePos;
        private volatile int _readPos;

        // Send thread
        private Thread _sendThread;
        private volatile bool _sendRunning;

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
        /// AudioSource が処理した音声データ（Volume, Pan, Spatialize 適用済み）を受け取る。
        /// </summary>
        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (!IsStreaming || _ringBuffer == null) return;

            _channels = channels;

            // Ring buffer に書き込み
            for (int i = 0; i < data.Length; i++)
            {
                int nextWrite = (_writePos + 1) % _ringSize;
                if (nextWrite == _readPos)
                    break; // buffer full, drop samples

                _ringBuffer[_writePos] = data[i];
                _writePos = nextWrite;
            }
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
                        _gain);
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
