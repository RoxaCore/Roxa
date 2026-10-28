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
    private NativeMenuItem _notifItem = null!;
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
        var lang = settings.Language ?? Languages.DefaultCode;
        TranslationManager.Instance.SetCulture(lang);
        TunRecovery.RecoverOnStartup();
        TunKillSwitch.CleanupLeftovers();
        // The RoxaTunExcl_ rules must be swept too; SyncRules only runs while
        // TUN is connecting, and if the app crashed mid-connect these rules
        // silently slow the user's apps down until the next connection.
        try { TunExclusion.CleanupLeftovers(); } catch { }
        // /32 routes left by a previous run (crash or exit mid-test). Without
        // this, after a network change the dead gateway stays put and the
        // server IP becomes unreachable.
        try { TestRouteBypass.ReleaseOrphanedHostRoutes(); } catch { }
        // Temp config files from previous sessions (server id in the name, a
        // new one left on every subscription refresh) must be swept from here.
        try { XrayEngine.SweepTempConfigs(); } catch { }
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // Dispose the tray icon first so it cannot linger as a ghost if anything else fails.
            try { DisposeTray(); } catch { }
            try { NotificationHelper.Cleanup(); } catch { }
            try { TunKillSwitch.Shutdown(); } catch { }
            TunRecovery.EmergencyCleanup();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Crash report: the automatic part (exception + stack + recent log)
            // is queued and either sent here or on the next run. Sending depends
            // on user consent (SendUsageStats); the local write always happens.
            try { BugReport.ReportCrash(e.ExceptionObject as Exception, SafeLogTail()); }
            catch { }
            try { DisposeTray(); } catch { }
            try { NotificationHelper.Cleanup(); } catch { }
            try { TunKillSwitch.Shutdown(); } catch { }
            TunRecovery.EmergencyCleanup();
        };

        // UI errors go through here, not AppDomain. Without this handler an
        // ordinary UI exception killed the whole app — and because exiting on
        // an unhandled exception does not guarantee ProcessExit runs, it left
        // DNS, the proxy and TUN behind in the user's system. Here it is
        // logged and execution continues, so the user can disconnect cleanly.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            try { TunManager.Log($"UI exception: {e.Exception}"); }
            catch { }
            try { BugReport.ReportCrash(e.Exception, SafeLogTail()); }
            catch { }
            e.Handled = true;
        };

        // An exception in a task nobody awaited only explodes during garbage
        // collection and, per .NET behavior, kills the whole process — hours
        // later, over work the user cannot connect to. Log and neutralize it.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var agg = e.Exception;
            try { e.SetObserved(); } catch { }
            try
            {
                if (agg == null) return;
                foreach (var ex in agg.InnerExceptions)
                    TunManager.Log($"Unobserved task exception: {ex}");
            }
            catch { }
        };
    }

    /// <summary>
    /// The last 40 log lines to attach to a report. Split out because all three
    /// handlers above needed it; repeating it was three places to lose silence.
    /// </summary>
    private string SafeLogTail()
    {
        try
        {
            if (MainWindow?.DataContext is ViewModels.MainViewModel vm)
                return vm.GetLogTail(40);
        }
        catch { }
        return "";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // First install: ask for the language before any other window, so the whole
            // UI is built in it instead of opening Persian and restarting later. Closing
            // the picker without a choice exits; the next run asks again because
            // settings.json still does not exist.
            if (Storage.FirstRun)
            {
                var picker = new LanguagePickerWindow();
                picker.LanguageChosen += _ =>
                {
                    // The culture is already applied by the picker, so this main window
                    // is built in the chosen language; no restart is involved.
                    MainWindow = BuildMainWindow();
                    desktop.MainWindow = MainWindow;
                    MainWindow.Show();

                    // The tray was built with the default (English) labels while the
                    // picker was open; rebuild it so its text follows the choice too.
                    // DisposeTray also stops the sync timer, so it must be restarted.
                    try { DisposeTray(); } catch { }
                    SetupTrayIcon();
                    StartTraySync();
                };
                desktop.MainWindow = picker;
            }
            else
            {
                desktop.MainWindow = MainWindow = BuildMainWindow();
            }

            SetupTrayIcon();
            StartTraySync();

            desktop.ShutdownRequested += (_, _) =>
            {
                try { DisposeTray(); } catch { }
                try { TunKillSwitch.Shutdown(); } catch { }
                TunRecovery.EmergencyCleanup();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The main window in the currently active language. We ask the culture info,
    /// not a manual list; otherwise an RTL language like Urdu was missed and shown
    /// with an LTR layout.
    /// </summary>
    private static MainWindow BuildMainWindow()
    {
        var win = new MainWindow
        {
            DataContext = new MainViewModel(),
        };
        if (!TranslationManager.Instance.IsRightToLeft)
            win.FlowDirection = FlowDirection.LeftToRight;
        return win;
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

        _notifItem = new NativeMenuItem(TranslationManager.Get("Tray_Notifications"))
        {
            ToggleType = MenuItemToggleType.CheckBox,
        };
        if (MainWindow?.DataContext is MainViewModel vmNotif)
            _notifItem.IsChecked = vmNotif.NotificationsEnabled;
        _notifItem.Click += (_, _) =>
        {
            if (MainWindow?.DataContext is MainViewModel vm)
            {
                vm.NotificationsEnabled = !vm.NotificationsEnabled;
                _notifItem.IsChecked = vm.NotificationsEnabled;
            }
        };
        menu.Add(_notifItem);

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
            // If building the tray icon failed (e.g. the ico file is missing),
            // every item stays null and this timer crashed the app every 2s.
            if (_trayIcon == null || _smartItem == null || _proxyItem == null || _notifItem == null) return;

            // Write only when the value really changed; writing every 2s
            // refreshed the tray window and raised tray messages each time.
            if (vm.IsSmartConnecting)
            {
                SetIfChanged(ref _smartHeader, TranslationManager.Get("Tray_Stop"), v => _smartItem.Header = v);
                SetTooltip($"Roxa - {TranslationManager.Get("Tray_Searching")}");
            }
            else if (vm.IsConnected)
            {
                SetIfChanged(ref _smartHeader, TranslationManager.Get("Tray_FindBetter"), v => _smartItem.Header = v);
                SetTooltip($"Roxa - {string.Format(TranslationManager.Get("Tray_ConnectedTo"), vm.SelectedServer?.Name)}");
            }
            else
            {
                SetIfChanged(ref _smartHeader, TranslationManager.Get("Tray_SmartConnect"), v => _smartItem.Header = v);
                SetTooltip("Roxa");
            }

            if (_proxyItem.IsChecked != vm.SetSystemProxy) _proxyItem.IsChecked = vm.SetSystemProxy;
            if (_notifItem.IsChecked != vm.NotificationsEnabled) _notifItem.IsChecked = vm.NotificationsEnabled;
        };
        _traySyncTimer.Start();
    }

    private string? _smartHeader;
    private string? _smartTooltip;

    private static void SetIfChanged(ref string? field, string value, Action<string> apply)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        apply(value);
    }

    private void SetTooltip(string value)
    {
        if (string.Equals(_smartTooltip, value, StringComparison.Ordinal)) return;
        _smartTooltip = value;
        _trayIcon!.ToolTipText = value;
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
            try { DisposeTray(); } catch { }
            try
            {
                if (MainWindow?.DataContext is MainViewModel vm)
                    vm.Shutdown();
            }
            catch { }
            finally
            {
                // Even if stopping the engine errors, the process must not stay a zombie.
                try { MainWindow?.ForceClose(); } catch { }
            }
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
