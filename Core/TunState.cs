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
    /// مقدارهای خوانده‌شده از دیسک را پیش از هر استفاده‌ای قابلِ اعتماد می‌کند.
    ///
    /// این فایل در %ProgramData% (مشترک میان همهٔ کاربرانِ ماشین) و %LocalAppData%
    /// (مالکش خودِ کاربر است) نوشته می‌شود، و بخشِ بزرگی از بازیابیِ آن با
    /// دسترسیِ مدیر اجرا می‌شود. بدونِ این، هر کاربرِ غیر-مدیر می‌توانست PIDِ
    /// دلخواه بگذارد و پروسهٔ دلخواه را با دسترسیِ مدیر بکشد، یا DNS و
    /// پروکسیِ سیستم را عوض کند — یعنی ارتقایِ سطحِ دسترسیِ محلی، نه صرفاً
    /// «درستیِ داده».
    ///
    /// قاعدهٔ کلی: هیچ فیلدی از خودِ فایل برایِ تصمیمِ اعتماد خوانده نمی‌شود.
    /// مسیر و نامِ موتور همیشه با مسیرِ خودِ همین نمونه مقایسه می‌شود، و
    /// هر مقداری که به فرمان یا رجیستری می‌رود باید شکلِ معتبرِ خودش را داشته باشد.
    /// </summary>
    private static void Sanitize(TunState st)
    {
        // JSON می‌پذیرد که فیلدی را صریحاً null کند؛ باید قبل از هر Count/RemoveAll درست شود.
        st.Routes ??= new();
        st.AdapterName ??= "";

        // --- موتور: باید همان xray.exeِ همین نصب باشد ---
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

        // --- رابط ---
        if (string.IsNullOrWhiteSpace(st.AdapterName) || st.AdapterName.Length > 256 ||
            st.AdapterName.IndexOf('"') >= 0)
            st.AdapterName = TunManager.AdapterName;

        // --- پروکسیِ قبلی: به رجیستری نوشته می‌شود، پس فقط شکلِ معتبر می‌پذیریم ---
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

        // --- DNS: هر رکورد باید شاخصِ رابطِ واقعی و آدرسِ IPِ معتبر داشته باشد ---
        if (st.DnsPrev is { Count: > 0 })
        {
            int before = st.DnsPrev.Count;
            st.DnsPrev.RemoveAll(r => !IsUsableDnsRecord(r));
            if (st.DnsPrev.Count != before)
                TunManager.Log($"tun-state: dropped {before - st.DnsPrev.Count} untrusted DNS record(s).");
            if (st.DnsPrev.Count == 0) st.DnsPrev = null;
        }
        else st.DnsPrev = null;

        // --- مسیرها: مقصد، ماسک و دروازه همگی باید IP باشند ---
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
    /// IP هست؟ یک عددِ خام مثل «۱» هم IPAddress.TryParse را رد می‌کند و
    /// 0.0.0.1 می‌سازد، پس باید همان رشتهٔ ورودی برگردد تا نوشتنِ آن در
    /// netsh / route / رجیستری معنای تعیین‌شده را داشته باشد.
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
        // نامِ رابط فقط در فرمانِ netsh می‌رود؛ گیومه مرزِ آرگومان را می‌شکند.
        if (string.IsNullOrWhiteSpace(r.Alias) || r.Alias.Length > 256 || r.Alias.IndexOf('"') >= 0) return false;
        if (!r.WasStatic) return true;              // DHCP: چیزی برایِ بازگردانی ندارد
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
    /// شکلِ ProxyServer: [scheme=]host[:port] و ممکن است با گیومه مانند
    /// [::1]:8080 باشد. میزبان یا IP است یا نامِ میزبانی از حروف، عدد، نقطه و خط تیره.
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
    /// شکلِ ProxyOverride: لیستِ جداشده با «;» از نام‌های دامنه، الگوهای ستاره‌دار،
    /// <local> و شبکه‌های CIDR/wildcard. هیچ کاراکترِ شکننده‌ای نباید داشته باشد.
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
    /// %ProgramData%\Roxa را با دسترسیِ بسته می‌سازد: نوشتن فقط برای SYSTEM و
    /// Administrators، و خواندن برای گروه Users.
    ///
    /// چرا اینجا و نه در نصاب: نصاب با PrivilegesRequired=lowest اجرا می‌شود (تا
    /// خودِ برنامه در {localappdata}\Programs قابلِ نوشتن بماند) و چنین نصابی
    /// نمی‌تواند DACLِ پوشه‌ای در ProgramData را عوض کند مگر مالکش باشد. ولی
    /// خودِ برنامه در هر دو حالت می‌تواند: پوشهٔ ساختهٔ نصابِ قدیمی را همان
    /// کاربری مالک است که نصبش کرده، و مالک همیشه DACLِ خودش را عوض می‌کند؛
    /// و نمونهٔ ارتقاداشته هم اصلاً دسترسیِ لازم را دارد.
    ///
    /// فقط یک بار در هر اجرا انجام می‌شود، و اگر از قبل بسته باشد هیچ کاری
    /// نمی‌کند تا هزینهٔ خواندنِ DACL در هر ذخیره پرداخت نشود.
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
            // مالک نیستیم و ارتقاع هم نیستیم — دفعهٔ بعد یک اجرای ارتقاداشته همین کار را می‌کند.
            TunManager.Log($"ProgramData ACL not applied ({ex.GetType().Name}); an elevated run will tighten it.");
        }
    }

    /// <summary>
    /// آیا DACL از قبل به شکلِ مطلوب است؟
    ///
    /// چیزی که دنبالش می‌گردیم دقیقاً همان وضعیتِ واقعیِ این ماشین پیش از اصلاح
    /// بود: ارث‌بریِ روشن، یک قانونِ GenericAll برای CREATOR OWNER، و Write برای
    /// گروه Users.
    ///
    /// نکتهٔ ظریف اینجا این است که نباید دنبال «آیا این قانونِ نوشتن است؟» گشت:
    /// Modify و FullControl هر دو بیت‌هایِ ReadAndExecute را هم دارند، پس یک قانونِ
    /// کاملاً فاقدِ نوشتن هم با آن ماسک تطبیق می‌کند و قضاوت برعکس می‌شود.
    /// راهِ درست این است که بپرسیم «آیا این قانون چیزی جز خواندن و اجرا می‌دهد؟»
    /// — یعنی آیا بیتِ خارج از مجموعهٔ مجاز دارد.
    /// </summary>
    private static bool TightEnough(System.Security.AccessControl.DirectorySecurity security)
    {
        // AreAccessRulesProtected == true یعنی ارث‌بری قطع شده و وضعیتِ مطلوبِ ماست؛
        // false یعنی هنوز از ProgramData ارث می‌برد و باید بسته شود.
        if (!security.AreAccessRulesProtected) return false;

        // هرچه یک کاربرِ عادی می‌تواند بکند: بخواند، اجرا کند، همگام شود.
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
