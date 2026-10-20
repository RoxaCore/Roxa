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

using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Roxa.Core;
using Roxa.Localization;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private DispatcherTimer? _remoteTimer;
    private UpdateInfo? _pendingUpdate;
    private Announcement? _currentAnnouncement;
    private string? _dismissedUpdateVersion;
    private bool _remoteStarted;
    private bool _remoteChecking;
    private bool _updateNotified;
    private readonly HashSet<int> _toastedAnnouncements = new();

    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private bool _bannerCanDismiss = true;
    [ObservableProperty] private string _bannerTitle = "";
    [ObservableProperty] private string _bannerBody = "";
    [ObservableProperty] private IBrush _bannerBackground = new SolidColorBrush(Color.Parse("#EFF6FF"));
    [ObservableProperty] private IBrush _bannerForeground = new SolidColorBrush(Color.Parse("#1D4ED8"));
    [ObservableProperty] private bool _isBannerBusy;
    [ObservableProperty] private bool _bannerHasAction;
    [ObservableProperty] private string _bannerActionText = "";

    [ObservableProperty] private bool _isAnnBannerVisible;
    [ObservableProperty] private string _annBannerTitle = "";
    [ObservableProperty] private string _annBannerBody = "";
    [ObservableProperty] private IBrush _annBannerBackground = new SolidColorBrush(Color.Parse("#EFF6FF"));
    [ObservableProperty] private IBrush _annBannerForeground = new SolidColorBrush(Color.Parse("#1D4ED8"));
    [ObservableProperty] private bool _annBannerHasAction;
    [ObservableProperty] private string _annBannerActionText = "";

    public void StartRemoteChecks()
    {
        if (_remoteStarted) return;
        _remoteStarted = true;
        _ = CheckRemoteAsync();
        _remoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(60) };
        _remoteTimer.Tick += (_, _) => _ = CheckRemoteAsync();
        _remoteTimer.Start();
    }

    private async Task CheckRemoteAsync()
    {
        if (_remoteChecking) return;
        _remoteChecking = true;
        try
        {
            var lang = string.IsNullOrWhiteSpace(_settings.Language) ? "fa" : _settings.Language;
            // The id is not built here: the main constructor in MainViewModel runs
            // when the ViewModel is created and also notifies DeviceId; repeating it
            // here only produced a less complete version that never raised OnPropertyChanged.
            if (!IsConnected)
            {
                var cc = await Task.Run(() => RemoteConfig.DetectCountryAsync());
                if (!string.IsNullOrEmpty(cc))
                {
                    _settings.Country = cc;
                    SaveSettings();
                }
            }
            var country = _settings.Country ?? "";
            var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
            var update = await Task.Run(() => RemoteConfig.CheckUpdateAsync(lang, country, _settings.ClientId, proxyPort,
                _settings.ConnectCount, includeStats: SendUsageStats));
            var announcements = await Task.Run(() => RemoteConfig.FetchAnnouncementsAsync(_settings.LastSeenAnnouncementId, lang, country, proxyPort));
            Post(() => ApplyRemote(update, announcements));
        }
        catch
        {
        }
        finally
        {
            _remoteChecking = false;
        }
    }

    private void ApplyRemote(UpdateInfo? update, List<Announcement>? announcements)
    {
        if (update is { UpdateAvailable: true })
            _pendingUpdate = update;

        Announcement? freshAnnouncement = null;
        if (announcements is { Count: > 0 })
        {
            var announcement = announcements[0];
            if (AgeAnnouncement(announcement))
            {
                freshAnnouncement = announcement;
                if (_toastedAnnouncements.Add(announcement.Id))
                    NotificationHelper.Show(announcement.Title, announcement.Body);
            }
        }

        if (_pendingUpdate is { UpdateAvailable: true })
        {
            ShowUpdateBanner(_pendingUpdate);
            if (!_updateNotified)
            {
                _updateNotified = true;
                NotificationHelper.Show(
                    TranslationManager.Instance["Upd_Notify"],
                    string.Format(TranslationManager.Instance["Upd_BannerTitle"], _pendingUpdate.Latest));
            }
            // Silent install was deliberately removed: this version neither downloads the
            // update package nor runs --update-apply. Version checking ends here with a
            // notification and the banner; the banner button simply opens the release page.
            // So even a forced update only insists — it never acts on its own.
        }
        if (freshAnnouncement != null)
        {
            ShowAnnouncementBanner(freshAnnouncement);
        }
    }

    private bool AgeAnnouncement(Announcement announcement)
    {
        if (_settings.ShownAnnouncementId != announcement.Id)
        {
            _settings.ShownAnnouncementId = announcement.Id;
            _settings.ShownAnnouncementCount = 1;
            SaveSettings();
            return true;
        }
        if (_settings.ShownAnnouncementCount < 2)
        {
            _settings.ShownAnnouncementCount++;
            SaveSettings();
            return true;
        }
        _settings.LastSeenAnnouncementId = announcement.Id;
        _settings.ShownAnnouncementId = 0;
        _settings.ShownAnnouncementCount = 0;
        SaveSettings();
        return false;
    }

    private void ShowUpdateBanner(UpdateInfo update)
    {
        if (_dismissedUpdateVersion == update.Latest && !update.Force) return;
        IsBannerBusy = false;
        BannerTitle = string.Format(TranslationManager.Instance["Upd_BannerTitle"], update.Latest);
        BannerBody = update.Force
            ? TranslationManager.Instance["Upd_ForceMsg"] +
              (string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n" + update.Notes)
            : update.Notes;
        BannerBackground = new SolidColorBrush(Color.Parse("#F5F3FF"));
        BannerForeground = new SolidColorBrush(Color.Parse("#6D28D9"));
        BannerActionText = TranslationManager.Instance["Upd_Btn_OpenPage"];
        BannerHasAction = !string.IsNullOrWhiteSpace(update.Url);
        BannerCanDismiss = !update.Force;
        IsBannerVisible = true;
    }

    private void ShowAnnouncementBanner(Announcement announcement)
    {
        _currentAnnouncement = announcement;
        AnnBannerTitle = announcement.Title;
        AnnBannerBody = announcement.Body;
        switch (announcement.Level)
        {
            case "warn":
                AnnBannerBackground = new SolidColorBrush(Color.Parse("#FEF2F2"));
                AnnBannerForeground = new SolidColorBrush(Color.Parse("#B91C1C"));
                break;
            case "update":
                AnnBannerBackground = new SolidColorBrush(Color.Parse("#F5F3FF"));
                AnnBannerForeground = new SolidColorBrush(Color.Parse("#6D28D9"));
                break;
            default:
                AnnBannerBackground = new SolidColorBrush(Color.Parse("#EFF6FF"));
                AnnBannerForeground = new SolidColorBrush(Color.Parse("#1D4ED8"));
                break;
        }
        AnnBannerActionText = TranslationManager.Instance["Ann_View"];
        AnnBannerHasAction = !string.IsNullOrWhiteSpace(announcement.Url);
        IsAnnBannerVisible = true;
    }

    [RelayCommand]
    private void DismissBanner()
    {
        if (!BannerCanDismiss) return;
        _dismissedUpdateVersion = _pendingUpdate?.Latest;
        IsBannerVisible = false;
    }

    [RelayCommand]
    private void AnnDismiss()
    {
        if (_currentAnnouncement != null && _currentAnnouncement.Id > _settings.LastSeenAnnouncementId)
        {
            _settings.LastSeenAnnouncementId = _currentAnnouncement.Id;
            SaveSettings();
        }
        _currentAnnouncement = null;
        IsAnnBannerVisible = false;
    }

    [RelayCommand]
    private void AnnBannerAction()
    {
        // https only. The server writes the announcement link by the same rule, but the
        // client must demand it too: UseShellExecute hands a URI to whatever handles it,
        // and anything that came from the network must be filtered here before opening.
        if (_currentAnnouncement is { } ann && !string.IsNullOrWhiteSpace(ann.Url)
            && Uri.TryCreate(ann.Url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                Process.Start(new ProcessStartInfo(ann.Url) { UseShellExecute = true });
            }
            catch
            {
            }
        }
        AnnDismiss();
    }

    /// <summary>
    /// The update banner button. This version downloads nothing and applies no update,
    /// so it only opens the release page. Same rule as AnnBannerAction: UseShellExecute
    /// hands a URI to whatever handles it, so anything from the network must be filtered
    /// here before opening; only https is accepted.
    /// </summary>
    [RelayCommand]
    private void BannerAction()
    {
        if (_pendingUpdate is not { UpdateAvailable: true } || string.IsNullOrWhiteSpace(_pendingUpdate.Url))
            return;
        if (!Uri.TryCreate(_pendingUpdate.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_pendingUpdate.Url) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
