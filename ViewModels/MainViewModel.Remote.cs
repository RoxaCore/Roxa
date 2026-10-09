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
            var lang = string.IsNullOrWhiteSpace(_settings.Language) ? "fa" : _settings.Language;
            if (string.IsNullOrWhiteSpace(_settings.ClientId))
            {
                _settings.ClientId = Guid.NewGuid().ToString("D");
                SaveSettings();
            }
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
            // اپدیت اجباری: نادیده گرفتنِ ردِ قبلی (چون دیگر قابل کنسل نیست) و شروع خودکار نصب.
            // LastUpdateAttempt برای جلوگیری از حلقهٔ ری‌استارت بی‌نهایت در صورتِ شکستِ apply حفظ می‌شود.
            if ((AutoUpdate || _pendingUpdate.Force) && !_installRunning && !IsBannerBusy
                && _settings.LastUpdateAttempt != _pendingUpdate.Latest
                && (_pendingUpdate.Force || _dismissedUpdateVersion != _pendingUpdate.Latest))
            {
                _ = InstallUpdateAsync();
            }
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
        if (_currentAnnouncement is { } ann && !string.IsNullOrWhiteSpace(ann.Url)
            && Uri.TryCreate(ann.Url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
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

    [RelayCommand]
    private async Task BannerAction()
    {
        await InstallUpdateAsync();
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { UpdateAvailable: true } || IsBannerBusy || _installRunning) return;
        IsBannerBusy = true;
        _installRunning = true;
        // وضعیت اتصال در لحظهٔ شروع آپدیت ثبت می‌شود؛ اگر کاربر هنگام دانلود قطع کند،
        // پس از ری‌استارت همچنان به همان سرور قبلی وصل شود.
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
            // دانلود کامل شد → نوار وارد مرحلهٔ نصب می‌شود (نامعین، تا پایان نصب توسط آپدیت‌گذار)
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

    /// <summary>آغاز نوار پیشرفت آپدیت؛ مرحلهٔ دانلود با درصد معین نمایش داده می‌شود.</summary>
    private void BeginUpdateProgress()
    {
        UpdateProgress = 0;
        IsUpdateProgressIndeterminate = false;
        IsUpdateProgressVisible = true;
        SetUpdateDownloadPercent(0);
    }

    /// <summary>به‌روزرسانی درصد دانلود و متن کنار نوار پیشرفت.</summary>
    private void SetUpdateDownloadPercent(double percent)
    {
        double p = Math.Clamp(percent, 0, 100);
        UpdateProgress = p;
        UpdateProgressText = string.Format(TranslationManager.Instance["Upd_Percent"], (int)Math.Round(p));
    }

    /// <summary>پایان نوار پیشرفت و مخفی‌سازی آن.</summary>
    private void EndUpdateProgress()
    {
        IsUpdateProgressVisible = false;
        IsUpdateProgressIndeterminate = false;
        UpdateProgress = 0;
        UpdateProgressText = "";
    }
}
