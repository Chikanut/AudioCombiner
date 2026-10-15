using System.Text.Json;

namespace AudioCombiner;

public sealed class TargetSettings
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public int DelayMs { get; set; }
    public int VolumePercent { get; set; } = 100;
}

public sealed class AppSettings
{
    public string? SourceId { get; set; }
    public string SourceName { get; set; } = "";
    public bool SourceIsLoopback { get; set; }
    public bool AutoStartRelay { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public List<TargetSettings> Targets { get; set; } = new();

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioCombiner");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// Налаштування зберігаються для всіх пристроїв, навіть вимкнених, щоб затримка не губилась
    public TargetSettings GetTarget(string id, string name)
    {
        var t = Targets.FirstOrDefault(x => x.DeviceId == id);
        if (t == null)
        {
            t = new TargetSettings { DeviceId = id, Name = name };
            Targets.Add(t);
        }
        t.Name = name;
        return t;
    }
}
