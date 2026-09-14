using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XrayClient.Core;

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

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void NotifyChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }
}
