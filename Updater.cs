using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace AudioCombiner;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string? DownloadUrl);

/// Оновлення з релізів GitHub: шукає новіший реліз, завантажує AudioCombiner.exe і підміняє поточний файл
public static class Updater
{
    public const string Repo = "Chikanut/AudioCombiner";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases";
    const string AssetName = "AudioCombiner.exe";

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    static readonly HttpClient http = CreateClient();

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Не вдалося визначити шлях до програми.");
    static string OldPath => ExePath + ".old";
    static string NewPath => ExePath + ".new";

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AudioCombiner", CurrentVersion.ToString()));
        return c;
    }

    /// Повертає новіший реліз або null, якщо оновлень немає
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null; // релізів ще немає
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= CurrentVersion) return null;

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    downloadUrl = a.GetProperty("browser_download_url").GetString();

        return new UpdateInfo(
            version,
            tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? ReleasesUrl : ReleasesUrl,
            downloadUrl);
    }

    /// Завантажує новий exe і ставить його на місце поточного. Запущений exe не можна видалити, але можна перейменувати.
    public static async Task InstallAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        if (info.DownloadUrl == null) throw new InvalidOperationException("У релізі немає файлу " + AssetName + ".");

        using (var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(NewPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long done = 0;
            int n, lastPct = -1;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct) progress?.Report(lastPct = pct);
                }
            }
            if (total > 0 && done != total) throw new IOException("Файл завантажено не повністю.");
        }

        if (!LooksLikeExe(NewPath))
        {
            TryDelete(NewPath);
            throw new InvalidDataException("Завантажений файл пошкоджений.");
        }

        TryDelete(OldPath);
        File.Move(ExePath, OldPath);
        try
        {
            File.Move(NewPath, ExePath);
        }
        catch
        {
            File.Move(OldPath, ExePath);
            throw;
        }
    }

    /// Запускає нову версію; вона дочекається, поки ця завершиться
    public static void Restart(bool hidden, bool relay)
    {
        var args = "--updated" + (hidden ? " --tray" : "") + (relay ? " --relay" : "");
        Process.Start(new ProcessStartInfo(ExePath, args) { UseShellExecute = false });
    }

    /// Прибирає файли, що лишились після попереднього оновлення
    public static void Cleanup()
    {
        try
        {
            TryDelete(OldPath);
            TryDelete(NewPath);
        }
        catch { }
    }

    static bool TryParseVersion(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];
        if (Version.TryParse(s, out var v))
        {
            version = Normalize(v);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    static bool LooksLikeExe(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 64 * 1024) return false;
        using var f = fi.OpenRead();
        return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
