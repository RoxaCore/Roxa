using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Roxa.Localization;

namespace Roxa.Core;

public static class TunManager
{
    private const string TunAdapterName = "roxa0";
    private const int CaptureTimeoutMs = 15000;
    private const int CaptureRetryDelayMs = 300;
    private const int ResolveTimeoutMs = 2500;
    // هر چند ثانیه گیت‌ویِ مسیرهای کپچر را چک کنیم و چند نوبتِ پایداری برای پذیرشِ
    // گیت‌ویِ جدید لازم است (کارتِ در حال قطع‌شدن چند ثانیه هنوز مسیرِ کم‌metric دارد).
    private const int GatewayWatchEvery = 5;
    private const int GatewayStableTicks = 2;

    private static readonly object _routesLock = new();
    private static readonly List<TunRouteRecord> _routes = new();

    private static string? _captureVia;
    private static int _captureGen;
    private static Timer? _gatewayTimer;
    private static string? _watchCandidate;
    private static int _watchStreak;
    private static int _watchScanning;

    private static readonly (string Prefix, string Dest, string Mask)[] _capturePrefixes =
    [
        ("0.0.0.0/1", "0.0.0.0", "128.0.0.0"),
        ("128.0.0.0/1", "128.0.0.0", "128.0.0.0"),
    ];

    /// <summary>
    /// آدرسِ ثابتِ آداپتورِ TUN. Wintun بدونِ آدرسِ تعریف‌شده خودش یک APIPA
    /// (169.254.x.x) می‌گیرد و آن‌وقت ویندوز 169.254.255.255 را «بِرودکستِ همان
    /// زیرشبکهٔ خودِ آداپتور» حساب می‌کند و کلِ NBNS/LLMNR/mDNS را — بی‌آنکه سرِ
    /// جدولِ مسیر برود — مستقیم داخلِ تونل می‌ریزد. برایِ همین هیچ مسیرِ استثنایی
    /// فایده نداشت: این تصمیم از رویِ آدرسِ آداپتور گرفته می‌شود، نه از رویِ
    /// مسیر. در یک نوبت ۱۶۳۰۹ بستهٔ NBNS روی پورت ۱۳۷ در یک دقیقه ثبت شد و
    /// حافظهٔ xray تا ۹۴۱ مگابایت بالا رفت. با آدرسِ ثابت، آداپتور دیگر
    /// لینک‌لوکال نیست و آن بِرودکست‌ها اصلاً ساخته نمی‌شوند. v2rayN هم همین کار
    /// را می‌کند (آداپتورش singbox_tun آدرسِ 172.19.0.1/30 دارد). دامنهٔ
    /// 198.18.0.0/15 برایِ سنجشِ شبکه رزرو شده (RFC 2544) و رویِ شبکه‌های واقعی
    /// استفاده نمی‌شود؛ /30 هم یعنی آداپتور فقط یک همسایه دارد.
    /// </summary>
    private const string TunAddress = "198.18.0.1";
    private const string TunAddressMask = "255.255.255.252";
    private static int _tunAddressSet;

    /// <summary>
    /// آدرسِ ثابت را رویِ آداپتورِ TUN می‌گذارد. فقط یک‌بار در هر نوبتِ اتصال
    /// اجرا می‌شود؛ اگر آداپتور تازه بالا آمده باشد netsh ممکن است یکی دو بار
    /// جواب ندهد، پس کوتاه تکرار می‌کنیم.
    /// </summary>
    private static bool EnsureTunAddress()
    {
        if (Interlocked.Exchange(ref _tunAddressSet, 1) == 1) return true;
        string address = PickTunAddress();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int code = RunNetsh($"interface ipv4 set address name=\"{TunAdapterName}\" static {address} {TunAddressMask}");
            if (code == 0)
            {
                Log($"TUN address set to {address}/30 on {TunAdapterName}.");
                return true;
            }
            Thread.Sleep(150);
        }
        Log($"Could not set TUN address on {TunAdapterName}; it may fall back to APIPA (169.254.x.x) and attract broadcasts.");
        return false;
    }

    /// <summary>
    /// آدرسِ پیش‌فرض را برمی‌گرداند، مگر اینکه آداپتورِ دیگری همان‌جا را
    /// اشغال کرده باشد؛ آن‌وقت اولین /30ِ خالیِ داخلِ همان /15 را برمی‌گرداند.
    /// دلیل: 198.18.0.1 دقیقاً آدرسِ پیش‌فرضِ TUN در clash/mihomo است
    /// (fake-ip-range پیش‌فرضِشان 198.18.0.0/16 است و آدرسِ TUN هم از همینجا
    /// گرفته می‌شود). اگر کاربر clash را روشن داشته باشد دو آداپتور یک IP
    /// می‌گیرند و مسیریابی به‌هم می‌ریزد. هر آدرسی که همین‌جا انتخاب شود
    /// داخلِ 198.18.0.0/15 می‌ماند، پس همان معنایِ PrivateIpCidrs و همان
    /// جلوگیری از broadcast لینک‌لوکال دست‌نخورده می‌ماند. اگر چیزی گرفته
    /// نشده یا جدولِ مسیر در دسترس نبود، دقیقاً همان رفتارِ امروزی برمی‌گردد.
    /// </summary>
    private static string PickTunAddress()
    {
        try
        {
            var table = IpRoute.GetTable();
            if (table == null || table.Count == 0) return TunAddress;

            uint tunIf = IpRoute.GetIfIndex(TunAdapterName);
            // ParseIp و مقادیرِ MIB هر دو در نمایِ بایتِ جابه‌جا‌شده‌اند (همان
            // قراردادی که MatchesRecord با آن کار می‌کند)، پس مقایسهٔ عددي
            // با < و > بی‌معناست: مثلاً 192.168.1.0 در این نما از 198.18.0.0
            // «بزرگ‌تر» درمی‌آید و فیلترِ دامنه خطا می‌کرد. عضویت در دامنه را
            // باید با ماسک سنجید. ماسکِ /15 در بایت 255.254.0.0 است، نه
            // 255.255.0.0، چون دامنه 198.18 و 198.19 را می‌پوشاند.
            uint base15 = IpRoute.ParseIp("198.18.0.0");
            uint mask15 = IpRoute.ParseIp("255.254.0.0");
            uint m30 = IpRoute.ParseIp("255.255.255.252");

            // مسیرهایی که داخلِ دامنهٔ ما نشسته‌اند و مالِ خودِ TUN نیستند.
            var taken = new List<(uint Net, uint Mask)>();
            foreach (var row in table)
            {
                if (tunIf != 0 && row.IfIndex == tunIf) continue;
                uint net = row.Dest & row.Mask;
                if ((net & mask15) != base15) continue;
                taken.Add((net, row.Mask));
            }
            if (taken.Count == 0) return TunAddress;

            // کلِ /15 یعنی 198.18 و 198.19؛ هر چهار آدرس یک /30 است.
            for (uint second = 18; second <= 19; second++)
            {
                for (uint third = 0; third < 256; third += 4)
                {
                    uint cand = IpRoute.ParseIp($"198.{second}.{third}.0");
                    bool busy = false;
                    foreach (var (net, mask) in taken)
                    {
                        // دو زیرشبکه وقتی هم‌پوشانی دارند که یکی داخلِ دیگری باشد.
                        if ((net & m30) == cand || (cand & mask) == net) { busy = true; break; }
                    }
                    if (busy) continue;

                    string addr = IpRoute.IpToString(cand | 1u);
                    if (addr == TunAddress) return TunAddress;
                    Log($"TUN address {TunAddress} is already in use on this machine; using {addr} instead.");
                    return addr;
                }
            }
        }
        catch { }
        // هیچ جای خالی پیدا نشد (یا هر خطایی رخ داد): همان آدرسِ پیش‌فرض.
        return TunAddress;
    }

    private static readonly string _wintunDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine");
    private static readonly string _wintunPath = Path.Combine(_wintunDirectory, "wintun.dll");
    private static readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    // وینتین از نسخهٔ ۰٫۱۴٫۱ فقط یک بستهٔ واحد منتشر می‌کند که شاملِ DLL هر چهار
    // معماری (amd64/x86/arm64/arm) است. آدرس‌هایِ قدیمیِ جداگانه مثلِ
    // wintun-amd64-0.14.1.zip از سایت حذف شده‌اند و هر دو ۴۰۴ می‌دهند؛ کدِ قبلی همان
    // آدرسِ مرده را می‌گرفت و هشِ یک فایلِ واحد را رویِ بستهٔ معماریِ خاص می‌سنجید،
    // پس همیشه شکست می‌خورد و حالتِ TUN بی‌صدا به پروکسی برمی‌گشت.
    private const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";

    // هشِ منتشرشده در سایتِ رسمی برایِ همان یک فایل؛ برایِ هر دو معماری درست است.
    private const string WintunSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

    public static string WintunDirectory => _wintunDirectory;
    public static string WintunPath => _wintunPath;
    public static string AdapterName => TunAdapterName;

    public static bool IsWintunAvailable()
    {
        bool exists = File.Exists(WintunPath);
        if (!exists)
            Log($"wintun.dll not found at: {WintunPath}");
        return exists;
    }

    public static async Task<bool> EnsureWintunDownloadedAsync(Action<string>? log = null)
    {
        if (IsWintunAvailable()) return true;

        Log("Downloading wintun driver...");
        log?.Invoke(TranslationManager.Get("Tun_DownloadingDriver"));

        try
        {
            Directory.CreateDirectory(WintunDirectory);
            string zipPath = Path.Combine(WintunDirectory, "wintun.zip");

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(WintunUrl);

            string actual = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(data)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, WintunSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log($"wintun hash mismatch: expected {WintunSha256}, got {actual}");
                log?.Invoke(TranslationManager.Get("Tun_HashBad"));
                return false;
            }

            await File.WriteAllBytesAsync(zipPath, data);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, WintunDirectory, true);
            try { File.Delete(zipPath); } catch { }

            // بستهٔ واحد شاملِ هر چهار معماری است؛ باید معماریِ همین سیستم را انتخاب کنیم.
            if (!File.Exists(_wintunPath) && !PickWintunDllForThisArch())
            {
                Log($"wintun archive contained no DLL for {RuntimeInformation.OSArchitecture}.");
                return false;
            }

            CleanupExtractedWintunTree();
            Log("wintun driver downloaded successfully.");
            log?.Invoke(TranslationManager.Get("Tun_DriverDownloaded"));
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to download wintun: {ex.Message}");
            log?.Invoke(string.Format(TranslationManager.Get("Tun_DownloadError"), ex.Message));
            return false;
        }
    }

    /// <summary>
    /// از بینِ همهٔ wintun.dll هایِ استخراج‌شده، نسخهٔ معماریِ همین سیستم را به
    /// engine\wintun.dll منتقل می‌کند. به ساختارِ دقیقِ بسته وابسته نیست: دنبالِ
    /// نامِ فایل و نامِ پوشهٔ معماری می‌گردد، پس هم برایِ بستهٔ امروز درست است
    /// و هم اگر ساختارِ داخلی عوض شد از کار نمی‌افتد.
    /// </summary>
    private static bool PickWintunDllForThisArch()
    {
        try
        {
            string fullTarget = Path.GetFullPath(_wintunPath);
            var dlls = Directory.GetFiles(WintunDirectory, "wintun.dll", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), fullTarget, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (dlls.Length == 0) return false;

            string want = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string sep = Path.DirectorySeparatorChar.ToString();
            string? chosen =
                dlls.FirstOrDefault(f => f.Contains($"{sep}{want}{sep}", StringComparison.OrdinalIgnoreCase))
                ?? dlls.FirstOrDefault(f => f.Contains(want, StringComparison.OrdinalIgnoreCase))
                ?? (dlls.Length == 1 ? dlls[0] : null);
            if (chosen == null) return false;

            File.Copy(chosen, _wintunPath, true);
            Log($"wintun.dll picked for {RuntimeInformation.OSArchitecture}: {Path.GetRelativePath(WintunDirectory, chosen)}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"wintun dll pick failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// فایلِ نهایی جا‌به‌جا شد؛ درختِ استخراج‌شده (bin\amd64\… و wintun.h) دیگر لازم
    /// نیست. فقط چیزی را حذف می‌کنیم که همین الان خودِ بسته ساخته است.
    /// </summary>
    private static void CleanupExtractedWintunTree()
    {
        try
        {
            string bin = Path.Combine(WintunDirectory, "bin");
            if (Directory.Exists(bin)) Directory.Delete(bin, true);
            foreach (var header in Directory.GetFiles(WintunDirectory, "wintun.h"))
                File.Delete(header);
        }
        catch { /* بهترین تلاش؛ فایلِ زائد فقط شلوغ است و آسیبی نمی‌زند */ }
    }

    public static async Task<bool> AddCaptureRoutesAsync(IEnumerable<string>? hosts, Action<string>? log = null)
    {
        long t0 = Environment.TickCount64;
        try
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                log?.Invoke(TranslationManager.Get("Tun_NeedAdmin"));
                return false;
            }

            // اجرای route print در پس‌زمینه تا رشتهٔ UI هنگام اتصال TUN قفل نشود.
            string? via = await Task.Run(GetDefaultGatewayVia);
            long tGateway = Environment.TickCount64;
            if (via == null)
            {
                Log("No default gateway found; capture routes skipped.");
                return false;
            }

            // همه میزبان‌های داده‌شده را resolve می‌کنیم (قبلاً فقط اولی در نظر گرفته
            // می‌شد و بقیهٔ آدرس‌های سرور زیر TUN گم می‌شدند).
            var hostList = hosts?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            var serverIps = await ResolveServerIpsAsync(hostList == null || hostList.Count == 0 ? null : hostList);
            long tDns = Environment.TickCount64;

            // هر AddRoute دو بار route.exe اجرا می‌کند (delete+add)؛ با ده‌ها سرور
            // ده‌ها پروسه می‌شود. LongRunning تا رشته‌های Thread-Pool در چند ثانیهٔ
            // اجرای همزمان route.exe بلوکه نشوند و پیوست‌های UI به تعویق نیفتند.
            await Task.Factory.StartNew(() =>
            {
                foreach (var ip in serverIps)
                {
                    AddRoute($"add {ip} mask 255.255.255.255 {via}");
                    string? net24 = Subnet24(ip);
                    if (net24 != null) AddRoute($"add {net24} mask 255.255.255.0 {via}");
                }
                AddRoute($"add 10.0.0.0 mask 255.0.0.0 {via}");
                AddRoute($"add 172.16.0.0 mask 255.240.0.0 {via}");
                AddRoute($"add 192.168.0.0 mask 255.255.0.0 {via}");
                // لینک‌لوکال. آداپتورِ TUN آدرسِ 169.254.x.x دارد، پس ویندوز بِرودکستِ
                // همان زیرشبکه (169.254.255.255 روی پورت‌های 138/1947) را به تونل
                // می‌فرستاد؛ در یک نوبت ۲۳۲۱ بسته در ۸ ثانیه ثبت شد و xray به‌ازایِ
                // هر کدام یک خط لاگ نوشت. TunExclusion.IsPrivate همین دامنه را
                // محلی حساب می‌کند، ولی اینجا جا افتاده بود.
                AddRoute($"add 169.254.0.0 mask 255.255.0.0 {via}");
                // بِرودکستِ محدود و مالتی‌کست. مسیرِ تونل (0.0.0.0/1 + 128.0.0.0/1) کلِ
                // فضای IPv4 را می‌پوشاند، پس این دو هم داخلِ تونل می‌افتادند. برایِ
                // همین اضافه کردنِ 169.254.0.0/16 کافی نبود: نیمی از طوفانِ ۳۳۲۷
                // بسته‌ایِ پورت ۱۹۴۷ مقصدش 255.255.255.255 بود. فرستنده را پیدا
                // کردم — hasplms.exe، سرویسِ لایسنسِ HASP/SafeNet است که برایِ پیدا
                // کردنِ دانگِلِ USB هر چند ثانیه broadcast می‌زند و این کار را حتی
                // وقتی دانگِلی وصل نیست هم ادامه می‌دهد. هر بسته در xray یک «اتصالِ
                // UDP» می‌ساخت؛ حافظه‌اش تا ۹۱۱ مگابایت بالا رفت، CPU سوخت و بازهٔ
                // پورت‌های یک‌بارمصرفِ ویندوز هم تمام شد (رویدادِ Tcpip 4266).
                // مسیرِ 224.0.0.0/4 هم برایِ همین دلیل اضافه می‌شود؛ v2rayN هم رویِ
                // آداپتورِ خودش همین دو را دارد.
                AddRoute($"add 255.255.255.255 mask 255.255.255.255 {via}");
                AddRoute($"add 224.0.0.0 mask 240.0.0.0 {via}");
            }, TaskCreationOptions.LongRunning);
            long tExcl = Environment.TickCount64;

            await Task.Run(() =>
            {
                RunRoute("delete 0.0.0.0 mask 128.0.0.0");
                RunRoute("delete 128.0.0.0 mask 128.0.0.0");
            });

            if (!await WaitCaptureRoutesAsync())
            {
                Log($"TUN adapter {TunAdapterName} did not appear in time; capture routes skipped.");
                await Task.Factory.StartNew(RemoveCaptureRoutes, TaskCreationOptions.LongRunning);
                return false;
            }
            long tCapture = Environment.TickCount64;

            Log($"Route setup: via={via} gateway={tGateway - t0} resolve={tDns - tGateway} excl={tExcl - tDns} capture={tCapture - tExcl} total={tCapture - t0}ms excluded={serverIps.Count}");
            Log($"Capture routes added: {serverIps.Count} server ip(s) excluded.");
            StartGatewayWatch(via);
            return true;
        }
        catch (Exception ex)
        {
            Log($"AddCaptureRoutes failed: {ex.Message}");
            await Task.Factory.StartNew(RemoveCaptureRoutes, TaskCreationOptions.LongRunning);
            return false;
        }
    }

    public static bool RemoveCaptureRoutes()
    {
        StopGatewayWatch();
        _captureVia = null;
        // آداپتور با قطعِ اتصال از بین می‌رود؛ پرچم را هم ریست می‌کنیم تا در
        // اتصالِ بعدی دوباره آدرسِ ثابت گذاشته شود.
        Interlocked.Exchange(ref _tunAddressSet, 0);
        List<TunRouteRecord> snapshot;
        lock (_routesLock)
        {
            if (_routes.Count == 0) return false;
            snapshot = _routes.ToList();
            _routes.Clear();
        }
        uint adapterIfIndex = IpRoute.GetIfIndex(TunAdapterName);
        int nativeDeleted = IpRoute.DeleteRoutes(snapshot, adapterIfIndex);
        var table = IpRoute.GetTable();
        if (table == null)
        {
            if (nativeDeleted < 0)
            {
                foreach (var rec in snapshot)
                {
                    try { RunRoute(DeleteArgsFor(rec)); }
                    catch { }
                }
            }
        }
        else
        {
            foreach (var rec in snapshot)
            {
                if (!table.Any(row => IpRoute.MatchesRecord(rec, row, adapterIfIndex))) continue;
                try { RunRoute(DeleteArgsFor(rec)); }
                catch { }
            }
        }
        Log($"Removed {snapshot.Count} tun route(s) (native {nativeDeleted}).");
        return true;
    }

    private static string DeleteArgsFor(TunRouteRecord rec) =>
        rec.Via != null ? $"delete {rec.Dest} mask {rec.Mask} {rec.Via}" : $"delete {rec.Dest} mask {rec.Mask}";

    public static List<TunRouteRecord> SnapshotRoutes()
    {
        lock (_routesLock)
            return _routes.Select(r => new TunRouteRecord { Dest = r.Dest, Mask = r.Mask, Via = r.Via }).ToList();
    }

    private static async Task<bool> WaitCaptureRoutesAsync()
    {
        long deadline = Environment.TickCount64 + CaptureTimeoutMs;
        while (true)
        {
            // تا آداپتر roxa0 بالا نیامده netsh بیهوده پروسه می‌سازد (هر نوبت ۲ پروسه
            // هر ۳۰۰ms) و رشتهٔ UI را هم پشت همین انتظار نگه می‌داشت؛ اول با
            // GetIfIndex بومی چک می‌شود و فقط بعد از ظاهرشدن آداپتر route اضافه می‌شود.
            if (IpRoute.GetIfIndex(TunAdapterName) == 0)
            {
                if (Environment.TickCount64 >= deadline) return false;
                await Task.Delay(CaptureRetryDelayMs);
                continue;
            }
            // آداپتر بالا آمد. قبل از افزودنِ مسیرها آدرسِ ثابت را می‌گذاریم تا
            // لینک‌لوکال نشود؛ این کار فقط یک‌بار در هر نوبتِ اتصال انجام می‌شود.
            await Task.Run(EnsureTunAddress);
            bool allAdded = true;
            foreach (var (prefix, dest, mask) in _capturePrefixes)
            {
                // netsh هم پروسهٔ جدا می‌خواهد؛ در پس‌زمینه اجرا می‌شود.
                bool ok = await Task.Run(() => RunNetsh($"interface ipv4 add route prefix={prefix} interface={TunAdapterName}") == 0);
                if (ok)
                {
                    lock (_routesLock)
                    {
                        if (!_routes.Any(r => r.Dest == dest && r.Mask == mask && r.Via == null))
                            _routes.Add(new TunRouteRecord { Dest = dest, Mask = mask });
                    }
                }
                else allAdded = false;
            }
            if (allAdded) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(CaptureRetryDelayMs);
        }
    }

    /// <summary>آخرین گیت‌ویِ در لاگ آمده؛ تا پایشِ هر ۵ ثانیه یک خطِ تکراری ننویسد.</summary>
    private static string? _lastLoggedGateway;

    public static string? GetDefaultGatewayVia()
    {
        try
        {
            // اولویت با گیت‌ویِ کارت شبکهٔ فیزیکی: وقتی TUN دیگری (مثل v2rayN) فعال است،
            // مسیرِ پیش‌فرضِ کمترین metric مالِ اوست و قبلاً excl/test ما واردِ TUNِ بیگانه
            // می‌شد؛ باید مستقیم به گیت‌وی واقعی برود.
            string? physical = FindPhysicalDefaultGateway();
            if (physical != null)
            {
                LogGatewayOnce(physical, "physical");
                return physical;
            }

            int code = RunTool("route.exe", "print -4 0.0.0.0", out string output);
            if (code != 0) return null;
            string? via = null;
            int bestMetric = int.MaxValue;
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || parts[0] != "0.0.0.0" || parts[1] != "0.0.0.0") continue;
                if (!int.TryParse(parts[^1], out int metric) || metric >= bestMetric) continue;
                if (IPAddress.TryParse(parts[2], out _))
                {
                    bestMetric = metric;
                    via = parts[2];
                }
                else if (IPAddress.TryParse(parts[3], out _))
                {
                    bestMetric = metric;
                    via = parts[3];
                }
            }
            if (via == null) return null;
            LogGatewayOnce(via, $"metric={bestMetric}");
            return via;
        }
        catch (Exception ex)
        {
            Log($"GetDefaultGatewayVia failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// لاگِ گیت‌وی فقط وقتی مقدار واقعاً عوض شده. پایشِ هر چند ثانیه همان یک
    /// خطِ «Default gateway via=… (physical)» را بی‌قید و شرط می‌نوشت — تا ۱۸ بار
    /// در دقیقه، برایِ ساعت‌ها، در حالی که هیچ چیزی عوض نشده بود.
    /// </summary>
    private static void LogGatewayOnce(string via, string detail)
    {
        lock (_routesLock)
        {
            if (string.Equals(_lastLoggedGateway, via, StringComparison.Ordinal)) return;
            _lastLoggedGateway = via;
        }
        Log($"Default gateway via={via} ({detail})");
    }

    /// <summary>
    /// گیت‌ویِ پیش‌فرضی که واقعاً به کارت شبکهٔ فیزیکی تعلق دارد: مسیر 0.0.0.0/0 با
    /// NextHop غیرصفر، غیر از آداپترهای TUN (نام/شرح)، و دقیقاً همان گیت‌ویِ ثبت‌شده روی آداپتر.
    /// </summary>
    private static string? FindPhysicalDefaultGateway()
    {
        var table = IpRoute.GetTable();
        if (table == null) return null;
        var defaults = table
            .Where(r => r.Dest == 0 && r.Mask == 0 && r.NextHop != 0)
            .OrderBy(r => r.Metric1)
            .ToList();
        if (defaults.Count == 0) return null;

        var nics = new Dictionary<uint, NetworkInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
            catch { }
        }

        foreach (var row in defaults)
        {
            if (!nics.TryGetValue(row.IfIndex, out var nic)) continue;
            if (IsTunLike(nic.Name) || IsTunLike(nic.Description)) continue;
            string hop = IpRoute.IpToString(row.NextHop);
            bool ownsGateway;
            try
            {
                ownsGateway = nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    string.Equals(g.Address.ToString(), hop, StringComparison.Ordinal));
            }
            catch { continue; }
            if (ownsGateway) return hop;
        }
        return null;
    }

    /// <summary>
    /// آیا هنوز یک مسیر 0.0.0.0/0 با این next-hop روی کارتِ شبکهٔ غیر-TUN وجود دارد؟
    /// برای تشخیصِ اینکه گیت‌ویِ فعلیِ مسیرهای سنجاق‌شدهٔ «معافیت TUN» هنوز زنده است یا
    /// آداپتر عوض شده و آن مسیرها به سیاه‌چاله تبدیل شده‌اند. ویندوز مسیرهای /32 را
    /// نگه می‌دارد، ولی next-hopِ مرده را حذف نمی‌کند.
    /// در شکِ تشخیص، گیت‌وی زنده فرض می‌شود تا بی‌جهت مسیرها پاک نشوند.
    /// </summary>
    public static bool IsPhysicalGatewayPresent(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return false;
        try
        {
            var table = IpRoute.GetTable();
            if (table == null) return true;
            var nics = new Dictionary<uint, NetworkInterface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
                catch { }
            }
            foreach (var row in table)
            {
                if (row.Dest != 0 || row.Mask != 0 || row.NextHop == 0) continue;
                if (!nics.TryGetValue(row.IfIndex, out var nic)) continue;
                if (IsTunLike(nic.Name) || IsTunLike(nic.Description)) continue;
                if (string.Equals(IpRoute.IpToString(row.NextHop), gateway, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// روشن کردنِ مشاهدهٔ گیت‌ویِ مسیرهای کپچر. این مسیرها (سرور VPN و شبکه‌های داخلی)
    /// به گیت‌ویِ فیزیکی وابسته‌اند؛ اگر آداپتر یا اینترنت عوض شود، next-hop مرده می‌ماند
    /// و سرور VPN از دسترس خارج می‌شود. قبلاً فقط هنگامِ راه‌اندازی بازسازی می‌شدند.
    /// </summary>
    private static void StartGatewayWatch(string via)
    {
        lock (_routesLock)
        {
            _captureVia = via;
            _captureGen++;
            _watchCandidate = null;
            _watchStreak = 0;
            if (_gatewayTimer != null) return;
            _gatewayTimer = new Timer(_ => WatchGateway(), null,
                GatewayWatchEvery * 1000, GatewayWatchEvery * 1000);
        }
        Log($"capture gateway watcher armed (via={via}).");
    }

    private static void StopGatewayWatch()
    {
        Timer? t;
        lock (_routesLock)
        {
            // شمارهٔ نسل بالا می‌رود تا اگر بازاعمالِ مسیرها در میانهٔ کار باشد،
            // مسیرهای تازه‌اش را خودش حذف کند و بعد از قطعِ اتصال جا نماند.
            _captureGen++;
            t = _gatewayTimer;
            _gatewayTimer = null;
            _watchCandidate = null;
            _watchStreak = 0;
            // در اتصالِ بعدی، گیتِوی دوباره در لاگ بیاید.
            _lastLoggedGateway = null;
        }
        t?.Dispose();
    }

    private static void WatchGateway()
    {
        if (Interlocked.Exchange(ref _watchScanning, 1) == 1) return;
        try
        {
            string? via;
            bool active;
            int gen;
            lock (_routesLock) { via = _captureVia; active = _routes.Count > 0; gen = _captureGen; }
            if (!active || via == null)
            {
                StopGatewayWatch();
                return;
            }

            string? gw = GetDefaultGatewayVia();
            if (gw == null) return;   // تعویض آداپتر؛ تا گیت‌ویِ معتبر داریم دست نزن.

            if (gw == via)
            {
                // GetDefaultGatewayVia گیت‌وی را از روی خودِ کارتِ فیزیکی خوانده، پس
                // حضورش ثابت شده؛ فراخوانیِ IsPhysicalGatewayPresent اینجا فقط یک
                // پیمایشِ تکراریِ کارت‌های شبکه است و عمداً حذف شد.
                lock (_routesLock) { _watchCandidate = null; _watchStreak = 0; }
                return;
            }

            // فقط وقتی گیت‌وی عوض شده لازم است بفهمیم قدیمی اصلاً رفته یا نه؛
            // «رفته» یعنی بی‌درنگ بازسازی کن، وگرنه دو نوبت پایداری صبر کن.
            bool dead = !IsPhysicalGatewayPresent(via);

            if (!dead)
            {
                // گیت‌ویِ پیشنهادی باید دو نوبت پشت‌سرهم پایدار باشد؛ کارتِ در حال
                // قطع‌شدن چند ثانیه هنوز مسیرِ پیش‌فرضِ کم‌metric دارد.
                lock (_routesLock)
                {
                    if (!string.Equals(_watchCandidate, gw, StringComparison.Ordinal))
                    {
                        _watchCandidate = gw;
                        _watchStreak = 1;
                        return;
                    }
                    if (++_watchStreak < GatewayStableTicks) return;
                }
            }

            Log($"capture gateway {via} -> {gw} ({(dead ? "old gateway is gone" : "changed")}); re-applying routes");
            ReapplyCaptureRoutes(gw, gen);
        }
        catch (Exception ex)
        {
            Log($"capture gateway watch failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _watchScanning, 0);
        }
    }

    /// <summary>
    /// ساختنِ دوبارهٔ همهٔ مسیرهای کپچر با گیت‌ویِ تازه. حذفِ مسیرِ کهنه باید
    /// <b>قبل</b> از افزودنِ جدید انجام شود؛ CreateIpForwardEntry خطای «از قبل موجود»
    /// را موفقیت حساب می‌کند، وگرنه next-hopِ مرده سرِ جایش می‌ماند.
    /// </summary>
    private static void ReapplyCaptureRoutes(string newVia, int gen)
    {
        List<TunRouteRecord> old;
        lock (_routesLock)
        {
            if (gen != _captureGen) return;   // اتصال در این فاصله قطع شده
            old = _routes.ToList();
        }
        if (old.Count == 0) return;

        var fresh = new List<TunRouteRecord>();
        foreach (var rec in old)
        {
            // حذف و افزودن برایِ همهٔ مسیرها، حتی وقتی گیت‌ویِ تازه با قدیمی یکی است؛
            // سیستم ممکن است خودش مسیر را در میانهٔ تعویضِ آداپتر پاک کرده باشد و
            // «ادامه بدون افزودن» آن را برایِ همیشه گم کند.
            try { RunRoute($"delete {rec.Dest} mask {rec.Mask} {rec.Via}"); }
            catch { /* ممکن است از قبل رفته باشد */ }
            try
            {
                if (!IpRoute.TryAddRoute(rec.Dest, rec.Mask, newVia, IpRoute.GetIfIndexForIp(newVia)))
                {
                    int code = RunRoute($"add {rec.Dest} mask {rec.Mask} {newVia}");
                    if (code != 0) throw new InvalidOperationException($"route add exited with {code}");
                }
                fresh.Add(new TunRouteRecord { Dest = rec.Dest, Mask = rec.Mask, Via = newVia });
            }
            catch (Exception ex) { Log($"capture re-route failed for {rec.Dest}: {ex.Message}"); }
        }

        bool raced;
        lock (_routesLock)
        {
            raced = gen != _captureGen;
            if (!raced)
            {
                _routes.Clear();
                _routes.AddRange(fresh);
                _captureVia = newVia;
                _watchCandidate = null;
                _watchStreak = 0;
            }
        }
        if (raced)
        {
            // قطعِ اتصال همزمان انجام شد؛ مسیرهای تازهٔ خودمان را هم پاک کن تا بعد از
            // خروج، مسیرِ گیت‌ویِ مرده روی سیستم جا نماند. (بیرون از قفل؛ route.exe کند است)
            foreach (var r in fresh)
                try { RunRoute($"delete {r.Dest} mask {r.Mask} {r.Via}"); }
                catch { }
            return;
        }
        Log($"capture routes moved to gateway {newVia} ({fresh.Count}/{old.Count} route(s)).");
    }

    private static bool IsTunLike(string s) =>
        s.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("roxa", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("sing", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("mihomo", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("clash", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("neko", StringComparison.OrdinalIgnoreCase);

    /// <summary>آیا الان مسیر پیش‌فرضِ متعلق به یک TUN (بیگانه یا خودمان) هست؟
    /// برای تصمیمِ «لازم است برای دریافت/تست دور بزنیم یا نه».</summary>
    public static bool AnyTunDefaultRoute()
    {
        try
        {
            var table = IpRoute.GetTable();
            if (table == null) return false;
            var nics = new Dictionary<uint, NetworkInterface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
                catch { }
            }
            foreach (var row in table)
            {
                if (row.Dest != 0 || row.Mask != 0) continue;
                if (nics.TryGetValue(row.IfIndex, out var nic) &&
                    (IsTunLike(nic.Name) || IsTunLike(nic.Description))) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static string? Subnet24(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return null;
        var b = addr.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
            (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
            (b[0] == 192 && b[1] == 168) ||
            (b[0] == 169 && b[1] == 254))
            return null;
        return $"{b[0]}.{b[1]}.{b[2]}.0";
    }

    private static async Task<List<string>> ResolveServerIpsAsync(IEnumerable<string>? hosts)
    {
        var ips = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(string Host, Task<string[]> Task)>();
        foreach (var host in hosts ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(host)) continue;
            if (IPAddress.TryParse(host, out var literal))
            {
                if (literal.AddressFamily == AddressFamily.InterNetwork && seen.Add(literal.ToString()))
                    ips.Add(literal.ToString());
                continue;
            }
            pending.Add((host, ResolveIPv4Async(host)));
        }
        if (pending.Count > 0)
        {
            var results = await Task.WhenAll(pending.Select(p => p.Task));
            foreach (var result in results)
                foreach (var addr in result)
                    if (seen.Add(addr))
                        ips.Add(addr);
        }
        return ips;
    }

    private static async Task<string[]> ResolveIPv4Async(string host)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromMilliseconds(ResolveTimeoutMs));
            return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToArray();
        }
        catch (Exception ex)
        {
            Log($"Resolve failed for {host}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private static void AddRoute(string addArgs)
    {
        var parts = addArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string dest = parts[1];
        string mask = parts[3];
        string? via = parts.Length > 4 ? parts[4] : null;

        // ابتدا API بومی بدون هیچ پروسه‌ای؛ ۱۹۵ پروسهٔ route.exe در هر اتصال TUN ≈ ۱۵ ثانیه طول می‌کشید.
        // (فقط برای مسیرهای استثنا با گیت‌وی مشخص؛ بقیهٔ مسیرها از همان مسیر route.exe می‌روند)
        if (via != null && IpRoute.TryAddRoute(dest, mask, via, IpRoute.GetIfIndexForIp(via)))
        {
            lock (_routesLock) _routes.Add(new TunRouteRecord { Dest = dest, Mask = mask, Via = via });
            return;
        }

        string deleteArgs = $"delete {parts[1]} mask {parts[3]}";
        try { RunRoute(deleteArgs); }
        catch { }
        int code = RunRoute(addArgs);
        if (code != 0)
            throw new InvalidOperationException($"route {addArgs} exited with {code}");
        lock (_routesLock) _routes.Add(new TunRouteRecord { Dest = dest, Mask = mask, Via = via });
    }

    public static int RunRoute(string args) => RunTool("route.exe", args, out _);

    public static int RunRoute(string args, out string stdout) => RunTool("route.exe", args, out stdout);

    public static bool IsCaptureActive
    {
        get { lock (_routesLock) return _routes.Count > 0; }
    }

    public static HashSet<string> CaptureHostRoutes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_routesLock)
            foreach (var r in _routes)
                if (string.Equals(r.Mask, "255.255.255.255", StringComparison.Ordinal) && r.Dest.Length > 0)
                    set.Add(r.Dest);
        return set;
    }

    public static Task<List<string>> ResolveIpsAsync(IEnumerable<string>? hosts) => ResolveServerIpsAsync(hosts);

    public static int RunNetsh(string args) => RunTool("netsh.exe", args, out _);

    private static int RunTool(string fileName, string arguments, out string stdout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null)
        {
            stdout = string.Empty;
            return -1;
        }

        // هر دو استریم را موازی می‌خوانیم؛ خواندن متوالی/همزمانِ sync می‌تواند
        // وقتی بافر خطای پروسه پر شود کل فراخواننده را برای همیشه مسدود کند.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        bool exited = proc.WaitForExit(5000);
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            try { proc.WaitForExit(2000); } catch { }
        }
        stdout = Collect(outTask);
        Collect(errTask);
        return exited ? proc.ExitCode : -1;
    }

    private static string Collect(Task<string> task)
    {
        try
        {
            return task.Wait(3000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly object _logGate = new();
    private static readonly System.Text.StringBuilder _logBuf = new();
    private static System.Threading.Timer? _logTimer;
    private const int LogFlushThreshold = 16 * 1024;
    private const long LogMaxBytes = 2L * 1024 * 1024;

    /// <summary>
    /// لاگِ تونل را بافر می‌کند. قبلاً هر فراخوانی یک Directory.CreateDirectory و یک
    /// File.AppendAllTextِ کامل (باز/بستنِ فایل، هر بار اسکنِ آنتی‌ویروس) بود؛ حالا
    /// حداکثر یک بار در ثانیه و در یک نوشتنِ واحد انجام می‌شود. اگر بافر بزرگ شود
    /// همان لحظه می‌نویسیم تا در دوره‌های پُرلاگ چیزی گم نشود.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            bool flushNow;
            lock (_logGate)
            {
                _logBuf.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                       .Append("] ").Append(message).Append(Environment.NewLine);
                if (_logTimer == null)
                    _logTimer = new Timer(_ => FlushLog(), null, 1000, 1000);
                flushNow = _logBuf.Length >= LogFlushThreshold;
            }
            if (flushNow) FlushLog();
        }
        catch { }
    }

    /// <summary>
    /// نوشتنِ یک‌جاِ بافر و برشِ فایلِ لاگ. فراخوانی چندباره بی‌خطر است؛ روی مسیرِ
    /// خروج هم صدا زده می‌شود تا آخرین خطوط قبل از پایانِ پروسه روی دیسک باشند.
    /// </summary>
    public static void FlushLog()
    {
        try
        {
            string chunk;
            lock (_logGate)
            {
                if (_logBuf.Length == 0) return;
                chunk = _logBuf.ToString();
                _logBuf.Clear();
            }

            Directory.CreateDirectory(_logDir);
            string logFile = Path.Combine(_logDir, "tun.log");
            try
            {
                var fi = new FileInfo(logFile);
                if (fi.Exists && fi.Length + chunk.Length > LogMaxBytes)
                    File.Move(logFile, logFile + ".old", true);
            }
            catch { }
            File.AppendAllText(logFile, chunk);
        }
        catch { }
    }
}
