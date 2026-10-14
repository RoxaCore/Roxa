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

using System.Diagnostics;
using System.Net.Sockets;

namespace Roxa.Core;

public static class TunKillSwitch
{
    private const string RulePrefix = "RoxaKillSw_";
    private const int ProbeIntervalMs = 1000;
    private const int HttpProbeEvery = 5;
    // هر پروبِ HTTP ۸ ثانیه فرصت دارد؛ با آستانهٔ قبلی (۳ ثانیه) ترافیکِ کاملاً سالمِ ایران
    // هم «خوانده‌نشده» حساب می‌شد و کیل‌سوییچ کلِ اینترنت را چند ثانیه مسدود می‌کرد.
    private const int HttpProbeTimeoutMs = 8000;
    // سه شکستِ متوالیِ HTTP لازم است تا اصلاً «بد» حساب شود (≈۱۵ ثانیه).
    private const int MaxHttpFails = 3;
    private const int MaxBadTicks = 6;
    private const int GoodTicksToRecover = 5;
    private const int GoodTicksToResetBad = 3;
    private const int MaxExesPerApp = 30;

    private static readonly object _lock = new();
    private static readonly object _ruleLock = new();
    private static readonly List<string> _primaryPaths = new();
    private static readonly HashSet<string> _exePaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _corePaths = new();
    private static readonly ushort[] PermitPorts = { 53, 67, 68, 123, 546, 547 };
    private static readonly uint[][] Ranges4 =
    {
        new uint[] { 0x7F000000, 0x7FFFFFFF },
        new uint[] { 0x0A000000, 0x0AFFFFFF },
        new uint[] { 0xAC100000, 0xAC1FFFFF },
        new uint[] { 0xC0A80000, 0xC0A8FFFF },
        new uint[] { 0xA9FE0000, 0xA9FEFFFF },
        new uint[] { 0xE0000000, 0xEFFFFFFF },
        new uint[] { 0xF0000000, 0xFFFFFFFF },
    };
    private static readonly (byte[] Low, byte[] High)[] Ranges6 = BuildRanges6();
    private static IntPtr _engine;
    private static Timer? _timer;
    private static bool _enabled;
    private static bool _tunnelUp;
    private static int _probePort;
    private static int _probeTick;
    private static int _httpFails;
    private static int _badTicks;
    private static int _goodStreak;
    private static int _scanning;
    private static int _gen;
    private static int _appliedGen;
    private static int _appliedEngaged;

    // وقتی خودِ برنامه عمداً اتصال را قطع می‌کند (تعویضِ سرور، قطعِ دستی، وصلِ مجددِ
    // خودکارِ بعد از تغییرِ شبکه)، بلاکِ WFP درست است ولی اعلانِ «اینترنت مسدود شد»
    // چیزی جز اضطراب نیست — کاربر خودش دکمه زده و می‌داند چه خبر است.
    // فقط از دستِ رفتنِ واقعیِ تونل (تشخیصِ پروب) باید خبر بدهد.
    private static volatile bool _expectedDown;

    /// <summary>
    /// آیا آخرینِ تغییرِ وضعیتِ بلاک، نتیجهٔ قطعِ عمدیِ خودِ برنامه بوده؟
    /// UI بر اساسِ آن تصمیم می‌گیرد اعلان نشان بدهد یا نه.
    /// </summary>
    public static bool LastTransitionExpected => _expectedDown;

    public static event Action<bool, int>? StateChanged;

    private static string RulesFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "killsw-rules.txt");

    public static void Configure(bool enabled, IEnumerable<string>? paths, int probePort)
    {
        int gen;
        int n;
        bool timerOn;
        lock (_lock)
        {
            _enabled = enabled;
            if (probePort > 0) _probePort = probePort;
            RebuildListsLocked(enabled ? paths : null);
            gen = ++_gen;
            n = _primaryPaths.Count;
            if (enabled && _timer == null)
            {
                _badTicks = 0;
                _goodStreak = 0;
                _httpFails = 0;
                _probeTick = 0;
                _timer = new Timer(_ => Probe(), null, ProbeIntervalMs, ProbeIntervalMs);
                TunManager.Log($"kill-switch armed ({_primaryPaths.Count} exempt app(s), {_exePaths.Count} exe(s))");
            }
            else if (!enabled && _timer != null)
            {
                _timer.Dispose();
                _timer = null;
                TunManager.Log("kill-switch disarmed");
            }
            timerOn = _timer != null;
        }
        // عمداً فایروالِ ویندوز را دست نمی‌زنیم:
        // کیل‌سوییچِ Roxa کاملاً با WFP (لایهٔ کِرنل) کار می‌کند و به فایروالِ ویندوز نیازی ندارد.
        // قبلاً اینجا «netsh advfirewall set allprofiles state on» می‌زدیم؛ دستورِ روشن‌کردنِ فایروال
        // هیچ‌وقت بازگردانده نمی‌شد و سیستمِ کاربر را برای همیشه تغییر می‌داد — رویِ بعضی سیستم‌ها
        // (پروفایلِ خروجیِ مسدود، یا AVِ جایگزین) هم کلِ اینترنت را می‌انداخت.
        // قانون‌های TunExclusion فقط وقتی اثر دارند که فایروالِ خودِ کاربر روشن باشد؛ اگر خاموش است،
        // چیزی هم برای مسدودکردن وجود ندارد، پس الزامی‌کردنِ آن لازم نیست.
        TunManager.Log($"kill-switch configure enabled={enabled} paths={n} timer={timerOn} gen={gen}");
        Apply(gen);
    }

    public static void SetTunnelUp(bool up, int probePort)
    {
        int gen;
        int portNow;
        // بالا رفتنِ تونل یعنی از این به بعد هر قطعیِ تونل غیرمنتظره است؛
        // پایین آمدنش از همین‌جا یعنی خودِ برنامه عمداً قطع کرده (قطع/تعویض سرور/وصل مجدد).
        _expectedDown = !up;
        lock (_lock)
        {
            if (probePort > 0) _probePort = probePort;
            _tunnelUp = up;
            _httpFails = 0;
            _badTicks = 0;
            _goodStreak = 0;
            _probeTick = 0;
            gen = ++_gen;
            portNow = _probePort;
        }
        TunManager.Log($"kill-switch tunnelUp={up} port={portNow} gen={gen}");
        Apply(gen);
    }

    public static void CleanupLeftovers()
    {
        try
        {
            var names = ReadRulesFile();
            if (names.Count == 0) return;
            foreach (var name in names)
                RemoveFirewallRule(name);
            DeleteRulesFile();
            TunManager.Log($"kill-switch leftovers removed ({names.Count})");
        }
        catch (Exception ex)
        {
            TunManager.Log($"kill-switch cleanup failed: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        int gen;
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            gen = ++_gen;
        }
        ApplyRules(gen, false, 0);
        lock (_ruleLock) WfpNative.CloseSession(ref _engine);
    }

    public static void EmergencyRelease()
    {
        int gen;
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _enabled = false;
            gen = ++_gen;
        }
        TunManager.Log("kill-switch: EMERGENCY RELEASE requested");
        ApplyRules(gen, false, 0);
    }

    private static void RebuildListsLocked(IEnumerable<string>? paths)
    {
        _primaryPaths.Clear();
        _exePaths.Clear();
        _corePaths.Clear();
        string? self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self)) _corePaths.Add(self);
        string xray = XrayEngine.EnginePath;
        if (!_corePaths.Any(x => string.Equals(x, xray, StringComparison.OrdinalIgnoreCase))) _corePaths.Add(xray);
        if (paths == null) return;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full = raw.Trim();
            if (_primaryPaths.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) continue;
            _primaryPaths.Add(full);
            ExpandInto(full, _exePaths);
        }
    }

    private static void ExpandInto(string path, HashSet<string> target)
    {
        try
        {
            if (File.Exists(path)) AddIfNotSelf(path, target);
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe"))
            {
                if (n++ >= MaxExesPerApp) break;
                AddIfNotSelf(f, target);
            }
        }
        catch { }
    }

    private static void AddIfNotSelf(string path, HashSet<string> target)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("xray", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Roxa", StringComparison.OrdinalIgnoreCase)) return;
        string? self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) &&
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) return;
        target.Add(path);
    }

    private static void Apply(int gen)
    {
        bool want;
        int count;
        lock (_lock)
        {
            want = _enabled && !_tunnelUp;
            count = _primaryPaths.Count;
        }
        Task.Factory.StartNew(() => ApplyRules(gen, want, count), TaskCreationOptions.LongRunning);
    }

    private static void ApplyRules(int gen, bool want, int count)
    {
        lock (_ruleLock)
        {
            if (gen < _appliedGen) return;
            _appliedGen = gen;
            try
            {
                if (want)
                {
                    if (_appliedEngaged == 1) WfpNative.CloseSession(ref _engine);
                    bool fresh = _appliedEngaged == 0;
                    if (EngageWfp() < 0)
                    {
                        _appliedEngaged = 0;
                        TunManager.Log("kill-switch engage failed");
                        RaiseState(false, count);
                        // در حالت «تونل قطع است» هیچ تغییر وضعیت دیگری رخ نمی‌دهد تا
                        // Apply دوباره صدا زده شود؛ بدون retry، کلید قطع برای همیشه می‌ماند.
                        ScheduleEngageRetry(gen);
                        return;
                    }
                    Interlocked.Exchange(ref _engageRetries, 0);
                    _appliedEngaged = 1;
                    if (fresh)
                    {
                        TunManager.Log($"kill-switch engaged: {count} app(s) exempt");
                        RaiseState(true, count);
                    }
                }
                else if (_appliedEngaged == 1)
                {
                    WfpNative.CloseSession(ref _engine);
                    _appliedEngaged = 0;
                    TunManager.Log("kill-switch released");
                    RaiseState(false, count);
                }
            }
            catch (Exception ex)
            {
                TunManager.Log($"kill-switch sync failed: {ex.Message}");
            }
        }
    }

    private static int _engageRetries;
    private const int MaxEngageRetries = 5;
    private const int EngageRetryDelayMs = 3000;

    private static void ScheduleEngageRetry(int gen)
    {
        if (Interlocked.Increment(ref _engageRetries) > MaxEngageRetries)
        {
            Interlocked.Exchange(ref _engageRetries, 0);
            TunManager.Log($"kill-switch engage failed {MaxEngageRetries} time(s); will retry on next state change");
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(EngageRetryDelayMs); } catch { }
            // want دوباره محاسبه می‌شود؛ اگر در این فاصله تونل برگشته یا کلید قطع
            // شده باشد، هیچ اتفاقی نمی‌افتد.
            Apply(gen);
        });
    }

    private static int EngageWfp()
    {
        uint rc = WfpNative.OpenSession(out IntPtr engine);
        if (rc != 0)
        {
            TunManager.Log($"kill-switch wfp open failed rc=0x{rc:X8}");
            return -1;
        }
        rc = WfpNative.AddSubLayer(engine);
        if (rc != 0)
        {
            TunManager.Log($"kill-switch wfp sublayer failed rc=0x{rc:X8}");
            WfpNative.CloseSession(ref engine);
            return -1;
        }
        foreach (var layer in new[] { WfpNative.ConnectLayerV4, WfpNative.ConnectLayerV6 })
        {
            rc = WfpNative.AddBlock(engine, layer);
            if (rc != 0)
            {
                TunManager.Log($"kill-switch wfp block failed rc=0x{rc:X8}");
                WfpNative.CloseSession(ref engine);
                return -1;
            }
        }
        List<string> exes;
        lock (_lock) exes = _corePaths.Concat(_exePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int permits = 0, failed = 0;
        foreach (var layer in new[] { WfpNative.ConnectLayerV4, WfpNative.ConnectLayerV6 })
        {
            foreach (var exe in exes)
            {
                rc = WfpNative.AddPermitApp(engine, layer, exe);
                if (rc == 0) permits++;
                else
                {
                    failed++;
                    TunManager.Log($"kill-switch wfp app permit failed rc=0x{rc:X8} {exe}");
                }
            }
            foreach (var port in PermitPorts)
            {
                rc = WfpNative.AddPermitPort(engine, layer, port);
                if (rc == 0) permits++;
                else
                {
                    failed++;
                    TunManager.Log($"kill-switch wfp port permit failed rc=0x{rc:X8} {port}");
                }
            }
        }
        foreach (var range in Ranges4)
        {
            rc = WfpNative.AddPermitRange4(engine, range[0], range[1]);
            if (rc == 0) permits++;
            else
            {
                failed++;
                TunManager.Log($"kill-switch wfp net4 permit failed rc=0x{rc:X8}");
            }
        }
        foreach (var range in Ranges6)
        {
            rc = WfpNative.AddPermitRange6(engine, range.Low, range.High);
            if (rc == 0) permits++;
            else
            {
                failed++;
                TunManager.Log($"kill-switch wfp net6 permit failed rc=0x{rc:X8}");
            }
        }
        _engine = engine;
        TunManager.Log($"kill-switch wfp ready: {permits} permit(s), {failed} failed, {exes.Count} exe(s)");
        return 1;
    }

    private static (byte[] Low, byte[] High)[] BuildRanges6()
    {
        var loop = new byte[16];
        loop[15] = 1;
        var ulaLow = new byte[16];
        ulaLow[0] = 0xFC;
        var ulaHigh = new byte[16];
        ulaHigh[0] = 0xFD;
        var llLow = new byte[16];
        llLow[0] = 0xFE;
        llLow[1] = 0x80;
        var llHigh = new byte[16];
        llHigh[0] = 0xFE;
        llHigh[1] = 0xBF;
        var mcLow = new byte[16];
        mcLow[0] = 0xFF;
        var mcHigh = new byte[16];
        mcHigh[0] = 0xFF;
        for (int i = 1; i < 16; i++)
        {
            ulaHigh[i] = 0xFF;
            mcHigh[i] = 0xFF;
        }
        for (int i = 2; i < 16; i++) llHigh[i] = 0xFF;
        return new[]
        {
            (loop, loop),
            (ulaLow, ulaHigh),
            (llLow, llHigh),
            (mcLow, mcHigh),
        };
    }

    private static void RaiseState(bool engaged, int count)
    {
        try { StateChanged?.Invoke(engaged, count); }
        catch (Exception ex) { TunManager.Log($"kill-switch state handler failed: {ex.Message}"); }
    }

    private static void Probe()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            try
            {
                string flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "ks-emergency.flag");
                if (File.Exists(flag))
                {
                    File.Delete(flag);
                    TunManager.Log("kill-switch: emergency flag received");
                    EmergencyRelease();
                    return;
                }
            }
            catch { }

            bool enabled, up;
            int port;
            lock (_lock) { enabled = _enabled; up = _tunnelUp; port = _probePort; }
            if (!enabled) return;

            string? fail = ProbeEngineFail(port);
            bool critical = fail is "no-routes" or "pid-dead" or "pid-exited" or "no-state-no-port";
            if (fail == null && port > 0)
            {
                int tick = Interlocked.Increment(ref _probeTick);
                if (tick % HttpProbeEvery == 0)
                {
                    string? httpFail = ProbeThroughTunnelFail(port);
                    if (httpFail == null) _httpFails = 0;
                    else
                    {
                        _httpFails++;
                        if (_httpFails >= MaxHttpFails) fail = "http:" + httpFail;
                    }
                }
            }
            if (fail == null && _httpFails >= MaxHttpFails) fail = "http-stale";
            if (fail == null)
            {
                _goodStreak++;
                if (!up && _goodStreak >= GoodTicksToRecover)
                {
                    int g;
                    lock (_lock)
                    {
                        _tunnelUp = true;
                        _goodStreak = 0;
                        _badTicks = 0;
                        _httpFails = 0;
                        _probeTick = 0;
                        g = ++_gen;
                    }
                    TunManager.Log("kill-switch: tunnel healthy again, recovering");
                    Apply(g);
                }
                else if (up && _goodStreak == GoodTicksToResetBad)
                {
                    _badTicks = 0;
                }
                return;
            }

            _goodStreak = 0;
            if (!up) return;

            // «http-stale» عمداً صفر است: فقط وضعیتِ بد را نگه می‌دارد و ثانیه‌به‌ثانیه شمارش را بالا نمی‌برد.
            // باآستانهٔ قبلی، پروبِ HTTP هر ۵ ثانیه اجرا می‌شد ولی بینِ آن‌ها هر ثانیه +۱ می‌خورد؛
            // نتیجه این بود که یک تایم‌اوتِ تنها ظرفِ ~۵ ثانیه کلِ ترافیک را مسدود و ~۶ ثانیه بعد رها می‌کرد.
            _badTicks += fail == "http-stale" ? 0 : fail.StartsWith("http:") ? 3 : critical ? 2 : 1;
            TunManager.Log($"kill-switch probe ({_badTicks}/{MaxBadTicks}): {fail} port={port}");
            if (_badTicks < MaxBadTicks) return;

            TunManager.Log("kill-switch: tunnel lost, engaging");
            // از دست رفتنِ تونل را خودِ پروب تشخیص داده؛ یعنی واقعی و غیرمنتظره است
            // و ارزشِ خبر دارد (برخلافِ قطعِ عمدیِ خودِ برنامه).
            _expectedDown = false;
            int gen;
            lock (_lock)
            {
                _tunnelUp = false;
                _httpFails = 0;
                _badTicks = 0;
                _goodStreak = 0;
                gen = ++_gen;
            }
            Apply(gen);
        }
        catch (Exception ex)
        {
            TunManager.Log($"kill-switch probe failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private static string? ProbeEngineFail(int port)
    {
        try
        {
            var state = TunState.Load();
            if (state != null)
            {
                if (state.EnginePid > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(state.EnginePid);
                        if (p.HasExited) return "pid-exited";
                    }
                    catch { return "pid-dead"; }
                }
                if (state.TunActive && TunManager.SnapshotRoutes().Count == 0) return "no-routes";
            }
            else if (port <= 0) return "no-state-no-port";
        }
        catch (Exception ex) { return "state:" + ex.GetType().Name; }

        if (port > 0)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect("127.0.0.1", port, null, null);
                bool waited = ar.AsyncWaitHandle.WaitOne(400);
                if (!waited) { ar.AsyncWaitHandle.Close(); return "tcp-timeout"; }
                try { client.EndConnect(ar); }
                finally { ar.AsyncWaitHandle.Close(); }
                if (!client.Connected) return "tcp-disconnected";
            }
            catch (Exception ex) { return "tcp:" + ex.GetType().Name; }
        }
        return null;
    }

    private static string? ProbeThroughTunnelFail(int port)
    {
        if (port <= 0) return "no-port";
        try
        {
            using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{port}"), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(HttpProbeTimeoutMs) };
            using var resp = http.GetAsync("https://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode) return null;
            return "status" + (int)resp.StatusCode;
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static int RemoveFirewallRule(string name)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) throw new InvalidOperationException("no fw com");
            dynamic policy = Activator.CreateInstance(policyType)!;
            try { policy.Rules.Remove(name); } catch { }
            return 0;
        }
        catch
        {
            return TunManager.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
        }
    }

    private static HashSet<string> ReadRulesFile()
    {
        try
        {
            if (!File.Exists(RulesFile)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return File.ReadAllLines(RulesFile)
                .Where(l => l.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void DeleteRulesFile()
    {
        try { if (File.Exists(RulesFile)) File.Delete(RulesFile); }
        catch { }
    }
}
