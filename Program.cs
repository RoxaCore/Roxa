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

using Avalonia;
using Avalonia.Media;
using System.Threading;

namespace Roxa;

sealed class Program
{
    // نام‌هایِ بینِ پروسه‌ای، همه در فضایِ Global.
    //
    // بدونِ `Global\`، هر سشن (Fast User Switching، RDP) نمونهٔ خودش را می‌دید و
    // کاربر با یک نصب، دو برنامهٔ همزمان داشت که هرکدام می‌خواستند TUN و پروکسیِ
    // سیستم را بگیرند.
    private const string MutexName = @"Global\Roxa唯一的实例";
    private const string ShowSignalName = @"Global\RoxaShowSignal";
    private const string ExitSignalName = @"Global\RoxaExitSignal";

    public static string[] StartupArgs = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;

        if (args.Length > 0 && (args[0] == "--baseline" || args[0] == "--validate" || args[0] == "--speed"))
        {
            var ct = new CancellationTokenSource().Token;
            int code = args[0] == "--speed"
                ? Core.SpeedRunner.RunAsync(args, ct).GetAwaiter().GetResult()
                : Core.BaselineRunner.RunAsync(args, ct).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        if (args.Length == 5 && args[0] == "--update-apply")
        {
            int appPid = int.TryParse(args[2], out var p) ? p : 0;
            int code = Core.UpdateApplier.Run(args[1], appPid, args[3], args[4]);
            Environment.Exit(code);
            return;
        }

        // پاکسازیِ هنگامِ حذفِ برنامه. نصاب این را پیش از پاک‌کردنِ فایل‌ها اجرا می‌کند.
        // بدونِ آن، اگر کاربر درست قبلِ حذف کرش کرده باشد یا اتصال باز مانده باشد،
        // DNS روی 127.0.0.1، پروکسیِ loopback، مسیرهای یتیم و قواعدِ فایروالِ
        // RoxaTunExcl_* برای همیشه رویِ سیستم می‌مانند؛ و بعد از حذفِ برنامه دیگر
        // هیچ‌کس آن‌ها را پاک نخواهد کرد — کاربر فقط می‌ماند با اینترنتِ مرده.
        // عمداً پیش از mutex: نصاب ممکن است در حالی اجرا شود که نمونه‌ای در کار نیست.
        if (Array.Exists(args, a => a == "--cleanup"))
        {
            try { Core.TunRecovery.RecoverOnStartup(); } catch { }
            try { Core.TunExclusion.CleanupLeftovers(); } catch { }
            try { Core.TunKillSwitch.CleanupLeftovers(); } catch { }
            try { Core.TestRouteBypass.ReleaseOrphanedHostRoutes(); } catch { }
            try { Core.TunRecovery.EmergencyCleanup(); } catch { }
            try { Core.TunManager.FlushLog(); } catch { }
            return;
        }

        // خروج آرام از راه دور: بدون kill کردن (kill آیکون سینی را شبح می‌کند).
        if (Array.Exists(args, a => a == "--exit"))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var remoteExit = EventWaitHandle.OpenExisting(ExitSignalName);
                    remoteExit.Set();
                }
            }
            catch { }
            return;
        }

        bool waitExit = Array.Exists(args, a => a == "--wait-exit");

        // نمونهٔ قبلی که در حالِ خروج است را صبر می‌کنیم تا واقعاً بمیرد. فقط با یک
        // حلقهٔ ثابتِ mutex نمی‌شد فهمید آیا کند است یا اصلاً نمرده؛ در حالتِ دوم
        // نمونهٔ جدید بعد از ۱۰ ثانیه بی‌صدا می‌مرد و برنامه ناپدید می‌شد.
        if (waitExit)
        {
            int waitPid = 0;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--wait-pid" && int.TryParse(args[i + 1], out int p)) waitPid = p;
            if (waitPid > 0 && waitPid != Environment.ProcessId)
            {
                try
                {
                    using var oldProc = System.Diagnostics.Process.GetProcessById(waitPid);
                    if (!oldProc.HasExited) oldProc.WaitForExit(30_000);
                }
                catch { /* پروسه از قبل رفته یا دسترسی نیست؛ mutex همین حالا آزاد است */ }
            }
        }

        Mutex? mutex = null;
        bool createdNew = false;
        int tries = waitExit ? 100 : 1;
        try
        {
            for (int i = 0; i < tries; i++)
            {
                mutex?.Dispose();
                mutex = new Mutex(true, MutexName, out createdNew);
                if (createdNew) break;
                if (i < tries - 1) Thread.Sleep(100);
            }
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }
        catch
        {
            // نمونهٔ elevated/غیر-elevated دیگری ممکن است مالک mutex باشد و دسترسی
            // ندهد؛ به‌جای کرش، مثل «نمونه موجود» رفتار کن.
            createdNew = false;
        }

        if (!createdNew)
        {
            bool handedOff = false;
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
                handedOff = true;
            }
            catch { }
            mutex?.Dispose();

            // نمونهٔ دیگری اجراست و پنجره‌اش را آوردیم — پس چیزی برای گفتن نیست.
            // ولی اگر تحویل هم نشد (مثلاً نمونهٔ متعلق به کاربرِ دیگر، یا مالکِ mutex
            // دسترسی نمی‌دهد)، کاربر دابل‌کلیک می‌زند و هیچ اتفاقی نمی‌افتد: نه
            // پنجره، نه خطا، نه حتی سرنخی برای فهمیدن چرا. این پیام دقیقاً همان
            // یک درصدِ کور است که قبلاً بی‌صدا بلعیده می‌شد.
            if (!handedOff)
                WarnInstanceBlocked();

            return;
        }

        try { Core.UpdateApplier.CleanupStaging(); } catch { }

        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, static (_, _) =>
        {
            try
            {
                if (Application.Current is App app)
                    app.ShowMainWindow();
            }
            catch { }
        }, null, -1, false);

        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, static (_, _) =>
        {
            try
            {
                if (Application.Current is App app)
                    app.ExitApp();
            }
            catch { }
        }, null, -1, false);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// وقتی نمونهٔ دیگری mutex را دارد ولی نتوانستیم سیگنالِ نمایشِ پنجره را به آن
    /// برسانیم، کاربر هیچ چیزی نمی‌بیند: نه پنجره، نه خطا، نه سرنخی. این تنها
    /// حالتِ واقعاً «کورِ» اجرای برنامه است و باید دیده شود.
    ///
    /// پیش از Avalonia بالا می‌آییم، پس به یک پیامِ سیستمی نیاز است — نه MessageBox
    /// که به WPF/WinForms وابسته باشد. همین user32 کافی است.
    /// </summary>
    private static void WarnInstanceBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string text;
            try { text = Localization.TranslationManager.Get("Msg_InstanceBlocked"); }
            catch { text = "Another instance of Roxa is already running and its window could not be brought up."; }

            const uint MB_OK = 0x0;
            const uint MB_ICONWARNING = 0x30;
            const uint MB_SETFOREGROUND = 0x10000;
            MessageBoxW(IntPtr.Zero, text, "Roxa", MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        }
        catch { /* نمایشِ پیام هم شکست خورد؛ اشکالی نیست، فقط نباید کرش کند */ }
    }

    // ExactSpelling لازم است: پیش‌فرضِ false باعث می‌شود runtime نامِ ورودی را به
    // "MessageBoxWW" تبدیل کند و پیدایش نکند — یعنی پیامِ ما بی‌صدا گم می‌شود.
    // Unicode هم لازم است چون متنِ پیام فارسی/عربی است.
    [System.Runtime.InteropServices.DllImport("user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // گلیف‌هایی که در فونتِ اصلی نیستند از اینجا پُر می‌شوند.
            //
            // Vazirmatn فقط لاتین و عربی/فارسی دارد؛ چینی، ژاپنی، کره‌ای، روسی و هندی
            // را ندارد و بدونِ این، کلِ متنِ آن زبان‌ها مربع نشان داده می‌شد (□□□).
            //
            // عمداً کام‌گذاری‌شده داخلِ خودِ FontFamily نیست: Avalonia از fallbackِ
            // چندفونتیِ درونِ FontFamily پشتیبانی نمی‌کند و آن لیست بی‌صدا دور ریخته
            // می‌شود (AvaloniaUI/Avalonia#19362). FontManagerOptions راهِ درست است:
            // برایِ هر گلیفِ ناموجود، به‌ترتیب از میانِ اینها می‌گردد و اولینی را که
            // کاراکتر را دارد برمی‌دارد. اگر فونتی رویِ سیستمِ کاربر نباشد، رد می‌شود.
            //
            // ترتیب مهم است: Segoe UI اول چون کریلیش و یونانی و لاتین را دارد؛ فونت‌های
            // CJK و هندی فقط برایِ کاراکترهایِ خودشان خوانده می‌شوند، پس فارسی و
            // انگلیسی همچنان Vazirmatn می‌مانند.
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Segoe UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft JhengHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                    new FontFallback { FontFamily = new FontFamily("Nirmala UI") },
                },
            })
            .LogToTrace();
}
