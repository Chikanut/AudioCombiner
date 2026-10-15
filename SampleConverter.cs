using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioCombiner;

public enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

public static class SampleConverter
{
    static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static SampleKind Detect(WaveFormat wf)
    {
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
        bool isPcm = wf.Encoding == WaveFormatEncoding.Pcm;
        if (wf is WaveFormatExtensible ext)
        {
            isFloat = ext.SubFormat == SubtypeFloat;
            isPcm = ext.SubFormat == SubtypePcm;
        }

        if (isFloat && wf.BitsPerSample == 32) return SampleKind.Float32;
        if (isPcm)
        {
            switch (wf.BitsPerSample)
            {
                case 16: return SampleKind.Pcm16;
                case 24: return SampleKind.Pcm24;
                case 32: return SampleKind.Pcm32;
            }
        }
        throw new NotSupportedException($"Непідтримуваний формат звуку: {wf}");
    }

    public static bool IsFloat32(WaveFormat wf)
    {
        try { return Detect(wf) == SampleKind.Float32; } catch { return false; }
    }

    public static void ToFloat(SampleKind kind, byte[] src, int bytes, float[] dst)
    {
        switch (kind)
        {
            case SampleKind.Float32:
                Buffer.BlockCopy(src, 0, dst, 0, bytes);
                break;
            case SampleKind.Pcm16:
                for (int i = 0, n = bytes / 2; i < n; i++)
                    dst[i] = BitConverter.ToInt16(src, i * 2) / 32768f;
                break;
            case SampleKind.Pcm24:
                for (int i = 0, n = bytes / 3; i < n; i++)
                {
                    int p = i * 3;
                    int v = src[p] | (src[p + 1] << 8) | ((sbyte)src[p + 2] << 16);
                    dst[i] = v / 8388608f;
                }
                break;
            case SampleKind.Pcm32:
                for (int i = 0, n = bytes / 4; i < n; i++)
                    dst[i] = BitConverter.ToInt32(src, i * 4) / 2147483648f;
                break;
        }
    }
}

public static class ChannelMixer
{
    /// Матриця [вихідний канал, вхідний канал] для перетворення кількості каналів
    public static float[,] Build(int src, int dst)
    {
        var m = new float[dst, src];
        if (src == dst)
        {
            for (int i = 0; i < src; i++) m[i, i] = 1;
        }
        else if (src == 1)
        {
            for (int d = 0; d < dst; d++) m[d, 0] = 1;
        }
        else if (dst == 1)
        {
            m[0, 0] = 0.5f;
            m[0, 1] = 0.5f;
        }
        else if (dst == 2)
        {
            // Даунмікс у стерео: FL FR [C LFE] BL BR SL SR
            m[0, 0] = 1;
            m[1, 1] = 1;
            if (src == 4)
            {
                m[0, 2] = 0.707f;
                m[1, 3] = 0.707f;
            }
            else
            {
                if (src > 2) { m[0, 2] = 0.707f; m[1, 2] = 0.707f; }
                for (int s = 4; s < src; s++)
                    m[s % 2, s] = 0.707f;
            }
            for (int d = 0; d < 2; d++)
            {
                float sum = 0;
                for (int s = 0; s < src; s++) sum += m[d, s];
                for (int s = 0; s < src; s++) m[d, s] /= sum;
            }
        }
        else
        {
            for (int i = 0; i < Math.Min(src, dst); i++) m[i, i] = 1;
        }
        return m;
    }
}
