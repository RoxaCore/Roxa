using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Roxa.Core;

/// <summary>تنظیم پروکسی سیستم. فقط ویندوز؛ بقیه پلتفرم‌ها no-op.</summary>
public static class SystemProxy
{
    public static void Set(string proxy)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        key?.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key?.SetValue("ProxyServer", proxy, RegistryValueKind.String);
        key?.SetValue("ProxyOverride", "<local>;localhost;127.0.0.1;*.local", RegistryValueKind.String);
        NotifyChange();
    }

    public static void Clear()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyChange();
    }

    public static bool GetCurrent(out bool enable, out string server, out string proxyOverride)
    {
        enable = false;
        server = "";
        proxyOverride = "";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return false;
            enable = Convert.ToInt32(key.GetValue("ProxyEnable", 0) ?? 0) == 1;
            server = key.GetValue("ProxyServer") as string ?? "";
            proxyOverride = key.GetValue("ProxyOverride") as string ?? "";
            return true;
        }
        catch { return false; }
    }

    public static void Apply(bool enable, string? server, string? proxyOverride)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key == null) return;
            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
            if (server != null) key.SetValue("ProxyServer", server, RegistryValueKind.String);
            if (proxyOverride != null) key.SetValue("ProxyOverride", proxyOverride, RegistryValueKind.String);
            NotifyChange();
        }
        catch { }
    }

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void NotifyChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
}
