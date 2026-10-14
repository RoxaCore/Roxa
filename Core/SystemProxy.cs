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

    public static bool IsDeadLoopback(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        bool sawLoopback = false;
        foreach (var part in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string v = part;
            int eq = v.IndexOf('=');
            if (eq >= 0) v = v[(eq + 1)..];
            v = v.Trim();
            if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) v = v[7..];
            int c = v.LastIndexOf(':');
            if (c <= 0) continue;
            string host = v[..c].Trim();
            // آدرس‌های IPv6 داخل براکت می‌آیند: [::1]:8080
            if (host.Length > 1 && host[0] == '[' && host[^1] == ']') host = host[1..^1];
            if (!int.TryParse(v[(c + 1)..].Trim(), out int port) || port <= 0 || port > 65535) continue;
            if (!IsLoopbackHost(host))
            {
                // پراکسی غیر-loopback مال ما نیست (پراکسی ما همیشه 127.0.0.1 است)؛
                // پاک کردنش تنظیمات کاربر را خراب می‌کند.
                return false;
            }
            sawLoopback = true;
            if (CanConnect(host, port)) return false;
        }
        return sawLoopback;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("127.", StringComparison.Ordinal)
        || host == "::1";

    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(400)) return false;
            return client.Connected;
        }
        catch { return false; }
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
