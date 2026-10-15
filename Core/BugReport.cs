// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// گزارشِ درِ صف بیش از این عمر نمی‌کند.
    ///
    /// سقفِ تعداد از اول بود، ولی مهلت نه — و کمبودِ مهلت یعنی اگر کاربر هرگز
    /// رضایت نمی‌داد، همان نخستین گزارشِ کرش برایِ همیشه روی دیسک می‌ماند.
    /// ۳۰ روز هم‌اندازهٔ سقفِ تعداد است و از ارزشِ یک گزارشِ کرش برایِ
    /// خطایابی هم بیشتر است: بعد از یک ماه، لاگ و stack آن برایِ نسخهٔ فعلی
    /// کاربردش را از دست داده.
    /// </summary>
    private const int PendingMaxAgeDays = 30;

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

    /// <summary>
    /// آیا فرستادنِ گزارش مجاز است؟
    ///
    /// نوشتنِ محلی در صف همیشه انجام می‌شود — بی‌ضرر است و اگر کاربر بعداً رضایت
    /// داد، گزارشِ کرشِ همان نخستین اجرا (که ارزشمندترینشان است) گم نمی‌شود.
    /// این کلید فقط «رفتن به سرور» را کنترل می‌کند.
    ///
    /// بدونِ این گیت، خطا و stack و ۴۰ خطِ آخرِ لاگ و ClientId برخلافِ آنچه در
    /// تنظیمات به کاربر گفته بودیم، بی‌صدا ارسال می‌شد — گزارشِ کرش از مسیرِ
    /// FlushPendingAsync هم می‌گذشت و اصلاً به تنظیمات نگاه نمی‌کرد.
    /// </summary>
    private static bool ConsentAllowsSend()
    {
        try { return Storage.LoadSettings().SendUsageStats; }
        catch { return false; } // نتوانستیم رضایت را بخوانیم ⇒ رضایت نداریم ⇒ نمی‌فرستیم
    }

    /// <summary>کرش: فایل در صف نوشته می‌شود و ارسال فوری بهترین-تلاش انجام می‌گیرد.</summary>
    public static void ReportCrash(Exception? ex, string logTail = "")
    {
        if (ex == null) return;
        var p = Create("crash");
        p.Error = Cut($"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}", 4000);
        p.Log = Cut(logTail, 4000);
        string? file = Enqueue(p);
        if (file == null) return;
        if (!ConsentAllowsSend()) return; // در صف می‌ماند؛ فقط اگر کاربر رضایت بدهد می‌رود
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
            // رویِ دیسک هم نسخهٔ پاک‌شده می‌ماند؛ مبادا خودِ فایلِ صف بعداً
            // کسی را لو بدهد، بی‌آنکه کسی آن را فرستاده باشد.
            File.WriteAllText(file, JsonSerializer.Serialize(Sanitized(p), JsonOptions), Encoding.UTF8);
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// صف را از گزارش‌هایِ قدیمی پاک می‌کند.
    ///
    /// عمداً پیش از بررسیِ رضایت اجرا می‌شود: این بهداشتِ محلی است، نه ارسال، و
    /// دقیقاً در همان حالتِ بی‌رضایت است که صف رشد می‌کند و اگر کسی آن را نگاه
    /// دارد باید کهنه شود.
    /// </summary>
    private static void SweepExpired()
    {
        try
        {
            if (!Directory.Exists(PendingDir)) return;
            DateTime cutoff = DateTime.Now.AddDays(-PendingMaxAgeDays);
            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// کاربر ارسالِ آمار را خاموش کرد ⇒ آنچه پیش‌تر در صف جمع شده باید برود.
    ///
    /// نوشتنِ محلیِ گزارشِ آینده عمداً ادامه می‌یابد (بی‌ضرر است و اگر بعداً
    /// رضایت داد، ارزشمندترین گزارش همان نخستین اجرای پس از کرش است)؛ ولی
    /// آنچه بدونِ رضایت مانده، پس از خاموش کردنِ صریحِ همان گزینه بی‌معناست.
    /// </summary>
    public static void DiscardPending()
    {
        try
        {
            if (!Directory.Exists(PendingDir)) return;
            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
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
            using var content = new StringContent(JsonSerializer.Serialize(Sanitized(p), JsonOptions), Encoding.UTF8, "application/json");
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

    /// <summary>
    /// ارسال همهٔ گزارش‌های در صف؛ با اولین شکست (قطعی شبکه) متوقف می‌شود.
    ///
    /// این مهم‌ترین نقطهٔ کنترلِ رضایت است: هم در شروعِ برنامه و هم بعد از هر
    /// اتصال صدا زده می‌شود، پس اگر اینجا گیت نداشته باشد، کلِ گزارشِ کرش بدونِ
    /// توجه به چک‌باکسِ تنظیمات بیرون می‌رود — دقیقاً همان چیزی که به کاربر
    /// گفته بودیم جزوِ آمار و اطلاعات است. بدونِ رضایت، فقط صف را می‌شماریم.
    /// </summary>
    public static async Task<int> FlushPendingAsync(int? proxyPort)
    {
        // قبل از گیتِ رضایت: پاک‌سازیِ کهنگی فرستادن نیست، بهداشتِ محلی است و
        // باید در همان حالتِ بی‌رضایت هم انجام شود که صف رشد می‌کند.
        SweepExpired();

        if (!ConsentAllowsSend()) return 0;

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

    private static readonly Regex UrlRe = new(
        @"\bhttps?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UuidRe = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex Ipv4Re = new(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>
    /// حذفِ چیزهایی که گزارشِ کرش نباید ببرد: لینکِ اشتراک (که توکن در مسیرش است)،
    /// UUID (شناسهٔ سرور) و آدرسِ IP.
    ///
    /// رویِ Log و Error اعمال می‌شود، نه رویِ Message: متنِ دستیِ کارب را تغییر
    /// نمی‌دهیم، چون کسی که خودش چیزی نوشته ممکن است برایِ خطایابی دقیقاً همان
    /// را بخواهد بفرستد — و SECURITY.md هم صریحاً از او خواسته چنین چیزی ننویسد.
    /// برایِ گزارشِ کرش هم Message خالی است، پس این دو بخش همان کلِ گزارش‌اند.
    ///
    /// نکته‌ها:
    ///  - لینک فقط وقتی پاک می‌شود که مسیرِ غیرِ خالی یا query داشته باشد؛
    ///    یک لینکِ خانگی مثلِ https://example.com که در لاگ برایِ خطایابیِ
    ///    آپدیت ارزشمند است می‌ماند، ولی هر مسیری که بتواند توکنِ اشتراک
    ///    باشد می‌رود.
    ///  - 127.x.x.x و 0.0.0.0 حذف نمی‌شوند: لوپ‌بک آدرسِ کسی نیست و بدونِ آن
    ///    خطای پروکسیِ محلیِ رایج‌ترین چیزی است که در گزارش می‌آید.
    ///  - IPv6 عمداً حذف نمی‌شود. یک الگویِ قابلِ اعتماد برایِ آن نمی‌شود
    ///    نوشت که متنِ کوتاهِ سه‌بخشیِ زمان (07:11:53) را هم نگیرد، و حذفِ
    ///    اشتباهیِ زمان‌ها از یک گزارشِ خطایابی هزینه‌ای واقعی است. اگر راهی
    ///    قطعی پیدا شد باید اضافه شود.
    /// </summary>
    public static string Scrub(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = UrlRe.Replace(s, static m => IsSecretUrl(m.Value) ? "<url>" : m.Value);
        s = UuidRe.Replace(s, "<uuid>");
        s = Ipv4Re.Replace(s, static m => IsPublicIpv4(m.Value) ? "<ip>" : m.Value);
        return s;
    }

    /// <summary>لینکِ دارایِ مسیر یا query ⇒ می‌تواند توکنِ اشتراک باشد.</summary>
    private static bool IsSecretUrl(string raw)
    {
        string t = raw.TrimEnd('.', ',', ')', ']', ';', '>', '"');
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        string path = u.AbsolutePath;
        return !string.IsNullOrEmpty(u.Query) || (path.Length > 1 && path != "/");
    }

    /// <summary>IPv4ِ معنادار و غیرِ لوپ‌بک ⇒ آدرسی است که باید برود.</summary>
    private static bool IsPublicIpv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        int first = -1;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out int v) || v > 255) return false;
            if (first < 0) first = v;
        }
        return first is not (0 or 127);
    }

    /// <summary>
    /// نسخهٔ پاک‌شدهٔ گزارش پیش از سریال‌سازی. خودِ Payload را تغییر نمی‌دهیم
    /// تا فراخواننده همان چیزی را ببیند که داده بود؛ فقط آنچه رویِ دیسک یا
    /// شبکه می‌رود پاک می‌شود.
    /// </summary>
    private static Payload Sanitized(Payload p)
    {
        if (p.Error.Length == 0 && p.Log.Length == 0) return p;
        return new Payload
        {
            Type = p.Type,
            Ver = p.Ver,
            Os = p.Os,
            Id = p.Id,
            Message = p.Message,
            Error = Scrub(p.Error),
            Log = Scrub(p.Log),
        };
    }
}
