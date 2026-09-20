using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>مدیریت پروسس موتور xray: دانلود خودکار، اجرا، توقف. کراس‌پلتفرم.</summary>
public sealed class XrayEngine : IDisposable
{
    private Process? _process;
    private bool _proxyActive;
    private string? _tempConfigPath;
    private AppSettings? _settings;
    private readonly object _lock = new();

    public event Action<string>? Output;
    public event Action? Stopped;

    public ServerInfo? CurrentServer { get; private set; }
    public int HttpPort { get; private set; }
    public int SocksPort { get; private set; }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string EngineDir => Path.Combine(AppContext.BaseDirectory, "engine");

    public static string EngineBinary => IsWindows ? "xray.exe" : "xray";

    public static string EnginePath => Path.Combine(EngineDir, EngineBinary);

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    private void Log(string message) => Output?.Invoke(message);

    public async Task EnsureEngineAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(EnginePath)) return;
        Directory.CreateDirectory(EngineDir);
        Log("Downloading Xray-core engine ...");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Roxa/2.0");

        string latestJson = await http.GetStringAsync("https://api.github.com/repos/XTLS/Xray-core/releases/latest", ct);
        using var doc = JsonDocument.Parse(latestJson);
        string version = doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";

        string assetName = (IsWindows, RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64) => "Xray-windows-64.zip",
            (true, Architecture.Arm64) => "Xray-windows-arm64-v8a.zip",
            (true, Architecture.X86) => "Xray-windows-32.zip",
            (false, Architecture.X64) => "Xray-linux-64.zip",
            (false, Architecture.Arm64) => "Xray-linux-arm64-v8a.zip",
            _ => throw new NotSupportedException("Unsupported OS/architecture.")
        };

        string downloadUrl = "";
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == assetName
                && asset.TryGetProperty("browser_download_url", out var url))
            {
                downloadUrl = url.GetString() ?? "";
                break;
            }
        }
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException($"Asset {assetName} not found in latest Xray release.");

        byte[] data;
        try
        {
            data = await http.GetByteArrayAsync(downloadUrl, ct);
        }
        catch (HttpRequestException)
        {
            Log("Direct GitHub unreachable, trying mirror ...");
            data = await http.GetByteArrayAsync("https://gh-proxy.com/" + downloadUrl, ct);
        }

        string zipPath = Path.Combine(Path.GetTempPath(), "xray-core-download.zip");
        await File.WriteAllBytesAsync(zipPath, data, ct);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, EngineDir, overwriteFiles: true);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
        if (!IsWindows)
        {
            try
            {
#pragma warning disable CA1416 // guarded by IsWindows check
                File.SetUnixFileMode(EnginePath,
#pragma warning restore CA1416
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best effort */ }
        }
        Log($"Xray-core {version} ready.");
    }

    public async Task StartAsync(ServerInfo server, bool setSystemProxy, AppSettings? settings = null, CancellationToken ct = default)
    {
        Stop();
        _settings = settings;
        if (!File.Exists(EnginePath))
        {
            Log("Engine not found, downloading ...");
            await EnsureEngineAsync(false, ct);
        }

        bool useTun = settings?.UseTunMode == true;
        if (useTun)
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                Log("TUN mode requires Administrator. Falling back to proxy mode.");
                useTun = false;
            }
            else if (!TunManager.IsWintunAvailable())
            {
                bool ok = await TunManager.EnsureWintunDownloadedAsync();
                if (!ok)
                {
                    Log("wintun.dll not available. Falling back to proxy mode.");
                    useTun = false;
                }
                else if (!TunManager.IsTunAdapterActive())
                {
                    TunManager.CreateTunAdapter();
                }
            }
            else if (!TunManager.IsTunAdapterActive())
            {
                TunManager.CreateTunAdapter();
            }
        }

        SocksPort = FindFreePort();
        HttpPort = FindFreePort();
        string configJson = XrayConfigBuilder.Build(server, SocksPort, HttpPort, settings);
        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);

        try
        {
            var psi = new ProcessStartInfo(EnginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            Process process;
            lock (_lock)
            {
                _process = process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.Exited += (_, _) => OnExited();
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            CurrentServer = server;
            Log($"Connecting to {server.Name} {(useTun ? "[TUN Mode]" : "")} ...");

            bool ready = false;
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(200, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException("Xray engine exited unexpectedly.");
                }
                if (TryConnect(HttpPort)) { ready = true; break; }
            }
            if (!ready) throw new InvalidOperationException("Proxy port did not come up.");

            if (!useTun && setSystemProxy && IsWindows && settings?.LeaveSystemProxyAsIs != true)
            {
                SystemProxy.Set($"127.0.0.1:{HttpPort}");
                _proxyActive = true;
            }
            Log($"Connected. HTTP 127.0.0.1:{HttpPort}  SOCKS 127.0.0.1:{SocksPort} {(useTun ? "[TUN]" : "")}");
        }
        finally
        {
            CleanupTempConfig(configPath);
        }
    }

    public static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task StartWithConfigAsync(string configJson, int httpPort, CancellationToken ct = default)
    {
        Stop();
        if (!File.Exists(EnginePath))
        {
            Log("Engine not found, downloading ...");
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);

        try
        {
            var psi = new ProcessStartInfo(EnginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            Process process;
            lock (_lock)
            {
                _process = process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.Exited += (_, _) => OnExited();
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            HttpPort = httpPort;

            bool ready = false;
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(300, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException("Xray test engine exited unexpectedly.");
                }
                if (TryConnect(HttpPort)) { ready = true; break; }
            }
            if (!ready) throw new InvalidOperationException("Xray test port did not come up.");

            Log("Test Xray engine started.");
        }
        finally
        {
            CleanupTempConfig(configPath);
        }
    }

    private static bool TryConnect(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch { return false; }
    }

    public void Stop()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
            process.Dispose();
        }
        if (_proxyActive)
        {
            _proxyActive = false;
            if (_settings?.LeaveSystemProxyAsIs != true && _settings?.ClearProxyOnDisconnect == true)
            {
                try { SystemProxy.Clear(); }
                catch (Exception ex) { Log($"Clear proxy failed: {ex.Message}"); }
            }
        }
        CurrentServer = null;
        HttpPort = 0;
        SocksPort = 0;
        CleanupTempConfig(_tempConfigPath);
        _tempConfigPath = null;
    }

    public void StopAndCleanupTun()
    {
        Stop();
        try
        {
            if (TunManager.IsTunAdapterActive())
            {
                TunManager.CleanupTunAdapter();
                Log("TUN adapter cleaned up.");
            }
        }
        catch (Exception ex) { Log($"TUN cleanup failed: {ex.Message}"); }
    }

    public async Task StartMultiAsync(string configJson, Dictionary<int, ServerInfo> portMap, CancellationToken ct = default)
    {
        Stop();
        if (!File.Exists(EnginePath))
        {
            Log("Engine not found, downloading ...");
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-multi-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);

        try
        {
            var psi = new ProcessStartInfo(EnginePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);

            Process process;
            lock (_lock)
            {
                _process = process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
                process.Exited += (_, _) => OnExited();
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }

            // Wait for ports to be ready
            bool allReady = false;
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(300, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException("Xray multi-test engine exited unexpectedly.");
                }
                allReady = portMap.Keys.All(p => TryConnect(p));
                if (allReady) break;
            }
            if (!allReady) throw new InvalidOperationException("Multi-test ports did not come up.");

            _tempConfigPath = configPath;
            Log("Multi-test Xray engine started.");
        }
        catch
        {
            CleanupTempConfig(configPath);
            throw;
        }
    }

    private void OnExited()
    {
        lock (_lock)
        {
            if (_proxyActive)
            {
                _proxyActive = false;
                try { SystemProxy.Clear(); } catch { }
            }
            CurrentServer = null;
            HttpPort = 0;
            SocksPort = 0;
        }
        Log("Engine stopped.");
        Stopped?.Invoke();
    }

    public void Dispose()
    {
        Stop();
        Output = null;
        Stopped = null;
    }

    private void CleanupTempConfig(string? configPath)
    {
        try { if (configPath != null && File.Exists(configPath)) File.Delete(configPath); } catch { }
    }
}
