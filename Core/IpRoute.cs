using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Roxa.Core;

public static class IpRoute
{
    private const uint NO_ERROR = 0;
    private const uint IFI_INVALID_INDEX = 0xFFFFFFFF;
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
    private static extern uint DeleteIpForwardEntry(ref Row pRoute);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetIfIndexByName(string pszInterfaceName);

    public static uint ParseIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return 0;
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
            return 0;
        return BitConverter.ToUInt32(addr.GetAddressBytes(), 0);
    }

    public static uint GetIfIndex(string name)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            uint index = GetIfIndexByName(name);
            return index == IFI_INVALID_INDEX ? 0 : index;
        }
        catch { return 0; }
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
