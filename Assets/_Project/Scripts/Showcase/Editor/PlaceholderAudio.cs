using System.IO;
using UnityEditor;
using UnityEngine;

namespace GemStore.Showcase.Editor
{
    /// <summary>Generates tiny placeholder WAV sounds so the project has audible feedback before real sounds exist.</summary>
    public static class PlaceholderAudio
    {
        /// <summary>Creates (once) a short decaying beep made of the given frequencies, one after another.</summary>
        public static AudioClip GetOrCreateBeep(string assetPath, float seconds, params float[] frequencies)
        {
            return GetOrCreateTone(assetPath, seconds, 24f, frequencies);
        }

        /// <summary>
        /// Creates (once) a sequence of sine notes. Every note starts with a quick attack and decays by <paramref name="decay"/>
        /// (higher = shorter). Delete the .wav and rebuild to regenerate it.
        /// </summary>
        public static AudioClip GetOrCreateTone(string assetPath, float seconds, float decay, params float[] frequencies)
        {
            if (!File.Exists(assetPath))
            {
                string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
                Directory.CreateDirectory(folder);

                const int sampleRate = 44100;
                int sampleCount = (int)(sampleRate * seconds);
                float noteLength = seconds / frequencies.Length;
                var bytes = new byte[44 + sampleCount * 2];

                void WriteText(int offset, string text)
                {
                    for (int i = 0; i < text.Length; i++) bytes[offset + i] = (byte)text[i];
                }

                void WriteInt(int offset, int value)
                {
                    bytes[offset] = (byte)value;
                    bytes[offset + 1] = (byte)(value >> 8);
                    bytes[offset + 2] = (byte)(value >> 16);
                    bytes[offset + 3] = (byte)(value >> 24);
                }

                void WriteShort(int offset, int value)
                {
                    bytes[offset] = (byte)value;
                    bytes[offset + 1] = (byte)(value >> 8);
                }

                WriteText(0, "RIFF");
                WriteInt(4, 36 + sampleCount * 2);
                WriteText(8, "WAVE");
                WriteText(12, "fmt ");
                WriteInt(16, 16);
                WriteShort(20, 1);
                WriteShort(22, 1);
                WriteInt(24, sampleRate);
                WriteInt(28, sampleRate * 2);
                WriteShort(32, 2);
                WriteShort(34, 16);
                WriteText(36, "data");
                WriteInt(40, sampleCount * 2);

                float phase = 0f;
                for (int i = 0; i < sampleCount; i++)
                {
                    float t = i / (float)sampleRate;
                    int noteIndex = Mathf.Min(frequencies.Length - 1, (int)(t / noteLength));
                    float noteTime = t - noteIndex * noteLength;
                    phase += 2f * Mathf.PI * frequencies[noteIndex] / sampleRate;
                    float envelope = Mathf.Exp(-noteTime * decay) * Mathf.Min(1f, noteTime * 400f);
                    short sample = (short)(Mathf.Sin(phase) * envelope * 0.5f * short.MaxValue);
                    WriteShort(44 + i * 2, sample);
                }

                File.WriteAllBytes(assetPath, bytes);
                AssetDatabase.ImportAsset(assetPath);
            }

            return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }
    }
}
