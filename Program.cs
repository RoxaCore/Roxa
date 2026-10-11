using Avalonia;
using System.Threading;

namespace Roxa;

sealed class Program
{
    private const string MutexName = "Roxa唯一的实例";
    private const string ShowSignalName = "RoxaShowSignal";
    private const string ExitSignalName = "RoxaExitSignal";

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
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
            }
            catch { }
            mutex?.Dispose();
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
