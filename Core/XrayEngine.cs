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

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>Manages the xray engine process: automatic download, start, stop. Cross-platform.</summary>
public sealed class XrayEngine : IDisposable
{
    private Process? _process;
    private JobObject? _job;
    private bool _proxyActive;
    private bool _prevProxyEnable;
    private string _prevProxyServer = "";
    private string _prevProxyOverride = "";
    private string _proxyValue = "";
    private string? _tempConfigPath;
    private AppSettings? _settings;
    private readonly object _lock = new();
    // Disconnecting now runs in the background, and two Stop calls may land on two
    // threads concurrently with StartAsync; this gate serializes both so kill/process/proxy don't run twice.
    private readonly SemaphoreSlim _stopGate = new(1, 1);

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

    /// <summary>
    /// Is this folder actually writable? The folder's existence alone is not enough: in Program Files
    /// the folder exists but a non-elevated user has no write permission.
    /// </summary>
    private static bool IsDirWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public bool IsRunning
    {
        get { lock (_lock) return _process is { HasExited: false }; }
    }

    private static string? _engineVersionCache;

    /// <summary>The minimum version on which the current configurations (new finalmask/mkcp) have been tested.</summary>
    public const int MinEngineMajor = 26;

    /// <summary>Reads and caches the engine binary's version once; returns "?" on error.</summary>
    public static async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        if (_engineVersionCache != null) return _engineVersionCache;
        try
        {
            if (!File.Exists(EnginePath)) return _engineVersionCache = "?";
            var psi = new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = "version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return _engineVersionCache = "?";
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            // WaitForExit blocks the UI thread at the same time (the first connection takes a few hundredths of a second).
            using var exitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exitCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await p.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return _engineVersionCache = "?";
            }
            string text = await stdoutTask;
            if (string.IsNullOrWhiteSpace(text)) text = await stderrTask;
            _engineVersionCache = ParseVersionLine(text) ?? "?";
        }
        catch { _engineVersionCache = "?"; }
        return _engineVersionCache;
    }

    /// <summary>Is the given version at least MinEngineMajor? Unreadable versions are not rejected (not judging is better than a false error).</summary>
    public static bool IsVersionSupported(string version)
    {
        if (!TryParseVersion(version, out int major, out _)) return true;
        return major >= MinEngineMajor;
    }

    private static string? ParseVersionLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            string s = line.Trim();
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            if (i >= s.Length) continue;
            int j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
            string cand = s[i..j].Trim('.');
            if (TryParseVersion(cand, out _, out _)) return cand;
        }
        return null;
    }

    private static bool TryParseVersion(string? v, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(v)) return false;
        var parts = v.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor)) return false;
        return true;
    }

    private async Task WarnIfEngineOldAsync(CancellationToken ct)
    {
        try
        {
            string v = await GetVersionAsync(ct);
            if (v == "?") return;
            if (!IsVersionSupported(v))
                Log(string.Format(TranslationManager.Get("Eng_WarnOld"), v, MinEngineMajor));
        }
        catch { }
    }

    private static readonly object _engineLogGate = new();
    private static readonly System.Text.StringBuilder _engineLogBuf = new();
    private static System.Threading.Timer? _engineLogTimer;
    private const int EngineLogFlushThreshold = 8 * 1024;
    private const long EngineLogMaxBytes = 4_000_000;

    private void Log(string message)
    {
        Output?.Invoke(message);
        try
        {
            bool flushNow;
            lock (_engineLogGate)
            {
                _engineLogBuf.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                             .Append("] ").Append(message).Append(Environment.NewLine);
                if (_engineLogTimer == null)
                    _engineLogTimer = new System.Threading.Timer(_ => FlushEngineLog(), null, 1000, 1000);
                flushNow = _engineLogBuf.Length >= EngineLogFlushThreshold;
            }
            if (flushNow) FlushEngineLog();
        }
        catch { }
    }

    /// <summary>
    /// Flushes the engine log buffer. Previously every line of xray output meant CreateDirectory +
    /// File.Exists + FileInfo.Length + AppendAllText; four file operations on the same
    /// thread that reads the pipe output, whose stalling also stalls the engine.
    /// </summary>
    public static void FlushEngineLog()
    {
        string? chunk = null;
        try
        {
            lock (_engineLogGate)
            {
                if (_engineLogBuf.Length == 0) return;
                chunk = _engineLogBuf.ToString();
            }

            // We don't duplicate the tunnel log path logic: the same ResolveLogDir already knows whether
            // the app folder is writable (installing in Program Files).
            string dir = TunManager.LogDirectory;
            string file = Path.Combine(dir, "engine.log");
            try
            {
                var fi = new FileInfo(file);
                // Rotate to .old instead of truncating completely: deleting the file means losing
                // the entire history, and the history is exactly what is needed when investigating
                // a reported bug.
                if (fi.Exists && fi.Length + chunk.Length > EngineLogMaxBytes)
                    File.Move(file, file + ".old", true);
            }
            catch { }
            File.AppendAllText(file, chunk);

            // Only remove from the buffer after the actual write; otherwise a disk error would lose
            // those lines forever.
            lock (_engineLogGate)
            {
                bool stillThere = _engineLogBuf.Length >= chunk.Length
                    && string.CompareOrdinal(_engineLogBuf.ToString(0, chunk.Length), chunk) == 0;
                if (stillThere) _engineLogBuf.Remove(0, chunk.Length);
            }
        }
        catch
        {
            // The write failed; it will be retried on the next flush, but there is also a cap so the
            // program doesn't choke on buffer buildup.
            lock (_engineLogGate)
            {
                if (_engineLogBuf.Length > EngineLogMaxBytes)
                    _engineLogBuf.Remove(0, _engineLogBuf.Length - EngineLogFlushThreshold);
            }
        }
    }

    public async Task EnsureEngineAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && File.Exists(EnginePath)) return;

        // The engine download lands inside the app folder. If the user installed the app in Program
        // Files (an installer run with «Run as administrator») while the app itself runs
        // asInvoker, writing here is impossible. Previously that same UnauthorizedAccessException
        // broke the entire connection chain and the user only saw a vague error; now the reason and
        // the fix are given as well.
        if (!IsDirWritable(EngineDir))
        {
            string msg = TranslationManager.Get("Eng_DirNotWritable");
            Log(msg);
            throw new IOException(msg);
        }

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

        // Fail-closed: this archive is unpacked and executed, so an asset whose digest the
        // release metadata did not publish cannot be verified at all. Refuse before any
        // download instead of installing an unverified binary.
        if (string.IsNullOrEmpty(expectedSha256))
            throw new InvalidDataException(TranslationManager.Get("Eng_NoDigest"));

        byte[] data;
        try
        {
            data = await http.GetByteArrayAsync(downloadUrl, ct);
        }
        catch (HttpRequestException)
        {
            // The mirror is only reached once the digest is known, so whatever it returns
            // is still verified below.
            Log(TranslationManager.Get("Eng_MirrorTrying"));
            data = await http.GetByteArrayAsync("https://gh-proxy.com/" + downloadUrl, ct);
        }

        string actual = HashHex(data);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(string.Format(TranslationManager.Get("Eng_DigestMismatch"), expectedSha256, actual));

        string zipPath = Path.Combine(Path.GetTempPath(), "roxa-core-download.zip");
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

    public async Task StartAsync(ServerInfo server, bool setSystemProxy, AppSettings? settings = null, CancellationToken ct = default, IEnumerable<string>? tunExclusionHosts = null, Action<string>? progress = null)
    {
        var perf = System.Diagnostics.Stopwatch.StartNew();
        // Each stage both lands in the engine log (via Output) and reaches the header
        // Status via progress, so the UI doesn't fall behind during the TUN connection.
        void Stage(string key)
        {
            string text = TranslationManager.Get(key);
            Log(text);
            progress?.Invoke(text);
        }
        Stage("Stg_Cleanup");
        // The previous cleanup (killing the process, removing routes, restoring DNS via powershell) is heavy;
        // it runs on a LongRunning background thread so the UI thread doesn't hang and
        // blocking Thread-Pool threads doesn't delay the continuations of the work.
        await Task.Factory.StartNew(Stop, TaskCreationOptions.LongRunning);
        Stage("Stg_Engine");
        _settings = settings;
        // Installing in Program Files means the engine folder is read-only for a regular user.
        // So engine repair is only allowed when elevated. Without it, instead of re-downloading — which
        // is impossible anyway and would only produce an obscure failure mid-connection — we throw a clear
        // error and stop; here we don't even touch the network.
        if (!File.Exists(EnginePath))
        {
            // EnsureEngineAsync checks writability before any download and stops with
            // Eng_DirNotWritable, so a non-elevated user never downloads anything.
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }
        else if (!await Task.Run(() => VerifyEngineIntegrity(requireSidecar: AdminHelper.IsRunningAsAdmin())))
        {
            if (!AdminHelper.IsRunningAsAdmin())
            {
                string corrupt = TranslationManager.Get("Eng_ReadOnlyRepair");
                Log(corrupt);
                throw new InvalidDataException(corrupt);
            }
            Log(TranslationManager.Get("Eng_IntegrityRetry"));
            await EnsureEngineAsync(true, ct);
            if (!await Task.Run(() => VerifyEngineIntegrity(requireSidecar: true)))
                throw new InvalidDataException(TranslationManager.Get("Eng_IntegrityFailed"));
        }

        await WarnIfEngineOldAsync(ct);

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
        string configPath = Path.Combine(Path.GetTempPath(), $"roxa-config-{server.Id}.json");
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
                // A synchronous TCP connect on the UI thread briefly locked the header on every 200ms tick.
                if (await Task.Run(() => TryConnect(HttpPort))) { ready = true; break; }
            }
            if (!ready) throw new InvalidOperationException(TranslationManager.Get("Eng_PortNotUp"));
            Log($"[perf] engine ready in {perf.ElapsedMilliseconds}ms");
            TunManager.Log($"[perf] engine ready in {perf.ElapsedMilliseconds}ms");

            if (useTun && settings?.UseTunMode == false)
                useTun = false;

            if (useTun)
                Stage("Stg_Routes");
            if (useTun && !await SetupTunRoutesAsync(server, tunExclusionHosts))
            {
                Log(TranslationManager.Get("Eng_TunFallback"));
                useTun = false;
            }

            if (useTun && settings?.UseTunMode == false)
            {
                await Task.Factory.StartNew(() => TunManager.RemoveCaptureRoutes(), TaskCreationOptions.LongRunning);
                Log(TranslationManager.Get("Eng_TunFallback"));
                useTun = false;
            }

            IsTunActive = useTun;
            UpdateState(process, st => st.TunActive = useTun);

            if (useTun && XrayConfigBuilder.LastDnsStub)
            {
                Stage("Stg_Dns");
                // The order matters: first a snapshot of the current DNS is taken and recorded in TunState,
                // then the system is changed. If it were the other way around and the app died in between
                // (a crash or a kill from Task Manager), nothing would remain to know what the user's DNS was
                // before, and the next sweep would silently turn their manual static setting into DHCP —
                // i.e. the setting they entered by hand would be lost forever.
                var snap = await Task.Run(TunDns.PrepareApply);
                if (snap != null)
                {
                    UpdateState(process, st =>
                    {
                        st.DnsApplied = true;
                        st.DnsPrev = snap;
                    });
                    // Applying it may itself take a few seconds (fallback to powershell).
                    bool applied = await Task.Run(() => TunDns.TryApply(snap));
                    if (applied)
                        Log("DNS -> 127.0.0.1 (local stub)");
                    else
                        UpdateState(process, st => { st.DnsApplied = false; st.DnsPrev = null; });
                }
            }

            if (!useTun && setSystemProxy && IsWindows && settings?.LeaveSystemProxyAsIs != true)
            {
                SystemProxy.GetCurrent(out bool prevEnable, out string prevServer, out string prevOverride);
                if (prevEnable && SystemProxy.IsDeadLoopback(prevServer))
                {
                    TunManager.Log($"ignoring stale system proxy {prevServer} (no listener).");
                    prevEnable = false;
                    prevServer = "";
                    prevOverride = "";
                }
                string proxyValue;
                if (settings is { BypassVpnWithoutTun: true } && settings.TunExcludedApps is { Count: > 0 }
                    && ProxyRouter.Start(settings.TunExcludedApps, HttpPort))
                    proxyValue = $"127.0.0.1:{ProxyRouter.Port}";
                else
                    proxyValue = $"127.0.0.1:{HttpPort}";
                if (!_proxyActive)
                {
                    _prevProxyEnable = prevEnable;
                    _prevProxyServer = prevServer;
                    _prevProxyOverride = prevOverride;
                    _proxyValue = proxyValue;
                }
                // Record first, then change the system. RestoreProxyIfOurs checks the current registry
                // value against ProxySet, so this early recording is a no-op if nothing has been changed
                // yet; but if it were the other way around and the app died between the change and the recording,
                // the user's previous proxy (e.g. their corporate proxy) would be lost forever and
                // only ProxyEnable would be set to zero.
                UpdateState(process, st =>
                {
                    st.ProxySet = proxyValue;
                    st.ProxyPrevEnable = prevEnable;
                    st.ProxyPrevServer = prevServer;
                    st.ProxyPrevOverride = prevOverride;
                });
                SystemProxy.Set(proxyValue);
                _proxyActive = true;
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
            // Stop() is serialized behind the gate; running it synchronously on the UI thread could
            // hold the UI until the gate was released when the connection failed.
            await Task.Factory.StartNew(Stop, TaskCreationOptions.LongRunning);
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
        await Task.Factory.StartNew(Stop, TaskCreationOptions.LongRunning);
        if (!File.Exists(EnginePath))
        {
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"roxa-test-{Guid.NewGuid():N}.json");
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
        _stopGate.Wait();
        try
        {
            IsTunActive = false;
            TunExclusion.Stop();
            TunManager.RemoveCaptureRoutes();
            try { TunDns.RestoreFromState(); } catch { }
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
            try { ProxyRouter.StopForUpstream(HttpPort); } catch { }
            ReleaseSystemProxy();
            CurrentServer = null;
            HttpPort = 0;
            SocksPort = 0;
            CleanupTempConfig(_tempConfigPath);
            _tempConfigPath = null;
        }
        finally { _stopGate.Release(); }
    }

    private void ReleaseSystemProxy()
    {
        bool restore;
        lock (_lock)
        {
            if (!_proxyActive) return;
            _proxyActive = false;
            restore = _settings?.LeaveSystemProxyAsIs != true && _settings?.ClearProxyOnDisconnect == true;
        }
        if (!restore) return;
        try
        {
            if (!SystemProxy.GetCurrent(out bool enable, out string server, out _)) return;
            if (!enable || !string.Equals(server, _proxyValue, StringComparison.OrdinalIgnoreCase)) return;
            bool restoreEnable = _prevProxyEnable;
            if (restoreEnable && SystemProxy.IsDeadLoopback(_prevProxyServer))
            {
                TunManager.Log($"not restoring stale system proxy {_prevProxyServer} (no listener).");
                restoreEnable = false;
            }
            SystemProxy.Apply(restoreEnable, restoreEnable ? _prevProxyServer : null, restoreEnable ? _prevProxyOverride : null);
            TunManager.Log($"system proxy restored to previous value (was {_proxyValue})");
        }
        catch (Exception ex) { Log(string.Format(TranslationManager.Get("Eng_ClearProxyFailed"), ex.Message)); }
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
        await Task.Factory.StartNew(Stop, TaskCreationOptions.LongRunning);
        if (!File.Exists(EnginePath))
        {
            Log(TranslationManager.Get("Eng_NotFound"));
            await EnsureEngineAsync(false, ct);
        }

        string configPath = Path.Combine(Path.GetTempPath(), $"roxa-multi-{Guid.NewGuid():N}.json");
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
            try { ProxyRouter.StopForUpstream(HttpPort); } catch { }
            CurrentServer = null;
            HttpPort = 0;
            SocksPort = 0;
        }
        ReleaseSystemProxy();
        if (!expected)
        {
            IsTunActive = false;
            TunExclusion.Stop();
            TunManager.RemoveCaptureRoutes();
            try { TunDns.RestoreFromState(); } catch { }
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

    /// <summary>
    /// Sweeps stale temp configs of previous sessions out of %TEMP%.
    /// The current session's config deliberately stays for diagnosis («Keep temp config for
    /// diagnosis»), but the server id changes with every subscription update and
    /// leaves a fresh file behind; without this sweep their number grows unbounded
    /// (on one system 170 stale files had accumulated). Only files older than
    /// 24 hours are deleted, so the running session and a freshly closed
    /// session stay untouched. Files related to updates are deliberately not listed.
    /// </summary>
    public static void SweepTempConfigs(int maxAgeHours = 24)
    {
        string temp;
        try { temp = Path.GetTempPath(); } catch { return; }
        if (string.IsNullOrEmpty(temp) || !Directory.Exists(temp)) return;

        string[] patterns = ["roxa-config-*.json", "roxa-test-*.json", "roxa-multi-*.json", "roxa-speed.json"];
        DateTime cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
        int removed = 0;
        foreach (var pattern in patterns)
        {
            string[] files = Array.Empty<string>();
            try { files = Directory.GetFiles(temp, pattern); } catch { }
            foreach (var file in files)
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists || fi.LastWriteTimeUtc > cutoff) continue;
                    fi.Delete();
                    removed++;
                }
                catch { }
            }
        }
        if (removed > 0) TunManager.Log($"swept {removed} stale temp config(s) older than {maxAgeHours}h from {temp}");
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
