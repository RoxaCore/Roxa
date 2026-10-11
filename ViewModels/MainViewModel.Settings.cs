using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{

    [ObservableProperty] private bool _showAdvancedDns;
    [ObservableProperty] private bool _useTunMode;

    [ObservableProperty] private bool _useProxyForSubscriptions;

    public string[] LanguageOptions { get; } = ["فارسی", "English", "العربية", "中文", "Русский", "Türkçe", "اردو", "Español", "Français", "한국어", "Deutsch", "日本語", "Português", "हिन्दी"];
    private static readonly (string name, string code)[] _languages = [("فارسی", "fa"), ("English", "en"), ("العربية", "ar"), ("中文", "zh"), ("Русский", "ru"), ("Türkçe", "tr"), ("اردو", "ur"), ("Español", "es"), ("Français", "fr"), ("한국어", "ko"), ("Deutsch", "de"), ("日本語", "ja"), ("Português", "pt-br"), ("हिन्दी", "hi")];
    [ObservableProperty] private string _selectedLanguage = "فارسی";
    partial void OnSelectedLanguageChanged(string value)
    {
        var match = _languages.FirstOrDefault(l => l.name == value);
        if (match.code != null)
        {
            Localization.TranslationManager.Instance.SetCulture(match.code);
            _settings.Language = match.code;
            SaveSettings();
            // --disconnect: ری‌استارتِ بعد از تغییر زبان باید اول اتصال را کامل قطع
            // کند و بعد از بالا آمدن، دوباره وصل نشود.
            RestartRequested?.Invoke("--disconnect");
        }
    }


    partial void OnSetSystemProxyChanged(bool value)
    {
        if (value) LeaveSystemProxyAsIs = false;
        _settings.SetSystemProxy = value;
        SaveSettings();
        if (IsConnected && !(_settings.UseTunMode) && !LeaveSystemProxyAsIs)
        {
            if (value)
                ApplySystemProxyEndpoint();
            else
            {
                Core.ProxyRouter.Stop();
                Core.SystemProxy.Clear();
            }
        }
    }

    partial void OnLeaveSystemProxyAsIsChanged(bool value)
    {
        if (value) SetSystemProxy = false;
        _settings.LeaveSystemProxyAsIs = value;
        SaveSettings();
    }
    partial void OnClearProxyOnDisconnectChanged(bool value) { _settings.ClearProxyOnDisconnect = value; SaveSettings(); }
    partial void OnAutoUpdateChanged(bool value) { _settings.AutoUpdate = value; SaveSettings(); }
    partial void OnShowIpColumnChanged(bool value) { _settings.ShowIpColumn = value; SaveSettings(); }
    partial void OnAutoUpdateIntervalHoursChanged(int value)
    {
        _settings.AutoUpdateIntervalHours = Math.Clamp(value, 1, 168);
        SaveSettings();
    }


    partial void OnSortFieldChanged(string value)
    {
        _settings.SortField = value;
        SaveSettings();
        RefreshGroups();
    }
    partial void OnGlobalSortChanged(bool value)
    {
        _settings.GlobalSort = value;
        SaveSettings();
        RefreshGroups();
    }
    partial void OnSortAscendingChanged(bool value)
    {
        _settings.SortAscending = value;
        SaveSettings();
        RefreshGroups();
    }
    partial void OnCustomDnsServersChanged(string value) { _settings.CustomDnsServers = value; SaveSettings(); }
    partial void OnCustomDnsAdvancedJsonChanged(string value) { _settings.CustomDnsAdvancedJson = value; SaveSettings(); }
    private bool _tunConfirmPending;

    partial void OnUseTunModeChanged(bool value)
    {
        if (value && !AdminHelper.IsRunningAsAdmin())
        {
            UseTunMode = false;
            ShowAdminWarningHandler?.Invoke();
            return;
        }
        if (value && !_settings.TunConfirmed)
        {
            if (!_tunConfirmPending) _ = ConfirmTunEnableAsync();
            return;
        }
        ApplyUseTunMode(value);
    }

    private async Task ConfirmTunEnableAsync()
    {
        _tunConfirmPending = true;
        bool ok = false;
        try
        {
            if (ConfirmTunEnableHandler != null)
                ok = await ConfirmTunEnableHandler();
        }
        finally { _tunConfirmPending = false; }

        if (ok)
        {
            // کاربر ممکن است در حین دیالوگ تیک را خاموش کرده باشد؛ در آن صورت
            // اعمالِ دوبارهٔ TUN با UI ناهمخوان می‌شود و اتصال غیرمنتظره وصل/قطع می‌شود.
            if (!UseTunMode) return;
            _settings.TunConfirmed = true;
            SaveSettings();
            ApplyUseTunMode(true);
        }
        else if (UseTunMode)
        {
            UseTunMode = false;
        }
    }

    private void ApplyUseTunMode(bool value)
    {
        bool changed = _settings.UseTunMode != value;
        _settings.UseTunMode = value;
        SaveSettings();

        if (!changed) { _tunToggleReconnect = null; return; }

        if (IsConnected && !IsTesting)
        {
            _tunToggleReconnect = value;
            DisconnectCore(notify: false);
            _ = Connect();
        }
        else _tunToggleReconnect = null;
    }

    partial void OnUseProxyForSubscriptionsChanged(bool value)
    {
        _settings.UseProxyForSubscriptions = value;
        SaveSettings();
    }

    [ObservableProperty] private bool _sendUsageStats;

    partial void OnSendUsageStatsChanged(bool value)
    {
        _settings.SendUsageStats = value;
        SaveSettings();
    }

    public string DeviceId => _settings.ClientId;

    [ObservableProperty] private bool _notificationsEnabled;

    partial void OnNotificationsEnabledChanged(bool value)
    {
        _settings.NotificationsEnabled = value;
        SaveSettings();
        NotificationHelper.Enabled = value;
    }

    [ObservableProperty] private bool _testsViaTun;
    [ObservableProperty] private bool _testsBypassingTun;

    partial void OnTestsViaTunChanged(bool value)
    {
        _settings.TestsViaTun = value;
        SaveSettings();
        Core.TestRouteBypass.TestsViaTun = value;
        RefreshTestsBypassingTun();
    }

    public void RefreshTestsBypassingTun() => TestsBypassingTun = Core.TestRouteBypass.IsBypassing;


    [RelayCommand]
    private void OpenDnsSettings() => OpenDnsSettingsHandler?.Invoke();

    [ObservableProperty] private ObservableCollection<TunExcludedItem> _tunExcludedApps = new();
    [ObservableProperty] private bool _tunExcludedEmpty = true;

    [RelayCommand]
    private async Task AddTunExcludedApp()
    {
        if (PickTunExcludedAppHandler == null) return;
        string? path = await PickTunExcludedAppHandler();
        if (string.IsNullOrWhiteSpace(path)) return;
        string name = Path.GetFileName(path);
        var existing = TunExcludedApps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? TunExcludedApps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Path = path;
            existing.Name = name;
            existing.IsEnabled = true;
        }
        else
            TunExcludedApps.Add(new TunExcludedItem { Path = path, Name = name, IsEnabled = true });
        PersistTunExcluded();
    }

    [RelayCommand]
    private void RemoveTunExcludedApp(TunExcludedItem? item)
    {
        if (item == null) return;
        var match = TunExcludedApps.FirstOrDefault(a => ReferenceEquals(a, item))
                ?? TunExcludedApps.FirstOrDefault(a => string.Equals(a.Path, item.Path, StringComparison.OrdinalIgnoreCase))
                ?? TunExcludedApps.FirstOrDefault(a => string.Equals(a.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;
        TunExcludedApps.Remove(match);
        PersistTunExcluded();
    }

    [RelayCommand]
    private void ToggleTunExcludedApp(TunExcludedItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
        PersistTunExcluded();
    }

    private void PersistTunExcluded()
    {
        _settings.TunExcludedApps = TunExcludedApps.Where(i => i.IsEnabled).Select(i => i.Path).ToList();
        _settings.TunExcludedAppsDisabled = TunExcludedApps.Where(i => !i.IsEnabled).Select(i => i.Path).ToList();
        SaveSettings();
        TunExcludedEmpty = TunExcludedApps.Count == 0;
        TunManager.Log($"exclusion persist: enabled={_settings.TunExcludedApps.Count} disabled={_settings.TunExcludedAppsDisabled.Count} [{string.Join(", ", TunExcludedApps.Select(i => i.Name + "=" + (i.IsEnabled ? "on" : "off")))}]");
        TunExclusion.SetExcluded(_settings.TunExcludedApps);
        TunKillSwitch.Configure(KillSwitchEnabled, _settings.TunExcludedApps, 0);
        if (IsConnected && !_settings.UseTunMode && !LeaveSystemProxyAsIs && SetSystemProxy && !IsTesting)
            ApplySystemProxyEndpoint();
    }

    [ObservableProperty] private bool _bypassVpnWithoutTun;

    partial void OnBypassVpnWithoutTunChanged(bool value)
    {
        _settings.BypassVpnWithoutTun = value;
        SaveSettings();
        if (IsConnected && !_settings.UseTunMode && !LeaveSystemProxyAsIs && SetSystemProxy && !IsTesting)
            ApplySystemProxyEndpoint();
    }

    private void ApplySystemProxyEndpoint()
    {
        bool on = _settings.BypassVpnWithoutTun && _settings.TunExcludedApps is { Count: > 0 };
        if (on && Core.ProxyRouter.Start(_settings.TunExcludedApps, _engine.HttpPort))
            Core.SystemProxy.Set($"127.0.0.1:{Core.ProxyRouter.Port}");
        else
        {
            Core.ProxyRouter.Stop();
            Core.SystemProxy.Set($"127.0.0.1:{_engine.HttpPort}");
        }
    }

    [ObservableProperty] private bool _killSwitchEnabled;
    [ObservableProperty] private bool _killSwitchEngaged;
    [ObservableProperty] private string _killSwitchStatus = "";

    partial void OnKillSwitchEnabledChanged(bool value)
    {
        _settings.KillSwitchEnabled = value;
        SaveSettings();
        TunKillSwitch.Configure(value, _settings.TunExcludedApps, 0);
        if (!value)
        {
            KillSwitchEngaged = false;
            KillSwitchStatus = "";
        }
    }

    [RelayCommand]
    private void EmergencyRelease()
    {
        TunKillSwitch.EmergencyRelease();
        KillSwitchEnabled = false;
    }

    private void OnKillSwitchStateChanged(bool engaged, int count)
    {
        Post(() =>
        {
            KillSwitchEngaged = engaged;
            KillSwitchStatus = engaged
                ? string.Format(TranslationManager.Instance["Set_KillSwitch_Waiting"], count)
                : "";
            if (engaged)
                NotificationHelper.Show(
                    TranslationManager.Get("Set_KillSwitch_Blocked_Title"),
                    string.Format(TranslationManager.Get("Set_KillSwitch_Blocked_Msg"), count));
            else
                NotificationHelper.Show(
                    TranslationManager.Get("Set_KillSwitch_Restored_Title"),
                    TranslationManager.Get("Set_KillSwitch_Restored_Msg"));
        });
    }

}
