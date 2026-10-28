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

    public string[] LanguageOptions { get; } = Languages.All.Select(l => l.Name).ToArray();
    private static readonly (string name, string code)[] _languages = Languages.All;
    [ObservableProperty] private string _selectedLanguage = Languages.DefaultName;
    partial void OnSelectedLanguageChanged(string value)
    {
        var match = _languages.FirstOrDefault(l => l.name == value);
        if (match.code != null)
        {
            Localization.TranslationManager.Instance.SetCulture(match.code);
            _settings.Language = match.code;
            // Save immediately, not debounced: the language restart begins right now and,
            // when the app is not connected, the whole exit takes under 500 ms; the debounce
            // timer never got a chance to write and the next instance read the previous
            // language ("I picked Persian and Arabic came up").
            SaveSettingsNow();
            // --disconnect: a restart after a language change must first disconnect fully
            // and must not connect again once it comes back up.
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
            // The user may have unticked the box while the dialog was open; in that case
            // re-applying TUN would drift from the UI and the connection would flip on and off.
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
        // Explicitly turning it off means "not right now", so whatever was already
        // queued must go too, rather than waiting for a consent that was never given.
        if (!value) Core.BugReport.DiscardPending();
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
            // A deliberate disconnect by the app itself (server switch, manual disconnect,
            // automatic reconnect after a network change) arms the right WFP block, but
            // announcing it is just noise; only a real tunnel loss should be reported.
            if (TunKillSwitch.LastTransitionExpected) return;
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

    /// <summary>
    /// The app version shown at the end of the settings page.
    ///
    /// It comes from <see cref="RemoteConfig.AppVersion"/>, which itself reads the
    /// <c>AssemblyInformationalVersion</c> attribute on the assembly — i.e. the same
    /// value as <c>&lt;Version&gt;</c> in Roxa.csproj. The number changes in one place
    /// and applies to both the UI and the update request — deliberately not a text
    /// constant, so it cannot be lost by the installer.
    /// </summary>
    public string AppVersionDisplay => RemoteConfig.AppVersion;

}
