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
    private const int GatewayCheckEvery = 5;
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
        Task.Run(() => SyncRules(gen, snapshot.Count > 0 ? snapshot : null));
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
        Task.Run(() =>
        {
            lock (_lock) ClearPinnedLocked();
            SyncRules(gen, snapshot.Count > 0 ? snapshot : null);
        });
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
            _scanTick = 0;
        }
        Task.Run(() => SyncRules(gen, null));
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
                {
                    string? gw = TunManager.GetDefaultGatewayVia();
                    if (gw == null) return;
                    if (_gateway != null && _gateway != gw)
                    {
                        TunManager.Log($"exclusion gateway changed {_gateway} -> {gw}; repinning");
                        ClearPinnedLocked();
                    }
                    _gateway = gw;
                }
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

    private static List<TcpRow> ReadTcpTable()
    {
        var rows = new List<TcpRow>();
        var psi = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return rows;
        using var reader = proc.StandardOutput;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryParseEndpoint(parts[1], out uint lip, out uint lport)) continue;
            if (!TryParseEndpoint(parts[2], out uint rip, out uint rport)) continue;
            if (!int.TryParse(parts[^1], out int pid) || pid <= 0) continue;
            if (rip == 0 || IsPrivate(rip)) continue;
            rows.Add(new TcpRow(pid, lip, lport, rip, rport));
        }
        proc.WaitForExit(3000);
        return rows;
    }

    private static bool TryParseEndpoint(string text, out uint ip, out uint portNet)
    {
        ip = 0;
        portNet = 0;
        int ci = text.LastIndexOf(':');
        if (ci <= 0 || ci == text.Length - 1) return false;
        string host = text[..ci];
        if (host.Contains(':')) return false;
        if (!int.TryParse(text[(ci + 1)..], out int port) || port < 0 || port > 65535) return false;
        if (!IPAddress.TryParse(host, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = addr.GetAddressBytes();
        ip = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        portNet = (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
        return true;
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
            // اگر route print اجرا نشد یا خروجی خالی داد، نباید نتیجه بگیریم که هیچ
            // پینی باقی نمانده؛ وگرنه فایل پین‌ها به‌اشتباه حذف می‌شود.
            int code = TunManager.RunRoute("print -4", out string output);
            if (code != 0 || string.IsNullOrWhiteSpace(output)) return want;
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || parts[1] != "255.255.255.255") continue;
                if (want.Contains(parts[0])) present.Add(parts[0]);
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
