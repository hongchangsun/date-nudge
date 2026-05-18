using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DateReminder
{
    /// <summary>
    /// 警报声提供器：用代码生成短促WAV音效，无需外部资源文件
    /// </summary>
    public static class AlarmSoundProvider
    {
        /// <summary>所有可选警报声名称</summary>
        public static readonly string[] SoundNames = { "短促蜂鸣", "双音警报", "急促滴答", "长鸣警笛", "电子提示" };

        /// <summary>默认警报声索引</summary>
        public const int DefaultSoundIndex = 0;

        private static readonly Dictionary<string, byte[]> _cache = new();

        /// <summary>获取指定警报声的WAV数据</summary>
        public static byte[] GetWavData(string soundName)
        {
            if (_cache.TryGetValue(soundName, out var cached))
                return cached;

            var data = soundName switch
            {
                "短促蜂鸣" => GenerateBeep(880, 200),
                "双音警报" => GenerateDualTone(660, 880, 150, 2),
                "急促滴答" => GenerateStaccato(1200, 80, 5),
                "长鸣警笛" => GenerateSiren(400, 900, 500),
                "电子提示" => GenerateChime(523, 659, 784, 100),
                _ => GenerateBeep(880, 200),
            };

            _cache[soundName] = data;
            return data;
        }

        /// <summary>播放指定警报声（同步，用于过期阻塞提示）</summary>
        public static void Play(string soundName)
        {
            var data = GetWavData(soundName);
            // 在线程池播放，避免阻塞UI
            Task.Run(() =>
            {
                // 方法1：SoundPlayer
                try
                {
                    var tempFile = Path.Combine(Path.GetTempPath(), $"date_reminder_alarm_{soundName}.wav");
                    File.WriteAllBytes(tempFile, data);
                    using var player = new System.Media.SoundPlayer(tempFile);
                    player.PlaySync();
                    return;
                }
                catch (Exception ex)
                {
                    LogSoundError($"Play_SoundPlayer失败: {ex.Message}，改用Beep");
                }
                // 方法2：Win32 Beep 后备
                try { PlayBeepFallback(soundName); }
                catch (Exception ex) { LogSoundError($"Play_Beep失败: {ex.Message}"); }
            });
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Beep(int dwFreq, int dwDuration);

        private static void LogSoundError(string msg)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "date_reminder_sound.log"), $"{DateTime.Now:HH:mm:ss} {msg}\n"); } catch { }
        }

        /// <summary>异步播放（不阻塞，用于试听）</summary>
        public static void PlayAsync(string soundName)
        {
            var data = GetWavData(soundName);
            // 方法1：SoundPlayer（可能静默失败）
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"date_reminder_alarm_{soundName}.wav");
                File.WriteAllBytes(tempFile, data);
                using var player = new System.Media.SoundPlayer(tempFile);
                player.Play();
                return; // 成功就返回
            }
            catch (Exception ex)
            {
                LogSoundError($"SoundPlayer失败: {ex.Message}，改用Beep");
            }
            // 方法2：Win32 Beep 后备（最可靠）
            try
            {
                PlayBeepFallback(soundName);
            }
            catch (Exception ex)
            {
                LogSoundError($"Beep后备也失败: {ex.Message}");
            }
        }

        /// <summary>用 Win32 Beep 播放对应声音的备用方案</summary>
        private static void PlayBeepFallback(string soundName)
        {
            switch (soundName)
            {
                case "短促蜂鸣":
                    Beep(880, 200);
                    break;
                case "双音警报":
                    Beep(660, 150);
                    Thread.Sleep(50);
                    Beep(880, 150);
                    break;
                case "急促滴答":
                    for (int i = 0; i < 5; i++) { Beep(1200, 80); Thread.Sleep(60); }
                    break;
                case "长鸣警笛":
                    Beep(440, 500);
                    break;
                case "电子提示":
                    Beep(523, 100);
                    Thread.Sleep(40);
                    Beep(659, 100);
                    Thread.Sleep(40);
                    Beep(784, 100);
                    break;
                default:
                    Beep(880, 200);
                    break;
            }
        }

        #region WAV 生成

        const int SampleRate = 22050;
        const short BitsPerSample = 16;
        const short Channels = 1;

        /// <summary>生成单频蜂鸣</summary>
        private static byte[] GenerateBeep(double freq, int durationMs)
        {
            int samples = SampleRate * durationMs / 1000;
            var pcm = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / SampleRate;
                // 带淡入淡出
                double envelope = 1.0;
                int fade = SampleRate / 50; // 20ms 淡入淡出
                if (i < fade) envelope = (double)i / fade;
                else if (i > samples - fade) envelope = (double)(samples - i) / fade;
                pcm[i] = (short)(short.MaxValue * 0.5 * envelope * Math.Sin(2 * Math.PI * freq * t));
            }
            return PcmToWav(pcm);
        }

        /// <summary>生成双音交替</summary>
        private static byte[] GenerateDualTone(double freq1, double freq2, int toneDurationMs, int repeats)
        {
            int toneSamples = SampleRate * toneDurationMs / 1000;
            int gapSamples = SampleRate * 50 / 1000; // 50ms 间隔
            int totalSamples = (toneSamples + gapSamples) * repeats * 2 - gapSamples;
            var pcm = new short[totalSamples];
            int pos = 0;
            for (int r = 0; r < repeats; r++)
            {
                // 第一个音
                for (int i = 0; i < toneSamples && pos < totalSamples; i++, pos++)
                {
                    double t = (double)i / SampleRate;
                    double envelope = GetEnvelope(i, toneSamples, SampleRate / 50);
                    pcm[pos] = (short)(short.MaxValue * 0.5 * envelope * Math.Sin(2 * Math.PI * freq1 * t));
                }
                // 间隔
                for (int i = 0; i < gapSamples && pos < totalSamples; i++, pos++) pcm[pos] = 0;
                // 第二个音
                for (int i = 0; i < toneSamples && pos < totalSamples; i++, pos++)
                {
                    double t = (double)i / SampleRate;
                    double envelope = GetEnvelope(i, toneSamples, SampleRate / 50);
                    pcm[pos] = (short)(short.MaxValue * 0.5 * envelope * Math.Sin(2 * Math.PI * freq2 * t));
                }
                // 间隔（最后一次不加）
                if (r < repeats - 1)
                    for (int i = 0; i < gapSamples && pos < totalSamples; i++, pos++) pcm[pos] = 0;
            }
            return PcmToWav(pcm);
        }

        /// <summary>生成急促短音（滴答声）</summary>
        private static byte[] GenerateStaccato(double freq, int tickMs, int count)
        {
            int tickSamples = SampleRate * tickMs / 1000;
            int gapSamples = SampleRate * 60 / 1000; // 60ms 间隔
            int totalSamples = (tickSamples + gapSamples) * count - gapSamples;
            var pcm = new short[totalSamples];
            int pos = 0;
            for (int n = 0; n < count; n++)
            {
                for (int i = 0; i < tickSamples && pos < totalSamples; i++, pos++)
                {
                    double t = (double)i / SampleRate;
                    double envelope = GetEnvelope(i, tickSamples, SampleRate / 80);
                    pcm[pos] = (short)(short.MaxValue * 0.5 * envelope * Math.Sin(2 * Math.PI * freq * t));
                }
                if (n < count - 1)
                    for (int i = 0; i < gapSamples && pos < totalSamples; i++, pos++) pcm[pos] = 0;
            }
            return PcmToWav(pcm);
        }

        /// <summary>生成上下滑音（警笛）</summary>
        private static byte[] GenerateSiren(double freqLow, double freqHigh, int durationMs)
        {
            int samples = SampleRate * durationMs / 1000;
            var pcm = new short[samples];
            double phase = 0;
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / SampleRate;
                // 频率在低高之间线性往返
                double progress = (double)i / samples;
                double cycle = progress * 2; // 两个周期
                double freq;
                if (cycle % 2 < 1)
                    freq = freqLow + (freqHigh - freqLow) * (cycle % 1);
                else
                    freq = freqHigh - (freqHigh - freqLow) * (cycle % 1);

                phase += 2 * Math.PI * freq / SampleRate;
                double envelope = GetEnvelope(i, samples, SampleRate / 40);
                pcm[i] = (short)(short.MaxValue * 0.45 * envelope * Math.Sin(phase));
            }
            return PcmToWav(pcm);
        }

        /// <summary>生成三音和弦（电子提示音）</summary>
        private static byte[] GenerateChime(double f1, double f2, double f3, int toneMs)
        {
            int toneSamples = SampleRate * toneMs / 1000;
            int gapSamples = SampleRate * 40 / 1000; // 40ms 间隔
            int totalSamples = (toneSamples + gapSamples) * 3 - gapSamples;
            var pcm = new short[totalSamples];
            int pos = 0;
            double[] freqs = { f1, f2, f3 };
            foreach (var freq in freqs)
            {
                for (int i = 0; i < toneSamples && pos < totalSamples; i++, pos++)
                {
                    double t = (double)i / SampleRate;
                    double envelope = GetEnvelope(i, toneSamples, SampleRate / 40);
                    pcm[pos] = (short)(short.MaxValue * 0.5 * envelope * Math.Sin(2 * Math.PI * freq * t));
                }
                if (freq != freqs.Last())
                    for (int i = 0; i < gapSamples && pos < totalSamples; i++, pos++) pcm[pos] = 0;
            }
            return PcmToWav(pcm);
        }

        private static double GetEnvelope(int i, int total, int fadeSamples)
        {
            if (i < fadeSamples) return (double)i / fadeSamples;
            if (i > total - fadeSamples) return (double)(total - i) / fadeSamples;
            return 1.0;
        }

        /// <summary>PCM short[] → WAV byte[]</summary>
        private static byte[] PcmToWav(short[] pcm)
        {
            int dataSize = pcm.Length * 2;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            // RIFF header
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + dataSize);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                    // chunk size
            w.Write((short)1);              // PCM format
            w.Write(Channels);
            w.Write(SampleRate);
            w.Write(SampleRate * Channels * BitsPerSample / 8); // byte rate
            w.Write((short)(Channels * BitsPerSample / 8));     // block align
            w.Write(BitsPerSample);

            // data chunk
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);
            foreach (var s in pcm)
                w.Write(s);

            w.Flush();
            return ms.ToArray();
        }

        #endregion
    }
}
