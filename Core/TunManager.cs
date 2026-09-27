using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using XrayClient.Localization;

namespace XrayClient.Core;

public static class TunManager
{
    private const string TunAdapterName = "xray0";

    private static readonly object _routesLock = new();
    private static readonly List<string> _routeDeleteArgs = new();

    private static readonly string _wintunDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine");
    private static readonly string _wintunPath = Path.Combine(_wintunDirectory, "wintun.dll");
    private static readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    public static string WintunDirectory => _wintunDirectory;
    public static string WintunPath => _wintunPath;

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

    public static async Task<bool> WaitTunAdapterAsync(int timeoutMs = 15000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (TryGetTunIndex(out _))
            {
                Log($"TUN adapter {TunAdapterName} is up.");
                return true;
            }
            await Task.Delay(500);
        }
        Log($"TUN adapter {TunAdapterName} did not appear in time.");
        return false;
    }

    public static bool AddCaptureRoutes(IEnumerable<string>? hosts, Action<string>? log = null)
    {
        try
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                log?.Invoke(TranslationManager.Get("Tun_NeedAdmin"));
                return false;
            }
            if (!TryGetTunIndex(out int tunIndex))
            {
                Log("TUN adapter index not found; capture routes skipped.");
                return false;
            }

            var (gateway, gatewayIndex) = GetDefaultGateway();
            if (gateway == null)
            {
                Log("No default gateway found; capture routes skipped.");
                return false;
            }

            string via = gateway == "0.0.0.0" ? $"0.0.0.0 IF {gatewayIndex}" : gateway;
            var serverIps = new List<string>();
            foreach (var host in hosts ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(host)) continue;
                if (IPAddress.TryParse(host, out var literal))
                {
                    if (literal.AddressFamily == AddressFamily.InterNetwork && !serverIps.Contains(host))
                        serverIps.Add(host);
                    continue;
                }
                try
                {
                    foreach (var addr in Dns.GetHostAddresses(host))
                        if (addr.AddressFamily == AddressFamily.InterNetwork && !serverIps.Contains(addr.ToString()))
                            serverIps.Add(addr.ToString());
                }
                catch (Exception ex) { Log($"Resolve failed for {host}: {ex.Message}"); }
            }

            foreach (var ip in serverIps)
                AddRoute($"add {ip} mask 255.255.255.255 {via}");

            AddRoute($"add 10.0.0.0 mask 255.0.0.0 {via}");
            AddRoute($"add 172.16.0.0 mask 255.240.0.0 {via}");
            AddRoute($"add 192.168.0.0 mask 255.255.0.0 {via}");

            AddRoute($"add 0.0.0.0 mask 128.0.0.0 0.0.0.0 IF {tunIndex}");
            AddRoute($"add 128.0.0.0 mask 128.0.0.0 0.0.0.0 IF {tunIndex}");

            Log($"Capture routes added: {serverIps.Count} server ip(s) excluded, tun if={tunIndex}.");
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
        lock (_routesLock)
        {
            if (_routeDeleteArgs.Count == 0) return false;
            int removed = _routeDeleteArgs.Count;
            foreach (var args in _routeDeleteArgs)
            {
                try { RunRoute(args); }
                catch { }
            }
            _routeDeleteArgs.Clear();
            Log($"Removed {removed} tun route(s).");
            return true;
        }
    }

    private static void AddRoute(string addArgs)
    {
        var parts = addArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string deleteArgs = $"delete {parts[1]} mask {parts[3]}";
        try { RunRoute(deleteArgs); }
        catch { }
        int code = RunRoute(addArgs);
        if (code != 0)
            throw new InvalidOperationException($"route {addArgs} exited with {code}");
        lock (_routesLock) _routeDeleteArgs.Add(deleteArgs);
    }

    private static int RunRoute(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "route.exe",
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(); } catch { }
            return -1;
        }
        return proc.ExitCode;
    }

    private static bool TryGetTunIndex(out int ifIndex)
    {
        ifIndex = 0;
        try
        {
            string output = RunPowerShell("Get-NetAdapter -Name xray0 -ErrorAction SilentlyContinue | Where-Object Status -eq Up | ForEach-Object { $_.InterfaceIndex }");
            return int.TryParse(output, out ifIndex);
        }
        catch { return false; }
    }

    private static (string? Gateway, int IfIndex) GetDefaultGateway()
    {
        string output = RunPowerShell("Get-NetRoute -DestinationPrefix 0.0.0.0/0 -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1 | ForEach-Object { $_.NextHop + '|' + $_.InterfaceIndex }");
        var parts = output.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (null, 0);
        return (parts[0].Trim(), int.TryParse(parts[1].Trim(), out int idx) ? idx : 0);
    }

    private static string RunPowerShell(string command, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command \"" + command + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell failed to start");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException("powershell timed out");
        }
        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(stderr.Trim());
        return stdout.Trim();
    }

    private static void Log(string message)
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