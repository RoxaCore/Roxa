using System.Runtime.InteropServices;

namespace XrayClient.Core;

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

    private static bool AddIcon()
    {
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = _uId,
            uFlags = NIF_ICON | NIF_TIP,
            hIcon = LoadIcon(iconPath),
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

    private static IntPtr LoadIcon(string path)
    {
        if (!System.IO.File.Exists(path))
            return IntPtr.Zero;
        return LoadImage(IntPtr.Zero, path, 1, 16, 16, 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public static void Cleanup()
    {
        lock (_lock)
        {
            _generation++;
            RemoveIcon();
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
