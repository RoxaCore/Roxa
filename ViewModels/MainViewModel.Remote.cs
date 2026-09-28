using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XrayClient.Core;
using XrayClient.Localization;

namespace XrayClient.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private DispatcherTimer? _remoteTimer;
    private UpdateInfo? _pendingUpdate;
    private Announcement? _currentAnnouncement;
    private string? _dismissedUpdateVersion;
    private bool _remoteStarted;
    private bool _remoteChecking;
    private bool _updateNotified;
    private bool _forceDialogShown;
    private readonly HashSet<int> _toastedAnnouncements = new();

    [ObservableProperty] private bool _isBannerVisible;
    [ObservableProperty] private string _bannerTitle = "";
    [ObservableProperty] private string _bannerBody = "";
    [ObservableProperty] private IBrush _bannerBackground = new SolidColorBrush(Color.Parse("#EFF6FF"));
    [ObservableProperty] private IBrush _bannerForeground = new SolidColorBrush(Color.Parse("#1D4ED8"));
    [ObservableProperty] private bool _isBannerUpdate;
    [ObservableProperty] private bool _isBannerBusy;
    [ObservableProperty] private bool _bannerHasAction;
    [ObservableProperty] private string _bannerActionText = "";

    public event Action? UpdateShutdownRequested;

    public void StartRemoteChecks()
    {
        if (_remoteStarted) return;
        _remoteStarted = true;
        _ = CheckRemoteAsync();
        _remoteTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
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
            var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
            var update = await Task.Run(() => RemoteConfig.CheckUpdateAsync(lang, proxyPort));
            var announcements = await Task.Run(() => RemoteConfig.FetchAnnouncementsAsync(_settings.LastSeenAnnouncementId, lang, proxyPort));
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
            if (AutoUpdate && _dismissedUpdateVersion != _pendingUpdate.Latest && !IsBannerBusy
                && _settings.LastUpdateAttempt != _pendingUpdate.Latest)
            {
                _ = InstallUpdateAsync();
            }
            else if (_pendingUpdate.Force && !_forceDialogShown)
            {
                _forceDialogShown = true;
                ShowUpdateRequiredHandler?.Invoke();
            }
        }
        else if (freshAnnouncement != null)
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
        if (_dismissedUpdateVersion == update.Latest) return;
        IsBannerUpdate = true;
        IsBannerBusy = false;
        BannerTitle = string.Format(TranslationManager.Instance["Upd_BannerTitle"], update.Latest);
        BannerBody = update.Notes;
        BannerBackground = new SolidColorBrush(Color.Parse("#F5F3FF"));
        BannerForeground = new SolidColorBrush(Color.Parse("#6D28D9"));
        BannerActionText = TranslationManager.Instance["Upd_Btn_Install"];
        BannerHasAction = !string.IsNullOrWhiteSpace(update.Url);
        IsBannerVisible = true;
    }

    private void ShowAnnouncementBanner(Announcement announcement)
    {
        _currentAnnouncement = announcement;
        IsBannerUpdate = false;
        IsBannerBusy = false;
        BannerTitle = announcement.Title;
        BannerBody = announcement.Body;
        switch (announcement.Level)
        {
            case "warn":
                BannerBackground = new SolidColorBrush(Color.Parse("#FEF2F2"));
                BannerForeground = new SolidColorBrush(Color.Parse("#B91C1C"));
                break;
            case "update":
                BannerBackground = new SolidColorBrush(Color.Parse("#F5F3FF"));
                BannerForeground = new SolidColorBrush(Color.Parse("#6D28D9"));
                break;
            default:
                BannerBackground = new SolidColorBrush(Color.Parse("#EFF6FF"));
                BannerForeground = new SolidColorBrush(Color.Parse("#1D4ED8"));
                break;
        }
        BannerActionText = TranslationManager.Instance["Ann_View"];
        BannerHasAction = !string.IsNullOrWhiteSpace(announcement.Url);
        IsBannerVisible = true;
    }

    [RelayCommand]
    private void DismissBanner()
    {
        if (IsBannerUpdate)
        {
            _dismissedUpdateVersion = _pendingUpdate?.Latest;
            IsBannerVisible = false;
            if (_currentAnnouncement != null)
                ShowAnnouncementBanner(_currentAnnouncement);
            return;
        }
        if (_currentAnnouncement != null && _currentAnnouncement.Id > _settings.LastSeenAnnouncementId)
        {
            _settings.LastSeenAnnouncementId = _currentAnnouncement.Id;
            SaveSettings();
        }
        _currentAnnouncement = null;
        if (_pendingUpdate is { UpdateAvailable: true })
            ShowUpdateBanner(_pendingUpdate);
        else
            IsBannerVisible = false;
    }

    [RelayCommand]
    private async Task BannerAction()
    {
        if (IsBannerUpdate)
        {
            await InstallUpdateAsync();
            return;
        }
        if (_currentAnnouncement is { } ann && !string.IsNullOrWhiteSpace(ann.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(ann.Url) { UseShellExecute = true });
            }
            catch
            {
            }
            DismissBanner();
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { UpdateAvailable: true } || IsBannerBusy) return;
        IsBannerBusy = true;
        BannerBody = TranslationManager.Instance["Upd_Downloading"];
        try
        {
            var proxyPort = IsConnected ? _engine.HttpPort : (int?)null;
            var info = _pendingUpdate;
            await Task.Run(() => UpdateInstaller.StageAsync(info, proxyPort));
            _settings.LastUpdateAttempt = info.Latest;
            SaveSettings();
            BannerBody = TranslationManager.Instance["Upd_Done"];
            UpdateInstaller.LaunchUpdater();
            UpdateShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            AppendLog($"[{DateTime.Now:HH:mm:ss}] Update failed: {ex.Message}");
            BannerBody = TranslationManager.Instance["Upd_Failed"];
            IsBannerBusy = false;
        }
    }
}
