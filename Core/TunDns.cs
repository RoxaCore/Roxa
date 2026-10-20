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
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;

namespace Roxa.Core;

public sealed class TunDnsRecord
{
    public int IfIndex { get; set; }
    public string Alias { get; set; } = "";
    public bool WasStatic { get; set; }
    public List<string> Servers { get; set; } = new();
}

public static class TunDns
{
    public const string StubIp = "127.0.0.1";
    public const int StubPort = 53;

    public static bool IsStubFree()
    {
        var ep = new IPEndPoint(IPAddress.Loopback, StubPort);
        try
        {
            using var l = new TcpListener(ep);
            l.Start();
            l.Stop();
        }
        catch { return false; }
        try
        {
            using var u = new UdpClient(ep);
        }
        catch { return false; }
        return true;
    }

    public static bool IsStubListening()
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Loopback, StubPort);
            using var l = new TcpListener(ep);
            l.Start();
            l.Stop();
            return false;
        }
        catch { return true; }
    }

    public static List<TunDnsRecord> Snapshot()
    {
        var list = new List<TunDnsRecord>();
        if (!OperatingSystem.IsWindows()) return list;
        foreach (var nic in NicCandidates())
        {
            try
            {
                int idx = (int)IpRoute.GetIfIndex(nic.Name);
                if (idx <= 0) continue;
                var (wasStatic, servers) = ReadNicDns(nic.Id);
                list.Add(new TunDnsRecord
                {
                    IfIndex = idx,
                    Alias = nic.Name,
                    WasStatic = wasStatic,
                    Servers = servers
                });
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Checks whether applying is possible and, if so, returns an immediate snapshot
    /// of the current DNS without having changed anything.
    ///
    /// This separation is deliberate. The old code changed the system first and then
    /// read the previous values into TunState; if the program died in that window
    /// (crash, or kill from Task Manager) nobody would be left to know what the user's
    /// DNS used to be, and the next sweep would silently make their manual static setting DHCP.
    /// </summary>
    public static List<TunDnsRecord>? PrepareApply()
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (!IsStubListening())
        {
            TunManager.Log("DNS stub not listening on 127.0.0.1:53, system DNS left untouched.");
            return null;
        }
        var prev = Snapshot();
        if (prev.Count == 0)
        {
            TunManager.Log("DNS snapshot empty, system DNS left untouched.");
            return null;
        }
        return prev;
    }

    /// <summary>
    /// Applies a previously taken snapshot to the system. The caller must have
    /// saved that snapshot themselves before this call.
    /// </summary>
    public static bool TryApply(List<TunDnsRecord> prev)
    {
        if (!OperatingSystem.IsWindows() || prev == null || prev.Count == 0) return false;

        // netsh without loading the PowerShell module: running powershell for Set-DnsClientServerAddress
        // took about 8 seconds (cold start + loading NetTCPIP) and was the biggest bottleneck of the TUN connection.
        if (TryApplyNetsh(prev))
        {
            TunManager.Log($"DNS switched to {StubIp} on {prev.Count} adapter(s).");
            return true;
        }

        // Fallback: the old powershell path if netsh fails for any reason.
        if (RunPowerShell(BuildApplyScript(prev)))
        {
            TunManager.Log($"DNS switched (powershell fallback) to {StubIp} on {prev.Count} adapter(s).");
            return true;
        }

        return false;
    }

    private static bool TryApplyNetsh(List<TunDnsRecord> records)
    {
        try
        {
            foreach (var r in records)
            {
                int code = TunManager.RunNetsh(
                    $"interface ipv4 set dnsservers name=\"{r.Alias}\" source=static address={StubIp} validate=no");
                if (code != 0)
                {
                    TunManager.Log($"netsh set dns failed on '{r.Alias}' (code {code}); powershell fallback.");
                    return false;
                }
            }
            // Equivalent of ipconfig /flushdns without an extra process
            try { DnsFlushResolverCache(); } catch { }
            return true;
        }
        catch { return false; }
    }

    private static string BuildApplyScript(List<TunDnsRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var r in records)
            sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ServerAddresses {StubIp}; ");
        sb.Append("ipconfig /flushdns | Out-Null");
        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("dnsapi.dll")]
    private static extern void DnsFlushResolverCache();

    public static void Restore(List<TunDnsRecord>? prev)
    {
        if (prev == null || prev.Count == 0 || !OperatingSystem.IsWindows()) return;
        if (TryRestoreNetsh(prev))
        {
            TunManager.Log($"DNS restored on {prev.Count} adapter(s).");
            return;
        }
        var sb = new StringBuilder();
        foreach (var r in prev)
        {
            if (r.WasStatic && r.Servers.Count > 0)
                sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ServerAddresses @({string.Join(",", r.Servers.Select(Quote))}); ");
            else
                sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ResetServerAddresses; ");
        }
        sb.Append("ipconfig /flushdns | Out-Null");
        if (RunPowerShell(sb.ToString()))
            TunManager.Log($"DNS restored (powershell fallback) on {prev.Count} adapter(s).");
        else
            TunManager.Log("DNS restore failed.");
    }

    private static bool TryRestoreNetsh(List<TunDnsRecord> records)
    {
        try
        {
            foreach (var r in records)
            {
                if (r.WasStatic && r.Servers.Count > 0)
                {
                    int code = TunManager.RunNetsh(
                        $"interface ipv4 set dnsservers name=\"{r.Alias}\" source=static address={r.Servers[0]} validate=no");
                    if (code != 0)
                    {
                        TunManager.Log($"netsh restore failed on '{r.Alias}' (code {code}); powershell fallback.");
                        return false;
                    }
                    for (int i = 1; i < r.Servers.Count; i++)
                    {
                        code = TunManager.RunNetsh(
                            $"interface ipv4 add dnsservers name=\"{r.Alias}\" address={r.Servers[i]} index={i + 1} validate=no");
                        if (code != 0) return false;
                    }
                }
                else
                {
                    int code = TunManager.RunNetsh($"interface ipv4 set dnsservers name=\"{r.Alias}\" source=dhcp");
                    if (code != 0)
                    {
                        TunManager.Log($"netsh restore-dhcp failed on '{r.Alias}' (code {code}); powershell fallback.");
                        return false;
                    }
                }
            }
            try { DnsFlushResolverCache(); } catch { }
            return true;
        }
        catch { return false; }
    }

    public static void RestoreFromState(TunState? state = null)
    {
        try
        {
            state ??= TunState.Load();
            if (state is not { DnsApplied: true }) return;
            Restore(state.DnsPrev);
            state.DnsApplied = false;
            state.DnsPrev = null;
            TunState.Save(state);
        }
        catch (Exception ex)
        {
            TunManager.Log($"DNS state restore error: {ex.Message}");
        }
    }

    /// <summary>
    /// Adapters whose manual DNS is 127.0.0.1 while the local DNS service is not
    /// reachable at all. A sign the TUN session's DNS stayed behind after a sudden
    /// exit (e.g. the app was killed); the TunState file is gone too, so a normal
    /// restore would never bring anything back. Reads and reports only; changes nothing.
    /// </summary>
    public static List<TunDnsRecord> FindDeadStubAdapters()
    {
        var list = new List<TunDnsRecord>();
        if (!OperatingSystem.IsWindows()) return list;
        if (IsStubListening()) return list;   // local DNS service is alive; so 127.0.0.1 is intentional.
        foreach (var nic in NicCandidates())
        {
            try
            {
                int idx = (int)IpRoute.GetIfIndex(nic.Name);
                if (idx <= 0) continue;
                var (wasStatic, servers) = ReadNicDns(nic.Id);
                if (!wasStatic || !servers.Contains(StubIp)) continue;
                list.Add(new TunDnsRecord { IfIndex = idx, Alias = nic.Name, WasStatic = true, Servers = servers });
            }
            catch { }
        }
        return list;
    }

    /// <summary>
    /// Returns leftover 127.0.0.1 DNS to automatic (or removes 127.0.0.1 from the list
    /// to preserve the user's other manual DNS). Does nothing without admin rights.
    /// Returns the number of repaired adapters; needsAdmin means repair was needed but rights were missing.
    /// </summary>
    public static int SweepDeadAdapters(out bool needsAdmin)
    {
        needsAdmin = false;
        var dead = FindDeadStubAdapters();
        if (dead.Count == 0) return 0;
        if (!AdminHelper.IsRunningAsAdmin())
        {
            TunManager.Log($"DNS sweep skipped, admin required ({dead.Count} stale adapter(s)).");
            needsAdmin = true;
            return 0;
        }
        // Read the real previous value from TunState instead of guessing from the
        // current list. The old guess was «keep every server except 127.0.0.1; if
        // nothing is left, DHCP». But we replace the whole list, so when the user's
        // static setting was exactly 127.0.0.1 or nothing but it remained in the
        // current list, keep came out empty and their manual setting silently became DHCP.
        var prevByIfIndex = new Dictionary<int, TunDnsRecord>();
        try
        {
            var st = TunState.Load();
            foreach (var p in st?.DnsPrev ?? new List<TunDnsRecord>())
                prevByIfIndex[p.IfIndex] = p;
        }
        catch { }

        var sb = new StringBuilder();
        foreach (var r in dead)
        {
            if (prevByIfIndex.TryGetValue(r.IfIndex, out var known) && known != null)
            {
                if (!known.WasStatic)
                {
                    // It was automatic (DHCP) before too; restore that.
                    sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ResetServerAddresses; ");
                    continue;
                }
                var original = known.Servers
                    .Where(s => !string.Equals(s, StubIp, StringComparison.OrdinalIgnoreCase)).ToList();
                if (original.Count > 0)
                {
                    sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ServerAddresses @({string.Join(",", original.Select(Quote))}); ");
                    continue;
                }
            }

            // The state file is unavailable (e.g. after uninstalling); best guess possible.
            var keep = r.Servers.Where(s => !string.Equals(s, StubIp, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keep.Count > 0)
                sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ServerAddresses @({string.Join(",", keep.Select(Quote))}); ");
            else
                sb.Append($"Set-DnsClientServerAddress -InterfaceIndex {r.IfIndex} -ResetServerAddresses; ");
        }
        sb.Append("ipconfig /flushdns | Out-Null");
        if (!RunPowerShell(sb.ToString())) return 0;
        TunManager.Log($"DNS sweep restored {dead.Count} adapter(s) from dead {StubIp}.");
        return dead.Count;
    }

    public static string[] PickUpstreamDns()
    {
        if (OperatingSystem.IsWindows())
        {
            var publicServers = new List<string>();
            var anyServers = new List<string>();
            try
            {
                foreach (var nic in NicCandidates())
                {
                    var (_, servers) = ReadNicDns(nic.Id);
                    foreach (var s in servers)
                    {
                        if (!IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (!anyServers.Contains(s)) anyServers.Add(s);
                        if (IsPublic(ip) && !publicServers.Contains(s)) publicServers.Add(s);
                    }
                }
            }
            catch { }
            if (publicServers.Count > 0) return publicServers.ToArray();
            if (anyServers.Count > 0) return anyServers.ToArray();
        }
        return new[] { "8.8.8.8", "1.1.1.1" };
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (!IPAddress.TryParse(ip.ToString(), out var v4)) return false;
        byte[] b = v4.GetAddressBytes();
        if (b[0] == 10) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 127) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        if (b[0] >= 224) return false;
        return true;
    }

    private static IEnumerable<NetworkInterface> NicCandidates()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            NetworkInterfaceType type;
            try
            {
                type = nic.NetworkInterfaceType;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (type == NetworkInterfaceType.Loopback) continue;
                if (string.Equals(nic.Name, TunManager.AdapterName, StringComparison.OrdinalIgnoreCase)) continue;
                if (nic.GetIPProperties().UnicastAddresses.Count == 0) continue;
            }
            catch { continue; }

            bool interesting;
            try { interesting = HasDefaultGateway(nic); }
            catch { interesting = false; }
            if (!interesting)
            {
                try { interesting = ReadNicDns(nic.Id).servers.Count > 0; }
                catch { interesting = false; }
            }
            if (interesting) yield return nic;
        }
    }

    private static bool HasDefaultGateway(NetworkInterface nic) =>
        nic.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

    private static (bool wasStatic, List<string> servers) ReadNicDns(string nicGuid)
    {
        var servers = new List<string>();
        bool wasStatic = false;
        string[] roots =
        {
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\",
            @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\"
        };
        foreach (var root in roots)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root + nicGuid);
                if (key == null) continue;
                string ns = key.GetValue("NameServer") as string ?? "";
                string dhcp = key.GetValue("DhcpNameServer") as string ?? "";
                if (ns.Length > 0) wasStatic = true;
                foreach (var part in (ns.Length > 0 ? ns : dhcp).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!servers.Contains(part)) servers.Add(part);
            }
            catch { }
        }
        return (wasStatic, servers);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static bool RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi);
            if (p == null) return false;

            // We drain the streams before WaitForExit; otherwise a full stdout buffer
            // stalls the process and the script fails with a pointless timeout.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                TunManager.Log("DNS powershell timed out.");
                return false;
            }
            string err;
            try { err = errTask.Wait(3000) ? errTask.Result : string.Empty; }
            catch { err = string.Empty; }
            try { outTask.Wait(1000); } catch { }
            if (p.ExitCode != 0)
            {
                TunManager.Log($"DNS powershell exit {p.ExitCode}: {err.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            TunManager.Log($"DNS powershell failed: {ex.Message}");
            return false;
        }
    }
}
