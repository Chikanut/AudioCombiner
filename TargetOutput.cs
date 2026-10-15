using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioCombiner;

/// <summary>
/// Один пристрій-ціль. Тримає кільцевий буфер у форматі джерела і на льоту
/// перетворює частоту та канали під формат пристрою.
/// Годинники різних пристроїв трохи розходяться, тому швидкість читання
/// плавно підлаштовується (до ±0.3%), щоб буфер тримався на потрібному рівні
/// без клацань і скидань.
/// </summary>
public sealed class TargetOutput : IWaveProvider, IDisposable
{
    const int SafetyMs = 40;        // запас буфера проти ривків
    const int RetuneMs = 5;         // точність застосування нової затримки
    const int HardLimitMs = 80;     // відхилення, після якого вирівнюємо стрибком
    const double MaxCorrection = 0.003;
    const double Gain = 0.0002;     // корекція швидкості на 1 мс відхилення
    const double Smoothing = 0.02;

    readonly object sync = new();
    readonly int srcRate, srcCh, dstRate, dstCh;
    readonly float[,] matrix;
    readonly float[] ring;
    readonly int capFrames;
    readonly float[] taps;
    readonly WasapiOut output;

    long writePos, readPos;
    double frac, avgLevel, correction;
    bool priming = true;
    int appliedDelay = -1, dropouts;

    public string DeviceId { get; }
    public string Name { get; }
    public WaveFormat WaveFormat { get; }
    public volatile int DelayMs;
    public volatile float Volume;
    public volatile bool Failed;
    public string? Error { get; private set; }

    public TargetOutput(MMDevice device, string id, string name, int srcRate, int srcChannels, int delayMs, float volume)
    {
        DeviceId = id;
        Name = name;
        this.srcRate = srcRate;
        srcCh = srcChannels;
        DelayMs = delayMs;
        Volume = volume;

        WaveFormat mix;
        using (var ac = device.AudioClient) mix = ac.MixFormat;
        dstRate = mix.SampleRate;
        dstCh = mix.Channels;
        // Віддаємо звук рівно у мікс-форматі пристрою — тоді Windows нічого не перетворює
        WaveFormat = SampleConverter.IsFloat32(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(dstRate, dstCh);

        matrix = ChannelMixer.Build(srcCh, dstCh);
        capFrames = srcRate * 4;
        ring = new float[capFrames * srcCh];
        taps = new float[4 * dstCh];

        output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception != null) Error = e.Exception.Message;
            Failed = true;
        };
        try
        {
            output.Init(this);
            output.Play();
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// Викликається з потоку захоплення
    public void Write(float[] data, int frames)
    {
        lock (sync)
        {
            long free = capFrames - (writePos - readPos) - 8;
            if (frames > free) readPos += frames - free; // переповнення: відкидаємо найстаріше

            int pos = (int)(writePos % capFrames);
            int first = Math.Min(frames, capFrames - pos);
            Array.Copy(data, 0, ring, pos * srcCh, first * srcCh);
            if (first < frames)
                Array.Copy(data, first * srcCh, ring, 0, (frames - first) * srcCh);
            writePos += frames;
        }
    }

    /// Викликається з потоку відтворення WASAPI
    public int Read(byte[] buffer, int offset, int count)
    {
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        lock (sync) Render(dst, dst.Length / dstCh);
        return count;
    }

    void Render(Span<float> dst, int frames)
    {
        double level = writePos - readPos - frac;
        int delay = DelayMs;
        double target = (delay + SafetyMs) * srcRate / 1000.0;
        double retune = RetuneMs * srcRate / 1000.0;
        double hard = HardLimitMs * srcRate / 1000.0;

        if (delay != appliedDelay)
        {
            // Затримку змінили повзунком — застосовуємо одразу
            appliedDelay = delay;
            if (level < target - retune) priming = true;
            else if (level > target + retune) Drop(ref level, target);
        }
        else if (!priming)
        {
            if (level > target + hard) Drop(ref level, target);
            else if (level < target - hard) priming = true;
        }

        if (priming)
        {
            if (level < target)
            {
                dst.Clear();
                return;
            }
            priming = false;
            avgLevel = level;
        }

        avgLevel += (level - avgLevel) * Smoothing;
        double errMs = (avgLevel - target) * 1000.0 / srcRate;
        correction = Math.Clamp(errMs * Gain, -MaxCorrection, MaxCorrection);
        double step = (double)srcRate / dstRate * (1.0 + correction);
        float vol = Volume;

        for (int i = 0; i < frames; i++)
        {
            if (readPos + 2 >= writePos)
            {
                // Дані скінчилися — тиша і повторна буферизація
                dst[(i * dstCh)..].Clear();
                priming = true;
                dropouts++;
                return;
            }

            LoadTaps();
            float t = (float)frac;
            int o = i * dstCh;
            for (int c = 0; c < dstCh; c++)
            {
                float v = Cubic(taps[c], taps[dstCh + c], taps[2 * dstCh + c], taps[3 * dstCh + c], t) * vol;
                dst[o + c] = Math.Clamp(v, -1f, 1f);
            }

            frac += step;
            long adv = (long)frac;
            readPos += adv;
            frac -= adv;
        }
    }

    void Drop(ref double level, double target)
    {
        long n = (long)(level - target);
        readPos += n;
        level -= n;
        avgLevel = level;
    }

    /// Чотири сусідні кадри (readPos-1 .. readPos+2), вже змішані під канали пристрою
    void LoadTaps()
    {
        for (int k = 0; k < 4; k++)
        {
            long frame = readPos - 1 + k;
            int idx = (int)(((frame % capFrames) + capFrames) % capFrames) * srcCh;
            for (int d = 0; d < dstCh; d++)
            {
                float sum = 0;
                for (int s = 0; s < srcCh; s++) sum += matrix[d, s] * ring[idx + s];
                taps[k * dstCh + d] = sum;
            }
        }
    }

    // Інтерполяція Катмулла-Рома
    static float Cubic(float y0, float y1, float y2, float y3, float t)
    {
        float a = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
        float b = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c = -0.5f * y0 + 0.5f * y2;
        return ((a * t + b) * t + c) * t + y1;
    }

    public string Status
    {
        get
        {
            lock (sync)
            {
                if (priming) return "буферизація…";
                double ms = (writePos - readPos - frac) * 1000.0 / srcRate;
                string s = $"працює · {dstRate / 1000.0:0.#} кГц · буфер {ms:0} мс · підстроювання {correction * 100:+0.00;-0.00;0.00}%";
                if (dropouts > 0) s += $" · пропусків: {dropouts}";
                return s;
            }
        }
    }

    public void Dispose()
    {
        try { output.Stop(); } catch { }
        output.Dispose();
    }
}
