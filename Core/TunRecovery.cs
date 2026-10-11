using System.Diagnostics;

namespace Roxa.Core;

public static class TunRecovery
{
    public static bool LeftoverNeedsAdmin { get; private set; }

    public static void RecoverOnStartup()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var state = TunState.Load();
            bool admin = AdminHelper.IsRunningAsAdmin();
            TunManager.Log($"Recovery start: state={(state != null ? "yes" : "no")} admin={admin}");

            KillOrphanEngine(state);
            if (state != null) RestoreProxyIfOurs(state);
            TunDns.RestoreFromState(state);
            // اگر فایل وضعیت گم شده باشد RestoreFromState هیچ کاری نمی‌کند؛
            // این خط مستقیم دنبال DNSِ جامانده‌ی 127.0.0.1 (بدون سرویسِ روی :53) می‌گردد.
            TunDns.SweepDeadAdapters(out bool dnsStaleNeedsAdmin);
            CleanupStaleProxy();

            int swept = IpRoute.SweepOrphanRoutes(state);
            if (swept > 0) TunManager.Log($"Recovery removed {swept} leftover route(s).");

            TunExclusion.SweepPersistedPins(GuardedDests(state));
            bool pinsLeft = TunExclusion.HasPersistedPins();

            bool engineLeft = state != null && IsOurEngineAlive(state);
            bool routesLeft = IpRoute.HasLeftoverRoutes(state);
            bool dnsLeft = (state != null && state.DnsApplied) || dnsStaleNeedsAdmin;

            if (!engineLeft && !routesLeft && !pinsLeft && !dnsLeft)
            {
                TunState.Delete();
                TunManager.Log("Recovery clean.");
            }
            else
            {
                TunManager.Log($"Recovery incomplete: engineAlive={engineLeft} routesLeft={routesLeft} pinsLeft={pinsLeft} dnsLeft={dnsLeft} admin={admin}");
            }

            LeftoverNeedsAdmin = !admin && (engineLeft || routesLeft || pinsLeft || dnsLeft);
        }
        catch (Exception ex)
        {
            TunManager.Log($"Recovery error: {ex.Message}");
        }
    }

    public static void RestoreProxyIfOurs(TunState state)
    {
        if (state.ProxySet == null) return;
        try
        {
            if (!SystemProxy.GetCurrent(out bool enable, out string server, out _)) return;
            if (!enable || !string.Equals(server, state.ProxySet, StringComparison.OrdinalIgnoreCase)) return;
            bool restoreEnable = state.ProxyPrevEnable;
            if (restoreEnable && SystemProxy.IsDeadLoopback(state.ProxyPrevServer))
            {
                TunManager.Log("Recovery: previous system proxy is stale, clearing instead.");
                restoreEnable = false;
            }
            SystemProxy.Apply(restoreEnable, restoreEnable ? state.ProxyPrevServer : null, restoreEnable ? state.ProxyPrevOverride : null);
            TunManager.Log($"Recovery restored proxy to previous value (was {state.ProxySet}).");
        }
        catch (Exception ex)
        {
            TunManager.Log($"Recovery proxy restore failed: {ex.Message}");
        }
    }

    private static void CleanupStaleProxy()
    {
        try
        {
            if (!SystemProxy.GetCurrent(out bool enable, out string server, out _)) return;
            if (!enable || !SystemProxy.IsDeadLoopback(server)) return;
            SystemProxy.Clear();
            TunManager.Log($"Recovery cleared stale system proxy {server} (no listener).");
        }
        catch (Exception ex)
        {
            TunManager.Log($"Recovery stale proxy cleanup failed: {ex.Message}");
        }
    }

    public static void EmergencyCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (Avalonia.Application.Current is App app && app.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                vm.Shutdown();
        }
        catch { }
        try { TunManager.RemoveCaptureRoutes(); } catch { }
        try
        {
            var state = TunState.Load();
            if (state != null)
            {
                KillOrphanEngine(state);
                RestoreProxyIfOurs(state);
                TunDns.RestoreFromState(state);
                IpRoute.SweepOrphanRoutes(state);
            }
            TunExclusion.SweepPersistedPins(GuardedDests(state));
            TunState.Delete();
        }
        catch { }
        // آخرین خطوطِ لاگ را پیش از پایانِ پروسه روی دیسک می‌ریزیم؛ بافرِ لاگ
        // حداکثر یک ثانیه عقب است و نباید موقعِ کرش/خروج گم شود.
        try { TunManager.FlushLog(); } catch { }
    }

    private static HashSet<string> GuardedDests(TunState? state)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state?.Routes == null) return set;
        foreach (var r in state.Routes)
            if (string.Equals(r.Mask, "255.255.255.255", StringComparison.Ordinal) && r.Dest.Length > 0)
                set.Add(r.Dest);
        return set;
    }

    private static void KillOrphanEngine(TunState? state)
    {
        if (state == null) return;
        bool killed = false;
        if (IsOurEngineAlive(state))
        {
            try
            {
                using var p = Process.GetProcessById(state.EnginePid);
                p.Kill(entireProcessTree: true);
                TunManager.Log($"Recovery killed orphan engine pid={state.EnginePid}.");
                killed = true;
            }
            catch (Exception ex)
            {
                TunManager.Log($"Recovery kill failed pid={state.EnginePid}: {ex.Message}");
            }
        }
        try
        {
            string procName = Path.GetFileNameWithoutExtension(state.EngineExePath);
            if (!string.IsNullOrEmpty(procName))
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    using (proc)
                    {
                        if (proc.Id == state.EnginePid) continue;
                        string? exe = null;
                        try { exe = proc.MainModule?.FileName; } catch { }
                        if (exe == null || !string.Equals(exe, state.EngineExePath, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            proc.Kill(entireProcessTree: true);
                            TunManager.Log($"Recovery killed orphan engine (path scan) pid={proc.Id}.");
                            killed = true;
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        if (killed) WaitAdapterGone(state.AdapterName);
    }

    private static bool IsOurEngineAlive(TunState state)
    {
        if (state.EnginePid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(state.EnginePid);
            if (p.HasExited) return false;
            if (state.EngineStartTicks != 0)
            {
                try
                {
                    if (Math.Abs(p.StartTime.ToFileTimeUtc() - state.EngineStartTicks) > TimeSpan.TicksPerSecond * 10)
                        return false;
                }
                catch { }
            }
            try
            {
                string? exe = p.MainModule?.FileName;
                if (exe != null && !string.Equals(exe, state.EngineExePath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { return false; }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch { return false; }
    }

    private static void WaitAdapterGone(string adapterName)
    {
        long deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline)
        {
            if (IpRoute.GetIfIndex(adapterName) == 0)
            {
                TunManager.Log($"Recovery: adapter {adapterName} removed.");
                return;
            }
            Thread.Sleep(200);
        }
        if (IpRoute.GetIfIndex(adapterName) != 0)
            TunManager.Log($"Recovery: adapter {adapterName} still present.");
    }
}
