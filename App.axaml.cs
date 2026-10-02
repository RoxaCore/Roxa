using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Roxa.Core;
using Roxa.Localization;
using Roxa.ViewModels;
using Roxa.Views;

namespace Roxa;

public partial class App : Application
{
    public MainWindow? MainWindow { get; private set; }
    private TrayIcon? _trayIcon;
    private NativeMenuItem _smartItem = null!;
    private NativeMenuItem _proxyItem = null!;
    private NativeMenuItem _showItem = null!;
    private NativeMenuItem _updateNoProxyItem = null!;
    private NativeMenuItem _updateProxyItem = null!;
    private NativeMenuItem _settingsItem = null!;
    private NativeMenuItem _exitItem = null!;
    private DispatcherTimer? _traySyncTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var settings = Storage.LoadSettings();
        var lang = settings.Language ?? "fa";
        TranslationManager.Instance.SetCulture(lang);
        TunRecovery.RecoverOnStartup();
        TunKillSwitch.CleanupLeftovers();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { TunKillSwitch.Shutdown(); } catch { }
            try { NotificationHelper.Cleanup(); DisposeTray(); } catch { }
            TunRecovery.EmergencyCleanup();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { TunKillSwitch.Shutdown(); } catch { }
            try { NotificationHelper.Cleanup(); DisposeTray(); } catch { }
            TunRecovery.EmergencyCleanup();
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var lang = TranslationManager.Instance.Culture.Name;
            bool isRtl = lang is "fa" or "ar";

            MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            if (!isRtl)
                MainWindow.FlowDirection = FlowDirection.LeftToRight;

            desktop.MainWindow = MainWindow;

            SetupTrayIcon();
            StartTraySync();

            desktop.ShutdownRequested += (_, _) =>
            {
                try { TunKillSwitch.Shutdown(); } catch { }
                DisposeTray();
                TunRecovery.EmergencyCleanup();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "roxa-icon.ico");
        if (!System.IO.File.Exists(iconPath)) return;

        var menu = new NativeMenu();

        _showItem = new NativeMenuItem(TranslationManager.Get("Tray_Show"));
        _showItem.Click += (_, _) => ShowMainWindow();
        menu.Add(_showItem);

        menu.Add(new NativeMenuItemSeparator());

        _smartItem = new NativeMenuItem(TranslationManager.Get("Tray_SmartConnect") + "    Ctrl+Shift+S");
        _smartItem.Click += async (_, _) =>
        {
            ShowMainWindow();
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                if (vm.IsSmartConnecting)
                    vm.CancelSmartConnect();
                else if (vm.IsConnected)
                    await vm.FindBetterServerCommand.ExecuteAsync(null);
                else
                    await vm.TraySmartConnectAsync();
            }
        };
        menu.Add(_smartItem);

        menu.Add(new NativeMenuItemSeparator());

        _proxyItem = new NativeMenuItem(TranslationManager.Get("Tray_Proxy"))
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        if (MainWindow?.DataContext is MainViewModel vmProxy)
            _proxyItem.IsChecked = vmProxy.SetSystemProxy;
        _proxyItem.Click += (_, _) =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                vm.SetSystemProxy = !vm.SetSystemProxy;
                _proxyItem.IsChecked = vm.SetSystemProxy;
            }
        };
        menu.Add(_proxyItem);

        menu.Add(new NativeMenuItemSeparator());

        _updateNoProxyItem = new NativeMenuItem(TranslationManager.Get("Tray_UpdateNoProxy"));
        _updateNoProxyItem.Click += async (_, _) =>
        {
            ShowMainWindow();
            if (MainWindow?.DataContext is MainViewModel vm)
                await vm.TrayUpdateSubscriptionsAsync(false);
        };
        menu.Add(_updateNoProxyItem);

        _updateProxyItem = new NativeMenuItem(TranslationManager.Get("Tray_UpdateProxy"));
        _updateProxyItem.Click += async (_, _) =>
        {
            ShowMainWindow();
            if (MainWindow?.DataContext is MainViewModel vm)
                await vm.TrayUpdateSubscriptionsAsync(true);
        };
        menu.Add(_updateProxyItem);

        menu.Add(new NativeMenuItemSeparator());

        _settingsItem = new NativeMenuItem(TranslationManager.Get("Tray_Settings"));
        _settingsItem.Click += (_, _) =>
        {
            ShowMainWindow();
            MainWindow?.SwitchToSettingsTab();
        };
        menu.Add(_settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        _exitItem = new NativeMenuItem(TranslationManager.Get("Tray_Exit"));
        _exitItem.Click += (_, _) => ExitApp();
        menu.Add(_exitItem);

        _trayIcon = new TrayIcon
        {
            IsVisible = true,
            Icon = new WindowIcon(iconPath),
            ToolTipText = "Roxa",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void StartTraySync()
    {
        _traySyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _traySyncTimer.Tick += (_, _) =>
        {
            if (MainWindow?.DataContext is not MainViewModel vm) return;
            // اگر ساخت آیکون تری شکست خورده باشد (مثلاً فایل ico موجود نیست) همهٔ
            // آیتم‌ها null می‌مانند و این تایمر هر ۲ ثانیه برنامه را crash می‌کرد.
            if (_trayIcon == null || _smartItem == null || _proxyItem == null) return;

            if (vm.IsSmartConnecting)
            {
                _smartItem.Header = TranslationManager.Get("Tray_Stop");
                _trayIcon.ToolTipText = $"Roxa - {TranslationManager.Get("Tray_Searching")}";
            }
            else if (vm.IsConnected)
            {
                _smartItem.Header = TranslationManager.Get("Tray_FindBetter");
                _trayIcon.ToolTipText = $"Roxa - {string.Format(TranslationManager.Get("Tray_ConnectedTo"), vm.SelectedServer?.Name)}";
            }
            else
            {
                _smartItem.Header = TranslationManager.Get("Tray_SmartConnect");
                _trayIcon.ToolTipText = "Roxa";
            }

            _proxyItem.IsChecked = vm.SetSystemProxy;
        };
        _traySyncTimer.Start();
    }

    internal void ShowMainWindow()
    {
        if (MainWindow == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            MainWindow.Show();
            MainWindow.WindowState = WindowState.Normal;
            MainWindow.Activate();
        });
    }

    internal void ExitApp()
    {
        Dispatcher.UIThread.Post(() =>
        {
            DisposeTray();
            if (MainWindow?.DataContext is MainViewModel vm)
                vm.Shutdown();
            MainWindow?.ForceClose();
        });
    }

    public void DisposeTray()
    {
        _traySyncTimer?.Stop();
        _traySyncTimer = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        try { NotificationHelper.Cleanup(); } catch { }
    }
}
