using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AudioCombiner;

public sealed class TargetConfig
{
    public string Id = "";
    public string Name = "";
    public int DelayMs;
    public float Volume = 1f;
}

/// <summary>
/// Захоплює звук із джерела і розсилає його на всі цілі.
/// Усі публічні методи викликаються з UI-потоку; Maintain() треба смикати таймером —
/// він перепідключає пристрої, що відвалилися (наприклад, Bluetooth).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    readonly MMDeviceEnumerator enumerator;
    IWaveIn? capture;
    WasapiOut? keepAlive;
    volatile TargetOutput[] outputs = [];
    Dictionary<string, TargetConfig> configs = new();
    readonly Dictionary<string, string> targetErrors = new();
    readonly Dictionary<string, DateTime> nextAttempt = new();

    string? sourceId;
    bool loopback;
    SampleKind kind;
    int srcChannels, srcRate, blockAlign;
    float[] scratch = [];
    long frameCounter;
    long lastDataTick;
    volatile bool captureFailed;
    DateTime nextCaptureAttempt;
    float peak;

    public bool Running { get; private set; }
    public string? SourceError { get; private set; }
    public bool SourceActive => capture != null && !captureFailed;
    public int ActiveCount => outputs.Length;
    public int ConfiguredCount => configs.Count;
    public volatile bool Metronome;

    public AudioEngine(MMDeviceEnumerator enumerator) => this.enumerator = enumerator;

    public void Start(string id, bool isLoopback)
    {
        Stop();
        sourceId = id;
        loopback = isLoopback;
        Running = true;
        captureFailed = false;
        nextCaptureAttempt = DateTime.MinValue;
        Maintain();
    }

    public void Stop()
    {
        Running = false;
        StopAll();
        targetErrors.Clear();
        nextAttempt.Clear();
        SourceError = null;
    }

    public void SetTargets(IEnumerable<TargetConfig> list)
    {
        configs = list.ToDictionary(c => c.Id);
        foreach (var o in outputs)
        {
            if (configs.TryGetValue(o.DeviceId, out var c))
            {
                o.DelayMs = c.DelayMs;
                o.Volume = c.Volume;
            }
        }
        Maintain();
    }

    public void Maintain()
    {
        if (!Running) return;

        // Сторож: якщо джерело мовчить понад 3 с — перезапускаємо
        if (capture != null && !captureFailed && Environment.TickCount64 - Interlocked.Read(ref lastDataTick) > 3000)
        {
            captureFailed = true;
            SourceError = "джерело перестало віддавати звук";
        }

        if (capture == null || captureFailed)
        {
            if (DateTime.Now < nextCaptureAttempt) return;
            StopAll();
            captureFailed = false;
            if (!TryStartCapture())
            {
                nextCaptureAttempt = DateTime.Now.AddSeconds(3);
                return;
            }
        }

        var keep = new List<TargetOutput>();
        bool changed = false;
        foreach (var o in outputs)
        {
            if (o.Failed || !configs.ContainsKey(o.DeviceId))
            {
                if (o.Failed)
                {
                    targetErrors[o.DeviceId] = o.Error ?? "пристрій відключився";
                    nextAttempt[o.DeviceId] = DateTime.Now.AddSeconds(2);
                }
                changed = true;
                outputs = outputs.Where(x => x != o).ToArray(); // спершу прибираємо з розсилки
                o.Dispose();
            }
            else keep.Add(o);
        }

        foreach (var cfg in configs.Values)
        {
            if (keep.Any(o => o.DeviceId == cfg.Id)) continue;
            if (loopback && cfg.Id == sourceId) continue;
            if (nextAttempt.TryGetValue(cfg.Id, out var t) && DateTime.Now < t) continue;
            try
            {
                var dev = enumerator.GetDevice(cfg.Id);
                if (dev.State != DeviceState.Active)
                {
                    targetErrors[cfg.Id] = "пристрій не підключено";
                    nextAttempt[cfg.Id] = DateTime.Now.AddSeconds(3);
                    continue;
                }
                keep.Add(new TargetOutput(dev, cfg.Id, cfg.Name, srcRate, srcChannels, cfg.DelayMs, cfg.Volume));
                targetErrors.Remove(cfg.Id);
                changed = true;
            }
            catch (Exception ex)
            {
                targetErrors[cfg.Id] = ex.Message;
                nextAttempt[cfg.Id] = DateTime.Now.AddSeconds(3);
            }
        }

        if (changed) outputs = keep.ToArray();
    }

    bool TryStartCapture()
    {
        try
        {
            var dev = enumerator.GetDevice(sourceId);
            if (dev.State != DeviceState.Active)
                throw new InvalidOperationException("джерело не підключене");

            IWaveIn cap = loopback ? new FastLoopbackCapture(dev) : new WasapiCapture(dev, true, 20);
            var wf = cap.WaveFormat;
            kind = SampleConverter.Detect(wf);
            srcChannels = wf.Channels;
            srcRate = wf.SampleRate;
            blockAlign = wf.BlockAlign;

            if (loopback)
            {
                // Loopback перестає віддавати дані в тиші — граємо на джерело тишу,
                // щоб потік був безперервним
                keepAlive = new WasapiOut(dev, AudioClientShareMode.Shared, true, 100);
                keepAlive.Init(new SilenceProvider(wf));
                keepAlive.Play();
            }

            cap.DataAvailable += OnData;
            cap.RecordingStopped += OnCaptureStopped;
            capture = cap;
            Interlocked.Exchange(ref lastDataTick, Environment.TickCount64);
            cap.StartRecording();
            SourceError = null;
            return true;
        }
        catch (Exception ex)
        {
            SourceError = ex.Message;
            StopAll();
            return false;
        }
    }

    void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (sender != capture || !Running) return;
        captureFailed = true;
        SourceError = e.Exception?.Message ?? "захоплення зупинилося";
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        int frames = e.BytesRecorded / blockAlign;
        if (frames == 0) return;
        Interlocked.Exchange(ref lastDataTick, Environment.TickCount64);

        int n = frames * srcChannels;
        if (scratch.Length < n) scratch = new float[n];
        SampleConverter.ToFloat(kind, e.Buffer, frames * blockAlign, scratch);

        if (Metronome) AddClicks(frames);
        frameCounter += frames;

        float p = 0;
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(scratch[i]);
            if (a > p) p = a;
        }
        if (p > peak) peak = p;

        foreach (var o in outputs) o.Write(scratch, frames);
    }

    /// Короткий «клац» раз на секунду — для підбору затримки на слух
    void AddClicks(int frames)
    {
        int clickLen = srcRate / 200;
        for (int f = 0; f < frames; f++)
        {
            long pos = (frameCounter + f) % srcRate;
            if (pos >= clickLen) continue;
            float v = 0.6f * MathF.Sin(2 * MathF.PI * 1000 * pos / srcRate) * (1 - pos / (float)clickLen);
            for (int c = 0; c < srcChannels; c++) scratch[f * srcChannels + c] += v;
        }
    }

    public float TakePeak()
    {
        float p = peak;
        peak = 0;
        return p;
    }

    public string GetTargetStatus(string id)
    {
        if (!Running || !configs.ContainsKey(id)) return "";
        var o = outputs.FirstOrDefault(x => x.DeviceId == id);
        if (o != null) return o.Status;
        if (!SourceActive) return "очікую джерело";
        return targetErrors.TryGetValue(id, out var err) ? "очікую: " + err : "підключаю…";
    }

    void StopAll()
    {
        var c = capture;
        capture = null;
        if (c != null)
        {
            c.DataAvailable -= OnData;
            c.RecordingStopped -= OnCaptureStopped;
            try { c.StopRecording(); } catch { }
            try { c.Dispose(); } catch { }
        }

        try { keepAlive?.Dispose(); } catch { }
        keepAlive = null;

        var outs = outputs;
        outputs = [];
        foreach (var o in outs)
        {
            try { o.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}

/// Стандартний WasapiLoopbackCapture віддає звук порціями по ~50 мс; цей — по ~10 мс,
/// тож рівень буфера в цілях тримається рівніше
sealed class FastLoopbackCapture(MMDevice device) : WasapiCapture(device, false, 20)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
}

/// Сповіщення Windows про підключення/відключення аудіопристроїв
public sealed class DeviceWatcher : IMMNotificationClient
{
    public event Action? Changed;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Changed?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => Changed?.Invoke();
    public void OnDeviceRemoved(string deviceId) => Changed?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}
