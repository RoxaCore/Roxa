using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XrayClient.Core;
using XrayClient.Localization;
using XrayClient.Models;

namespace XrayClient.ViewModels;

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
            RestartRequested?.Invoke();
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
                Core.SystemProxy.Set($"127.0.0.1:{_engine.HttpPort}");
            else
                Core.SystemProxy.Clear();
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
    partial void OnUseTunModeChanged(bool value)
    {
        if (value && !AdminHelper.IsRunningAsAdmin())
        {
            UseTunMode = false;
            ShowAdminWarningHandler?.Invoke();
            return;
        }
        _settings.UseTunMode = value;
        SaveSettings();

        if (IsConnected && !IsTesting)
        {
            Disconnect();
            _ = Connect();
        }
    }

    partial void OnUseProxyForSubscriptionsChanged(bool value)
    {
        _settings.UseProxyForSubscriptions = value;
        SaveSettings();
    }


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
        if (TunExcludedApps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            return;
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
        TunExclusion.SetExcluded(_settings.TunExcludedApps);
    }

}
