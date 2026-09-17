using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using XrayClient.Core;
using XrayClient.Localization;
using XrayClient.ViewModels;
using XrayClient.Views;

namespace XrayClient;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var settings = Storage.LoadSettings();
        var lang = settings.Language ?? "fa";
        TranslationManager.Instance.SetCulture(lang);
        FailSafeCleanup();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = Core.Storage.LoadSettings();
            var lang = settings.Language ?? "fa";
            bool isRtl = lang is "fa" or "ar";

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            if (!isRtl)
                desktop.MainWindow.FlowDirection = FlowDirection.LeftToRight;
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