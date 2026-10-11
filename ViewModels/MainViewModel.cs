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
using Roxa.Models;

namespace Roxa.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly XrayEngine _engine = new();
    private AppSettings _settings = new();
    private CancellationTokenSource _testCts = new();

     public IClipboard? Clipboard { get; set; }
     public Func<ServerInfo, Task<bool>>? EditServerHandler { get; set; }
     public Func<string, string, Task>? ShowQrHandler { get; set; }
     public Func<string, Task<bool>>? ConfirmDeleteHandler { get; set; }
     public Func<Task<bool>>? ConfirmTunEnableHandler { get; set; }
     public Action<string>? ToastHandler { get; set; }
     public Action? OpenDnsSettingsHandler { get; set; }
     public Action<ServerInfo>? FocusServerHandler { get; set; }
     public Action? SelectionRefreshHandler { get; set; }
     public Func<Task<string?>>? PickTunExcludedAppHandler { get; set; }

    [ObservableProperty] private ObservableCollection<ServerInfo> _servers = new();
    [ObservableProperty] private ObservableCollection<SubscriptionInfo> _subscriptions = new();
    [ObservableProperty] private ObservableCollection<ServerGroup> _groups = new();
    [ObservableProperty] private ServerInfo? _selectedServer;
    [ObservableProperty] private string _serverLink = "";
    [ObservableProperty] private string _subUrl = "";
    [ObservableProperty] private string _subName = "";
    [ObservableProperty] private string _status = TranslationManager.Instance["Status_Ready"];
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _setSystemProxy = true;
    [ObservableProperty] private bool _autoConnect;
    [ObservableProperty] private bool _leaveSystemProxyAsIs;
    [ObservableProperty] private bool _clearProxyOnDisconnect = true;
    [ObservableProperty] private bool _autoUpdateSubs;
    [ObservableProperty] private bool _autoUpdate;
    [ObservableProperty] private int _autoUpdateIntervalHours = 12;
    [ObservableProperty] private bool _showIpColumn;
      [ObservableProperty] private bool _isTesting;
      [ObservableProperty] private bool _isSmartConnecting;
      [ObservableProperty] private bool _isFindingBetter;
      [ObservableProperty] private int _searchTestedCount;
      [ObservableProperty] private int _searchTotalCount;
      [ObservableProperty] private int _testTestedCount;
      [ObservableProperty] private int _testTotalCount;

      public double SearchProgressPercent => SearchTotalCount > 0 ? (double)SearchTestedCount / SearchTotalCount : 0;
      public string SearchProgressText => SearchTotalCount > 0
          ? string.Format(TranslationManager.Instance["Tray_SearchingProgress"], SearchTestedCount, SearchTotalCount)
          : TranslationManager.Instance["Tray_Searching"];
      public double TestProgressPercent => TestTotalCount > 0 ? Math.Min(1.0, (double)TestTestedCount / TestTotalCount) : 0;
      public string TestProgressText => TestTotalCount > 0 ? $"{TestTestedCount}/{TestTotalCount}" : "";
      public string LastUpdateDisplay
      {
          get
          {
              if (_settings.LastSubscriptionUpdate == DateTime.MinValue) return TranslationManager.Instance["LastUpdate_Never"];
              var elapsed = DateTime.Now - _settings.LastSubscriptionUpdate;
              if (elapsed.TotalHours < 1) return string.Format(TranslationManager.Instance["LastUpdate_MinAgo"], (int)elapsed.TotalMinutes);
              if (elapsed.TotalDays < 1) return string.Format(TranslationManager.Instance["LastUpdate_HourAgo"], (int)elapsed.TotalHours);
              return string.Format(TranslationManager.Instance["LastUpdate_DayAgo"], (int)elapsed.TotalDays);
          }
      }

      // --- وضعیت جستجوی تدریجی ---
      private List<ServerInfo> _searchPool = new();
      private DateTime _lastSearchTime = DateTime.MinValue;
      private const int SearchBatchSize = 5;
      private static readonly TimeSpan SearchExpiry = TimeSpan.FromMinutes(15);

     public bool ShowSmartConnectButton => !IsSmartConnecting && !IsConnected;

     public IRelayCommand? SmartConnectActionCommand => IsConnected ? FindBetterServerCommand : SmartConnectCommand as IRelayCommand;

     [ObservableProperty] private int _duplicateCount;

     public bool HasDuplicates => DuplicateCount > 0;

     /// <summary>
     /// مبنای واحد تشخیص تکراری برای شمارش، حذف و افزودن: ServerInfo.GetDupeKey —
     /// آدرس + پورت + پروتکل + ترنسپورت + مسیر + حالت + امنیت + رمزنگاری +
     /// احراز هویت و بقیهٔ فیلدهای مؤثر بر اتصال؛ نام و لینک در کلید نیستند.
     /// </summary>
     public static string DupeKey(ServerInfo s) => s.GetDupeKey();

      [ObservableProperty] private string _selectedSubscriptionName = "";
      [ObservableProperty] private string _sortField = TranslationManager.Instance["Sort_None"];
      [ObservableProperty] private bool _globalSort;
      [ObservableProperty] private int _selectedCount;
      [ObservableProperty] private bool _selectAllChecked;
      [ObservableProperty] private bool _sortAscending = true;
      [ObservableProperty] private string _customDnsServers = "";
      [ObservableProperty] private string _customDnsAdvancedJson = "";

      public string[] SortOptions => [TranslationManager.Instance["Sort_None"], TranslationManager.Instance["Sort_Latency"], TranslationManager.Instance["Sort_Speed"], TranslationManager.Instance["Sort_Name"]];


    // آرگومان اضافه (مثل --tab subscriptions) به فرمان relaunch پیوست می‌شود.
    public event Action<string>? RestartRequested;

    private ServerInfo? DisplayServer => IsConnected ? _engine.CurrentServer ?? SelectedServer : SelectedServer;

    public string HomeServerName => DisplayServer?.Name ?? TranslationManager.Instance["Home_NoServer"];
    public string HomeShortName => DisplayServer?.ShortName ?? "";
    public string HomeProtocol => DisplayServer != null ? DisplayServer.Protocol.ToString().ToLowerInvariant() : "";
    public string HomeIp => DisplayServer?.Address ?? "";
    public string HomeFlag => DisplayServer?.Flag ?? "??";
    public string HomeAddress => DisplayServer != null ? $"{DisplayServer.Address}:{DisplayServer.Port}" : "";
    public string HomeLatency => DisplayServer?.LatencyDisplay ?? "";
    public string HomeSpeed => DisplayServer?.SpeedDisplay ?? "";
    public string PortText => IsConnected ? $"HTTP {HttpPort} · SOCKS {SocksPort}" : "";
    public int HttpPort => _engine.HttpPort;
    public int SocksPort => _engine.SocksPort;

    [ObservableProperty] private string _liveDownSpeed = "0 B/s";
    [ObservableProperty] private string _liveUpSpeed = "0 B/s";
    [ObservableProperty] private double _speedBlinkOpacity = 1.0;
    private long _lastBytesIn;
    private long _lastBytesOut;
    private string _lastInterfaceId = "";
    private DispatcherTimer? _speedTimer;
    private DispatcherTimer? _blinkTimer;
    private double _blinkPhase;

    public MainViewModel()
    {
        _settings = Storage.LoadSettings();
        SetSystemProxy = _settings.SetSystemProxy;
        AutoConnect = _settings.AutoConnect;
        AutoUpdateSubs = _settings.AutoUpdateSubs;
        AutoUpdate = _settings.AutoUpdate;
        AutoUpdateIntervalHours = _settings.AutoUpdateIntervalHours <= 0 ? 12 : _settings.AutoUpdateIntervalHours;
        ShowIpColumn = _settings.ShowIpColumn;
        _sortField = _settings.SortField;
        _sortAscending = _settings.SortAscending;
        _globalSort = _settings.GlobalSort;
        _customDnsServers = _settings.CustomDnsServers ?? "";
        _customDnsAdvancedJson = _settings.CustomDnsAdvancedJson ?? "";
        _leaveSystemProxyAsIs = _settings.LeaveSystemProxyAsIs;
        _clearProxyOnDisconnect = _settings.ClearProxyOnDisconnect;
        _useProxyForSubscriptions = _settings.UseProxyForSubscriptions;
        _bypassVpnWithoutTun = _settings.BypassVpnWithoutTun;
        _testsViaTun = _settings.TestsViaTun;
        Core.TestRouteBypass.TestsViaTun = _settings.TestsViaTun;
        _notificationsEnabled = _settings.NotificationsEnabled;
        Core.NotificationHelper.Enabled = _settings.NotificationsEnabled;
        _sendUsageStats = _settings.SendUsageStats;
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            _settings.ClientId = Guid.NewGuid().ToString("D");
            Core.Storage.SaveSettings(_settings);
            OnPropertyChanged(nameof(DeviceId));
        }
        if (_settings.UseTunMode && !Core.AdminHelper.IsRunningAsAdmin())
        {
            TunDisabledNeedsAdmin = true;
            _settings.UseTunMode = false;
            Core.Storage.SaveSettings(_settings);
            _useTunMode = false;
        }
        else
        {
            _useTunMode = _settings.UseTunMode;
        }
        var savedLang = _languages.FirstOrDefault(l => l.code == _settings.Language);
        _selectedLanguage = savedLang.name ?? "فارسی";
        Localization.TranslationManager.Instance.SetCulture(_settings.Language ?? "fa");
        if (_settings.TunExcludedApps != null)
            foreach (var app in _settings.TunExcludedApps)
                if (!string.IsNullOrWhiteSpace(app))
                    TunExcludedApps.Add(new TunExcludedItem { Path = app, Name = Path.GetFileName(app), IsEnabled = true });
        if (_settings.TunExcludedAppsDisabled != null)
            foreach (var app in _settings.TunExcludedAppsDisabled)
                if (!string.IsNullOrWhiteSpace(app) && !TunExcludedApps.Any(a => string.Equals(a.Path, app, StringComparison.OrdinalIgnoreCase)))
                    TunExcludedApps.Add(new TunExcludedItem { Path = app, Name = Path.GetFileName(app), IsEnabled = false });
        TunExcludedEmpty = TunExcludedApps.Count == 0;
        KillSwitchEnabled = _settings.KillSwitchEnabled;
        TunKillSwitch.StateChanged += OnKillSwitchStateChanged;
        TunKillSwitch.Configure(_settings.KillSwitchEnabled, _settings.TunExcludedApps, 0);
        foreach (var s in Storage.LoadSubscriptions())
            Subscriptions.Add(s);
        foreach (var server in Storage.LoadServers())
        {
            Enrich(server);
            Servers.Add(server);
        }
        foreach (var sub in Subscriptions)
        {
            foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
            {
                s.SubDataLimit = sub.DataLimit;
                s.SubUsedTraffic = sub.UsedTraffic;
                s.SubExpireDate = sub.ExpireDate;
            }
        }
        if (!string.IsNullOrEmpty(_settings.ActiveServerId))
            SelectedServer = Servers.FirstOrDefault(s => s.Id == _settings.ActiveServerId);
        if (Subscriptions.Count > 0) SelectedSubscriptionName = Subscriptions[0].Name;
        RefreshGroups();

        _engine.Output += line => Post(() => AppendLog(line));
        _engine.Stopped += () =>
        {
            bool expected = _engine.LastExitExpected;
            Post(() =>
            {
                bool wasConnected = IsConnected;
                IsConnected = false;
                if (!expected && wasConnected)
                {
                    string msg = TranslationManager.Instance["Notify_EngineDied"];
                    AppendLog(msg);
                    Core.NotificationHelper.ShowError(msg);
                }
            });
        };
        AppendLog(string.Format(TranslationManager.Instance["Log_ServersLoaded"], Servers.Count, Subscriptions.Count));

        // ارسال گزارش‌های در صف (کرشِ اجرای قبلی یا گزارشی که هنگام قطع شبکه ماند).
        _ = Task.Run(async () =>
        {
            try
            {
                int n = await Core.BugReport.FlushPendingAsync(null);
                if (n > 0)
                    Post(() => AppendLog(string.Format(TranslationManager.Instance["Log_ReportFlushed"], n)));
            }
            catch { }
        });

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _speedTimer.Tick += (_, _) => UpdateLiveSpeed();
        _speedTimer.Start();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _blinkTimer.Tick += (_, _) => BlinkTick();
        // آغاز/توقف در OnIsConnectedChanged؛ در حالتِ قطع بودن، این تایمر فقط
        // رشتهٔ UI را ۱۰ بار در ثانیه بیدار می‌کرد تا opacity را ۱ بگذارد.
        if (IsConnected) _blinkTimer.Start();
    }

    private void BlinkTick()
    {
        if (!IsConnected)
        {
            if (SpeedBlinkOpacity != 1.0) SpeedBlinkOpacity = 1.0;
            _blinkPhase = 0;
            _blinkTimer?.Stop();
            return;
        }
        _blinkPhase += 0.45;
        if (_blinkPhase > Math.PI * 2) _blinkPhase -= Math.PI * 2;
        SpeedBlinkOpacity = 0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(_blinkPhase));
    }

    private void UpdateLiveSpeed()
    {
        if (!IsConnected) { LiveDownSpeed = "0 B/s"; LiveUpSpeed = "0 B/s"; SpeedBlinkOpacity = 1.0; return; }
        try
        {
            var ni = FindActiveNetworkInterface();
            if (ni == null) return;
            if (ni.Id != _lastInterfaceId)
            {
                _lastInterfaceId = ni.Id;
                var initStats = ni.GetIPStatistics();
                _lastBytesIn = initStats.BytesReceived;
                _lastBytesOut = initStats.BytesSent;
                return;
            }
            var stats = ni.GetIPStatistics();
            long bytesIn = stats.BytesReceived;
            long bytesOut = stats.BytesSent;

            long diffIn = bytesIn - _lastBytesIn;
            long diffOut = bytesOut - _lastBytesOut;
            LiveDownSpeed = diffIn > 0 ? FormatSpeed(diffIn / 3) : "0 B/s";
            LiveUpSpeed = diffOut > 0 ? FormatSpeed(diffOut / 3) : "0 B/s";
            _lastBytesIn = bytesIn;
            _lastBytesOut = bytesOut;
        }
        catch { }
    }

    private const long NicCacheMs = 5000;
    private static System.Net.NetworkInformation.NetworkInterface? _cachedNic;
    private static long _cachedNicAt;
    private static readonly object _nicGate = new();

    /// <summary>
    /// پیمایشِ کاملِ کارت‌های شبکه (به‌همراهِ دو بار GetIPStatistics برای هر کارت) هر ۳
    /// ثانیه و روی رشتهٔ UI گران تمام می‌شد؛ نتیجه را ۵ ثانیه نگه می‌داریم و فقط وقتی
    /// کارتِ ذخیره‌شده از کار افتاده باشد دوباره می‌گردیم.
    /// </summary>
    private static System.Net.NetworkInformation.NetworkInterface? FindActiveNetworkInterface()
    {
        lock (_nicGate)
        {
            var cached = _cachedNic;
            if (cached != null && Environment.TickCount64 - _cachedNicAt < NicCacheMs)
            {
                try
                {
                    if (cached.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                        return cached;
                }
                catch { }
            }
        }

        System.Net.NetworkInformation.NetworkInterface? found = null;
        try { found = ResolveActiveNetworkInterface(); } catch { }
        lock (_nicGate) { _cachedNic = found; _cachedNicAt = Environment.TickCount64; }
        return found;
    }

    private static System.Net.NetworkInformation.NetworkInterface? ResolveActiveNetworkInterface()
    {
        var ni = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            .FirstOrDefault();
        if (ni != null) return ni;
        return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                        n.GetIPStatistics().BytesReceived > 0)
            .OrderByDescending(n => n.GetIPStatistics().BytesReceived)
            .FirstOrDefault();
    }

    private static string FormatSpeed(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B/s";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB/s";
        double mb = kb / 1024.0;
        return $"{mb:F1} MB/s";
    }

    partial void OnSelectedServerChanged(ServerInfo? value)
    {
        // DataGrid Clear() during sort can push null while still connected — restore active server
        if (value == null && IsConnected)
        {
            ServerInfo? restore = null;
            if (!string.IsNullOrEmpty(_settings.ActiveServerId))
                restore = Servers.FirstOrDefault(s => s.Id == _settings.ActiveServerId);
            restore ??= Servers.FirstOrDefault(s => s.IsActiveConnection);
            if (restore != null)
            {
                Post(() =>
                {
                    if (SelectedServer == null)
                        SelectedServer = restore;
                });
            }
            return;
        }

        if (value != null)
            _settings.ActiveServerId = value.Id;
        SaveSettings();
        RefreshConnectionFlags();
        foreach (var s in Servers)
            s.IsSelected = ReferenceEquals(s, value);
        OnPropertyChanged(nameof(HomeServerName));
        OnPropertyChanged(nameof(HomeShortName));
        OnPropertyChanged(nameof(HomeProtocol));
        OnPropertyChanged(nameof(HomeIp));
        OnPropertyChanged(nameof(HomeFlag));
        OnPropertyChanged(nameof(HomeAddress));
        OnPropertyChanged(nameof(HomeLatency));
        OnPropertyChanged(nameof(HomeSpeed));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        Status = value ? string.Format(TranslationManager.Instance["Status_ConnectedTo"], SelectedServer?.Name ?? "") : TranslationManager.Instance["Status_Ready"];
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(ShowSmartConnectButton));
        OnPropertyChanged(nameof(SmartConnectActionCommand));
        OnPropertyChanged(nameof(HomeServerName));
        OnPropertyChanged(nameof(HomeShortName));
        OnPropertyChanged(nameof(HomeProtocol));
        OnPropertyChanged(nameof(HomeIp));
        OnPropertyChanged(nameof(HomeFlag));
        OnPropertyChanged(nameof(HomeAddress));
        OnPropertyChanged(nameof(HomeLatency));
        OnPropertyChanged(nameof(HomeSpeed));
        OnPropertyChanged(nameof(ShowDisconnectedStatus));
        RefreshConnectionFlags();
        RefreshTestsBypassingTun();
        if (value) _blinkTimer?.Start();
        else
        {
            _blinkTimer?.Stop();
            SpeedBlinkOpacity = 1.0;
            _blinkPhase = 0;
        }
        if (!value)
        {
            LiveDownSpeed = "0 B/s";
            LiveUpSpeed = "0 B/s";
            _lastBytesIn = 0;
            _lastBytesOut = 0;
            _lastInterfaceId = "";
        }
    }

    partial void OnIsSmartConnectingChanged(bool value) => OnPropertyChanged(nameof(ShowSmartConnectButton));
    partial void OnDuplicateCountChanged(int value) => OnPropertyChanged(nameof(HasDuplicates));
    partial void OnSearchTestedCountChanged(int value) { OnPropertyChanged(nameof(SearchProgressPercent)); OnPropertyChanged(nameof(SearchProgressText)); }
    partial void OnSearchTotalCountChanged(int value) { OnPropertyChanged(nameof(SearchProgressPercent)); OnPropertyChanged(nameof(SearchProgressText)); }
    partial void OnTestTestedCountChanged(int value) { OnPropertyChanged(nameof(TestProgressPercent)); OnPropertyChanged(nameof(TestProgressText)); }
    partial void OnTestTotalCountChanged(int value) { OnPropertyChanged(nameof(TestProgressPercent)); OnPropertyChanged(nameof(TestProgressText)); }


    public async Task OnStartedAsync()
    {
        // صدازننده async void است؛ هر استثنایی اینجا باعث crash کل برنامه می‌شود.
        try
        {
            StartRemoteChecks();
            if (AutoUpdateSubs && Subscriptions.Count > 0)
            {
                var since = DateTime.Now - _settings.LastSubscriptionUpdate;
                if (since.TotalHours >= AutoUpdateIntervalHours)
                    await UpdateSubscriptions();
            }
            bool restartReconnect = Program.StartupArgs.Length > 0 && Array.Exists(Program.StartupArgs, a => a == "--reconnect");
            // ری‌استارتِ بعد از قطعِ عمدی (مثلاً عوض کردن زبان) نباید دوباره وصل شود؛
            // AutoConnect فقط برای اجرای عادیِ برنامه است، نه برای این اجرا.
            bool noAutoConnect = Program.StartupArgs.Length > 0 && Array.Exists(Program.StartupArgs, a => a == "--no-autoconnect");
            if ((restartReconnect || (AutoConnect && !noAutoConnect)) && !IsConnected)
            {
                if (SelectedServer != null)
                    await Connect();
                else
                {
                    AppendLog(TranslationManager.Instance["Log_AutoConnectNoServer"]);
                    ToastHandler?.Invoke(TranslationManager.Instance["Toast_AutoConnectSelect"]);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
        }
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private readonly object _logLock = new();
    private readonly List<string> _allLogLines = new();
    private readonly List<string> _visibleLines = new();
    private DateTime _lastLogFlush = DateTime.MinValue;
    private readonly List<string> _logBuffer = new();
    [ObservableProperty] private string _logFilter = TranslationManager.Instance["Filter_All"];
    [ObservableProperty] private string _filteredLogText = "";
    public string[] LogFilters { get; } = [TranslationManager.Instance["Filter_All"], TranslationManager.Instance["Filter_Errors"], TranslationManager.Instance["Filter_Connection"]];
    private const int MaxVisibleLines = 20;

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logLock)
        {
            _logBuffer.Add(line);
            if ((DateTime.Now - _lastLogFlush).TotalMilliseconds < 1000)
            {
                DispatcherTimer.RunOnce(FlushLog, TimeSpan.FromMilliseconds(1100));
                return;
            }
            FlushLogUnsafe();
        }
    }

    private void FlushLog()
    {
        lock (_logLock)
        {
            if (_logBuffer.Count == 0) return;
            FlushLogUnsafe();
        }
    }

    private void FlushLogUnsafe()
    {
        _allLogLines.AddRange(_logBuffer);
        _logBuffer.Clear();
        _lastLogFlush = DateTime.Now;
        if (_allLogLines.Count > 200)
            _allLogLines.RemoveRange(0, _allLogLines.Count - 200);
        RebuildVisibleLines();
        Post(() => { FilteredLogText = string.Join(Environment.NewLine, _visibleLines); });
    }

    partial void OnLogFilterChanged(string value) { lock (_logLock) { RebuildVisibleLines(); } FilteredLogText = string.Join(Environment.NewLine, _visibleLines); }

    private void RebuildVisibleLines()
    {
        _visibleLines.Clear();
        int start = Math.Max(0, _allLogLines.Count - 100);

        // فیلتر را یک‌بار در هر بازسازی حل می‌کنیم، نه به‌ازای هر خط؛ قبلاً هر
        // نوبت چند صد جست‌وجوی ResourceManager روی رشتهٔ UI انجام می‌شد.
        string filter = LogFilter;
        bool showAll = true, errorsOnly = false, connectionOnly = false;
        if (string.Equals(filter, TranslationManager.Instance["Filter_Errors"], StringComparison.Ordinal))
        { showAll = false; errorsOnly = true; }
        else if (string.Equals(filter, TranslationManager.Instance["Filter_Connection"], StringComparison.Ordinal))
        { showAll = false; connectionOnly = true; }

        for (int i = start; i < _allLogLines.Count; i++)
        {
            var l = _allLogLines[i];
            bool show;
            if (errorsOnly) show = LogMatches(l, "Status_Error") || l.Contains("error", StringComparison.OrdinalIgnoreCase);
            else if (connectionOnly) show = LogMatches(l, "Status_Connected") || LogMatches(l, "Status_Disconnected") || LogMatches(l, "Log_Connected") || LogMatches(l, "Log_Disconnected") || LogMatches(l, "Ctx_Connect") || l.Contains("connect", StringComparison.OrdinalIgnoreCase);
            else show = showAll;
            if (show) _visibleLines.Add(l);
        }
        while (_visibleLines.Count > MaxVisibleLines)
            _visibleLines.RemoveAt(0);
    }

    private static bool LogMatches(string line, string key)
    {
        foreach (var raw in TranslationManager.Instance[key].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('(', ')', '.', ',', ':', ';', '!', '?', '"', '\'');
            if (word.Length < 2 || word.Contains('{')) continue;
            if (line.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void Enrich(ServerInfo s) => s.CountryCode = GeoFlags.DetectCountry(s.Name, s.Address);
    private void SaveServers() => Storage.SaveServers(Servers.ToList());

    private readonly object _settingsSaveGate = new();
    private System.Threading.Timer? _settingsSaveTimer;
    private const int SettingsSaveDelayMs = 500;

    private void SaveSettings()
    {
        // ذخیره را ۵۰۰ میلی‌ثانیه عقب می‌اندازیم؛ قبلاً هر یک از حدود ۲۵ setter
        // (حتی انتخابِ هر سرور) بلافاصله سریالایز و روی دیسک نوشته می‌شد و با
        // حرکتِ سریع در رابط ده‌ها نوشتنِ پشت‌سرهم داشتیم.
        lock (_settingsSaveGate)
        {
            _settingsSaveTimer ??= new System.Threading.Timer(
                _ => SaveSettingsNow(), null, -1, -1);
            _settingsSaveTimer.Change(SettingsSaveDelayMs, -1);
        }
    }

    /// <summary>
    /// فوری‌کردنِ ذخیرهٔ به‌تعویق‌افتاده. خروجِ برنامه این را صدا می‌زند تا آخرین
    /// تغییرِ تنظیمات در ۵۰۰ میلی‌ثانیهٔ آخر گم نشود.
    /// </summary>
    /// <summary>
    /// برای مسیرهایی که بلافاصله ری‌استارت یا خروج دارند. نباید منتظرِ ذخیرهٔ
    /// debounce شده بمانند، چون نمونهٔ بعدی همین فایل را می‌خواند.
    /// </summary>
    public void FlushPendingSettings() => SaveSettingsNow();

    private void SaveSettingsNow()
    {
        System.Threading.Timer? pending;
        lock (_settingsSaveGate)
        {
            pending = _settingsSaveTimer;
            _settingsSaveTimer = null;
        }
        try { pending?.Dispose(); } catch { }
        try { Storage.SaveSettings(_settings); } catch { }
    }
    private void SaveSubs() => Storage.SaveSubscriptions(Subscriptions.ToList());

     partial void OnIsTestingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();


    public Action? ShowAdminWarningHandler { get; set; }
    public bool TunDisabledNeedsAdmin { get; private set; }


    private async Task CopyTextAsync(string text)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(text);
    }

    // ---------- سرور ----------


      public void Shutdown()
      {
          // رویدادِ static را رها می‌کنیم؛ وگرنه نمونهٔ قبلیِ ViewModel با کلِ
          // نمودارِ اشیایش برای همیشه نگه داشته می‌شود.
          try { TunKillSwitch.StateChanged -= OnKillSwitchStateChanged; } catch { }
          // ذخیرهٔ به‌تعویق‌افتادهٔ تنظیمات را همین‌جا می‌ریزیم تا گم نشود.
          SaveSettingsNow();
          try { SaveServers(); } catch { }
          _engine.Dispose();
      }
 }
