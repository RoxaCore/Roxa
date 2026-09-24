using Avalonia;
using System;
using System.Threading;

namespace XrayClient;

sealed class Program
{
    private const string MutexName = "Roxa唯一的实例";

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--baseline" || args[0] == "--validate" || args[0] == "--speed"))
        {
            var ct = new CancellationTokenSource().Token;
            int code = args[0] == "--speed"
                ? Core.SpeedRunner.RunAsync(args, ct).GetAwaiter().GetResult()
                : Core.BaselineRunner.RunAsync(args, ct).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance is already running — exit silently
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
