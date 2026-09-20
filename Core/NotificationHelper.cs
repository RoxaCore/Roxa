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

    private static bool _visible;

    private static void EnsureVisible()
    {
        if (_visible) return;
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
        Shell_NotifyIcon(NIM_ADD, ref nid);
        _visible = true;
    }

    private static IntPtr LoadIcon(string path)
    {
        if (!System.IO.File.Exists(path))
            return IntPtr.Zero;
        return LoadImage(IntPtr.Zero, path, 1, 16, 16, 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public static void Show(string title, string message)
    {
        try
        {
            EnsureVisible();
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
        }
        catch { }
    }

    public static void ShowConnected(string serverName, string latency)
    {
        Show("Roxa", $"Connected to {serverName}\nLatency: {latency}");
    }

    public static void ShowDisconnected()
    {
        Show("Roxa", "Disconnected");
    }

    public static void ShowSmartConnect(string serverName, string latency)
    {
        Show("Roxa", $"Smart Connect: {serverName}\nLatency: {latency}");
    }

    public static void ShowError(string message)
    {
        try
        {
            EnsureVisible();
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
        }
        catch { }
    }
}
