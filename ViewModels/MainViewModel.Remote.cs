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
    private bool _installRunning;
    private readonly HashSet<int> _toastedAnnouncements = new();

    // Download progress of the update package, shown inside the banner. The
    // indeterminate flag flips on once the bytes are all in: from there to the
    // app closing there is no percentage to report, only "installing".
    [ObservableProperty] private bool _isUpdateProgressVisible;
    [ObservableProperty] private bool _isUpdateProgressIndeterminate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateProgressText = "";

    partial void OnIsBannerVisibleChanged(bool value)
    {
        if (!value && !_installRunning)
        {
            IsUpdateProgressVisible = false;
            UpdateProgress = 0;
            UpdateProgressText = "";
        }
    }

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

    /// <summary>Raised once the update payload is staged and the updater is about to
    /// take over: the app must exit so the files it is running from can be replaced.</summary>
    public event Action? UpdateShutdownRequested;

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
            var lang = string.IsNullOrWhiteSpace(_settings.Language) ? Localization.Languages.DefaultCode : _settings.Language;
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
            // Installing is only ever user-driven: the banner button starts
            // InstallUpdateAsync, which stages the payload and then exits this
            // app so --update-apply can replace the files. Nothing runs on its
            // own here — even a forced update only insists, it never acts.
            // LastUpdateAttempt still records the attempt, so a failed download
            // is not retried in a loop on every hourly check.
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
        // While the install is running the hourly check must not re-enable the
        // buttons: the payload is already staged and the app is about to close.
        if (!_installRunning) IsBannerBusy = false;
        BannerTitle = string.Format(TranslationManager.Instance["Upd_BannerTitle"], update.Latest);
        BannerBody = update.Force
            ? TranslationManager.Instance["Upd_ForceMsg"] +
              (string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n" + update.Notes)
            : update.Notes;
        BannerBackground = new SolidColorBrush(Color.Parse("#F5F3FF"));
        BannerForeground = new SolidColorBrush(Color.Parse("#6D28D9"));
        BannerActionText = TranslationManager.Instance["Upd_Btn_Install"];
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
    /// The update banner button: stages the payload (download, sha256 check,
    /// extract) and hands over to the second-stage updater. Everything after the
    /// click runs inside the app — the user never sees a browser or a raw zip.
    /// </summary>
    [RelayCommand]
    private async Task BannerAction()
    {
        await InstallUpdateAsync();
    }

    /// <summary>
    /// Download + stage the update, then close the app so --update-apply can
    /// replace the files. The staged copy is verified against the sha256 the
    /// feed published, so a tampered package stops here.
    ///
    /// Failure is always recoverable: the banner shows it, the buttons come
    /// back and the current version keeps running untouched.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { UpdateAvailable: true } || IsBannerBusy || _installRunning) return;
        IsBannerBusy = true;
        _installRunning = true;
        // The connection is torn down by the restart, so remember it: after the
        // update is applied the app can come back up on the same server.
        bool wasConnected = IsConnected;
        BannerBody = TranslationManager.Instance["Upd_Downloading"];
        try
        {
            BeginUpdateProgress();
            var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
            var info = _pendingUpdate;
            var progress = new Progress<double>(SetUpdateDownloadPercent);
            await Task.Run(() => UpdateInstaller.StageAsync(info, proxyPort, progress));
            _settings.LastUpdateAttempt = info.Latest;
            SaveSettings();
            if (wasConnected)
                UpdateInstaller.MarkReconnectAfterUpdate();
            // From here there is no percentage left to show: the files are on
            // disk and only the swap itself remains (a few seconds at most).
            SetUpdateDownloadPercent(100);
            IsUpdateProgressIndeterminate = true;
            UpdateProgressText = TranslationManager.Instance["Upd_Installing"];
            BannerBody = TranslationManager.Instance["Upd_Done"];
            UpdateInstaller.LaunchUpdater();
            UpdateShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AppendLog($"Update failed: {ex.Message}");
            BannerBody = TranslationManager.Instance["Upd_Failed"];
            EndUpdateProgress();
            IsBannerBusy = false;
            _installRunning = false;
        }
    }

    /// <summary>Show an empty progress bar at 0% before the download starts.</summary>
    private void BeginUpdateProgress()
    {
        UpdateProgress = 0;
        IsUpdateProgressIndeterminate = false;
        IsUpdateProgressVisible = true;
        SetUpdateDownloadPercent(0);
    }

    /// <summary>Called from the download loop with the received percentage.</summary>
    private void SetUpdateDownloadPercent(double percent)
    {
        double p = Math.Clamp(percent, 0, 100);
        UpdateProgress = p;
        UpdateProgressText = string.Format(TranslationManager.Instance["Upd_Percent"], (int)Math.Round(p));
    }

    /// <summary>Hide the progress row when there is nothing left to report.</summary>
    private void EndUpdateProgress()
    {
        IsUpdateProgressVisible = false;
        IsUpdateProgressIndeterminate = false;
        UpdateProgress = 0;
        UpdateProgressText = "";
    }
}
