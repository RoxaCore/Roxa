using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Roxa.Core;

public static class TunExclusion
{
    private const uint MIB_TCP_STATE_DELETE_TCB = 12;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ScanIntervalMs = 1000;
    private const int GatewayCheckEvery = 10;
    // گیت‌ویِ پیشنهادی باید این‌قدر نوبت پشت‌سرهم پایدار باشد تا بپذیریمش؛ در لحظهٔ
    // تعویض آداپتر، کارتِ در حال قطع‌شدن چند ثانیه مسیرِ پیش‌فرضِ کم‌metric دارد.
    private const int GatewayStableTicks = 2;
    private const int MaxNewPinsPerScan = 10;
    private const long PidCacheTtlMs = 15000;
    private const string RulePrefix = "RoxaTunExcl_";

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetTcpEntry(ref MibTcpRow pTcpRow);

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>
    /// جدول TCP به‌همراه PIDِ مالک، مستقیم از iphlpapi. جایگزینِ netstat.exe شد چون
    /// فراخوانیِ هر ثانیه‌ایِ آن ۶۰ پروسه در دقیقه می‌ساخت و سیستم را کند می‌کرد.
    /// آدرس‌ها و پورت‌ها در همان ترتیبِ بایتِ شبکه‌اند که SetTcpEntry انتظار دارد.
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    private sealed record PidPath(string Path, long Ticks);

    private sealed record TcpRow(int Pid, uint LocalIp, uint LocalPort, uint RemoteIp, uint RemotePort);

    private static readonly object _lock = new();
    private static readonly object _ruleLock = new();
    private static readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _excludedPaths = new();
    private static readonly Dictionary<int, PidPath> _pidCache = new();
    private static readonly HashSet<uint> _pinned = new();
    private static readonly HashSet<(int Pid, uint Dst)> _rstPairs = new();
    private static string? _gateway;
    private static string? _candidateGateway;
    private static int _candidateStreak;
    private static Timer? _timer;
    private static int _scanTick;
    private static int _scanning;
    private static int _rulesGen;
    private static int _appliedRulesGen;
    private static readonly int _ourPid = Environment.ProcessId;

    private static string RulesFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "tunexcl-rules.txt");

    private static string PinsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "tunexcl-pins.txt");

    public static void Start(IEnumerable<string>? excludedPaths)
    {
        List<string> snapshot;
        int gen;
        lock (_lock)
        {
            if (_pinned.Count == 0) SweepPersistedPins(null);
            ApplyExcludedLocked(excludedPaths);
            snapshot = _excludedPaths.ToList();
            gen = ++_rulesGen;
            if (_timer == null)
            {
                TunManager.Log($"exclusion started ({_excluded.Count} app(s))");
                _timer = new Timer(_ => Scan(), null, 0, ScanIntervalMs);
            }
        }
        Task.Factory.StartNew(() => SyncRules(gen, snapshot.Count > 0 ? snapshot : null), TaskCreationOptions.LongRunning);
    }

    public static void SetExcluded(IEnumerable<string>? excludedPaths)
    {
        List<string> snapshot;
        int gen;
        lock (_lock)
        {
            if (!ApplyExcludedLocked(excludedPaths)) return;
            if (_excluded.Count == 0)
                TunManager.Log("exclusion list cleared");
            else if (_timer != null)
                TunManager.Log($"exclusion list updated ({_excluded.Count} app(s)); rescanning");
            else
                TunManager.Log($"exclusion list updated ({_excluded.Count} app(s)); scanner idle");
            snapshot = _excludedPaths.ToList();
            gen = ++_rulesGen;
        }
        Task.Factory.StartNew(() =>
        {
            lock (_lock) ClearPinnedLocked();
            SyncRules(gen, snapshot.Count > 0 ? snapshot : null);
        }, TaskCreationOptions.LongRunning);
    }

    public static void Stop()
    {
        int gen;
        lock (_lock)
        {
            gen = ++_rulesGen;
            if (_timer != null)
            {
                TunManager.Log("exclusion stopped");
                _timer.Dispose();
                _timer = null;
            }
            ClearPinnedLocked();
            _pidCache.Clear();
            _gateway = null;
            _candidateGateway = null;
            _candidateStreak = 0;
            _scanTick = 0;
        }
        Task.Factory.StartNew(() => SyncRules(gen, null), TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// قانون‌هایی را که از نشستِ قبلی مانده‌اند پاک می‌کند.
    /// SyncRules فقط هنگامِ اتصالِ TUN اجرا می‌شود و Stop هم در مسیرِ عادیِ
    /// قطع اتصال صدا می‌خورد؛ اما اگر برنامه هنگامِ وصل کرش کند یا از مسیرِ
    /// ProcessExit/UnhandledException بیرون برود (که TunExclusion.Stop را صدا
    /// نمی‌زند)، این قانون‌ها می‌مانند. هر کدام «dir=out action=block UDP 443»
    /// رویِ یک برنامهٔ مشخص است، یعنی از دیدِ کاربر همان «این برنامه
    /// اینترنتش کنده» — بی‌آنکه چیزی به Roxa اشاره کند و تا اتصالِ بعدیِ TUN
    /// هم باقی می‌ماند. در شروعِ برنامه نشستِ فعالی وجود ندارد (تک‌نمونه‌ای
    /// بودنِ برنامه با mutex در Program.cs تضمین شده)، پس هرچه در RulesFile
    /// ثبت است باقی‌مانده است. الگو دقیقاً همانِ TunKillSwitch.CleanupLeftovers
    /// است که کنارش صدا زده می‌شود.
    /// </summary>
    public static void CleanupLeftovers()
    {
        try
        {
            var names = ReadRulesFile();
            if (names.Count == 0) return;
            foreach (var name in names)
                TunManager.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
            try { if (File.Exists(RulesFile)) File.Delete(RulesFile); } catch { }
            TunManager.Log($"exclusion leftovers removed ({names.Count})");
        }
        catch (Exception ex)
        {
            TunManager.Log($"exclusion cleanup failed: {ex.Message}");
        }
    }

    private static bool ApplyExcludedLocked(IEnumerable<string>? excludedPaths)
    {
        var nextNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextFull = new List<string>();
        if (excludedPaths != null)
            foreach (var path in excludedPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                string p = path.Trim();
                string name = Path.GetFileName(p);
                if (name.Length == 0) continue;
                if (nextNames.Add(name)) nextFull.Add(p);
            }
        bool changed = !nextNames.SetEquals(_excluded) ||
                       _excludedPaths.Count != nextFull.Count ||
                       !_excludedPaths.SequenceEqual(nextFull, StringComparer.OrdinalIgnoreCase);
        _excluded.Clear();
        _excluded.UnionWith(nextNames);
        _excludedPaths.Clear();
        _excludedPaths.AddRange(nextFull);
        return changed;
    }

    private static void Scan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        try
        {
            lock (_lock)
            {
                if (_timer == null || _excluded.Count == 0) return;
                if (_scanTick % GatewayCheckEvery == 0)
                    RefreshGatewayLocked();
                _scanTick++;
            }
            if (_gateway == null) return;
            var rows = ReadTcpTable();
            if (rows.Count == 0) return;
            lock (_lock)
            {
                if (_timer == null || _excluded.Count == 0 || _gateway == null) return;
                ProcessRowsLocked(rows);
            }
        }
        catch (Exception ex)
        {
            TunManager.Log($"exclusion scan failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    /// <summary>
    /// به‌روزرسانیِ گیت‌ویِ معتبر برای سنجاق کردن مسیرها.
    /// ۱) اگر گیت‌ویِ فعلی دیگر روی هیچ کارتِ غیر-TUN نیست (آداپتر عوض شده)، مسیرهای
    ///    سنجاق‌شده به سیاه‌چاله تبدیل شده‌اند و باید همین حالا حذف شوند، نه اینکه
    ///    تا یافتنِ جایگزین منتظر بمانند.
    /// ۲) گیت‌ویِ جدید فقط بعد از پایداری چند نوبتِ پیاپی پذیرفته می‌شود، چون کارتِ
    ///    در حال قطع‌شدن چند ثانیه هنوز مسیرِ پیش‌فرضِ کم‌metric دارد.
    /// ۳) اگر تشخیص خالی برگشت، گیت‌ویِ قبلی حفظ می‌شود تا اسکن از کار نیفتد.
    /// </summary>
    private static void RefreshGatewayLocked()
    {
        // اول گیت‌ویِ سالم را پیدا می‌کنیم؛ بررسیِ «آیا گیت‌ویِ قدیمی هنوز هست» فقط
        // وقتی لازم است که گیت‌ویِ فعلی فرق کرده باشد. اگر GetDefaultGatewayVia همان
        // قبلی را برگرداند، خودِ آن ثابت کرده روی کارتِ فیزیکی هست و فراخوانیِ دوم
        // صرفاً یک پیمایشِ تکراریِ کارت‌های شبکه است.
        string? gw = TunManager.GetDefaultGatewayVia();

        if (_gateway != null && !string.Equals(gw, _gateway, StringComparison.Ordinal)
            && !TunManager.IsPhysicalGatewayPresent(_gateway))
        {
            TunManager.Log($"exclusion gateway {_gateway} no longer present; dropping stale pins");
            ClearPinnedLocked();
            _gateway = null;
            _candidateGateway = null;
            _candidateStreak = 0;
        }

        if (gw == null)
        {
            // تشخیص گیت‌وی هنگام تعویض آداپتر گاهی چند نوبت خالی برمی‌گردد؛ تا گیت‌ویِ
            // معتبر نداریم چیزی سنجاق نمی‌کنیم، ولی اسکن نباید متوقف شود.
            if (_gateway == null)
                TunManager.Log("exclusion gateway detection empty; pinning deferred");
            return;
        }

        if (_gateway == gw)
        {
            _candidateGateway = null;
            _candidateStreak = 0;
            ReconcilePinsLocked();
            return;
        }
        if (_gateway == null)
        {
            TunManager.Log($"exclusion gateway acquired {gw}");
            _gateway = gw;
            _candidateGateway = null;
            _candidateStreak = 0;
            ReconcilePinsLocked();
            return;
        }
        if (!string.Equals(_candidateGateway, gw, StringComparison.Ordinal))
        {
            _candidateGateway = gw;
            _candidateStreak = 1;
            return;
        }
        if (++_candidateStreak < GatewayStableTicks) return;
        TunManager.Log($"exclusion gateway changed {_gateway} -> {gw}; repinning");
        ClearPinnedLocked();
        _gateway = gw;
        _candidateGateway = null;
        _candidateStreak = 0;
    }

    /// <summary>
    /// تطبیقِ مسیرهای سنجاق‌شده با جدول مسیر واقعی. ویندوز هنگام تعویض آداپتر یا تجدید
    /// DHCP مسیرهای /32 را حذف می‌کند، ولی <c>_pinned</c> هنوز فکر می‌کند سرِ جایشان
    /// هستند و دیگر هرگز دوباره ساخته نمی‌شوند → ترافیکِ برنامهٔ معاف به TUN می‌رود
    /// یا پشتِ گیت‌ویِ مرده گیر می‌کند.
    /// </summary>
    private static void ReconcilePinsLocked()
    {
        if (_pinned.Count == 0) return;
        // FilterPresent در شکِ route print همه را «حاضر» برمی‌گرداند؛ یعنی فقط وقتی
        // مطمئنیم مسیر رفته، آن را حذف می‌کنیم.
        var present = FilterPresent(_pinned.Select(IpText).ToList());
        var missing = _pinned.Where(v => !present.Contains(IpText(v))).ToList();
        if (missing.Count == 0) return;
        foreach (var ip in missing)
        {
            _pinned.Remove(ip);
            DropRstForDestLocked(ip);
        }
        PersistPinsLocked();
        TunManager.Log($"exclusion reconcile dropped {missing.Count} vanished pin(s); will re-pin");
    }

    /// <summary>
    /// فراموش‌کردنِ resetهای انجام‌شده برای یک مقصد، تا بعد از ساختنِ مسیرِ تازه همان
    /// اتصال دوباره reset شود و برنامه واقعاً از مسیرِ سالم استفاده کند.
    /// </summary>
    private static void DropRstForDestLocked(uint dst)
    {
        foreach (var key in _rstPairs.Where(k => k.Dst == dst).ToList())
            _rstPairs.Remove(key);
    }

    private static List<TcpRow> ReadTcpTable()
    {
        var rows = new List<TcpRow>();
        int size = 0;
        uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != ErrorInsufficientBuffer && rc != NoError) return rows;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != NoError) return rows;

            // ستون اول تعداد ردیف است، بعد از آن آرایهٔ ردیفها با اندازهٔ ثابت.
            int count = Marshal.ReadInt32(buf);
            IntPtr ptr = IntPtr.Add(buf, sizeof(int));
            int stride = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr);
                ptr += stride;
                if (row.OwningPid == 0 || row.OwningPid == (uint)_ourPid) continue;
                if (row.RemoteAddr == 0 || IsPrivate(row.RemoteAddr)) continue;
                rows.Add(new TcpRow(
                    (int)row.OwningPid, row.LocalAddr, row.LocalPort, row.RemoteAddr, row.RemotePort));
            }
        }
        catch (Exception ex)
        {
            TunManager.Log($"exclusion tcp table read failed: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return rows;
    }

    private static void ProcessRowsLocked(List<TcpRow> rows)
    {
        int newPins = 0;
        foreach (var row in rows)
        {
            if (row.Pid == _ourPid) continue;
            string exe = ResolveExeName(row.Pid);
            if (exe.Length == 0 || !_excluded.Contains(exe)) continue;

            if (!_pinned.Contains(row.RemoteIp))
            {
                if (newPins >= MaxNewPinsPerScan) continue;
                if (!PinRouteLocked(row.RemoteIp)) continue;
                _pinned.Add(row.RemoteIp);
                newPins++;
                PersistPinsLocked();
                // مسیرِ تازه فقط با resetِ دوبارهٔ همان اتصال به برنامه می‌رسد؛ وگرنه
                // سوکتِ کهنه روی گیت‌ویِ مرده می‌ماند و برنامه «قطع» دیده می‌شود.
                DropRstForDestLocked(row.RemoteIp);
                TunManager.Log($"exclusion pin {IpText(row.RemoteIp)} via {_gateway}");
            }
            if (_rstPairs.Add((row.Pid, row.RemoteIp)))
            {
                ResetTcp(row);
                TunManager.Log($"exclusion reset pid={row.Pid} exe={exe} -> {IpText(row.RemoteIp)}");
            }
        }
    }

    private static bool PinRouteLocked(uint ip)
    {
        string dest = IpText(ip);
        TunManager.RunRoute($"delete {dest} mask 255.255.255.255");
        int code = TunManager.RunRoute($"add {dest} mask 255.255.255.255 {_gateway}");
        return code == 0;
    }

    private static void ClearPinnedLocked()
    {
        int total = _pinned.Count;
        if (total > 0)
        {
            var ips = _pinned.Select(IpText).ToList();
            foreach (var ip in ips) TryDeletePin(ip);
            var still = FilterPresent(ips);
            if (still.Count > 0)
            {
                foreach (var ip in still) TryDeletePin(ip);
                still = FilterPresent(still);
            }
            if (still.Count > 0)
                TunManager.Log($"exclusion removed {total - still.Count}/{total} pinned route(s); {still.Count} still present");
            else
                TunManager.Log($"exclusion removed {total} pinned route(s)");
        }
        _pinned.Clear();
        _rstPairs.Clear();
        PersistPinsLocked();
    }

    private static void TryDeletePin(string ip)
    {
        try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); }
        catch { }
    }

    private static HashSet<string> FilterPresent(IEnumerable<string> ips)
    {
        var want = ips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (want.Count == 0) return want;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // از جدولِ بومی می‌خوانیم نه از خروجیِ route print؛ اجرایِ پروسه در هر نوبت
            // یعنی ساختِ پروسه + اسکنِ آنتی‌ویروس، و این چک هر چند ثانیه تکرار می‌شود.
            // اگر خواندن شکست خورد نباید نتیجه بگیریم که هیچ پینی باقی نمانده؛
            // وگرنه فایل پین‌ها به‌اشتباه حذف می‌شود.
            var table = IpRoute.GetTable();
            if (table == null) return want;
            foreach (var row in table)
            {
                if (row.Mask != uint.MaxValue) continue;   // فقط مسیرهای /32
                string dst = IpRoute.IpToString(row.Dest);
                if (want.Contains(dst)) present.Add(dst);
            }
        }
        catch
        {
            return want;
        }
        return present;
    }

    private static void PersistPinsLocked()
    {
        try
        {
            if (_pinned.Count == 0)
            {
                if (File.Exists(PinsFile)) File.Delete(PinsFile);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PinsFile)!);
            string tmp = PinsFile + ".tmp";
            File.WriteAllLines(tmp, _pinned.Select(IpText));
            File.Move(tmp, PinsFile, true);
        }
        catch (Exception ex) { TunManager.Log($"exclusion pins persist failed: {ex.Message}"); }
    }

    public static bool HasPersistedPins()
    {
        try { return File.Exists(PinsFile) && new FileInfo(PinsFile).Length > 0; }
        catch { return false; }
    }

    public static bool IsPinned(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = addr.GetAddressBytes();
        uint v = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        lock (_lock) return _pinned.Contains(v);
    }

    public static int SweepPersistedPins(HashSet<string>? protectedDests)
    {
        string[] lines;
        try
        {
            if (!File.Exists(PinsFile)) return 0;
            lines = File.ReadAllLines(PinsFile);
        }
        catch (Exception ex)
        {
            TunManager.Log($"exclusion pins sweep read failed: {ex.Message}");
            return 0;
        }

        var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            if (!IPAddress.TryParse(s, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) continue;
            if (protectedDests != null && protectedDests.Contains(s)) continue;
            pending.Add(s);
        }

        int removed = 0;
        foreach (var ip in pending)
        {
            try { if (TunManager.RunRoute($"delete {ip} mask 255.255.255.255") == 0) removed++; }
            catch { }
        }

        var still = FilterPresent(pending);
        try
        {
            if (still.Count == 0)
            {
                if (File.Exists(PinsFile)) File.Delete(PinsFile);
            }
            else File.WriteAllLines(PinsFile, still);
        }
        catch { }

        if (removed > 0 || still.Count > 0)
            TunManager.Log($"exclusion sweep removed {removed} leftover pin(s); {still.Count} still present");
        return removed;
    }

    private static void ResetTcp(TcpRow row)
    {
        var target = new MibTcpRow
        {
            State = MIB_TCP_STATE_DELETE_TCB,
            LocalAddr = row.LocalIp,
            LocalPort = row.LocalPort,
            RemoteAddr = row.RemoteIp,
            RemotePort = row.RemotePort
        };
        try
        {
            uint rc = SetTcpEntry(ref target);
            if (rc != 0) TunManager.Log($"exclusion reset rc={rc} {IpText(row.RemoteIp)}");
        }
        catch (Exception ex) { TunManager.Log($"exclusion reset failed: {ex.Message}"); }
    }

    private static string ResolveExeName(int pid)
    {
        long now = Environment.TickCount64;
        if (_pidCache.TryGetValue(pid, out var cached))
        {
            if (now - cached.Ticks < PidCacheTtlMs) return Path.GetFileName(cached.Path);
            _pidCache.Remove(pid);
        }
        string path = "";
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint len = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref len)) path = sb.ToString(0, (int)len);
            }
            finally { CloseHandle(handle); }
        }
        if (path.Length > 0) _pidCache[pid] = new PidPath(path, now);
        return path.Length > 0 ? Path.GetFileName(path) : "";
    }

    private static void SyncRules(int gen, List<string>? paths)
    {
        lock (_ruleLock)
        {
            if (gen < _appliedRulesGen) return;
            _appliedRulesGen = gen;
            try
            {
                var previous = ReadRulesFile();
                var desired = new List<(string Name, string Path)>();
                if (paths != null)
                    foreach (var raw in paths)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string p = raw.Trim();
                        string name = RuleNameFor(p);
                        if (!desired.Any(d => d.Name == name)) desired.Add((name, p));
                    }
                var cleanup = previous.Union(desired.Select(d => d.Name), StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var name in cleanup)
                    TunManager.RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                foreach (var (name, path) in desired)
                {
                    int code = TunManager.RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=out action=block protocol=UDP remoteport=443 program=\"{path}\"");
                    if (code != 0) TunManager.Log($"exclusion udp-block add failed ({code}) {path}");
                }
                WriteRulesFile(desired.Select(d => d.Name).ToList());
                if (desired.Count > 0)
                    TunManager.Log($"exclusion udp-block applied for {desired.Count} app(s)");
                else if (previous.Count > 0)
                    TunManager.Log("exclusion udp-block removed");
            }
            catch (Exception ex)
            {
                TunManager.Log($"exclusion udp-block sync failed: {ex.Message}");
            }
        }
    }

    private static string RuleNameFor(string path)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path.Trim().ToLowerInvariant()));
        return RulePrefix + Convert.ToHexString(hash.AsSpan(0, 8));
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

    private static void WriteRulesFile(List<string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RulesFile)!);
            File.WriteAllLines(RulesFile, names);
        }
        catch { }
    }

    private static bool IsPrivate(uint ip)
    {
        byte b0 = (byte)(ip & 0xFF);
        byte b1 = (byte)((ip >> 8) & 0xFF);
        if (b0 == 0 || b0 == 10 || b0 == 127 || b0 >= 224) return true;
        if (b0 == 169 && b1 == 254) return true;
        if (b0 == 172 && b1 >= 16 && b1 <= 31) return true;
        if (b0 == 192 && b1 == 168) return true;
        return false;
    }

    private static string IpText(uint ip) =>
        $"{ip & 0xFF}.{(ip >> 8) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 24) & 0xFF}";
}
