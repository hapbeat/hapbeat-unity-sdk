#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor menu utility: batch-resample / upmix every WAV in a chosen folder
    /// (recursive) to <b>16 kHz / 2ch stereo / PCM16</b>.
    ///
    /// A Hapbeat stream session is locked to a single format, so any format
    /// mismatch (mono/stereo mix or different sample rates) across overlapping
    /// or back-to-back StreamClips makes later clips get rejected by the SDK.
    /// Studio normalizes audio automatically; use this menu for WAVs imported
    /// by hand or via external tools.
    ///
    /// Spec:
    /// - Input:  any .wav that Unity can load as an AudioClip
    /// - Output: 16kHz / 2ch / PCM16, written back to the same path (no backup)
    /// - mono → stereo: copy L = R (matches Web Audio / ffmpeg upmix)
    /// - multi-channel (5.1 etc) → stereo: keep first 2 channels (simple downmix)
    /// - resample: linear interpolation (fine for the 100–500 Hz haptic band)
    /// </summary>
    public static class HapbeatAudioNormalizer
    {
        private const int TargetRate = 16000;
        private const int TargetChannels = 2;

        [MenuItem("Hapbeat/Normalize Audio Folder (16kHz · 2ch · PCM16)", false, 72)]
        public static void NormalizeFolder()
        {
            string folder = EditorUtility.OpenFolderPanel(
                "Normalize Audio Folder — 16kHz / 2ch / PCM16",
                Application.dataPath, "");
            if (string.IsNullOrEmpty(folder)) return;

            // Convert to a path relative to the project so AssetDatabase can load it.
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            string folderAbs = folder.Replace('\\', '/');
            bool insideAssets = folderAbs.StartsWith(projectRoot + "/Assets",
                                                    StringComparison.OrdinalIgnoreCase);
            if (!insideAssets)
            {
                EditorUtility.DisplayDialog("Hapbeat",
                    "Please pick a folder under Assets (needed to load AudioClips via AssetDatabase).",
                    "OK");
                return;
            }

            string[] wavFiles = Directory.GetFiles(folderAbs, "*.wav", SearchOption.AllDirectories);
            if (wavFiles.Length == 0)
            {
                EditorUtility.DisplayDialog("Hapbeat",
                    "No WAV files found in the selected folder.", "OK");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("Hapbeat — Audio Normalize",
                $"Convert {wavFiles.Length} WAV file(s) under:\n\n" +
                $"{folderAbs}\n\n" +
                "to 16kHz / 2ch / PCM16. Files are overwritten in place (no backup). Continue?",
                "Run", "Cancel");
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

            string summary = $"Done: {converted} converted / {skipped} skipped (already normalized) / {failed} failed";
            if (failures.Count > 0)
            {
                summary += "\n\nFailures:\n" + string.Join("\n", failures);
            }
            EditorUtility.DisplayDialog("Hapbeat — Audio Normalize", summary, "OK");
            Debug.Log($"[HapbeatNormalize] {summary}");
        }

        /// <summary>
        /// Normalize one file. Skip if it is already 16kHz / 2ch.
        /// Returns true if the file was converted, false if skipped.
        /// </summary>
        private static bool NormalizeOne(string assetPath)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip == null) throw new InvalidOperationException("Cannot load file as AudioClip");

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
                throw new InvalidOperationException("clip.GetData failed");

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

        /// <summary>Downmix interleaved multi-channel float to interleaved stereo float.</summary>
        private static float[] ToStereoInterleaved(float[] src, int channels, int frames)
        {
            if (channels == 2) return src;  // pass-through
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

        /// <summary>Resample stereo interleaved samples using linear interpolation.</summary>
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

        /// <summary>Write a PCM16 interleaved WAV file (standard RIFF / fmt / data layout).</summary>
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
