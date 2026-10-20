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
using System.Runtime.InteropServices;
using Roxa.Localization;

namespace Roxa.Core;

public static class TunManager
{
    private const string TunAdapterName = "roxa0";
    private const int CaptureTimeoutMs = 15000;
    private const int CaptureRetryDelayMs = 300;
    private const int ResolveTimeoutMs = 2500;
    // Every how many seconds to check the gateway of the capture routes, and how many
    // stability ticks are needed to accept a new gateway (a card that is going down still has a low-metric route for a few seconds).
    private const int GatewayWatchEvery = 5;
    private const int GatewayStableTicks = 2;

    private static readonly object _routesLock = new();
    private static readonly List<TunRouteRecord> _routes = new();

    private static string? _captureVia;
    private static int _captureGen;
    private static Timer? _gatewayTimer;
    private static string? _watchCandidate;
    private static int _watchStreak;
    private static int _watchScanning;

    private static readonly (string Prefix, string Dest, string Mask)[] _capturePrefixes =
    [
        ("0.0.0.0/1", "0.0.0.0", "128.0.0.0"),
        ("128.0.0.0/1", "128.0.0.0", "128.0.0.0"),
    ];

    /// <summary>
    /// The static address of the TUN adapter. Without a defined address Wintun picks
    /// an APIPA (169.254.x.x) address itself, and then Windows treats 169.254.255.255
    /// as "broadcast of the adapter's own subnet" and drops all NBNS/LLMNR/mDNS —
    /// without even consulting the route table — straight into the tunnel. That is why
    /// no exception route helped: this decision is made from the adapter address, not
    /// from the route. In one run, 16309 NBNS packets on port 137 were logged in a
    /// single minute and xray's memory climbed to 941 MB. With a static address the
    /// adapter is no longer link-local and those broadcasts are never generated.
    /// v2rayN does the same thing (its adapter singbox_tun has address 172.19.0.1/30).
    /// The 198.18.0.0/15 range is reserved for network benchmarking (RFC 2544) and is
    /// not used on real networks; /30 also means the adapter has only one neighbor.
    /// </summary>
    private const string TunAddress = "198.18.0.1";
    private const string TunAddressMask = "255.255.255.252";
    private static int _tunAddressSet;

    /// <summary>
    /// Sets the static address on the TUN adapter. Runs only once per connection;
    /// if the adapter has just come up, netsh may fail to answer once or twice,
    /// so we retry briefly.
    /// </summary>
    private static bool EnsureTunAddress()
    {
        if (Interlocked.Exchange(ref _tunAddressSet, 1) == 1) return true;
        string address = PickTunAddress();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int code = RunNetsh($"interface ipv4 set address name=\"{TunAdapterName}\" static {address} {TunAddressMask}");
            if (code == 0)
            {
                Log($"TUN address set to {address}/30 on {TunAdapterName}.");
                return true;
            }
            Thread.Sleep(150);
        }
        Log($"Could not set TUN address on {TunAdapterName}; it may fall back to APIPA (169.254.x.x) and attract broadcasts.");
        return false;
    }

    /// <summary>
    /// Returns the default address unless another adapter already occupies that spot;
    /// then it returns the first free /30 inside the same /15.
    /// Reason: 198.18.0.1 is exactly the default TUN address in clash/mihomo
    /// (their default fake-ip-range is 198.18.0.0/16 and the TUN address is also
    /// taken from there). If the user has clash running, the two adapters get the
    /// same IP and routing breaks. Any address chosen here stays inside
    /// 198.18.0.0/15, so the meaning of PrivateIpCidrs and the link-local broadcast
    /// prevention remain untouched. If nothing is taken or the route table was not
    /// available, exactly the current behavior is returned.
    /// </summary>
    private static string PickTunAddress()
    {
        try
        {
            var table = IpRoute.GetTable();
            if (table == null || table.Count == 0) return TunAddress;

            uint tunIf = IpRoute.GetIfIndex(TunAdapterName);
            // ParseIp and the MIB values are both in byte-swapped form (the same
            // convention MatchesRecord works with), so a numeric comparison with
            // < and > is meaningless: for example 192.168.1.0 appears "greater"
            // than 198.18.0.0 in this view and the range filter would be wrong.
            // Range membership must be tested with the mask. The /15 mask in bytes
            // is 255.254.0.0, not 255.255.0.0, because the range covers 198.18 and 198.19.
            uint base15 = IpRoute.ParseIp("198.18.0.0");
            uint mask15 = IpRoute.ParseIp("255.254.0.0");
            uint m30 = IpRoute.ParseIp("255.255.255.252");

            // Routes that sit inside our range and do not belong to the TUN itself.
            var taken = new List<(uint Net, uint Mask)>();
            foreach (var row in table)
            {
                if (tunIf != 0 && row.IfIndex == tunIf) continue;
                uint net = row.Dest & row.Mask;
                if ((net & mask15) != base15) continue;
                taken.Add((net, row.Mask));
            }
            if (taken.Count == 0) return TunAddress;

            // The whole /15 means 198.18 and 198.19; every four addresses make a /30.
            for (uint second = 18; second <= 19; second++)
            {
                for (uint third = 0; third < 256; third += 4)
                {
                    uint cand = IpRoute.ParseIp($"198.{second}.{third}.0");
                    bool busy = false;
                    foreach (var (net, mask) in taken)
                    {
                        // Two subnets overlap only when one is inside the other.
                        if ((net & m30) == cand || (cand & mask) == net) { busy = true; break; }
                    }
                    if (busy) continue;

                    string addr = IpRoute.IpToString(cand | 1u);
                    if (addr == TunAddress) return TunAddress;
                    Log($"TUN address {TunAddress} is already in use on this machine; using {addr} instead.");
                    return addr;
                }
            }
        }
        catch { }
        // No free spot was found (or some error occurred): the default address.
        return TunAddress;
    }

    private static readonly string _wintunDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine");
    private static readonly string _wintunPath = Path.Combine(_wintunDirectory, "wintun.dll");
    private static readonly string _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

    // Since version 0.14.1 wintun publishes only a single bundle that contains the DLL
    // for all four architectures (amd64/x86/arm64/arm). The old separate URLs like
    // wintun-amd64-0.14.1.zip were removed from the site and both return 404; the old
    // code fetched that dead URL and hashed one file against the arch-specific package,
    // so it always failed and TUN mode silently fell back to proxy.
    private const string WintunUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";

    // The hash published on the official site for that single file; correct for both architectures.
    private const string WintunSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

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
            string zipPath = Path.Combine(WintunDirectory, "wintun.zip");

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(WintunUrl);

            string actual = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(data)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, WintunSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log($"wintun hash mismatch: expected {WintunSha256}, got {actual}");
                log?.Invoke(TranslationManager.Get("Tun_HashBad"));
                return false;
            }

            await File.WriteAllBytesAsync(zipPath, data);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, WintunDirectory, true);
            try { File.Delete(zipPath); } catch { }

            // The single bundle contains all four architectures; we must pick this system's architecture.
            if (!File.Exists(_wintunPath) && !PickWintunDllForThisArch())
            {
                Log($"wintun archive contained no DLL for {RuntimeInformation.OSArchitecture}.");
                return false;
            }

            CleanupExtractedWintunTree();
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

    /// <summary>
    /// Copies the build matching this system's architecture out of all the extracted
    /// wintun.dll files into engine\wintun.dll. It does not depend on the exact package
    /// layout: it looks for the file name and the architecture folder name, so it is
    /// correct for today's package and keeps working if the internal layout changes.
    /// </summary>
    private static bool PickWintunDllForThisArch()
    {
        try
        {
            string fullTarget = Path.GetFullPath(_wintunPath);
            var dlls = Directory.GetFiles(WintunDirectory, "wintun.dll", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFullPath(f), fullTarget, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (dlls.Length == 0) return false;

            string want = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string sep = Path.DirectorySeparatorChar.ToString();
            string? chosen =
                dlls.FirstOrDefault(f => f.Contains($"{sep}{want}{sep}", StringComparison.OrdinalIgnoreCase))
                ?? dlls.FirstOrDefault(f => f.Contains(want, StringComparison.OrdinalIgnoreCase))
                ?? (dlls.Length == 1 ? dlls[0] : null);
            if (chosen == null) return false;

            File.Copy(chosen, _wintunPath, true);
            Log($"wintun.dll picked for {RuntimeInformation.OSArchitecture}: {Path.GetRelativePath(WintunDirectory, chosen)}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"wintun dll pick failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The final file has been copied; the extracted tree (bin\amd64\… and wintun.h) is
    /// no longer needed. We only delete what the package itself just created.
    /// </summary>
    private static void CleanupExtractedWintunTree()
    {
        try
        {
            string bin = Path.Combine(WintunDirectory, "bin");
            if (Directory.Exists(bin)) Directory.Delete(bin, true);
            foreach (var header in Directory.GetFiles(WintunDirectory, "wintun.h"))
                File.Delete(header);
        }
        catch { /* best effort; the stray file is only clutter and does no harm */ }
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

            // Run route print in the background so the UI thread does not freeze during the TUN connection.
            string? via = await Task.Run(GetDefaultGatewayVia);
            long tGateway = Environment.TickCount64;
            if (via == null)
            {
                Log("No default gateway found; capture routes skipped.");
                return false;
            }

            // Resolve all the given hosts (previously only the first one was taken into
            // account and the rest of the server addresses were lost under the TUN).
            var hostList = hosts?.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            var serverIps = await ResolveServerIpsAsync(hostList == null || hostList.Count == 0 ? null : hostList);
            long tDns = Environment.TickCount64;

            // Each AddRoute runs route.exe twice (delete+add); with dozens of servers that is
            // dozens of processes. LongRunning so the Thread-Pool threads are not blocked
            // during the few seconds of concurrent route.exe runs and UI updates are not delayed.
            await Task.Factory.StartNew(() =>
            {
                foreach (var ip in serverIps)
                {
                    AddRoute($"add {ip} mask 255.255.255.255 {via}");
                    string? net24 = Subnet24(ip);
                    if (net24 != null) AddRoute($"add {net24} mask 255.255.255.0 {via}");
                }
                AddRoute($"add 10.0.0.0 mask 255.0.0.0 {via}");
                AddRoute($"add 172.16.0.0 mask 255.240.0.0 {via}");
                AddRoute($"add 192.168.0.0 mask 255.255.0.0 {via}");
                // Link-local. The TUN adapter has a 169.254.x.x address, so Windows was
                // sending the broadcast of that same subnet (169.254.255.255 on ports
                // 138/1947) into the tunnel; in one run 2321 packets were logged in 8
                // seconds and xray wrote one log line for each. TunExclusion.IsPrivate
                // treats this range as local, but it was missing here.
                AddRoute($"add 169.254.0.0 mask 255.255.0.0 {via}");
                // Limited broadcast and multicast. The tunnel routes (0.0.0.0/1 +
                // 128.0.0.0/1) cover the entire IPv4 space, so these two also fell into
                // the tunnel. That is why adding 169.254.0.0/16 was not enough: half of
                // the 3327-packet storm on port 1947 was destined for 255.255.255.255. I
                // found the sender — hasplms.exe, the HASP/SafeNet license service, which
                // broadcasts every few seconds to find a USB dongle and keeps doing so
                // even when no dongle is plugged in. Each packet created a "UDP
                // connection" in xray; its memory climbed to 911 MB, the CPU burned, and
                // Windows' ephemeral port range was exhausted (Tcpip event 4266). The
                // 224.0.0.0/4 route is added for the same reason; v2rayN also has both of
                // these on its own adapter.
                AddRoute($"add 255.255.255.255 mask 255.255.255.255 {via}");
                AddRoute($"add 224.0.0.0 mask 240.0.0.0 {via}");
            }, TaskCreationOptions.LongRunning);
            long tExcl = Environment.TickCount64;

            await Task.Run(() =>
            {
                RunRoute("delete 0.0.0.0 mask 128.0.0.0");
                RunRoute("delete 128.0.0.0 mask 128.0.0.0");
            });

            if (!await WaitCaptureRoutesAsync())
            {
                Log($"TUN adapter {TunAdapterName} did not appear in time; capture routes skipped.");
                await Task.Factory.StartNew(RemoveCaptureRoutes, TaskCreationOptions.LongRunning);
                return false;
            }
            long tCapture = Environment.TickCount64;

            Log($"Route setup: via={via} gateway={tGateway - t0} resolve={tDns - tGateway} excl={tExcl - tDns} capture={tCapture - tExcl} total={tCapture - t0}ms excluded={serverIps.Count}");
            Log($"Capture routes added: {serverIps.Count} server ip(s) excluded.");
            StartGatewayWatch(via);
            return true;
        }
        catch (Exception ex)
        {
            Log($"AddCaptureRoutes failed: {ex.Message}");
            await Task.Factory.StartNew(RemoveCaptureRoutes, TaskCreationOptions.LongRunning);
            return false;
        }
    }

    public static bool RemoveCaptureRoutes()
    {
        StopGatewayWatch();
        _captureVia = null;
        // The adapter goes away when the connection drops; we reset the flag too so
        // the static address is set again on the next connection.
        Interlocked.Exchange(ref _tunAddressSet, 0);
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
            // Until the roxa0 adapter comes up, netsh spawns processes in vain (2 per round
            // every 300ms) and it also held the UI thread behind this same wait; first the
            // native GetIfIndex checks, and routes are only added after the adapter appears.
            if (IpRoute.GetIfIndex(TunAdapterName) == 0)
            {
                if (Environment.TickCount64 >= deadline) return false;
                await Task.Delay(CaptureRetryDelayMs);
                continue;
            }
            // The adapter came up. Before adding the routes we set the static address so
            // it does not go link-local; this runs only once per connection.
            await Task.Run(EnsureTunAddress);
            bool allAdded = true;
            foreach (var (prefix, dest, mask) in _capturePrefixes)
            {
                // netsh also wants its own process; it runs in the background.
                bool ok = await Task.Run(() => RunNetsh($"interface ipv4 add route prefix={prefix} interface={TunAdapterName}") == 0);
                if (ok)
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

    /// <summary>Last gateway that appeared in the log; so the every-5-seconds watch does not write a duplicate line.</summary>
    private static string? _lastLoggedGateway;

    public static string? GetDefaultGatewayVia()
    {
        try
        {
            // The physical NIC's gateway takes priority: when another TUN (e.g. v2rayN) is
            // active, the lowest-metric default route belongs to it and previously excl/test
            // would enter the foreign TUN; it must go straight to the real gateway.
            string? physical = FindPhysicalDefaultGateway();
            if (physical != null)
            {
                LogGatewayOnce(physical, "physical");
                return physical;
            }

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
            LogGatewayOnce(via, $"metric={bestMetric}");
            return via;
        }
        catch (Exception ex)
        {
            Log($"GetDefaultGatewayVia failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Log the gateway only when the value really changed. The every-few-seconds watch
    /// used to write that one "Default gateway via=… (physical)" line unconditionally —
    /// up to 18 times a minute, for hours, while nothing had actually changed.
    /// </summary>
    private static void LogGatewayOnce(string via, string detail)
    {
        lock (_routesLock)
        {
            if (string.Equals(_lastLoggedGateway, via, StringComparison.Ordinal)) return;
            _lastLoggedGateway = via;
        }
        Log($"Default gateway via={via} ({detail})");
    }

    /// <summary>
    /// The default gateway that really belongs to a physical NIC: a 0.0.0.0/0 route with a
    /// non-zero NextHop, excluding TUN adapters (name/description), and exactly the gateway registered on that adapter.
    /// </summary>
    private static string? FindPhysicalDefaultGateway()
    {
        var table = IpRoute.GetTable();
        if (table == null) return null;
        var defaults = table
            .Where(r => r.Dest == 0 && r.Mask == 0 && r.NextHop != 0)
            .OrderBy(r => r.Metric1)
            .ToList();
        if (defaults.Count == 0) return null;

        var nics = new Dictionary<uint, NetworkInterface>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
            catch { }
        }

        foreach (var row in defaults)
        {
            if (!nics.TryGetValue(row.IfIndex, out var nic)) continue;
            if (IsTunLike(nic.Name) || IsTunLike(nic.Description)) continue;
            string hop = IpRoute.IpToString(row.NextHop);
            bool ownsGateway;
            try
            {
                ownsGateway = nic.GetIPProperties().GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    string.Equals(g.Address.ToString(), hop, StringComparison.Ordinal));
            }
            catch { continue; }
            if (ownsGateway) return hop;
        }
        return null;
    }

    /// <summary>
    /// Is there still a 0.0.0.0/0 route with this next-hop on a non-TUN network card?
    /// Used to detect whether the gateway of the pinned "TUN exclusion" routes is still
    /// alive, or the adapter has changed and those routes have become black holes.
    /// Windows keeps /32 routes but does not remove the dead next-hop.
    /// When detection fails, the gateway is assumed alive so the routes are not cleared for nothing.
    /// </summary>
    public static bool IsPhysicalGatewayPresent(string gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway)) return false;
        try
        {
            var table = IpRoute.GetTable();
            if (table == null) return true;
            var nics = new Dictionary<uint, NetworkInterface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
                catch { }
            }
            foreach (var row in table)
            {
                if (row.Dest != 0 || row.Mask != 0 || row.NextHop == 0) continue;
                if (!nics.TryGetValue(row.IfIndex, out var nic)) continue;
                if (IsTunLike(nic.Name) || IsTunLike(nic.Description)) continue;
                if (string.Equals(IpRoute.IpToString(row.NextHop), gateway, StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Starts watching the gateway of the capture routes. These routes (the VPN server
    /// and internal networks) depend on the physical gateway; if the adapter or the
    /// internet changes, the next-hop stays dead and the VPN server becomes unreachable. Previously they were only rebuilt at startup.
    /// </summary>
    private static void StartGatewayWatch(string via)
    {
        lock (_routesLock)
        {
            _captureVia = via;
            _captureGen++;
            _watchCandidate = null;
            _watchStreak = 0;
            if (_gatewayTimer != null) return;
            _gatewayTimer = new Timer(_ => WatchGateway(), null,
                GatewayWatchEvery * 1000, GatewayWatchEvery * 1000);
        }
        Log($"capture gateway watcher armed (via={via}).");
    }

    private static void StopGatewayWatch()
    {
        Timer? t;
        lock (_routesLock)
        {
            // The generation number goes up so that if a route re-apply is mid-flight,
            // it deletes its own fresh routes itself and nothing is left behind after the disconnect.
            _captureGen++;
            t = _gatewayTimer;
            _gatewayTimer = null;
            _watchCandidate = null;
            _watchStreak = 0;
            // So the gateway shows up in the log again on the next connection.
            _lastLoggedGateway = null;
        }
        t?.Dispose();
    }

    private static void WatchGateway()
    {
        if (Interlocked.Exchange(ref _watchScanning, 1) == 1) return;
        try
        {
            string? via;
            bool active;
            int gen;
            lock (_routesLock) { via = _captureVia; active = _routes.Count > 0; gen = _captureGen; }
            if (!active || via == null)
            {
                StopGatewayWatch();
                return;
            }

            string? gw = GetDefaultGatewayVia();
            if (gw == null) return;   // adapter switching; don't touch anything while we still have a valid gateway.

            if (gw == via)
            {
                // GetDefaultGatewayVia read the gateway from the physical card itself, so
                // its presence is proven; calling IsPhysicalGatewayPresent here would only
                // be a redundant scan of the network cards and was deliberately removed.
                lock (_routesLock) { _watchCandidate = null; _watchStreak = 0; }
                return;
            }

            // Only when the gateway has changed do we need to find out whether the old one
            // has actually left; "gone" means rebuild immediately, otherwise wait two stable ticks.
            bool dead = !IsPhysicalGatewayPresent(via);

            if (!dead)
            {
                // The candidate gateway must be stable for two ticks in a row; a card that
                // is going down still has a low-metric default route for a few seconds.
                lock (_routesLock)
                {
                    if (!string.Equals(_watchCandidate, gw, StringComparison.Ordinal))
                    {
                        _watchCandidate = gw;
                        _watchStreak = 1;
                        return;
                    }
                    if (++_watchStreak < GatewayStableTicks) return;
                }
            }

            Log($"capture gateway {via} -> {gw} ({(dead ? "old gateway is gone" : "changed")}); re-applying routes");
            ReapplyCaptureRoutes(gw, gen);
        }
        catch (Exception ex)
        {
            Log($"capture gateway watch failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _watchScanning, 0);
        }
    }

    /// <summary>
    /// Rebuilds all capture routes with the new gateway. Deleting the old route must
    /// happen <b>before</b> adding the new one; CreateIpForwardEntry treats the "already
    /// exists" error as success, otherwise the dead next-hop stays in place.
    /// </summary>
    private static void ReapplyCaptureRoutes(string newVia, int gen)
    {
        List<TunRouteRecord> old;
        lock (_routesLock)
        {
            if (gen != _captureGen) return;   // the connection dropped in the meantime
            old = _routes.ToList();
        }
        if (old.Count == 0) return;

        var fresh = new List<TunRouteRecord>();
        foreach (var rec in old)
        {
            // Delete and add for every route, even when the new gateway equals the old one;
            // the system may have removed the route itself mid-adapter-swap and
            // "continue without adding" would lose it forever.
            try { RunRoute($"delete {rec.Dest} mask {rec.Mask} {rec.Via}"); }
            catch { /* it may already be gone */ }
            try
            {
                if (!IpRoute.TryAddRoute(rec.Dest, rec.Mask, newVia, IpRoute.GetIfIndexForIp(newVia)))
                {
                    int code = RunRoute($"add {rec.Dest} mask {rec.Mask} {newVia}");
                    if (code != 0) throw new InvalidOperationException($"route add exited with {code}");
                }
                fresh.Add(new TunRouteRecord { Dest = rec.Dest, Mask = rec.Mask, Via = newVia });
            }
            catch (Exception ex) { Log($"capture re-route failed for {rec.Dest}: {ex.Message}"); }
        }

        bool raced;
        lock (_routesLock)
        {
            raced = gen != _captureGen;
            if (!raced)
            {
                _routes.Clear();
                _routes.AddRange(fresh);
                _captureVia = newVia;
                _watchCandidate = null;
                _watchStreak = 0;
            }
        }
        if (raced)
        {
            // The disconnect happened concurrently; clear our own fresh routes too so that
            // after exit the dead-gateway route does not linger on the system. (outside the lock; route.exe is slow)
            foreach (var r in fresh)
                try { RunRoute($"delete {r.Dest} mask {r.Mask} {r.Via}"); }
                catch { }
            return;
        }
        Log($"capture routes moved to gateway {newVia} ({fresh.Count}/{old.Count} route(s)).");
    }

    private static bool IsTunLike(string s) =>
        s.Contains("tun", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("tap", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("wireguard", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("roxa", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("sing", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("mihomo", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("clash", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("neko", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is there currently a default route belonging to a TUN (foreign or ours)?
    /// Used to decide "whether we need to bypass for fetch/test".</summary>
    public static bool AnyTunDefaultRoute()
    {
        try
        {
            var table = IpRoute.GetTable();
            if (table == null) return false;
            var nics = new Dictionary<uint, NetworkInterface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try { uint idx = IpRoute.GetIfIndex(nic.Name); if (idx != 0) nics[idx] = nic; }
                catch { }
            }
            foreach (var row in table)
            {
                if (row.Dest != 0 || row.Mask != 0) continue;
                if (nics.TryGetValue(row.IfIndex, out var nic) &&
                    (IsTunLike(nic.Name) || IsTunLike(nic.Description))) return true;
            }
            return false;
        }
        catch { return false; }
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

        // Native API first, without spawning a single process; 195 route.exe processes per TUN connection took ≈ 15 seconds.
        // (only for the exception routes with an explicit gateway; the rest of the routes still go through route.exe)
        if (via != null && IpRoute.TryAddRoute(dest, mask, via, IpRoute.GetIfIndexForIp(via)))
        {
            lock (_routesLock) _routes.Add(new TunRouteRecord { Dest = dest, Mask = mask, Via = via });
            return;
        }

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

    /// <summary>
    /// The gateway of the /32 route currently on the system for this IP; null means no route exists.
    ///
    /// Needed to preserve the user's pre-existing route. The /32 destination route is
    /// unique, so to place our own route we must delete the previous one — and if we
    /// did not know something was there, we never restore it.
    /// </summary>
    public static string? GetHostRouteVia(string ip)
    {
        try
        {
            if (!System.Net.IPAddress.TryParse(ip, out _)) return null;
            if (RunTool("route.exe", $"print -4 {ip}", out string output) != 0) return null;
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (parts[0] != ip || parts[1] != "255.255.255.255") continue;
                // The third column is the gateway. It may be "On-link", which cannot be
                // rebuilt via route.exe; we return exactly that so its owner can decide.
                return parts[2];
            }
        }
        catch { }
        return null;
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

        // We read both streams in parallel; sequential blocking sync reads can block the
        // entire caller forever once the process's error buffer fills up.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        bool exited = proc.WaitForExit(5000);
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            try { proc.WaitForExit(2000); } catch { }
        }
        stdout = Collect(outTask);
        Collect(errTask);
        return exited ? proc.ExitCode : -1;
    }

    private static string Collect(Task<string> task)
    {
        try
        {
            return task.Wait(3000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly object _logGate = new();
    private static readonly System.Text.StringBuilder _logBuf = new();
    private static System.Threading.Timer? _logTimer;
    private const int LogFlushThreshold = 16 * 1024;
    private const long LogMaxBytes = 2L * 1024 * 1024;

    /// <summary>
    /// Buffers the tunnel log. Previously every call did one Directory.CreateDirectory
    /// and one full File.AppendAllText (open/close the file, an antivirus scan every
    /// time); now it runs at most once a second and in a single write. If the buffer
    /// grows large we write immediately so nothing is lost during heavy-log periods.
    /// </summary>
    public static void Log(string message)
    {
        try
        {
            bool flushNow;
            lock (_logGate)
            {
                _logBuf.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                       .Append("] ").Append(message).Append(Environment.NewLine);
                if (_logTimer == null)
                    _logTimer = new Timer(_ => FlushLog(), null, 1000, 1000);
                flushNow = _logBuf.Length >= LogFlushThreshold;
            }
            if (flushNow) FlushLog();
        }
        catch { }
    }

    /// <summary>
    /// Writes the whole buffer at once and rotates the log file. Calling it several times is safe;
    /// it is also invoked on the exit path so the last lines are on disk before the process ends.
    /// </summary>
    public static void FlushLog()
    {
        string? chunk = null;
        try
        {
            lock (_logGate)
            {
                if (_logBuf.Length == 0) return;
                chunk = _logBuf.ToString();
            }

            string logFile = Path.Combine(ResolveLogDir(), "tun.log");
            try
            {
                var fi = new FileInfo(logFile);
                if (fi.Exists && fi.Length + chunk.Length > LogMaxBytes)
                    File.Move(logFile, logFile + ".old", true);
            }
            catch { }
            File.AppendAllText(logFile, chunk);

            // Remove from the buffer only after the write really happened. The old code emptied
            // the buffer first and wrote afterwards; a disk error right then lost those lines forever.
            lock (_logGate)
            {
                bool stillThere = _logBuf.Length >= chunk.Length
                    && string.CompareOrdinal(_logBuf.ToString(0, chunk.Length), chunk) == 0;
                if (stillThere) _logBuf.Remove(0, chunk.Length);
            }
        }
        catch
        {
            // The write failed (unwritable folder or full disk). The next flush retries;
            // but if it grows without bound it would choke the program, so we cap it.
            lock (_logGate)
            {
                if (_logBuf.Length > LogMaxBytes)
                    _logBuf.Remove(0, _logBuf.Length - LogFlushThreshold);
            }
        }
    }

    // Installing the program under Program Files is only possible with elevation, but
    // the program itself runs asInvoker and has no write permission in its own folder.
    // Without this alternative path the whole log was silently lost — and then nobody
    // could investigate the reported bug, because that log was the only evidence.
    private static string? _logDirResolved;

    private static string FallbackLogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa", "logs");

    /// <summary>
    /// Picks the first path that is actually writable and keeps it for the rest of the
    /// run; re-scanning on every log flush is pointless overhead.
    /// </summary>
    private static string ResolveLogDir()
    {
        if (_logDirResolved != null) return _logDirResolved;
        foreach (var dir in new[] { _logDir, FallbackLogDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                _logDirResolved = dir;
                if (!string.Equals(dir, _logDir, StringComparison.OrdinalIgnoreCase))
                    Log($"install directory is not writable; logs go to {dir}");
                return dir;
            }
            catch { }
        }
        _logDirResolved = FallbackLogDir;
        return _logDirResolved;
    }

    /// <summary>The actually writable path for the logs; the engine's log also writes here.</summary>
    public static string LogDirectory => ResolveLogDir();
}
