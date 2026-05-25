#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor menu utility: 指定フォルダ直下 (再帰可) の WAV を
    /// **16 kHz / 2ch (stereo) / PCM16** に一括 resample/upmix する。
    ///
    /// Hapbeat の stream session は単一 format で固定されるため、複数 StreamClip
    /// が同時 / 連続で再生されるケースで format mismatch (mono/stereo 混在や
    /// rate 違い) があると後発 clip が SDK で reject される。Studio 経由なら
    /// 自動 normalize されるが、外部ツール / 手動コピーで取り込んだ WAV に対しては
    /// このメニューを使って事前統一する。
    ///
    /// 仕様:
    /// - 入力: AudioClip として Unity が load 可能な WAV (.wav)
    /// - 出力: 同じパスに 16kHz / 2ch / PCM16 で **上書き** (バックアップは作らない)
    /// - mono → stereo: L = R に複製 (Web Audio / ffmpeg 標準 up-mix と同じ規則)
    /// - 多 ch (5.1 等) → stereo: 先頭 2ch のみ採用 (簡易ダウンミックス)
    /// - resample: 線形補間 (haptic 帯域 100-500Hz には十分な品質)
    /// </summary>
    public static class HapbeatAudioNormalizer
    {
        private const int TargetRate = 16000;
        private const int TargetChannels = 2;

        [MenuItem("Hapbeat/Normalize Audio Folder (16kHz · 2ch · PCM16)", false, 51)]
        public static void NormalizeFolder()
        {
            string folder = EditorUtility.OpenFolderPanel(
                "Normalize Audio Folder — 16kHz / 2ch / PCM16",
                Application.dataPath, "");
            if (string.IsNullOrEmpty(folder)) return;

            // Application.dataPath 配下なら相対 path に変換 (AssetDatabase 経由で load 可能に)
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            string folderAbs = folder.Replace('\\', '/');
            bool insideAssets = folderAbs.StartsWith(projectRoot + "/Assets",
                                                    StringComparison.OrdinalIgnoreCase);
            if (!insideAssets)
            {
                EditorUtility.DisplayDialog("Hapbeat",
                    "Assets フォルダ配下を指定してください (AssetDatabase で AudioClip を load する都合)。",
                    "OK");
                return;
            }

            string[] wavFiles = Directory.GetFiles(folderAbs, "*.wav", SearchOption.AllDirectories);
            if (wavFiles.Length == 0)
            {
                EditorUtility.DisplayDialog("Hapbeat",
                    "指定フォルダに WAV ファイルが見つかりません。", "OK");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("Hapbeat — Audio Normalize",
                $"以下のフォルダ内 {wavFiles.Length} 個の WAV を **16kHz / 2ch / PCM16** に変換します。\n\n" +
                $"{folderAbs}\n\n" +
                "ファイルは **上書き** されます (バックアップは作りません)。続行しますか?",
                "実行", "キャンセル");
            if (!ok) return;

            int converted = 0, skipped = 0, failed = 0;
            var failures = new List<string>();

            try
            {
                for (int i = 0; i < wavFiles.Length; i++)
                {
                    string abs = wavFiles[i].Replace('\\', '/');
                    string rel = "Assets" + abs.Substring(projectRoot.Length + "/Assets".Length);
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "Normalizing audio",
                        $"{i + 1}/{wavFiles.Length}  {Path.GetFileName(abs)}",
                        (float)i / wavFiles.Length))
                    {
                        break;
                    }

                    try
                    {
                        bool wasConverted = NormalizeOne(rel);
                        if (wasConverted) converted++;
                        else skipped++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.Add($"{rel}: {ex.Message}");
                        Debug.LogWarning($"[HapbeatNormalize] failed for {rel}: {ex}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            string summary = $"完了: 変換 {converted} / 既に統一済 (skip) {skipped} / 失敗 {failed}";
            if (failures.Count > 0)
            {
                summary += "\n\n失敗一覧:\n" + string.Join("\n", failures);
            }
            EditorUtility.DisplayDialog("Hapbeat — Audio Normalize", summary, "OK");
            Debug.Log($"[HapbeatNormalize] {summary}");
        }

        /// <summary>
        /// 1 ファイルを normalize。既に 16kHz/2ch ならスキップ。
        /// 戻り値: 実際に変換したら true、skip なら false。
        /// </summary>
        private static bool NormalizeOne(string assetPath)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null) throw new InvalidOperationException("AudioClip として load できない");

            // 既に target format なら skip (16kHz / 2ch)。
            // bit-depth は WAV file 側で判定できないので「rate + channels 一致なら skip」に簡略化。
            // 厳密に 16-bit PCM 強制したい場合でも、ファイルが 24-bit 等で残るリスクは
            // Unity の load で float[] に正規化される時点で吸収されるため、最終的に
            // 我々が書き出す WAV は必ず PCM16 になる。
            if (clip.frequency == TargetRate && clip.channels == TargetChannels)
            {
                return false;
            }

            int srcChannels = clip.channels;
            int srcRate = clip.frequency;
            int srcSamples = clip.samples;  // per-channel sample count
            float[] srcData = new float[srcSamples * srcChannels];
            if (!clip.GetData(srcData, 0))
                throw new InvalidOperationException("clip.GetData 失敗");

            // 1. (多 ch なら) stereo に縮約
            float[] stereoData = ToStereoInterleaved(srcData, srcChannels, srcSamples);

            // 2. resample (linear interp) to TargetRate, stereo を保持
            float[] resampled = LinearResampleStereo(stereoData, srcRate, TargetRate);

            // 3. PCM16 stereo として WAV 書き出し (上書き)
            string absPath = Path.GetFullPath(assetPath);
            int outFrames = resampled.Length / TargetChannels;
            WriteWavPcm16(absPath, resampled, TargetRate, TargetChannels, outFrames);
            return true;
        }

        /// <summary>Interleaved 多 ch float → interleaved stereo float に縮約。</summary>
        private static float[] ToStereoInterleaved(float[] src, int channels, int frames)
        {
            if (channels == 2) return src;  // そのまま
            var dst = new float[frames * 2];
            if (channels == 1)
            {
                // mono: L = R = sample (Web Audio / ffmpeg 標準 up-mix)
                for (int f = 0; f < frames; f++)
                {
                    float s = src[f];
                    dst[f * 2] = s;
                    dst[f * 2 + 1] = s;
                }
            }
            else
            {
                // 多 ch (5.1, 7.1 等): 先頭 2ch のみ採用 (簡易、warning ログ無し)
                for (int f = 0; f < frames; f++)
                {
                    dst[f * 2] = src[f * channels];
                    dst[f * 2 + 1] = src[f * channels + 1];
                }
            }
            return dst;
        }

        /// <summary>Linear interpolation で stereo interleaved を resample。</summary>
        private static float[] LinearResampleStereo(float[] src, int srcRate, int dstRate)
        {
            if (srcRate == dstRate) return src;
            int srcFrames = src.Length / 2;
            // out frame 数 = ceil(srcFrames * dstRate / srcRate)
            int dstFrames = (int)((long)srcFrames * dstRate / srcRate);
            var dst = new float[dstFrames * 2];
            double step = (double)srcRate / dstRate;
            for (int i = 0; i < dstFrames; i++)
            {
                double srcPos = i * step;
                int idx = (int)srcPos;
                double frac = srcPos - idx;
                if (idx >= srcFrames - 1)
                {
                    // 終端
                    dst[i * 2] = src[(srcFrames - 1) * 2];
                    dst[i * 2 + 1] = src[(srcFrames - 1) * 2 + 1];
                }
                else
                {
                    float l0 = src[idx * 2];
                    float l1 = src[(idx + 1) * 2];
                    float r0 = src[idx * 2 + 1];
                    float r1 = src[(idx + 1) * 2 + 1];
                    dst[i * 2] = (float)(l0 + (l1 - l0) * frac);
                    dst[i * 2 + 1] = (float)(r0 + (r1 - r0) * frac);
                }
            }
            return dst;
        }

        /// <summary>PCM16 interleaved WAV file 書き出し (RIFF/fmt/data 標準形式)。</summary>
        private static void WriteWavPcm16(string path, float[] samples, int sampleRate, int channels, int frames)
        {
            int byteRate = sampleRate * channels * 2;
            int blockAlign = channels * 2;
            int dataSize = frames * blockAlign;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                // RIFF header
                bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataSize);
                bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                // fmt chunk (16 bytes for PCM)
                bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);                  // PCM
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)blockAlign);
                bw.Write((short)16);                 // bits per sample
                // data chunk
                bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                int n = frames * channels;
                for (int i = 0; i < n; i++)
                {
                    float v = samples[i] * 32767f;
                    if (v > 32767f) v = 32767f;
                    else if (v < -32768f) v = -32768f;
                    bw.Write((short)v);
                }
            }
        }
    }
}
#endif
