using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace XrayClient.Core;

public static class TunKillSwitch
{
    private const string RulePrefix = "RoxaKillSw_";
    private const int ProbeIntervalMs = 1000;
    private const int HttpProbeEvery = 5;
    private const int MaxHttpFails = 2;
    private const int MaxBadTicks = 6;
    private const int GoodTicksToRecover = 5;
    private const int GoodTicksToResetBad = 3;
    private const int MaxExesPerApp = 30;

    private static readonly object _lock = new();
    private static readonly object _ruleLock = new();
    private static readonly List<string> _primaryPaths = new();
    private static readonly HashSet<string> _exePaths = new(StringComparer.OrdinalIgnoreCase);
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
                TunManager.Log($"kill-switch armed ({_primaryPaths.Count} app(s), {_exePaths.Count} exe(s))");
            }
            else if (!enabled && _timer != null)
            {
                _timer.Dispose();
                _timer = null;
                TunManager.Log("kill-switch disarmed");
            }
            timerOn = _timer != null;
        }
        if (enabled)
        {
            int fw = TunManager.RunNetsh("advfirewall set allprofiles state on");
            if (fw != 0) TunManager.Log($"kill-switch: firewall profiles enable failed ({fw})");
        }
        TunManager.Log($"kill-switch configure enabled={enabled} paths={n} timer={timerOn} gen={gen}");
        Apply(gen);
    }

    public static void SetTunnelUp(bool up, int probePort)
    {
        int gen;
        int portNow;
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
    }

    private static void RebuildListsLocked(IEnumerable<string>? paths)
    {
        _primaryPaths.Clear();
        _exePaths.Clear();
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
            name.Equals("XrayClient", StringComparison.OrdinalIgnoreCase)) return;
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
            want = _enabled && !_tunnelUp && _primaryPaths.Count > 0;
            count = _primaryPaths.Count;
        }
        Task.Run(() => ApplyRules(gen, want, count));
    }

    private static void ApplyRules(int gen, bool want, int count)
    {
        lock (_ruleLock)
        {
            if (gen < _appliedGen) return;
            _appliedGen = gen;
            try
            {
                if (want && _appliedEngaged == 0)
                {
                    List<string> exes;
                    lock (_lock) exes = _exePaths.ToList();
                    if (exes.Count == 0) return;
                    WriteRulesFile(exes.Select(RuleNameFor).Distinct().ToList());
                    int added = 0;
                    foreach (var exe in exes)
                    {
                        string name = RuleNameFor(exe);
                        int code = AddFirewallRule(name, exe);
                        if (code == 0) added++;
                        else TunManager.Log($"kill-switch add failed ({code}) {exe}");
                    }
                    _appliedEngaged = 1;
                    TunManager.Log($"kill-switch engaged: {added}/{exes.Count} rule(s), {count} app(s) waiting");
                    RaiseState(true, count);
                }
                else if (!want && _appliedEngaged == 1)
                {
                    var names = ReadRulesFile();
                    foreach (var name in names)
                        RemoveFirewallRule(name);
                    DeleteRulesFile();
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

            _badTicks += fail == "http-stale" ? 1 : fail.StartsWith("http:") ? 2 : critical ? 2 : 1;
            TunManager.Log($"kill-switch probe ({_badTicks}/{MaxBadTicks}): {fail} port={port}");
            if (_badTicks < MaxBadTicks) return;

            TunManager.Log("kill-switch: tunnel lost, engaging");
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
                if (TunManager.SnapshotRoutes().Count == 0) return "no-routes";
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
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(3000) };
            using var resp = http.GetAsync("https://www.gstatic.com/generate_204").GetAwaiter().GetResult();
            if (resp.IsSuccessStatusCode) return null;
            return "status" + (int)resp.StatusCode;
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static string RuleNameFor(string path)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(path.Trim().ToLowerInvariant()));
        return RulePrefix + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static int AddFirewallRule(string name, string exe)
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            var ruleType = Type.GetTypeFromProgID("HNetCfg.FwRule");
            if (policyType == null || ruleType == null) throw new InvalidOperationException("no fw com");
            dynamic policy = Activator.CreateInstance(policyType)!;
            dynamic rule = Activator.CreateInstance(ruleType)!;
            rule.Name = name;
            rule.Description = "Roxa kill switch";
            rule.ApplicationName = exe;
            rule.Direction = 2;
            rule.Action = 2;
            rule.Enabled = true;
            rule.Protocol = 256;
            rule.Profiles = 0x7FFFFFFF;
            try { policy.Rules.Remove(name); } catch { }
            policy.Rules.Add(rule);
            return 0;
        }
        catch
        {
            return TunManager.RunNetsh($"advfirewall firewall add rule name=\"{name}\" dir=out action=block program=\"{exe}\"");
        }
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

    private static void WriteRulesFile(List<string> names)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RulesFile)!);
            File.WriteAllLines(RulesFile, names);
        }
        catch { }
    }

    private static void DeleteRulesFile()
    {
        try { if (File.Exists(RulesFile)) File.Delete(RulesFile); }
        catch { }
    }
}
