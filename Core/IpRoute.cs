using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Roxa.Core;

public static class IpRoute
{
    private const uint NO_ERROR = 0;
    private const int RowSizeBytes = 56;

    [StructLayout(LayoutKind.Sequential)]
    public struct Row
    {
        public uint Dest;
        public uint Mask;
        public uint Policy;
        public uint NextHop;
        public uint IfIndex;
        public uint Type;
        public uint Proto;
        public uint Age;
        public uint NextHopAs;
        public uint Metric1;
        public uint Metric2;
        public uint Metric3;
        public uint Metric4;
        public uint Status;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetIpForwardTable(IntPtr pTable, ref uint pdwSize, bool bOrder);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint DeleteIpForwardEntry(ref Row row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint CreateIpForwardEntry(ref Row row);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetBestInterface(uint destIp, out uint bestIfIndex);

    public static uint ParseIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return 0;
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return 0;
        return BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
    }

    /// <summary>برعکسِ ParseIp: مقدار uint مسیر MIB را به رشتهٔ IP تبدیل می‌کند.</summary>
    public static string IpToString(uint ip) => new IPAddress(BitConverter.GetBytes(ip)).ToString();

    /// <summary>
    /// ایندکس رابط شبکه‌ای که به مقصدِ ip می‌رسد (برای گرفتن IfIndex گیت‌وی پیش‌فرض).
    /// </summary>
    public static uint GetIfIndexForIp(string ip)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        uint dest = ParseIp(ip);
        if (dest == 0) return 0;
        try
        {
            return GetBestInterface(dest, out uint ifIndex) == NO_ERROR ? ifIndex : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// افزودن مسیر با API بومی بدون هیچ پروسه‌ای؛ خطای «از قبل موجود» (5010)
    /// موفقیت حساب می‌شود. در شکست (دسترسی ادمین، مقدار نامعتبر) false و
    /// فراخواننده به route.exe برمی‌گردد.
    /// </summary>
    public static bool TryAddRoute(string dest, string mask, string via, uint gatewayIfIndex, uint metric = 0xFFFF)
    {
        if (!OperatingSystem.IsWindows() || gatewayIfIndex == 0) return false;
        uint d = ParseIp(dest), m = ParseIp(mask), v = ParseIp(via);
        if (d == 0 || m == 0 || v == 0) return false;
        var row = new Row
        {
            Dest = d,
            Mask = m,
            NextHop = v,
            IfIndex = gatewayIfIndex,
            Type = 3,   // MIB_IPROUTE_TYPE: indirect (next-hop ≠ destination)
            Proto = 3,  // MIB_IPPROTO_NETMGMT (همان مقداری که route.exe می‌گذارد)
            Metric1 = metric
        };
        try
        {
            uint err = CreateIpForwardEntry(ref row);
            return err == NO_ERROR || err == ERROR_OBJECT_ALREADY_EXISTS;
        }
        catch { return false; }
    }

    private const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;

    public static uint GetIfIndex(string name)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                var props = nic.GetIPProperties();
                try
                {
                    int index = props.GetIPv4Properties()?.Index ?? 0;
                    if (index != 0) return (uint)index;
                }
                catch { }
                try
                {
                    int index = props.GetIPv6Properties()?.Index ?? 0;
                    if (index != 0) return (uint)index;
                }
                catch { }
                return 0;
            }
        }
        catch { }
        return 0;
    }

    public static List<Row>? GetTable()
    {
        if (!OperatingSystem.IsWindows()) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint size = 0;
                GetIpForwardTable(IntPtr.Zero, ref size, false);
                if (size == 0) return null;
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal((int)size);
                uint err = GetIpForwardTable(buffer, ref size, false);
                if (err != NO_ERROR) continue;
                int count = Marshal.ReadInt32(buffer);
                var rows = new List<Row>(count);
                int offset = 4;
                for (int i = 0; i < count; i++)
                {
                    rows.Add(Marshal.PtrToStructure<Row>(IntPtr.Add(buffer, offset)));
                    offset += RowSizeBytes;
                }
                return rows;
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool MatchesRecord(TunRouteRecord rec, Row row, uint adapterIfIndex)
    {
        if (row.Dest != ParseIp(rec.Dest) || row.Mask != ParseIp(rec.Mask)) return false;
        if (rec.Via != null) return row.NextHop == ParseIp(rec.Via);
        return adapterIfIndex != 0 && row.IfIndex == adapterIfIndex;
    }

    public static int DeleteRoutes(IReadOnlyList<TunRouteRecord> records, uint adapterIfIndex)
    {
        if (!OperatingSystem.IsWindows() || records.Count == 0) return -1;
        var table = GetTable();
        if (table == null) return -1;
        int deleted = 0;
        foreach (var rec in records)
        {
            foreach (var row in table)
            {
                if (!MatchesRecord(rec, row, adapterIfIndex)) continue;
                var copy = row;
                try { if (DeleteIpForwardEntry(ref copy) == NO_ERROR) deleted++; }
                catch { }
            }
        }
        return deleted;
    }

    public static int SweepOrphanRoutes(TunState? state)
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var table = GetTable();
        if (table == null) return -1;
        string adapterName = state?.AdapterName ?? TunManager.AdapterName;
        uint adapterIfIndex = GetIfIndex(adapterName);
        int deleted = 0;
        foreach (var row in table)
        {
            bool match = adapterIfIndex != 0 && row.IfIndex == adapterIfIndex;
            if (!match && state != null)
                match = state.Routes.Any(rec => MatchesRecord(rec, row, adapterIfIndex));
            if (!match) continue;
            var copy = row;
            try { if (DeleteIpForwardEntry(ref copy) == NO_ERROR) deleted++; }
            catch { }
        }
        return deleted;
    }

    public static bool HasLeftoverRoutes(TunState? state)
    {
        if (!OperatingSystem.IsWindows()) return false;
        string adapterName = state?.AdapterName ?? TunManager.AdapterName;
        uint adapterIfIndex = GetIfIndex(adapterName);
        var table = GetTable();
        if (table == null) return adapterIfIndex != 0 || state != null;
        foreach (var row in table)
        {
            if (adapterIfIndex != 0 && row.IfIndex == adapterIfIndex) return true;
            if (state != null && state.Routes.Any(rec => MatchesRecord(rec, row, adapterIfIndex))) return true;
        }
        return false;
    }
}
