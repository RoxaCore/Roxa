using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Roxa.Core;

/// <summary>
/// گزارش باگ/کرش به پنل (api/report.php).
/// کرش در لحظه در پوشهٔ صف نوشته می‌شود و ارسال فوری بهترین-تلاش است؛ وگرنه
/// در اجرای بعدی (یا بعد از اتصال) FlushPending آن را می‌فرستد.
/// هر گزارش دو بخش دارد: بخش خودکار برنامه (نسخه، سیستم‌عامل، شناسه، خطا/لاگ)
/// و متن اختیاری کاربر.
/// </summary>
public static class BugReport
{
    private static readonly string PendingDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "reports-pending");

    private const int PendingCap = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Payload
    {
        public string Type { get; set; } = "manual";
        public string Ver { get; set; } = RemoteConfig.AppVersion;
        public string Os { get; set; } = "";
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
        public string Log { get; set; } = "";
    }

    public static string OsInfo()
    {
        try
        {
            return RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
        }
        catch
        {
            return "";
        }
    }

    public static string DeviceId()
    {
        try
        {
            return Storage.LoadSettings().ClientId ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static Payload Create(string type) => new() { Type = type, Os = OsInfo(), Id = DeviceId() };

    /// <summary>کرش: فایل در صف نوشته می‌شود و ارسال فوری بهترین-تلاش انجام می‌گیرد.</summary>
    public static void ReportCrash(Exception? ex, string logTail = "")
    {
        if (ex == null) return;
        var p = Create("crash");
        p.Error = Cut($"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}", 4000);
        p.Log = Cut(logTail, 4000);
        string? file = Enqueue(p);
        if (file == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await SendAsync(p, null)) File.Delete(file);
            }
            catch { }
        });
    }

    public static string? Enqueue(Payload p)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            var files = Directory.GetFiles(PendingDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            while (files.Count >= PendingCap)
            {
                try { File.Delete(files[0]); } catch { }
                files.RemoveAt(0);
            }
            string file = Path.Combine(PendingDir, $"{DateTime.Now:yyyyMMddHHmmssfff}-{p.Type}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(p, JsonOptions), Encoding.UTF8);
            return file;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> SendAsync(Payload p, int? proxyPort, CancellationToken ct = default)
    {
        try
        {
            using var handler = new HttpClientHandler();
            if (proxyPort is > 0)
            {
                handler.Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort.Value}");
                handler.UseProxy = true;
            }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Roxa/3.0");
            using var content = new StringContent(JsonSerializer.Serialize(p, JsonOptions), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{RemoteConfig.BaseUrl}/api/report.php", content, ct);
            if (!resp.IsSuccessStatusCode) return false;
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ارسال همهٔ گزارش‌های در صف؛ با اولین شکست (قطعی شبکه) متوقف می‌شود.</summary>
    public static async Task<int> FlushPendingAsync(int? proxyPort)
    {
        int sent = 0;
        try
        {
            if (!Directory.Exists(PendingDir)) return 0;
            foreach (var file in Directory.GetFiles(PendingDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Payload>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                    if (p == null || (p.Message == "" && p.Error == "" && p.Log == ""))
                    {
                        File.Delete(file);
                        continue;
                    }
                    if (!await SendAsync(p, proxyPort)) break;
                    File.Delete(file);
                    sent++;
                }
                catch
                {
                    break;
                }
            }
        }
        catch { }
        return sent;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
