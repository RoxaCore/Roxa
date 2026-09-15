using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using XrayClient.Core;
using XrayClient.ViewModels;
using XrayClient.Views;

namespace XrayClient;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        FailSafeCleanup();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.ShutdownRequested += (_, _) =>
            {
                try
                {
                    if (AdminHelper.IsRunningAsAdmin() && TunManager.IsTunAdapterActive())
                        TunManager.CleanupTunAdapter();
                }
                catch { }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void FailSafeCleanup()
    {
        try
        {
            if (AdminHelper.IsRunningAsAdmin() && TunManager.IsTunAdapterActive())
            {
                TunManager.CleanupTunAdapter();
            }
        }
        catch { }
    }
}