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

namespace Roxa.Core;

public static class TunRecovery
{
    public static bool LeftoverNeedsAdmin { get; private set; }
    private static int _cleanupDone;

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
            // If the state file is missing, RestoreFromState does nothing;
            // this line searches directly for a leftover 127.0.0.1 DNS (with no service on :53).
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
        // It is registered on both ProcessExit and ShutdownRequested; without this guard the whole
        // cleanup (killing the engine, removing routes, restoring DNS) ran twice, doubling the
        // exit time — the very window the next instance waits for.
        // UnhandledException calls this same method too.
        if (Interlocked.Exchange(ref _cleanupDone, 1) == 1) return;
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
        // We flush the last log lines to disk before the process ends; both buffers
        // lag by at most one second and must not be lost on crash/exit.
        try { TunManager.FlushLog(); } catch { }
        try { XrayEngine.FlushEngineLog(); } catch { }
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
                // The last defense against a tampered state file: here the real name and
                // path of the process are compared against this instance's own path, not
                // the value read from the file. Without it, writing an arbitrary PID
                // into tun-state.json would mean killing any process with admin rights.
                if (!IsOurEngineProcess(p))
                {
                    TunManager.Log($"Recovery refused to kill pid={state.EnginePid}: not this install's xray.exe.");
                }
                else
                {
                    p.Kill(entireProcessTree: true);
                    TunManager.Log($"Recovery killed orphan engine pid={state.EnginePid}.");
                    killed = true;
                }
            }
            catch (Exception ex)
            {
                TunManager.Log($"Recovery kill failed pid={state.EnginePid}: {ex.Message}");
            }
        }
        try
        {
            // The search path also comes from this very install, not from inside the file.
            string enginePath = XrayEngine.EnginePath;
            string procName = Path.GetFileNameWithoutExtension(enginePath);
            if (!string.IsNullOrEmpty(procName))
            {
                foreach (var proc in Process.GetProcessesByName(procName))
                {
                    using (proc)
                    {
                        if (proc.Id == state.EnginePid) continue;
                        string? exe = null;
                        try { exe = proc.MainModule?.FileName; } catch { }
                        if (exe == null || !string.Equals(exe, enginePath, StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>
    /// Is this process really the xray.exe of this very install?
    ///
    /// Both name and path must be readable and match. An empty or unreadable path
    /// means we could not verify, and in that case we must not kill — wrongly rejecting
    /// a healthy process is better than wrongly killing it.
    /// </summary>
    private static bool IsOurEngineProcess(Process p)
    {
        try
        {
            string enginePath = XrayEngine.EnginePath;
            if (!string.Equals(
                    p.ProcessName,
                    Path.GetFileNameWithoutExtension(XrayEngine.EngineBinary),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string? exe = p.MainModule?.FileName;
            return exe != null &&
                   string.Equals(exe, enginePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
                // Ignoring the StartTime read error is exactly the point where the
                // PID-reuse protection becomes ineffective: if the recorded process is dead
                // and Windows handed that same number to another program, here we must
                // say "no" so the innocent process is not killed. Our own engine
                // always runs as this same user and reading it never fails; and if we
                // still get rejected here, the scan below matches the full path,
                // finds and removes the real engine, so nothing is lost.
                catch { return false; }
            }
            try
            {
                string? exe = p.MainModule?.FileName;
                // Null must be rejected too: reading this very path is what proves
                // the process is our xray.exe. Without it, saying "no" is safer than "yes",
                // and the path-matching scan below will find our real engine.
                if (exe == null || !string.Equals(exe, state.EngineExePath, StringComparison.OrdinalIgnoreCase))
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
