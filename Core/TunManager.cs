using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using XrayClient.Localization;

namespace XrayClient.Core;

public static class TunManager
{
    private const string TunAdapterName = "xray0";
    private const int CaptureTimeoutMs = 15000;
    private const int CaptureRetryDelayMs = 300;
    private const int ResolveTimeoutMs = 2500;

    private static readonly object _routesLock = new();
    private static readonly List<TunRouteRecord> _routes = new();

    private static readonly (string Prefix, string Dest, string Mask)[] _capturePrefixes =
    [
        ("0.0.0.0/1", "0.0.0.0", "128.0.0.0"),
        ("128.0.0.0/1", "128.0.0.0", "128.0.0.0"),
    ];

    private static readonly string _wintunDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine");
    private static readonly string _wintunPath = Path.Combine(_wintunDirectory, "wintun.dll");
    private static readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

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
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string url = $"https://www.wintun.net/builds/wintun-{arch}-0.14.1.zip";
            const string expectedSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";
            string zipPath = Path.Combine(WintunDirectory, "wintun.zip");

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(url);

            string actual = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(data)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log($"wintun hash mismatch: expected {expectedSha256}, got {actual}");
                log?.Invoke(TranslationManager.Get("Tun_HashBad"));
                return false;
            }

            await File.WriteAllBytesAsync(zipPath, data);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, WintunDirectory, true);
            File.Delete(zipPath);

            if (!File.Exists(_wintunPath))
            {
                var extracted = Directory.GetFiles(WintunDirectory, "wintun*.dll")
                    .FirstOrDefault(f => !Path.GetFileName(f).Equals("wintun.dll", StringComparison.OrdinalIgnoreCase));
                if (extracted != null)
                    File.Move(extracted, _wintunPath, true);
            }

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

            string? via = GetDefaultGatewayVia();
            long tGateway = Environment.TickCount64;
            if (via == null)
            {
                Log("No default gateway found; capture routes skipped.");
                return false;
            }

            string? currentHost = hosts?.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
            var serverIps = await ResolveServerIpsAsync(currentHost == null ? null : new[] { currentHost });
            long tDns = Environment.TickCount64;

            foreach (var ip in serverIps)
            {
                AddRoute($"add {ip} mask 255.255.255.255 {via}");
                string? net24 = Subnet24(ip);
                if (net24 != null) AddRoute($"add {net24} mask 255.255.255.0 {via}");
            }
            AddRoute($"add 10.0.0.0 mask 255.0.0.0 {via}");
            AddRoute($"add 172.16.0.0 mask 255.240.0.0 {via}");
            AddRoute($"add 192.168.0.0 mask 255.255.0.0 {via}");
            long tExcl = Environment.TickCount64;

            RunRoute("delete 0.0.0.0 mask 128.0.0.0");
            RunRoute("delete 128.0.0.0 mask 128.0.0.0");

            if (!await WaitCaptureRoutesAsync())
            {
                Log($"TUN adapter {TunAdapterName} did not appear in time; capture routes skipped.");
                RemoveCaptureRoutes();
                return false;
            }
            long tCapture = Environment.TickCount64;

            Log($"Route setup: via={via} gateway={tGateway - t0} resolve={tDns - tGateway} excl={tExcl - tDns} capture={tCapture - tExcl} total={tCapture - t0}ms excluded={serverIps.Count}");
            Log($"Capture routes added: {serverIps.Count} server ip(s) excluded.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"AddCaptureRoutes failed: {ex.Message}");
            RemoveCaptureRoutes();
            return false;
        }
    }

    public static bool RemoveCaptureRoutes()
    {
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
            bool allAdded = true;
            foreach (var (prefix, dest, mask) in _capturePrefixes)
            {
                if (RunNetsh($"interface ipv4 add route prefix={prefix} interface={TunAdapterName}") == 0)
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

    public static string? GetDefaultGatewayVia()
    {
        try
        {
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
            Log($"Default gateway via={via} (metric={bestMetric})");
            return via;
        }
        catch (Exception ex)
        {
            Log($"GetDefaultGatewayVia failed: {ex.Message}");
            return null;
        }
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
        stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(); } catch { }
            return -1;
        }
        return proc.ExitCode;
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            string logFile = Path.Combine(_logDir, "tun.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
