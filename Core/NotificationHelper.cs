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

namespace Roxa.Core;

public static class NotificationHelper
{
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const int NIF_INFO = 0x10;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIIF_INFO = 0x01;
    private const int NIIF_ERROR = 0x02;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    private static readonly IntPtr _hWnd = IntPtr.Zero;
    private static readonly uint _uId = 1;
    private static readonly object _lock = new();

    private static bool _visible;
    private static int _generation;
    private const int RemoveDelayMs = 5000;

    /// <summary>وقتی false است هیچ بالونی در ناحیه اعلان ویندوز نمایش داده نمی‌شود (تنظیم کاربر).</summary>
    public static bool Enabled { get; set; } = true;

    // هندل آیکون یک بار ساخته می‌شود؛ ساختن LoadImage در هر بار نمایش پنجره،
    // بدون DestroyIcon باعث نشت GDI handle می‌شد.
    private static IntPtr _hIcon;

    private static bool AddIcon()
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = _uId,
            uFlags = NIF_ICON | NIF_TIP,
            hIcon = GetIcon(),
            szTip = "Roxa"
        };
        bool ok = Shell_NotifyIcon(NIM_ADD, ref nid);
        if (ok) _visible = true;
        return ok;
    }

    private static void RemoveIcon()
    {
        if (!_visible) return;
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = _uId
        };
        Shell_NotifyIcon(NIM_DELETE, ref nid);
        _visible = false;
    }

    private static IntPtr GetIcon()
    {
        if (_hIcon != IntPtr.Zero) return _hIcon;
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        if (!System.IO.File.Exists(iconPath)) return IntPtr.Zero;
        _hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x00000010);
        return _hIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static void Cleanup()
    {
        lock (_lock)
        {
            _generation++;
            RemoveIcon();
            if (_hIcon != IntPtr.Zero)
            {
                try { DestroyIcon(_hIcon); } catch { }
                _hIcon = IntPtr.Zero;
            }
        }
    }

    private static bool EnsureIcon()
    {
        if (_visible) return true;
        return AddIcon();
    }

    private static void ScheduleAutoRemove(int generation)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(RemoveDelayMs);
            lock (_lock)
            {
                if (_generation == generation)
                    RemoveIcon();
            }
        });
    }

    public static void Show(string title, string message)
    {
        try
        {
            if (!Enabled) return;
            lock (_lock)
            {
                if (!EnsureIcon()) return;
                _generation++;
                int generation = _generation;
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hWnd,
                    uID = _uId,
                    uFlags = NIF_INFO,
                    szInfoTitle = title,
                    szInfo = message,
                    dwInfoFlags = NIIF_INFO
                };
                Shell_NotifyIcon(NIM_MODIFY, ref nid);
                ScheduleAutoRemove(generation);
            }
        }
        catch { }
    }

    public static void ShowConnected(string serverName, string latency)
    {
        Show("Roxa", string.Format(Localization.TranslationManager.Get("Notify_Connected"), serverName, latency));
    }

    public static void ShowDisconnected()
    {
        Show("Roxa", Localization.TranslationManager.Get("Notify_Disconnected"));
    }

    public static void ShowSmartConnect(string serverName, string latency)
    {
        Show("Roxa", string.Format(Localization.TranslationManager.Get("Notify_SmartConnect"), serverName, latency));
    }

    public static void ShowError(string message)
    {
        try
        {
            if (!Enabled) return;
            lock (_lock)
            {
                if (!EnsureIcon()) return;
                _generation++;
                int generation = _generation;
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hWnd,
                    uID = _uId,
                    uFlags = NIF_INFO,
                    szInfoTitle = "Roxa",
                    szInfo = message,
                    dwInfoFlags = NIIF_ERROR
                };
                Shell_NotifyIcon(NIM_MODIFY, ref nid);
                ScheduleAutoRemove(generation);
            }
        }
        catch { }
    }
}
