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
    // The automatic update path: _autoStaging is true while the background
    // download runs, _autoStagedVersion is the version already sitting verified
    // in %TEMP% waiting for the exit, _autoAttemptedVersion stops a failed
    // download from retrying on every hourly check within the same session.
    private bool _autoStaging;
    private string? _autoStagedVersion;
    private string? _autoAttemptedVersion;
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
        // Every 15 minutes so the server's 30-minute "online" window always
        // covers the gap between two check-ins (it used to be 60 minutes, which
        // made the dashboard count a fraction of the real online users).
        _remoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
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
            // The banner button installs immediately (InstallUpdateAsync). The
            // automatic path, gated by the AutoUpdateApp setting, downloads the
            // payload in the background now and applies it when the app exits
            // (see StartAutoStage and MainViewModel.Shutdown). Nothing ever
            // installs mid-session: the swap needs this process gone.
            if (_settings.AutoUpdateApp)
                StartAutoStage(_pendingUpdate);
            // With the automatic install off there is no background download —
            // ask out loud instead (every 10 days / once per new version).
            else
                MaybePromptUpdate(_pendingUpdate);
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
        // While the automatic download or a manual install owns the banner, the
        // hourly check must not repaint it: the progress or "installs on exit"
        // text would be replaced by the notes. The same holds once the payload
        // is staged — the banner keeps saying it will install at exit.
        if (_autoStaging || _installRunning || _autoStagedVersion == update.Latest) return;
        IsBannerBusy = false;
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
            var info = _pendingUpdate;
            // If the automatic path already downloaded and verified this exact
            // version, there is nothing left to fetch: go straight to the swap.
            if (!(UpdateInstaller.IsStaged && _autoStagedVersion == info.Latest))
            {
                BeginUpdateProgress();
                var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
                var progress = new Progress<double>(SetUpdateDownloadPercent);
                await Task.Run(() => UpdateInstaller.StageAsync(info, proxyPort, progress));
                _settings.LastUpdateAttempt = info.Latest;
                SaveSettings();
            }
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

    /// <summary>
    /// The automatic update path. As soon as a new version is announced, the
    /// payload is downloaded and verified in the background and kept staged on
    /// disk; the swap itself only happens when the app exits (see Shutdown), so
    /// a running session is never interrupted. One attempt per version per
    /// session: a failed download leaves the banner — and its Install button —
    /// as the fallback and is retried on the next run, not on every hourly check.
    /// </summary>
    private void StartAutoStage(UpdateInfo update)
    {
        if (_installRunning || _autoStaging) return;          // a download or an install is already running
        if (string.IsNullOrWhiteSpace(update.Url)) return;    // nothing to fetch
        if (_autoStagedVersion == update.Latest) return;      // already on disk, waiting for the exit
        if (_autoAttemptedVersion == update.Latest) return;   // failed once this session
        _autoAttemptedVersion = update.Latest;
        _ = AutoStageAsync(update);
    }

    /// <summary>Download + verify in the background, reporting progress in the banner.</summary>
    private async Task AutoStageAsync(UpdateInfo update)
    {
        _autoStaging = true;
        try
        {
            IsBannerBusy = true;   // the Install button must not start a second download
            BeginUpdateProgress();
            BannerBody = TranslationManager.Instance["Upd_Downloading"];
            var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
            var progress = new Progress<double>(SetUpdateDownloadPercent);
            await Task.Run(() => UpdateInstaller.StageAsync(update, proxyPort, progress));
            _settings.LastUpdateAttempt = update.Latest;
            SaveSettings();
            _autoStagedVersion = update.Latest;
            EndUpdateProgress();
            IsBannerBusy = false;
            // The banner may have been dismissed while the download ran; then
            // the notes stay hidden and only a later check shows them again.
            if (_dismissedUpdateVersion != update.Latest || update.Force)
                BannerBody = TranslationManager.Instance["Upd_AutoReady"];
        }
        catch (Exception ex)
        {
            AppendLog($"Auto update download failed: {ex.Message}");
            EndUpdateProgress();
            IsBannerBusy = false;
        }
        finally
        {
            _autoStaging = false;
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

    // ---------- the small update window (manual check + the 10-day question) ----------

    [ObservableProperty] private bool _isUpdateChecking;
    [ObservableProperty] private string _updateCheckButtonText = "";

    private UpdateWindowViewModel? _updateWindow;
    private CancellationTokenSource? _updateWindowCts;
    private bool _updateFlowRunning;
    private bool _updatePromptOpen;

    /// <summary>Opens the update window (modal) with the given state. Wired in MainWindow.</summary>
    public Func<UpdateWindowViewModel, Task>? ShowUpdateWindowHandler { get; set; }

    /// <summary>The yes/no question; returns true when the user picks "yes". Wired in MainWindow.</summary>
    public Func<UpdateInfo, Task<bool>>? ShowUpdatePromptHandler { get; set; }

    partial void OnIsUpdateCheckingChanged(bool value)
        => UpdateCheckButtonText = TranslationManager.Instance[value ? "Set_UpdateChecking" : "Set_UpdateCheck"];

    /// <summary>Settings → "check for updates": opens the window and runs the whole flow in it.</summary>
    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        // A window is already open, or the banner owns an install right now.
        if (_updateWindow != null || _installRunning || UpdateInstaller.UpdaterLaunched) return;
        await OpenUpdateWindowAsync(null);
    }

    /// <summary>
    /// Opens the small window and runs the flow behind it: check (unless an
    /// update is already known), download, then hand over to the updater. The
    /// window can cancel at any point; on success the app closes itself and the
    /// second-stage updater brings it back on the new version.
    /// </summary>
    private async Task OpenUpdateWindowAsync(UpdateInfo? known)
    {
        if (_updateWindow != null || ShowUpdateWindowHandler is null) return;
        var vm = new UpdateWindowViewModel();
        _updateWindow = vm;
        _updateWindowCts = new CancellationTokenSource();
        vm.CancelRequested = () => { try { _updateWindowCts?.Cancel(); } catch { } };
        vm.RetryRequested = () => _ = RetryUpdateFlowAsync(vm);
        try
        {
            var closed = ShowUpdateWindowHandler(vm);
            await RunUpdateFlowAsync(vm, known, _updateWindowCts.Token);
            // The flow leaves the window open when there is nothing to do
            // (up to date / failed); wait until the user closes it.
            await closed;
        }
        catch { }
        finally
        {
            // Deliberately not disposed: a Retry task may still hold the token,
            // and an undisposed CTS is safe — only a disposed one can throw.
            _updateWindowCts = null;
            _updateWindow = null;
        }
    }

    /// <summary>Retry runs the whole flow again — the check included, so a stale result is never trusted.</summary>
    private async Task RetryUpdateFlowAsync(UpdateWindowViewModel vm)
    {
        var cts = _updateWindowCts;
        if (cts == null || _updateFlowRunning) return;
        await RunUpdateFlowAsync(vm, null, cts.Token);
    }

    /// <summary>
    /// check → download → install, all inside the window. The window owns the
    /// visual state; this method only moves it from state to state. While it
    /// runs, _installRunning keeps the banner from painting over the progress
    /// or starting a second install of the same payload.
    /// </summary>
    private async Task RunUpdateFlowAsync(UpdateWindowViewModel vm, UpdateInfo? known, CancellationToken ct)
    {
        if (_updateFlowRunning) return;
        _updateFlowRunning = true;
        _installRunning = true;
        bool handedOff = false;
        try
        {
            var info = known;
            if (info is not { UpdateAvailable: true })
            {
                vm.ShowChecking();
                IsUpdateChecking = true;
                try
                {
                    var lang = string.IsNullOrWhiteSpace(_settings.Language)
                        ? Localization.Languages.DefaultCode : _settings.Language;
                    var country = _settings.Country ?? "";
                    var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
                    info = await Task.Run(() => RemoteConfig.CheckUpdateAsync(lang, country, _settings.ClientId,
                        proxyPort, _settings.ConnectCount, includeStats: SendUsageStats, ct), ct);
                }
                finally
                {
                    IsUpdateChecking = false;
                }
                ct.ThrowIfCancellationRequested();
                // null means the check itself failed (no network, server error) —
                // that is NOT the same as "no new version", which comes back as
                // a real UpdateInfo with UpdateAvailable=false.
                if (info is null)
                {
                    vm.ShowCheckFailed();
                    return;
                }
                if (!info.UpdateAvailable)
                {
                    vm.ShowUpToDate();
                    return;   // the window stays open with a Close button
                }
                _pendingUpdate = info;
            }

            vm.ShowVersion(info!);

            // A background auto-stage of the same payload may already be running;
            // a second download would only fight it over the same file.
            while (_autoStaging)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(250, ct);
            }
            ct.ThrowIfCancellationRequested();

            vm.ShowDownloading(0);
            if (!(UpdateInstaller.IsStaged && _autoStagedVersion == info!.Latest))
            {
                var port = IsConnected ? _engine.HttpPort : (int?)null;
                var progress = new Progress<double>(p => vm.ShowDownloading(p));
                await Task.Run(() => UpdateInstaller.StageAsync(info!, port, progress, ct), ct);
                _settings.LastUpdateAttempt = info!.Latest;
                SaveSettings();
            }

            ct.ThrowIfCancellationRequested();
            // Everything is on disk; the swap needs this process gone.
            if (IsConnected) UpdateInstaller.MarkReconnectAfterUpdate();
            vm.ShowInstalling();
            UpdateInstaller.LaunchUpdater();
            handedOff = true;
            UpdateShutdownRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            vm.CloseNow();
        }
        catch (Exception ex)
        {
            AppendLog($"Update window failed: {ex.Message}");
            vm.ShowFailed();
        }
        finally
        {
            IsUpdateChecking = false;
            _updateFlowRunning = false;
            // On success the process is on its way out and the flag must stay
            // set; otherwise the banner would offer the same install again.
            if (!handedOff) _installRunning = false;
        }
    }

    /// <summary>
    /// The every-10-days question, asked only with the automatic install off —
    /// then the app downloads by itself and there is nothing to ask. A version
    /// never asked about is asked right away; the same pending version comes
    /// back as a reminder no more often than every 10 days. The answer is
    /// recorded before the window opens: closing it with X still counts as
    /// "asked", or the hourly check would pop it up again right away.
    /// </summary>
    private void MaybePromptUpdate(UpdateInfo update)
    {
        if (!_settings.UpdatePromptEnabled) return;
        if (_settings.AutoUpdateApp) return;
        if (_updateWindow != null || _updatePromptOpen || _installRunning || _autoStaging) return;

        bool newVersion = _settings.LastPromptedUpdateVersion != update.Latest;
        bool tenDaysPassed = _settings.LastUpdatePromptUtc == default
            || (DateTime.UtcNow - _settings.LastUpdatePromptUtc).TotalDays >= 10;
        if (!newVersion && !tenDaysPassed) return;

        _settings.LastUpdatePromptUtc = DateTime.UtcNow;
        _settings.LastPromptedUpdateVersion = update.Latest;
        SaveSettings();
        _ = PromptUpdateAsync(update);
    }

    private async Task PromptUpdateAsync(UpdateInfo update)
    {
        _updatePromptOpen = true;
        try
        {
            if (ShowUpdatePromptHandler is null) return;
            if (await ShowUpdatePromptHandler(update))
                await OpenUpdateWindowAsync(update);
        }
        catch { }
        finally
        {
            _updatePromptOpen = false;
        }
    }
}
