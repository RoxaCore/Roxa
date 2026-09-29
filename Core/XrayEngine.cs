using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using XrayClient.Localization;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>مدیریت پروسس موتور xray: دانلود خودکار، اجرا، توقف. کراس‌پلتفرم.</summary>
public sealed class XrayEngine : IDisposable
{
    private Process? _process;
    private JobObject? _job;
    private bool _proxyActive;
    private string? _tempConfigPath;
    private AppSettings? _settings;
    private readonly object _lock = new();

    public event Action<string>? Output;
    public event Action? Stopped;

    public bool LastExitExpected { get; private set; }

    public ServerInfo? CurrentServer { get; private set; }
    public int HttpPort { get; private set; }
    public int SocksPort { get; private set; }
    public bool IsTunActive { get; private set; }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string EngineDir => Path.Combine(AppContext.BaseDirectory, "engine");

    public static string EngineBinary => IsWindows ? "xray.exe" : "xray";

    public static string EnginePath => Path.Combine(EngineDir, EngineBinary);

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    private void Log(string message)
    {
        Output?.Invoke(message);
        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "engine.log");
            if (File.Exists(file) && new FileInfo(file).Length > 4_000_000) File.WriteAllText(file, "");
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public async Task EnsureEngineAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(EnginePath)) return;
        Directory.CreateDirectory(EngineDir);
        Log(TranslationManager.Get("Eng_Downloading"));
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
            _ => throw new NotSupportedException(TranslationManager.Get("Eng_UnsupportedOs"))
        };

        string downloadUrl = "";
        string? expectedSha256 = null;
        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) && name.GetString() == assetName
                && asset.TryGetProperty("browser_download_url", out var url))
            {
                downloadUrl = url.GetString() ?? "";
                if (asset.TryGetProperty("digest", out var digest))
                {
                    string? d = digest.GetString();
                    if (d != null && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        expectedSha256 = d["sha256:".Length..].Trim();
                }
                break;
            }
        }
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException(string.Format(TranslationManager.Get("Eng_AssetMissing"), assetName));

        byte[] data;
        bool usedMirror = false;
        try
        {
            data = await http.GetByteArrayAsync(downloadUrl, ct);
        }
        catch (HttpRequestException)
        {
            if (string.IsNullOrEmpty(expectedSha256))
                throw;
            Log(TranslationManager.Get("Eng_MirrorTrying"));
            data = await http.GetByteArrayAsync("https://gh-proxy.com/" + downloadUrl, ct);
            usedMirror = true;
        }

        if (!string.IsNullOrEmpty(expectedSha256))
        {
            string actual = HashHex(data);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(string.Format(TranslationManager.Get("Eng_DigestMismatch"), expectedSha256, actual));
        }
        else if (usedMirror)
        {
            throw new InvalidDataException(TranslationManager.Get("Eng_NoDigest"));
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
        File.WriteAllText(EnginePath + ".sha256", HashHex(File.ReadAllBytes(EnginePath)));
        Log(string.Format(TranslationManager.Get("Eng_Ready"), version));
    }

    private static string HashHex(byte[] data)
        => BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(data)).Replace("-", "").ToLowerInvariant();

    private static bool VerifyEngineIntegrity(bool requireSidecar)
    {
        try
        {
            if (!File.Exists(EnginePath)) return false;
            string sidecar = EnginePath + ".sha256";
            if (!File.Exists(sidecar))
            {
                if (requireSidecar) return false;
                File.WriteAllText(sidecar, HashHex(File.ReadAllBytes(EnginePath)));
                return true;
            }
            string expected = File.ReadAllText(sidecar).Trim();
            string actual = HashHex(File.ReadAllBytes(EnginePath));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public async Task StartAsync(ServerInfo server, bool setSystemProxy, AppSettings? settings = null, CancellationToken ct = default, IEnumerable<string>? tunExclusionHosts = null)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        Stop();
        _settings = settings;
        if (!File.Exists(EnginePath))
        {
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }
        else if (!VerifyEngineIntegrity(requireSidecar: AdminHelper.IsRunningAsAdmin()))
        {
            Log(TranslationManager.Get("Eng_IntegrityRetry"));
            await EnsureEngineAsync(true, ct);
            if (!VerifyEngineIntegrity(requireSidecar: true))
                throw new InvalidDataException(TranslationManager.Get("Eng_IntegrityFailed"));
        }

        bool useTun = settings?.UseTunMode == true;
        if (useTun && !AdminHelper.IsRunningAsAdmin())
        {
            Log(TranslationManager.Get("Eng_TunFallback"));
            useTun = false;
        }
        else if (useTun && !TunManager.IsWintunAvailable())
        {
            bool ok = await TunManager.EnsureWintunDownloadedAsync();
            if (!ok)
            {
                Log(TranslationManager.Get("Eng_WintunFallback"));
                useTun = false;
            }
        }

        SocksPort = FindFreePort();
        HttpPort = FindFreePort();
        string configJson = XrayConfigBuilder.Build(server, SocksPort, HttpPort, settings, useTun);
        // Keep temp config for diagnosis (diff vs external client configs)
        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-config-{server.Id}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);
        RestrictTempFile(configPath);

        try
        {
            var process = LaunchEngine(configPath);
            UpdateState(process, null);

            CurrentServer = server;
            Log(string.Format(TranslationManager.Get("Eng_ConnectingTo"), server.Name, useTun ? "[TUN Mode]" : ""));

            bool ready = false;
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(200, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException(TranslationManager.Get("Eng_ExitedUnexpectedly"));
                }
                if (TryConnect(HttpPort)) { ready = true; break; }
            }
            if (!ready) throw new InvalidOperationException(TranslationManager.Get("Eng_PortNotUp"));
            Log($"[perf] engine ready in {perf.ElapsedMilliseconds}ms");
            TunManager.Log($"[perf] engine ready in {perf.ElapsedMilliseconds}ms");

            if (useTun && settings?.UseTunMode == false)
                useTun = false;

            if (useTun && !await SetupTunRoutesAsync(server, tunExclusionHosts))
            {
                Log(TranslationManager.Get("Eng_TunFallback"));
                useTun = false;
            }

            if (useTun && settings?.UseTunMode == false)
            {
                TunManager.RemoveCaptureRoutes();
                Log(TranslationManager.Get("Eng_TunFallback"));
                useTun = false;
            }

            IsTunActive = useTun;

            if (!useTun && setSystemProxy && IsWindows && settings?.LeaveSystemProxyAsIs != true)
            {
                SystemProxy.GetCurrent(out bool prevEnable, out string prevServer, out string prevOverride);
                string proxyValue = $"127.0.0.1:{HttpPort}";
                SystemProxy.Set(proxyValue);
                _proxyActive = true;
                UpdateState(process, st =>
                {
                    st.ProxySet = proxyValue;
                    st.ProxyPrevEnable = prevEnable;
                    st.ProxyPrevServer = prevServer;
                    st.ProxyPrevOverride = prevOverride;
                });
            }

            var routeSnapshot = TunManager.SnapshotRoutes();
            if (routeSnapshot.Count > 0)
                UpdateState(process, st => st.Routes = routeSnapshot);

            Log(string.Format(TranslationManager.Get("Eng_ConnectedPorts"), HttpPort, SocksPort, useTun ? "[TUN]" : ""));
            Log($"[perf] start total {perf.ElapsedMilliseconds}ms tun={useTun}");
            TunManager.Log($"[perf] start total {perf.ElapsedMilliseconds}ms tun={useTun}");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private async Task<bool> SetupTunRoutesAsync(ServerInfo server, IEnumerable<string>? extraHosts)
    {
        try
        {
            var hosts = new List<string> { server.Address };
            if (extraHosts != null)
                hosts.AddRange(extraHosts);

            if (!await TunManager.AddCaptureRoutesAsync(hosts, Log))
            {
                Log(string.Format(TranslationManager.Get("Eng_TunRoutesFailed"), "see tun.log"));
                return false;
            }

            Log(TranslationManager.Get("Eng_TunRoutesAdded"));
            return true;
        }
        catch (Exception ex)
        {
            Log(string.Format(TranslationManager.Get("Eng_TunRoutesFailed"), ex.Message));
            return false;
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
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);
        RestrictTempFile(configPath);

        try
        {
            LaunchEngine(configPath);

            HttpPort = httpPort;

            bool ready = false;
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(300, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException(TranslationManager.Get("Eng_TestExited"));
                }
                if (TryConnect(HttpPort)) { ready = true; break; }
            }
            if (!ready) throw new InvalidOperationException(TranslationManager.Get("Eng_TestPortNotUp"));

            Log(TranslationManager.Get("Eng_TestStarted"));
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
        IsTunActive = false;
        TunExclusion.Stop();
        TunManager.RemoveCaptureRoutes();
        Process? process;
        JobObject? job;
        lock (_lock)
        {
            process = _process;
            _process = null;
            job = _job;
            _job = null;
        }
        if (process != null)
        {
            int pid = process.Id;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* already exited */ }
            try { process.Dispose(); } catch { }
            try
            {
                var st = TunState.Load();
                if (st != null && st.EnginePid == pid) TunState.Delete();
            }
            catch { }
        }
        job?.Dispose();
        if (_proxyActive)
        {
            _proxyActive = false;
            if (_settings?.LeaveSystemProxyAsIs != true && _settings?.ClearProxyOnDisconnect == true)
            {
                try { SystemProxy.Clear(); }
                catch (Exception ex) { Log(string.Format(TranslationManager.Get("Eng_ClearProxyFailed"), ex.Message)); }
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
        bool hadRoutes = TunManager.RemoveCaptureRoutes();
        Stop();
        if (hadRoutes)
            Log(TranslationManager.Get("Eng_TunCleaned"));
    }

    public async Task StartMultiAsync(string configJson, Dictionary<int, ServerInfo> portMap, CancellationToken ct = default)
    {
        Stop();
        if (!File.Exists(EnginePath))
        {
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"xrayclient-multi-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, configJson, ct);
        RestrictTempFile(configPath);

        try
        {
            LaunchEngine(configPath);

            // Wait for ports to be ready
            bool allReady = false;
            int maxAttempts = 30 + portMap.Count * 2;
            for (int i = 0; i < maxAttempts && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(300, ct);
                lock (_lock)
                {
                    if (_process == null || _process.HasExited)
                        throw new InvalidOperationException(TranslationManager.Get("Eng_MultiExited"));
                }
                allReady = portMap.Keys.All(p => TryConnect(p));
                if (allReady) break;
            }
            if (!allReady) throw new InvalidOperationException(TranslationManager.Get("Eng_MultiPortsNotUp"));

            _tempConfigPath = configPath;
            Log(TranslationManager.Get("Eng_MultiStarted"));
        }
        catch
        {
            CleanupTempConfig(configPath);
            throw;
        }
    }

    private void OnExited(Process process)
    {
        bool expected;
        lock (_lock)
        {
            expected = !ReferenceEquals(_process, process);
            if (!expected) _process = null;
            if (_proxyActive)
            {
                _proxyActive = false;
                try { SystemProxy.Clear(); } catch { }
            }
            CurrentServer = null;
            HttpPort = 0;
            SocksPort = 0;
        }
        if (!expected)
        {
            IsTunActive = false;
            TunExclusion.Stop();
            TunManager.RemoveCaptureRoutes();
            try
            {
                var st = TunState.Load();
                if (st != null && st.EnginePid == process.Id) TunState.Delete();
            }
            catch { }
            try { process.Dispose(); } catch { }
        }
        LastExitExpected = expected;
        Log(TranslationManager.Get("Eng_Stopped"));
        Stopped?.Invoke();
    }

    private Process LaunchEngine(string configPath)
    {
        if (OperatingSystem.IsWindows())
        {
            JobObject? job = JobObject.Create();
            Process process;
            try
            {
                process = ProcessStarter.StartSuspendedInJob(
                    EnginePath, new[] { "run", "-c", configPath }, job, Log);
            }
            catch
            {
                job?.Dispose();
                throw;
            }
            lock (_lock)
            {
                _job = job;
                _process = process;
            }
            process.Exited += (_, _) => OnExited(process);
            process.EnableRaisingEvents = true;
            return process;
        }

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
        Process proc;
        lock (_lock)
        {
            _process = proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data); };
            proc.Exited += (_, _) => OnExited(proc);
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        return proc;
    }

    private void UpdateState(Process process, Action<TunState>? mutate)
    {
        try
        {
            var st = TunState.Load();
            if (st == null || st.EnginePid != process.Id)
            {
                var fresh = CreateState(process);
                if (st != null)
                {
                    if (st.Routes.Count > 0) fresh.Routes = st.Routes;
                    if (st.ProxySet != null)
                    {
                        fresh.ProxySet = st.ProxySet;
                        fresh.ProxyPrevEnable = st.ProxyPrevEnable;
                        fresh.ProxyPrevServer = st.ProxyPrevServer;
                        fresh.ProxyPrevOverride = st.ProxyPrevOverride;
                    }
                }
                st = fresh;
            }
            mutate?.Invoke(st);
            TunState.Save(st);
        }
        catch { }
    }

    private static TunState CreateState(Process process)
    {
        long ticks = 0;
        try { ticks = process.StartTime.ToFileTimeUtc(); } catch { }
        return new TunState
        {
            EnginePid = process.Id,
            EngineStartTicks = ticks,
            EngineExePath = EnginePath,
            AdapterName = TunManager.AdapterName
        };
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

    private static void RestrictTempFile(string path)
    {
        try
        {
            if (IsWindows)
            {
#pragma warning disable CA1416
                var fi = new FileInfo(path);
                var security = fi.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                foreach (System.Security.AccessControl.AuthorizationRule rule in rules)
                    security.RemoveAccessRule((System.Security.AccessControl.FileSystemAccessRule)rule);
                var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (user != null)
                    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        user, System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.AccessControlType.Allow));
                fi.SetAccessControl(security);
#pragma warning restore CA1416
            }
            else
            {
#pragma warning disable CA1416 // non-Windows branch of IsWindows
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            }
        }
        catch { /* best effort */ }
    }
}
