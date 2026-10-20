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
using System.Text.Json;

namespace Roxa.Core;

public sealed class TunRouteRecord
{
    public string Dest { get; set; } = "";
    public string Mask { get; set; } = "";
    public string? Via { get; set; }
}

public sealed class TunState
{
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private static int _aclTried;

    public int EnginePid { get; set; }
    public long EngineStartTicks { get; set; }
    public string EngineExePath { get; set; } = "";
    public string AdapterName { get; set; } = TunManager.AdapterName;
    public List<TunRouteRecord> Routes { get; set; } = new();
    public bool TunActive { get; set; }
    public string? ProxySet { get; set; }
    public bool ProxyPrevEnable { get; set; }
    public string? ProxyPrevServer { get; set; }
    public string? ProxyPrevOverride { get; set; }
    public bool DnsApplied { get; set; }
    public List<TunDnsRecord>? DnsPrev { get; set; }
    public DateTime SavedUtc { get; set; }

    public static string ProgramDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roxa", "tun-state.json");

    public static string LocalAppDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa", "tun-state.json");

    public static TunState? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (_lock)
        {
            TunState? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var path in new[] { ProgramDataPath, LocalAppDataPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var st = JsonSerializer.Deserialize<TunState>(File.ReadAllText(path));
                    if (st == null) continue;
                    var time = File.GetLastWriteTimeUtc(path);
                    if (best == null || time > bestTime)
                    {
                        best = st;
                        bestTime = time;
                    }
                }
                catch { }
            }
            if (best != null) Sanitize(best);
            return best;
        }
    }

    public static void Save(TunState state)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            state.SavedUtc = DateTime.UtcNow;
            string json;
            try { json = JsonSerializer.Serialize(state, _jsonOptions); }
            catch { return; }
            if (!TryWrite(ProgramDataPath, json))
                TryWrite(LocalAppDataPath, json);
        }
    }

    public static void Delete()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            foreach (var path in new[] { ProgramDataPath, LocalAppDataPath })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
            }
        }
    }

    /// <summary>
    /// Makes values read from disk trustworthy before any use.
    ///
    /// This file is written to %ProgramData% (shared among all users of the
    /// machine) and %LocalAppData% (owned by the user themselves), and a large
    /// part of its recovery runs with administrative privileges. Without this,
    /// any non-admin user could set an arbitrary PID and kill an arbitrary
    /// process with admin rights, or change the system DNS and proxy — that is,
    /// local privilege escalation, not merely "data integrity".
    ///
    /// General rule: no field from the file itself is used for a trust decision.
    /// The engine path and name are always compared against this very instance's
    /// own path, and any value going into a command or the registry must have its valid form.
    /// </summary>
    private static void Sanitize(TunState st)
    {
        // JSON allows a field to be explicitly null; fix it before any Count/RemoveAll.
        st.Routes ??= new();
        st.AdapterName ??= "";

        // --- engine: it must be this install's own xray.exe ---
        string own;
        try { own = XrayEngine.EnginePath; } catch { own = ""; }
        if (own.Length == 0 ||
            st.EnginePid <= 0 ||
            !string.Equals(st.EngineExePath, own, StringComparison.OrdinalIgnoreCase))
        {
            if (st.EnginePid != 0 || st.EngineStartTicks != 0 || st.EngineExePath.Length != 0)
                TunManager.Log(
                    $"tun-state: engine record rejected (pid={st.EnginePid} path='{st.EngineExePath}'); " +
                    "it does not point at this install's xray.exe.");
            st.EnginePid = 0;
            st.EngineStartTicks = 0;
            st.EngineExePath = "";
        }

        // --- adapter ---
        if (string.IsNullOrWhiteSpace(st.AdapterName) || st.AdapterName.Length > 256 ||
            st.AdapterName.IndexOf('"') >= 0)
            st.AdapterName = TunManager.AdapterName;

        // --- previous proxy: written to the registry, so only a valid form is accepted ---
        if (st.ProxySet != null && !IsUsableProxyServer(st.ProxySet))
        {
            TunManager.Log("tun-state: ProxySet rejected (not a valid proxy address); dropping the proxy record.");
            st.ProxySet = null;
        }
        if (st.ProxyPrevServer != null && !IsUsableProxyServer(st.ProxyPrevServer))
        {
            TunManager.Log("tun-state: ProxyPrevServer rejected (not a valid proxy address); clearing it.");
            st.ProxyPrevServer = null;
            st.ProxyPrevEnable = false;
        }
        if (st.ProxyPrevOverride != null && !IsUsableProxyOverride(st.ProxyPrevOverride))
        {
            TunManager.Log("tun-state: ProxyPrevOverride rejected (not a valid bypass list); clearing it.");
            st.ProxyPrevOverride = null;
        }

        // --- DNS: each record must carry a real adapter index and a valid IP address ---
        if (st.DnsPrev is { Count: > 0 })
        {
            int before = st.DnsPrev.Count;
            st.DnsPrev.RemoveAll(r => !IsUsableDnsRecord(r));
            if (st.DnsPrev.Count != before)
                TunManager.Log($"tun-state: dropped {before - st.DnsPrev.Count} untrusted DNS record(s).");
            if (st.DnsPrev.Count == 0) st.DnsPrev = null;
        }
        else st.DnsPrev = null;

        // --- routes: destination, mask and gateway must all be IPs ---
        if (st.Routes.Count > 0)
        {
            int before = st.Routes.Count;
            st.Routes.RemoveAll(r =>
                r == null || !IsUsableIp(r.Dest) || !IsUsableIp(r.Mask) ||
                (r.Via != null && !IsUsableIp(r.Via)));
            if (st.Routes.Count != before)
                TunManager.Log($"tun-state: dropped {before - st.Routes.Count} untrusted route record(s).");
        }
    }

    /// <summary>
    /// Is it an IP? A raw number like "1" also passes IPAddress.TryParse and
    /// yields 0.0.0.1, so the input string itself must be returned so that writing it to
    /// netsh / route / the registry keeps its intended meaning.
    /// </summary>
    private static bool IsUsableIp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 45) return false;
        if (s.IndexOf('"') >= 0 || s.Any(char.IsWhiteSpace)) return false;
        if (!IPAddress.TryParse(s, out var ip)) return false;
        return string.Equals(ip.ToString(), s, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableDnsRecord(TunDnsRecord? r)
    {
        if (r == null) return false;
        if (r.IfIndex <= 0) return false;
        // The adapter name only goes into the netsh command; a quote breaks the argument boundary.
        if (string.IsNullOrWhiteSpace(r.Alias) || r.Alias.Length > 256 || r.Alias.IndexOf('"') >= 0) return false;
        if (!r.WasStatic) return true;              // DHCP: nothing to restore
        if (r.Servers.Count == 0 || r.Servers.Count > 8) return false;
        foreach (var s in r.Servers)
            if (!IsUsableIp(s)) return false;
        return true;
    }

    private static bool IsUsableProxyServer(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 400) return false;
        if (s.IndexOf('"') >= 0 || s.Any(char.IsWhiteSpace)) return false;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!IsUsableHostPort(part)) return false;
        return true;
    }

    /// <summary>
    /// ProxyServer form: [scheme=]host[:port], and it may be bracketed like
    /// [::1]:8080. The host is either an IP or a hostname of letters, digits, dots and hyphens.
    /// </summary>
    private static bool IsUsableHostPort(string v)
    {
        if (v.Length == 0 || v.Length > 260) return false;
        if (v.IndexOf('"') >= 0 || v.Any(char.IsWhiteSpace)) return false;
        string host = v;
        int eq = host.IndexOf('=');
        if (eq >= 0) host = host[(eq + 1)..];
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host[7..];
        string port = "";
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']');
            if (close <= 1) return false;
            string inside = host[1..close];
            if (!IPAddress.TryParse(inside, out _)) return false;
            port = close + 1 < host.Length && host[close + 1] == ':' ? host[(close + 2)..] : "";
            if (close + 1 < host.Length && host[close + 1] != ':') return false;
        }
        else
        {
            int c = host.LastIndexOf(':');
            if (c >= 0)
            {
                port = host[(c + 1)..];
                host = host[..c];
            }
            if (host.Length == 0 || host.Length > 255) return false;
            if (!IPAddress.TryParse(host, out _))
                foreach (char ch in host)
                    if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-')) return false;
        }
        if (port.Length == 0) return true;
        return int.TryParse(port, out int p) && p >= 1 && p <= 65535;
    }

    /// <summary>
    /// ProxyOverride form: a ";"-separated list of domain names, star patterns,
    /// <local> and CIDR/wildcard networks. It must contain no fragile characters.
    /// </summary>
    private static bool IsUsableProxyOverride(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 1024) return false;
        if (s.IndexOf('"') >= 0 || s.Any(char.IsWhiteSpace)) return false;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("<local>", StringComparison.OrdinalIgnoreCase)) continue;
            if (part.Length > 255) return false;
            foreach (char ch in part)
                if (!(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '*' or '/' or ':')) return false;
        }
        return true;
    }

    private static bool TryWrite(string path, string json)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            if (string.Equals(path, ProgramDataPath, StringComparison.OrdinalIgnoreCase))
                EnsureProgramDataAcl();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Creates %ProgramData%\Roxa with tight permissions: write only for SYSTEM and
    /// Administrators, and read for the Users group.
    ///
    /// Why here and not in the installer: the installer runs with PrivilegesRequired=lowest (so
    /// the program itself stays writable in {localappdata}\Programs), and such an installer
    /// cannot change the DACL of a ProgramData folder unless it owns it. But the
    /// program itself can in both cases: the folder created by the old installer is owned by
    /// the user who installed it, and an owner always changes their own DACL; and the
    /// elevated instance simply has the required access.
    ///
    /// It runs only once per run, and if it is already tight it does nothing,
    /// so the cost of reading the DACL is not paid on every save.
    /// </summary>
    private static void EnsureProgramDataAcl()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _aclTried, 1) == 1) return;
        string dir = Path.GetDirectoryName(ProgramDataPath)!;
        try
        {
#pragma warning disable CA1416
            var di = new DirectoryInfo(dir);
            var security = di.GetAccessControl();

            if (TightEnough(security))
            {
                TunManager.Log("ProgramData ACL verified: SYSTEM+Administrators full, Users read-only.");
                return;
            }

            security.SetAccessRuleProtection(true, false);
            foreach (System.Security.AccessControl.AuthorizationRule rule in
                     security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                security.RemoveAccessRule((System.Security.AccessControl.FileSystemAccessRule)rule);

            const System.Security.AccessControl.FileSystemRights full =
                System.Security.AccessControl.FileSystemRights.FullControl;
            const System.Security.AccessControl.FileSystemRights read =
                System.Security.AccessControl.FileSystemRights.ReadAndExecute |
                System.Security.AccessControl.FileSystemRights.Synchronize;

            AddAcl(security, new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null), full);
            AddAcl(security, new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null), full);
            AddAcl(security, new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null), read);

            di.SetAccessControl(security);
#pragma warning restore CA1416
            TunManager.Log("ProgramData ACL tightened: SYSTEM+Administrators full, Users read-only.");
        }
        catch (Exception ex)
        {
            // Not the owner and not elevated either — the next elevated run will do it.
            TunManager.Log($"ProgramData ACL not applied ({ex.GetType().Name}); an elevated run will tighten it.");
        }
    }

    /// <summary>
    /// Is the DACL already in the desired shape?
    ///
    /// What we are looking for is exactly this machine's actual pre-fix state:
    /// inheritance on, one GenericAll rule for CREATOR OWNER, and Write for the
    /// Users group.
    ///
    /// The subtlety here is that we must not ask "is this rule a write rule?":
    /// Modify and FullControl both carry the ReadAndExecute bits too, so a rule with
    /// no write at all would still match that mask and the verdict would be reversed.
    /// The right approach is to ask "does this rule grant anything beyond reading and executing?"
    /// — i.e. does it have any bit outside the allowed set.
    /// </summary>
    private static bool TightEnough(System.Security.AccessControl.DirectorySecurity security)
    {
        // AreAccessRulesProtected == true means inheritance is blocked, which is our desired state;
        // false means it still inherits from ProgramData and must be blocked.
        if (!security.AreAccessRulesProtected) return false;

        // Everything an ordinary user can do: read, execute, synchronize.
        const System.Security.AccessControl.FileSystemRights reader =
            System.Security.AccessControl.FileSystemRights.ReadAndExecute |
            System.Security.AccessControl.FileSystemRights.Synchronize;

        var sys = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var admins = new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

        bool sawSystem = false, sawAdmins = false;
        foreach (System.Security.AccessControl.AuthorizationRule rule in
                 security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
        {
            if (rule is not System.Security.AccessControl.FileSystemAccessRule fr) continue;
            if (fr.AccessControlType != System.Security.AccessControl.AccessControlType.Allow) continue;
            if (fr.IdentityReference is not System.Security.Principal.SecurityIdentifier sid) continue;
            if (sid.Equals(sys) || sid.Equals(admins))
            {
                if ((fr.FileSystemRights & System.Security.AccessControl.FileSystemRights.FullControl)
                        == System.Security.AccessControl.FileSystemRights.FullControl)
                {
                    if (sid.Equals(sys)) sawSystem = true; else sawAdmins = true;
                    continue;
                }
            }
            if ((fr.FileSystemRights & ~reader) != 0) return false;
        }
        return sawSystem && sawAdmins;
    }

    private static void AddAcl(
        System.Security.AccessControl.DirectorySecurity security,
        System.Security.Principal.SecurityIdentifier sid,
        System.Security.AccessControl.FileSystemRights rights)
        => security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            sid, rights, System.Security.AccessControl.AccessControlType.Allow));
}
