using Avalonia;
using System;
using System.Threading;

namespace XrayClient;

sealed class Program
{
    private const string MutexName = "Roxa唯一的实例";
    private const string ShowSignalName = "RoxaShowSignal";

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

        bool waitExit = Array.Exists(args, a => a == "--wait-exit");
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

        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, static (_, _) =>
        {
            if (Application.Current is App app)
                app.ShowMainWindow();
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
