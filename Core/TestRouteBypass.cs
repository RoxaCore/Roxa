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

using System.Net;

namespace Roxa.Core;

public sealed class TestRouteScope : IDisposable
{
    private readonly List<string> _ips = new();
    private bool _disposed;

    internal void Track(string ip) { lock (_ips) _ips.Add(ip); }

    /// <summary>Has a bypass route actually been added? (for showing a message in the log/status)</summary>
    public bool HasRoutes { get { lock (_ips) return _ips.Count > 0; } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        string[] ips;
        lock (_ips) { ips = _ips.ToArray(); _ips.Clear(); }
        // route delete is per-process, and Dispose is called on the UI thread;
        // routes are released in the background so the UI doesn't freeze for seconds.
        if (ips.Length > 0)
            Task.Factory.StartNew(() =>
            {
                foreach (var ip in ips) TestRouteBypass.Release(ip);
            }, TaskCreationOptions.LongRunning);
    }
}

public static class TestRouteBypass
{
    private sealed class Held
    {
        public string Via = "";
        public int Refs;

        // The user's own pre-existing route for this IP, if one was there before us.
        //
        // Without this, delete at add time and delete again in Release means the
        // permanent destruction of a route nobody knew existed — the user may have
        // set a fixed manual route for a specific host, and we wipe it unknowingly.
        // null means there was nothing, or something not reconstructable (e.g. On-link).
        public string? OriginalVia;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private static string? _via;
    private static long _viaAt;

    private static readonly string HeldFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roxa", "test-routes.txt");

    public static bool TestsViaTun { get; set; }

    public static bool IsBypassing => !TestsViaTun && AdminHelper.IsRunningAsAdmin() && TunManager.IsCaptureActive;

    /// <param name="forceDirect">
    /// For probing the health of the direct route (internet check): build the bypass
    /// even when «تست از مسیر تونل» is set, because the tunnel may be dead after a network change.
    /// </param>
    public static async Task<TestRouteScope> BeginAsync(IEnumerable<string?>? hosts, bool forceDirect = false)
    {
        var scope = new TestRouteScope();
        try
        {
            if ((!forceDirect && TestsViaTun) || hosts == null) return scope;
            if (!AdminHelper.IsRunningAsAdmin()) return scope;
            // Bypassing is needed when our own TUN is active or a foreign TUN (like v2rayN)
            // takes the default route; in the normal case (no TUN) the normal route is the
            // direct one and there is no need to add a route.
            if (!TunManager.IsCaptureActive && !TunManager.AnyTunDefaultRoute()) return scope;

            var wanted = new List<string>();
            foreach (var raw in hosts)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim();
                if (!wanted.Contains(h, StringComparer.OrdinalIgnoreCase)) wanted.Add(h);
            }
            if (wanted.Count == 0) return scope;

            // route print (Gateway) and the route delete/add loop are process-based. These
            // used to run on the UI thread, and every TCP ping in smart-connect/tests held
            // the UI for hundreds of milliseconds; now they run in a LongRunning background.
            string? via = await Task.Run(Gateway);
            if (via == null) return scope;

            var guard = TunManager.CaptureHostRoutes();
            var ips = await TunManager.ResolveIpsAsync(wanted);

            await Task.Factory.StartNew(() =>
            {
                foreach (var ip in ips)
                {
                    if (guard.Contains(ip) || TunExclusion.IsPinned(ip)) continue;
                    bool ours = false;
                    lock (_lock)
                    {
                        if (_held.TryGetValue(ip, out var held))
                        {
                            held.Refs++;
                            ours = true;
                        }
                        else
                        {
                            // Before touching anything, check whether the user has a
                            // route for this IP; if so, it must be restored later.
                            string? original = TunManager.GetHostRouteVia(ip);

                            TunManager.RunRoute($"delete {ip} mask 255.255.255.255");
                            if (TunManager.RunRoute($"add {ip} mask 255.255.255.255 {via}") == 0)
                            {
                                _held[ip] = new Held { Via = via, Refs = 1, OriginalVia = original };
                                SaveHeld();
                                ours = true;
                            }
                            else if (original != null)
                            {
                                // We deleted the user's route but ours failed to
                                // be created; don't silently leave it.
                                try { TunManager.RunRoute($"add {ip} mask 255.255.255.255 {original}"); } catch { }
                                TunManager.Log($"host route restore failed for {ip} -> {original}");
                            }
                        }
                    }
                    if (ours) scope.Track(ip);
                }
            }, TaskCreationOptions.LongRunning);
        }
        catch { }
        return scope;
    }

    internal static void Release(string ip)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(ip, out var held)) return;
            held.Refs--;
            if (held.Refs > 0) return;
            _held.Remove(ip);
            SaveHeld();
            if (TunManager.CaptureHostRoutes().Contains(ip)) return;
            if (TunExclusion.IsPinned(ip)) return;
            try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); } catch { }
            RestoreOriginal(ip, held.OriginalVia);
        }
    }

    /// <summary>
    /// Restores the user's own route. Only when it had a real gateway; On-link and
    /// anything else cannot be rebuilt with route.exe, and it gets logged so at
    /// least it's known what was lost.
    /// </summary>
    private static void RestoreOriginal(string ip, string? original)
    {
        if (original == null) return;
        if (!IPAddress.TryParse(original, out _))
        {
            TunManager.Log($"pre-existing host route {ip} had non-restorable gateway '{original}'");
            return;
        }
        try
        {
            if (TunManager.RunRoute($"add {ip} mask 255.255.255.255 {original}") == 0)
                TunManager.Log($"restored pre-existing host route {ip} -> {original}");
        }
        catch { }
    }

    /// <summary>
    /// Rewrites the backup file from the current state. Must be called inside _lock.
    /// </summary>
    private static void SaveHeld()
    {
        try
        {
            string? dir = Path.GetDirectoryName(HeldFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var lines = new List<string>(_held.Count);
            foreach (var kv in _held)
                lines.Add($"{kv.Key}\t{kv.Value.Via}\t{kv.Value.OriginalVia ?? ""}");
            File.WriteAllLines(HeldFile, lines);
        }
        catch { /* if it didn't write, we only lose the safe network after a crash */ }
    }

    /// <summary>
    /// Clears the /32 routes left behind by a previous run and restores the
    /// user's own route. Called both at program startup and in <c>--cleanup</c>
    /// (during uninstall).
    ///
    /// The reason is not only crashes: <see cref="TestRouteScope.Dispose"/> defers
    /// the release into the background, so if the program closes right after a test,
    /// that task never finishes and the routes stay. If the network later changes,
    /// the dead gateway remains in place and the server IP — exactly what the next
    /// connection needs — becomes unreachable.
    /// </summary>
    public static void ReleaseOrphanedHostRoutes()
    {
        string[] lines;
        try
        {
            if (!File.Exists(HeldFile)) return;
            lines = File.ReadAllLines(HeldFile);
        }
        catch { return; }

        foreach (var line in lines)
        {
            try
            {
                var parts = line.Split('\t');
                if (parts.Length == 0 || !IPAddress.TryParse(parts[0], out _)) continue;
                string ip = parts[0];
                string original = parts.Length > 2 ? parts[2] : "";

                lock (_lock)
                {
                    if (_held.ContainsKey(ip)) continue;   // alive and held right now
                }

                try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); } catch { }
                RestoreOriginal(ip, string.IsNullOrEmpty(original) ? null : original);
            }
            catch { }
        }

        // Only delete the file when nothing alive remains. Otherwise we also burn
        // the records of routes currently being held, and if we crash at that very
        // moment those routes become ownerless.
        lock (_lock)
        {
            if (_held.Count > 0)
            {
                SaveHeld();
            }
            else
            {
                try { File.Delete(HeldFile); } catch { }
            }
        }
    }

    private static string? Gateway()
    {
        long now = Environment.TickCount64;
        if (_via != null && now - _viaAt < 5000) return _via;
        _via = TunManager.GetDefaultGatewayVia();
        _viaAt = now;
        return _via;
    }
}
