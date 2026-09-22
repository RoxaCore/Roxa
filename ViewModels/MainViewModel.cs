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

public enum PingType { Ping, TcpPing, RealDelay, HttpPing }

public partial class MainViewModel : ViewModelBase
{
    private readonly XrayEngine _engine = new();
    private AppSettings _settings = new();
    private CancellationTokenSource _testCts = new();

     public IClipboard? Clipboard { get; set; }
     public Func<ServerInfo, Task<bool>>? EditServerHandler { get; set; }
     public Func<string, string, Task>? ShowQrHandler { get; set; }
     public Func<string, Task<bool>>? ConfirmDeleteHandler { get; set; }
     public Action<string>? ToastHandler { get; set; }
     public Action? OpenDnsSettingsHandler { get; set; }

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
    [ObservableProperty] private int _autoUpdateIntervalHours = 12;
     [ObservableProperty] private PingType _selectedPingType = PingType.Ping;
     public PingType[] PingTypeOptions { get; } = (PingType[])Enum.GetValues(typeof(PingType));
      [ObservableProperty] private bool _isTesting;
      [ObservableProperty] private bool _isSmartConnecting;
      [ObservableProperty] private bool _isFindingBetter;
      [ObservableProperty] private int _searchTestedCount;
      [ObservableProperty] private int _searchTotalCount;

      public double SearchProgressPercent => SearchTotalCount > 0 ? (double)SearchTestedCount / SearchTotalCount : 0;
      public string SearchProgressText => SearchTotalCount > 0
          ? string.Format(TranslationManager.Instance["Tray_SearchingProgress"], SearchTestedCount, SearchTotalCount)
          : TranslationManager.Instance["Tray_Searching"];
      public string LastUpdateDisplay
      {
          get
          {
              if (_settings.LastSubscriptionUpdate == DateTime.MinValue) return "آخرین بروزرسانی: هرگز";
              var elapsed = DateTime.Now - _settings.LastSubscriptionUpdate;
              if (elapsed.TotalHours < 1) return $"آخرین بروزرسانی: {(int)elapsed.TotalMinutes} دقیقه پیش";
              if (elapsed.TotalDays < 1) return $"آخرین بروزرسانی: {(int)elapsed.TotalHours} ساعت پیش";
              return $"آخرین بروزرسانی: {(int)elapsed.TotalDays} روز پیش";
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
      [ObservableProperty] private string _selectedSubscriptionName = "";
      [ObservableProperty] private string _sortField = TranslationManager.Instance["Sort_None"];
      [ObservableProperty] private bool _globalSort;
      [ObservableProperty] private int _selectedCount;
      [ObservableProperty] private bool _selectAllChecked;
      [ObservableProperty] private bool _sortAscending = true;
      [ObservableProperty] private string _customDnsServers = "";
      [ObservableProperty] private string _customDnsAdvancedJson = "";
    [ObservableProperty] private bool _showAdvancedDns;
    [ObservableProperty] private bool _useTunMode;
    [ObservableProperty] private bool _useProxyForSubscriptions;
      [ObservableProperty] private Dictionary<int, double> _columnWidths = new()
      {
          { 0, 80 },   // Type
          { 1, 150 },  // Remarks
          { 2, 150 },  // Address
          { 3, 70 },   // Port
          { 4, 80 },   // Security
          { 5, 80 },   // Transport
          { 6, 70 },   // TLS
          { 7, 90 },   // Subs group
          { 8, 90 },   // Delay
           { 9, 90 }    // Speed
       };

      public string[] SortOptions => [TranslationManager.Instance["Sort_None"], TranslationManager.Instance["Sort_Latency"], TranslationManager.Instance["Sort_Speed"], TranslationManager.Instance["Sort_Name"]];

    public string[] LanguageOptions { get; } = ["فارسی", "English", "中文", "Русский", "Türkçe", "اردو", "Español", "Français", "한국어", "Deutsch", "日本語", "Português", "हिन्दी"];
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

    public event Action? RestartRequested;

    public string HomeServerName => SelectedServer?.Name ?? "سروری انتخاب نشده";
    public string HomeShortName => SelectedServer?.ShortName ?? "";
    public string HomeFlag => SelectedServer?.Flag ?? "🌐";
    public string HomeAddress => SelectedServer != null ? $"{SelectedServer.Address}:{SelectedServer.Port}" : "";
    public string HomeLatency => SelectedServer?.LatencyDisplay ?? "";
    public string HomeSpeed => SelectedServer?.SpeedDisplay ?? "";
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

    public MainViewModel()
    {
        _settings = Storage.LoadSettings();
        SetSystemProxy = _settings.SetSystemProxy;
        AutoConnect = _settings.AutoConnect;
        AutoUpdateSubs = _settings.AutoUpdateSubs;
        AutoUpdateIntervalHours = _settings.AutoUpdateIntervalHours <= 0 ? 12 : _settings.AutoUpdateIntervalHours;
        _sortField = _settings.SortField;
        _sortAscending = _settings.SortAscending;
        _globalSort = _settings.GlobalSort;
        _customDnsServers = _settings.CustomDnsServers ?? "";
        _customDnsAdvancedJson = _settings.CustomDnsAdvancedJson ?? "";
        _leaveSystemProxyAsIs = _settings.LeaveSystemProxyAsIs;
        _clearProxyOnDisconnect = _settings.ClearProxyOnDisconnect;
        _useProxyForSubscriptions = _settings.UseProxyForSubscriptions;
        if (_settings.UseTunMode && !Core.AdminHelper.IsRunningAsAdmin())
        {
            _settings.UseTunMode = false;
            Core.Storage.SaveSettings(_settings);
            _useTunMode = false;
        }
        else
        {
            _useTunMode = _settings.UseTunMode;
        var savedLang = _languages.FirstOrDefault(l => l.code == _settings.Language);
        _selectedLanguage = savedLang.name ?? "فارسی";
        Localization.TranslationManager.Instance.SetCulture(_settings.Language ?? "fa");
        }
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
        _engine.Stopped += () => Post(() => IsConnected = false);
        AppendLog(string.Format(TranslationManager.Instance["Log_ServersLoaded"], Servers.Count, Subscriptions.Count));

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _speedTimer.Tick += (_, _) => UpdateLiveSpeed();
        _speedTimer.Start();
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
            SpeedBlinkOpacity = SpeedBlinkOpacity > 0.5 ? 0.3 : 1.0;
            _lastBytesIn = bytesIn;
            _lastBytesOut = bytesOut;
        }
        catch { }
    }

    private static System.Net.NetworkInformation.NetworkInterface? FindActiveNetworkInterface()
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
        _settings.ActiveServerId = value?.Id;
        SaveSettings();
        foreach (var s in Servers)
            s.IsSelected = ReferenceEquals(s, value);
        OnPropertyChanged(nameof(HomeServerName));
        OnPropertyChanged(nameof(HomeShortName));
        OnPropertyChanged(nameof(HomeFlag));
        OnPropertyChanged(nameof(HomeAddress));
        OnPropertyChanged(nameof(HomeLatency));
        OnPropertyChanged(nameof(HomeSpeed));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        Status = value ? $"{TranslationManager.Instance["Status_Connected"]} به {SelectedServer?.Name ?? ""}" : TranslationManager.Instance["Status_Ready"];
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(ShowSmartConnectButton));
        OnPropertyChanged(nameof(SmartConnectActionCommand));
        RefreshConnectionFlags();
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
    partial void OnAutoUpdateIntervalHoursChanged(int value)
    {
        _settings.AutoUpdateIntervalHours = Math.Clamp(value, 1, 168);
        SaveSettings();
    }

    public async Task OnStartedAsync()
    {
        if (AutoUpdateSubs && Subscriptions.Count > 0)
        {
            var since = DateTime.Now - _settings.LastSubscriptionUpdate;
            if (since.TotalHours >= AutoUpdateIntervalHours)
                await UpdateSubscriptions();
        }
        if (AutoConnect && !IsConnected)
        {
            if (SelectedServer != null)
                await Connect();
            else
            {
                AppendLog("اتصال خودکار: سروری انتخاب نشده است.");
                ToastHandler?.Invoke("اتصال خودکار: ابتدا یک سرور انتخاب کنید");
            }
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
            if ((DateTime.Now - _lastLogFlush).TotalMilliseconds < 1000) return;
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
        for (int i = start; i < _allLogLines.Count; i++)
        {
            var l = _allLogLines[i];
            bool show = LogFilter switch
            {
                var f when f == TranslationManager.Instance["Filter_Errors"] => l.Contains("خطا") || l.Contains("error", StringComparison.OrdinalIgnoreCase),
                var f when f == TranslationManager.Instance["Filter_Connection"] => l.Contains("اتصال") || l.Contains("متصل") || l.Contains("قطع") || l.Contains("connect", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (show) _visibleLines.Add(l);
        }
        while (_visibleLines.Count > MaxVisibleLines)
            _visibleLines.RemoveAt(0);
    }

    private static void Enrich(ServerInfo s) => s.CountryCode = GeoFlags.DetectCountry(s.Name, s.Address);
    private void SaveServers() => Storage.SaveServers(Servers.ToList());
    private void SaveSettings() => Storage.SaveSettings(_settings);
    private void SaveSubs() => Storage.SaveSubscriptions(Subscriptions.ToList());

     partial void OnIsTestingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();

     partial void OnSelectAllCheckedChanged(bool value)
     {
         foreach (var s in Servers)
             s.IsChecked = value;
     }

    /// <summary>بازسازی گروه‌های تاشو از روی سرورها و ساب‌ها. وضعیت باز/بسته هر گروه حفظ می‌شود.</summary>
    public void RefreshGroups()
    {
        var expanded = Groups.ToDictionary(g => g.Key, g => g.IsExpanded);
        Groups.Clear();
        foreach (var s in Servers)
            s.Owner = this;
        RefreshConnectionFlags();

        var seen = new HashSet<string>();
        int dupeCount = 0;
        foreach (var s in Servers)
        {
            string key = $"{s.Address}:{s.Port}:{s.UserId}:{s.Password}";
            if (!seen.Add(key)) dupeCount++;
        }
        DuplicateCount = dupeCount;

        // ساب‌ها — جدیدترین بالاتر
        foreach (var sub in Subscriptions.AsEnumerable().Reverse())
        {
            bool isExp = expanded.TryGetValue(sub.Name, out bool e) ? e : !_settings.CollapsedGroups.Contains(sub.Name);
            var group = new ServerGroup(this, sub.Name, sub.Name, sub, isExp);
            var sortedServers = Servers.Where(s => s.SubscriptionName == sub.Name).ToList();
            SortServers(sortedServers);
            foreach (var s in sortedServers)
                group.Servers.Add(s);
            Groups.Add(group);
        }
        var manual = Servers.Where(s => string.IsNullOrEmpty(s.SubscriptionName)).ToList();
        if (manual.Count > 0)
        {
            bool isExp = expanded.TryGetValue("", out bool e) ? e : !_settings.CollapsedGroups.Contains("");
            var group = new ServerGroup(this, "", "📝 دستی", null, isExp);
            SortServers(manual);
            foreach (var s in manual)
                group.Servers.Add(s);
            Groups.Add(group);
        }

        // تب «همه سرورها» — آخرین تب، بسته به صورت پیش‌فرض
        bool allExp = expanded.TryGetValue("__all__", out bool ae) ? ae : false;
        var allGroup = new ServerGroup(this, "__all__", TranslationManager.Instance["AllServers_Title"], null, allExp);
        var allServers = Servers.ToList();
        SortServers(allServers);
        foreach (var s in allServers)
            allGroup.Servers.Add(s);
        Groups.Add(allGroup);
    }

    private void UpdateGroupContent(ServerGroup group)
    {
        var servers = Servers.Where(s =>
            string.IsNullOrEmpty(s.SubscriptionName) ? string.IsNullOrEmpty(group.Key) :
            s.SubscriptionName == group.Key).ToList();
        SortServers(servers);
        group.Servers.Clear();
        foreach (var s in servers)
            group.Servers.Add(s);
        group.UpdateTitle();
    }

    private void SortServers(List<ServerInfo> servers)
    {
        try
        {
            var connected = servers.FirstOrDefault(s => s.IsActiveConnection);
            var rest = servers.Where(s => s != connected).ToList();

            List<ServerInfo> sorted;
            switch (SortField)
            {
                case var sf when sf == TranslationManager.Instance["Sort_Latency"]:
                    sorted = rest.OrderBy(s => s.LatencyMs < 0 ? int.MaxValue : s.LatencyMs).ToList();
                    break;
                case var sf when sf == TranslationManager.Instance["Sort_Speed"]:
                    sorted = rest.OrderByDescending(s => s.DownloadMbps < 0 ? -1 : s.DownloadMbps).ToList();
                    break;
                case var sf when sf == TranslationManager.Instance["Sort_Name"]:
                    sorted = rest.OrderBy(s => s.Name).ToList();
                    break;
                default:
                    sorted = rest.OrderBy(s => 0).ThenBy(s => s.Name).ToList();
                    break;
            }

            servers.Clear();
            if (connected != null) servers.Add(connected);
            foreach (var s in sorted) servers.Add(s);
        }
        catch
        {
        }
    }

    private void AutoSortAfterTest(string metric)
    {
        if (GlobalSort)
        {
            var allServers = Servers.ToList();
            var connected = allServers.FirstOrDefault(s => s.IsActiveConnection);
            var rest = allServers.Where(s => s != connected).ToList();
            rest.Sort(ServerInfo.CompareByPriority);

            var sorted = new List<ServerInfo>();
            if (connected != null) sorted.Add(connected);
            sorted.AddRange(rest);

            var grouped = sorted.GroupBy(s => s.SubscriptionName ?? "").ToDictionary(g => g.Key, g => g.ToList());
            foreach (var g in Groups)
            {
                g.Servers.Clear();
                if (g.Key == "__all__")
                {
                    foreach (var s in sorted) g.Servers.Add(s);
                    continue;
                }
                if (grouped.TryGetValue(g.Key == "__all__" ? "" : g.Key, out var groupServers))
                    foreach (var s in groupServers) g.Servers.Add(s);
            }
        }
        else
        {
            foreach (var g in Groups)
            {
                if (g.Key == "__all__")
                {
                    g.Servers.Clear();
                    foreach (var s in Servers) g.Servers.Add(s);
                    continue;
                }
                var connected = g.Servers.FirstOrDefault(s => s.IsActiveConnection);
                var rest = g.Servers.Where(s => s != connected).ToList();
                rest.Sort(ServerInfo.CompareByPriority);

                g.Servers.Clear();
                if (connected != null) g.Servers.Add(connected);
                foreach (var s in rest) g.Servers.Add(s);
            }
        }
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
    }

    partial void OnUseProxyForSubscriptionsChanged(bool value)
    {
        _settings.UseProxyForSubscriptions = value;
        SaveSettings();
    }

    public Action? ShowAdminWarningHandler { get; set; }

    [RelayCommand]
    private void OpenDnsSettings() => OpenDnsSettingsHandler?.Invoke();

    public void SetGroupExpanded(string key, bool expanded)
    {
        if (expanded) _settings.CollapsedGroups.Remove(key);
        else _settings.CollapsedGroups.Add(key);
        SaveSettings();
    }

    private void RefreshConnectionFlags()
    {
        foreach (var s in Servers)
            s.IsActiveConnection = IsConnected && ReferenceEquals(s, SelectedServer);
    }

    private async Task CopyTextAsync(string text)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(text);
    }

    // ---------- سرور ----------

    [RelayCommand]
    private void AddLink()
    {
        string input = (ServerLink ?? "").Trim();
        if (input.Length == 0) return;
        int added = 0;
        int duplicates = 0;
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UrlParser.IsProtocolLink(line)) { AppendLog(TranslationManager.Instance["Log_InvalidLink"]); continue; }
            try
            {
                var server = UrlParser.Parse(line);
                Enrich(server);
                if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal)))
                {
                    AppendLog(string.Format(TranslationManager.Instance["Log_ServerDup"], server.Name));
                    Status = $"سرور «{server.Name}» قبلا اضافه شده است";
                    ToastHandler?.Invoke($"سرور «{server.Name}» قبلا اضافه شده است");
                    duplicates++;
                    continue;
                }
                Servers.Insert(0, server);
                server.IsHighlighted = true;
                added++;
                AppendLog(string.Format(TranslationManager.Instance["Log_ServerAdded"], server.Name));
            }
            catch (FormatException ex) { AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message)); }
        }
        if (added > 0) { SaveServers(); SelectedServer = Servers.LastOrDefault(); Status = $"{added} سرور اضافه شد"; }
        else if (duplicates > 0) { Status = $"{duplicates} سرور تکراری بود"; }
        ServerLink = "";
        RefreshGroups();
    }

    [RelayCommand]
    private async Task AddManualServer(string? protocol)
    {
        var server = new ServerInfo();
        if (Enum.TryParse<ProxyProtocol>(protocol, out var p))
            server.Protocol = p;
        server.Name = $"سرور جدید ({protocol})";

        if (EditServerHandler != null && await EditServerHandler(server))
        {
            Enrich(server);
            Servers.Insert(0, server);
            server.IsHighlighted = true;
            SaveServers();
            SelectedServer = server;
            RefreshGroups();
            AppendLog(string.Format(TranslationManager.Instance["Log_ServerAdded"], server.Name));
            Status = $"سرور «{server.Name}» اضافه شد";
            ToastHandler?.Invoke($"سرور «{server.Name}» اضافه شد");
        }
    }

    [RelayCommand]
    private async Task ImportFromClipboard()
    {
        if (Clipboard == null) { Status = "دسترسی به کلیپبورد ممکن نیست"; return; }
        var text = await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) { Status = "کلیپبورد خالی است"; return; }

        int added = 0;
        int duplicates = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UrlParser.IsProtocolLink(line)) continue;
            try
            {
                var server = UrlParser.Parse(line);
                Enrich(server);
                if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal)))
                {
                    duplicates++;
                    continue;
                }
                Servers.Insert(0, server);
                server.IsHighlighted = true;
                added++;
            }
            catch { }
        }
        if (added > 0)
        {
            SaveServers();
            RefreshGroups();
            Status = $"{added} سرور از کلیپبورد اضافه شد";
            ToastHandler?.Invoke($"{added} سرور اضافه شد");
        }
        else if (duplicates > 0) { Status = $"{duplicates} سرور تکراری بود"; }
        else { Status = "لینک معتبری در کلیپبورد نبود"; }
    }

     [RelayCommand]
     private async Task RemoveServer(ServerInfo? server)
     {
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
         if (targets.Count == 0) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler($"آیا از حذف {targets.Count} سرور متمعن هستید؟");
             if (!confirmed) return;
         }
         foreach (var s in targets)
         {
             if (ReferenceEquals(SelectedServer, s) && IsConnected) Disconnect();
             Servers.Remove(s);
             if (ReferenceEquals(SelectedServer, s)) SelectedServer = null;
         }
         SaveServers();
         RefreshGroups();
          AppendLog(string.Format(TranslationManager.Instance["Log_ServersDeleted"], targets.Count));
      }

    [RelayCommand]
    private void CloneServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        var clone = server.Clone();
        Enrich(clone);
        clone.Link = UrlBuilder.Build(clone);
        Servers.Add(clone);
        SaveServers();
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], clone.Name));
    }

    [RelayCommand]
    private void RemoveDuplicates()
    {
        var seen = new HashSet<string>();
        var dupes = new List<ServerInfo>();
        foreach (var s in Servers)
        {
            string key = $"{s.Address}:{s.Port}:{s.UserId}:{s.Password}";
            if (!seen.Add(key)) dupes.Add(s);
        }
        foreach (var d in dupes)
        {
            if (ReferenceEquals(SelectedServer, d) && IsConnected) Disconnect();
            Servers.Remove(d);
        }
        SaveServers();
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_DupesRemoved"], dupes.Count));
    }

    [RelayCommand]
    private void SelectServer(ServerInfo? server)
    {
        if (server != null) SelectedServer = server;
    }

    [RelayCommand]
    private async Task EditServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null || EditServerHandler == null) return;
        string backup = JsonSerializer.Serialize(server);
        if (await EditServerHandler(server))
        {
            server.Link = UrlBuilder.Build(server);
            Enrich(server);
            SaveServers();
            OnPropertyChanged(nameof(HomeServerName));
            OnPropertyChanged(nameof(HomeShortName));
            OnPropertyChanged(nameof(HomeFlag));
            OnPropertyChanged(nameof(HomeAddress));
            AppendLog($"«{server.Name}» ویرایش شد.");
        }
        else
        {
            var restored = JsonSerializer.Deserialize<ServerInfo>(backup);
            if (restored != null)
                foreach (var prop in typeof(ServerInfo).GetProperties().Where(p => p.CanWrite && p.Name != nameof(ServerInfo.Id)))
                    prop.SetValue(server, prop.GetValue(restored));
        }
    }

    [RelayCommand]
    private async Task ShareServer(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        string link = UrlBuilder.Build(server);
        server.Link = link;
        await CopyTextAsync(link);
        Status = TranslationManager.Instance["Status_Ready"];
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], server.Name));
        if (ShowQrHandler != null)
            await ShowQrHandler(server.Name, link);
    }

    [RelayCommand]
    private async Task CopyLink(ServerInfo? server)
    {
        server ??= SelectedServer;
        if (server == null) return;
        await CopyTextAsync(UrlBuilder.Build(server));
        AppendLog(string.Format(TranslationManager.Instance["Log_Copied"], server.Name));
    }

    [RelayCommand]
    private void Sort(string? field)
    {
        if (!string.IsNullOrWhiteSpace(field)) SortField = field;
        string sortKey = SortOptions.Zip(ServerGroup.SortOptionKeys, (opt, k) => (opt, k))
            .FirstOrDefault(x => x.opt == SortField).k ?? "none";
        List<ServerInfo> ordered = sortKey switch
        {
            "name" => Servers.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "speed" => Servers.OrderByDescending(s => s.DownloadMbps).ToList(),
            _ => Servers.OrderBy(s => s.LatencyMs < 0 ? int.MaxValue : s.LatencyMs).ToList()
        };
        Servers.Clear();
        foreach (var s in ordered)
            Servers.Add(s);
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Sorted"], SortField));
    }

    // ---------- اتصال ----------

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect()
    {
        var server = SelectedServer;
        if (server == null || !Servers.Contains(server)) return;
        try
        {
            Status = "در حال اتصال ...";
            await _engine.StartAsync(server, SetSystemProxy, _settings);
            IsConnected = true;
            LiveDownSpeed = "0 B/s";
            LiveUpSpeed = "0 B/s";
            SpeedBlinkOpacity = 1.0;
            RefreshConnectionFlags();
            Status = $"متصل به {server.Name}";
            _settings.ActiveServerId = server.Id;
            SaveSettings();
            AppendLog(string.Format(TranslationManager.Instance["Log_Connected"], _engine.HttpPort));
            NotificationHelper.ShowConnected(server.Name, server.LatencyDisplay);

            // Initialize byte counters for speed calculation
            _ = Task.Run(() =>
            {
                try
                {
                    var ni = FindActiveNetworkInterface();
                    if (ni != null)
                    {
                        _lastInterfaceId = ni.Id;
                        var stats = ni.GetIPStatistics();
                        _lastBytesIn = stats.BytesReceived;
                        _lastBytesOut = stats.BytesSent;
                    }
                }
                catch { }
            });

            // آپدیت تأخیر واقعی بعد از اتصال
            _ = Task.Run(async () =>
            {
                try
                {
                    using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{_engine.HttpPort}"), UseProxy = true };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync("http://www.gstatic.com/generate_204");
                    sw.Stop();
                    if (resp.IsSuccessStatusCode)
                    {
                        int ms = (int)sw.ElapsedMilliseconds;
                        Post(() => server.RecordRealDelay(ms));
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            IsConnected = false;
            RefreshConnectionFlags();
            Status = "خطا در اتصال";
            NotificationHelper.ShowError("Connection failed");
            AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message));
        }
        finally { ConnectCommand.NotifyCanExecuteChanged(); }
    }

    private bool CanConnect() => SelectedServer != null && !IsConnected && !IsTesting;

    [RelayCommand]
    private void Disconnect()
    {
        if (UseTunMode)
            _engine.StopAndCleanupTun();
        else
            _engine.Stop();
        IsConnected = false;
        RefreshConnectionFlags();
        AppendLog(TranslationManager.Instance["Log_Disconnected"]);
        NotificationHelper.ShowDisconnected();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        if (SelectedServer == null) return;
        if (IsConnected) Disconnect();
        await Connect();
    }

    [RelayCommand]
    private async Task ConnectServer(ServerInfo? server)
    {
        if (server != null) SelectedServer = server;
        RefreshConnectionFlags();
        if (SelectedServer != null && !IsConnected && !IsTesting)
            await Connect();
    }

    // ---------- تست ----------

    [RelayCommand]
    private void ToggleTun() => UseTunMode = !UseTunMode;

    [RelayCommand]
    private void ToggleSystemProxy() => SetSystemProxy = !SetSystemProxy;

    [RelayCommand]
    private async Task SmartConnect()
    {
        if (IsTesting) return;
        if (Servers.Count == 0) { Status = "سروری نیست"; ToastHandler?.Invoke("سروری برای اتصال وجود ندارد"); return; }
        if (IsConnected) Disconnect();

        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        var ct = _testCts.Token;
        IsTesting = true;
        IsSmartConnecting = true;

        try
        {
            int totalServers = Servers.Count;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalServers;
            OnPropertyChanged(nameof(SearchProgressText));

            // --- مرحله ۱: پینگ سریع TCP روی همه سرورها ---
            Status = "اتصال هوشمند: پینگ سریع ...";
            ToastHandler?.Invoke("در حال پیدا کردن بهترین سرور ...");
            AppendLog("Smart connect: stage 1 - fast TCP ping on all servers");

            foreach (var s in Servers) { s.LatencyMs = -1; s.RealDelayMs = -1; s.PingMs = -1; s.DownloadMbps = -1; }

            using var sem1 = new SemaphoreSlim(20);
            var pingTasks = Servers.ToList().Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem1.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000);
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally
                {
                    try { sem1.Release(); } catch { }
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });
                }
            });
            try { await Task.WhenAll(pingTasks); }
            catch (OperationCanceledException) { }

            // صبر تا همه Post() ها اجرا بشن
            await Dispatcher.UIThread.InvokeAsync(() => { });
            AutoSortAfterTest("latency");

            // --- انتخاب ۱۲ کاندید برتر ---
            var candidates = Servers.Where(s => s.LatencyMs > 0).OrderBy(s => s.LatencyMs).Take(12).ToList();

            // ذخیره مابقی سرورها برای جستجوی تدریجی
            var candidateIds = new HashSet<string>(candidates.Select(c => c.Id));
            _searchPool = Servers.Where(s => !candidateIds.Contains(s.Id)).ToList();
            _lastSearchTime = DateTime.Now;
            AppendLog($"Search pool: {_searchPool.Count} untested servers saved for incremental search");

            // آپدیت تعداد کل برای فاز تأخیر واقعی
            tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = candidates.Count;
            OnPropertyChanged(nameof(SearchProgressText));

            if (candidates.Count == 0)
            {
                IsTesting = false;
                IsSmartConnecting = false;
                Status = "اتصال هوشمند: هیچ سروری پاسخ نداد";
                ToastHandler?.Invoke("هیچ سروری پاسخ نداد");
                AppendLog("Smart connect: no server responded to ping");
                return;
            }

            AppendLog($"Smart connect: {candidates.Count} candidates selected (best: {candidates[0].LatencyDisplay})");

            // --- مرحله ۲: تأخیر واقعی یکی یکی ---
            Status = $"اتصال هوشمند: تأخیر واقعی {candidates.Count} کاندید ...";
            AppendLog($"Smart connect: testing {candidates.Count} candidates one by one");

            foreach (var candidate in candidates)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    AppendLog($"Smart connect: testing {candidate.Name} ...");
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });

                    var testEngine = new XrayEngine();
                    try
                    {
                        int socks = XrayEngine.FindFreePort();
                        int http = XrayEngine.FindFreePort();
                        string cfg = XrayConfigBuilder.Build(candidate, socks, http);
                        await testEngine.StartWithConfigAsync(cfg, http, ct);

                        using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{http}"), UseProxy = true };
                        using var http2 = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

                        var sw = Stopwatch.StartNew();
                        using var resp = await http2.GetAsync("http://www.gstatic.com/generate_204", ct);
                        sw.Stop();

                        if (resp.IsSuccessStatusCode)
                        {
                            int ms = (int)sw.ElapsedMilliseconds;
                            AppendLog($"Smart connect: {candidate.Name} → {ms}ms");
                            Post(() => candidate.RecordRealDelay(ms));
                        }
                        else
                        {
                            AppendLog($"Smart connect: {candidate.Name} → failed (status {resp.StatusCode})");
                            Post(() => candidate.RecordRealDelay(-2));
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Smart connect: {candidate.Name} → error: {ex.Message}");
                        Post(() => candidate.RecordRealDelay(-2));
                    }
                    finally
                    {
                        try { testEngine.Stop(); testEngine.Dispose(); } catch { }
                    }
                }
                catch (OperationCanceledException) { break; }
            }

            // --- انتخاب نهایی ---
            var finalBest = candidates.Where(s => s.LatencyMs > 0).OrderBy(s => s.LatencyMs).FirstOrDefault();

            IsTesting = false;
            IsSmartConnecting = false;

            if (finalBest == null)
            {
                Status = "اتصال هوشمند: تأخیر واقعی ناموفق";
                ToastHandler?.Invoke("تأخیر واقعی ناموفق بود");
                NotificationHelper.ShowError("Smart connect: real delay test failed");
                AppendLog("Smart connect: real delay test failed for all candidates");
                return;
            }

            Status = $"اتصال هوشمند: بهترین سرور → {finalBest.Name} ({finalBest.LatencyDisplay})";
            ToastHandler?.Invoke($"اتصال به {finalBest.Name} ({finalBest.LatencyDisplay})");
            NotificationHelper.ShowSmartConnect(finalBest.Name, finalBest.LatencyDisplay);
            AppendLog($"Smart connect: connecting to {finalBest.Name} ({finalBest.LatencyDisplay})");

            SelectedServer = finalBest;
            RefreshConnectionFlags();
            await Connect();
        }
        catch (OperationCanceledException)
        {
            IsTesting = false;
            IsSmartConnecting = false;
            Status = "اتصال هوشمند لغو شد";
        }
        catch (Exception ex)
        {
            IsTesting = false;
            IsSmartConnecting = false;
            Status = $"خطا: {ex.Message}";
            AppendLog($"Smart connect error: {ex.Message}");
        }
        finally { FlushLog(); }
    }

    // ---------- یافتن سرور بهتر ----------
    [RelayCommand]
    private async Task FindBetterServer()
    {
        if (IsTesting) return;
        if (!IsConnected || SelectedServer == null) return;

        // بررسی انقضا: اگه ۱۵ دقیقه گذشته، از اول شروع کن
        if (_searchPool.Count == 0 || (DateTime.Now - _lastSearchTime) > SearchExpiry)
        {
            AppendLog("Search pool expired or empty. Rebuilding from all servers...");
            _searchPool = Servers.Where(s => s.Id != SelectedServer.Id).ToList();
            // shuffle for variety
            var rng = new Random();
            _searchPool = _searchPool.OrderBy(_ => rng.Next()).ToList();
        }

        // برداشتن batch بعدی
        var batch = _searchPool.Take(SearchBatchSize).ToList();
        _searchPool.RemoveRange(0, Math.Min(SearchBatchSize, _searchPool.Count));
        _lastSearchTime = DateTime.Now;

        if (batch.Count == 0)
        {
            ToastHandler?.Invoke("همه سرورها قبلاً بررسی شدند");
            AppendLog("Find better: no more servers to test");
            return;
        }

        var oldCts2 = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts2?.Dispose();
        var ct = _testCts.Token;
        IsTesting = true;
        IsFindingBetter = true;

        try
        {
            int totalTests = batch.Count * 2;
            int tested = 0;
            SearchTestedCount = 0;
            SearchTotalCount = totalTests;
            OnPropertyChanged(nameof(SearchProgressText));

            Status = $"یافتن سرور بهتر: تست {batch.Count} سرور ...";
            AppendLog($"Find better: testing batch of {batch.Count} ({_searchPool.Count} remaining in pool)");
            ToastHandler?.Invoke($"بررسی {batch.Count} سرور جدید ...");

            // --- مرحله ۱: TCP ping سریع روی batch ---
            using var sem = new SemaphoreSlim(10);
            var pingTasks = batch.Select(async s =>
            {
                if (ct.IsCancellationRequested) return;
                await sem.WaitAsync(ct);
                try
                {
                    var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 3000);
                    Post(() => s.RecordPing(ok ? ms : -2));
                }
                catch { Post(() => s.RecordPing(-2)); }
                finally
                {
                    try { sem.Release(); } catch { }
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });
                }
            });
            try { await Task.WhenAll(pingTasks); }
            catch (OperationCanceledException) { }

            await Dispatcher.UIThread.InvokeAsync(() => { });
            AutoSortAfterTest("latency");

            // فیلتر: فقط سرورهایی که TCP موفق داشتن
            var survivors = batch.Where(s => s.LatencyMs > 0).ToList();
            if (survivors.Count == 0)
            {
                IsTesting = false;
                IsFindingBetter = false;
                Status = "یافتن سرور بهتر: هیچ کاندیدی پاسخ نداد";
                AppendLog("Find better: no survivors from TCP ping");
                return;
            }

            AppendLog($"Find better: {survivors.Count} survivors, starting real delay test");

            // --- مرحله ۲: تأخیر واقعی ---
            ServerInfo? bestCandidate = null;
            foreach (var candidate in survivors)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    AppendLog($"Find better: testing {candidate.Name} ...");
                    tested++;
                    Post(() => { SearchTestedCount = tested; OnPropertyChanged(nameof(SearchProgressText)); });

                    var testEngine = new XrayEngine();
                    try
                    {
                        int socks = XrayEngine.FindFreePort();
                        int http = XrayEngine.FindFreePort();
                        string cfg = XrayConfigBuilder.Build(candidate, socks, http);
                        await testEngine.StartWithConfigAsync(cfg, http, ct);

                        using var handler = new HttpClientHandler { Proxy = new System.Net.WebProxy($"http://127.0.0.1:{http}"), UseProxy = true };
                        using var http2 = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

                        var sw = Stopwatch.StartNew();
                        using var resp = await http2.GetAsync("http://www.gstatic.com/generate_204", ct);
                        sw.Stop();

                        if (resp.IsSuccessStatusCode)
                        {
                            int ms = (int)sw.ElapsedMilliseconds;
                            AppendLog($"Find better: {candidate.Name} → {ms}ms");
                            Post(() => candidate.RecordRealDelay(ms));

                            if (bestCandidate == null || ms < bestCandidate.LatencyMs)
                                bestCandidate = candidate;
                        }
                        else
                        {
                            Post(() => candidate.RecordRealDelay(-2));
                        }
                    }
                    catch
                    {
                        Post(() => candidate.RecordRealDelay(-2));
                    }
                    finally
                    {
                        try { testEngine.Stop(); testEngine.Dispose(); } catch { }
                    }
                }
                catch (OperationCanceledException) { break; }
            }

            // --- مقایسه با سرور فعلی ---
            if (bestCandidate != null && bestCandidate.LatencyMs > 0)
            {
                int currentMs = SelectedServer.LatencyMs > 0 ? SelectedServer.LatencyMs : 999;

                if (bestCandidate.LatencyMs < currentMs)
                {
                    // سرور بهتر پیدا شد → سوییچ
                    AppendLog($"Find better: switching from {SelectedServer.Name} ({currentMs}ms) → {bestCandidate.Name} ({bestCandidate.LatencyMs}ms)");
                    ToastHandler?.Invoke($"سوییچ به {bestCandidate.Name} ({bestCandidate.LatencyMs}ms)");

                    Disconnect();
                    SelectedServer = bestCandidate;
                    RefreshConnectionFlags();
                    await Connect();
                    AutoSortAfterTest("latency");
                }
                else
                {
                    AppendLog($"Find better: current server ({currentMs}ms) is still best. Best candidate: {bestCandidate.Name} ({bestCandidate.LatencyMs}ms)");
                    ToastHandler?.Invoke($"سرور فعلی هنوز بهترینه ({currentMs}ms)");
                    Status = $"یافتن سرور بهتر: سرور فعلی بهترینه ({currentMs}ms)";
                }
            }
            else
            {
                Status = "یافتن سرور بهتر: تأخیر واقعی ناموفق";
                AppendLog("Find better: all real delay tests failed");
            }
        }
        catch (OperationCanceledException)
        {
            Status = "یافتن سرور بهتر لغو شد";
        }
        catch (Exception ex)
        {
            Status = $"خطا: {ex.Message}";
            AppendLog($"Find better error: {ex.Message}");
        }
        finally
        {
            IsTesting = false;
            IsFindingBetter = false;
            FlushLog();
        }
    }

     [RelayCommand]
     private async Task TestAll()
     {
         if (IsTesting) return;
         if (Servers.Count == 0) { Status = "سروری نیست"; return; }
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
            Status = $"پینگ {Servers.Count} سرور ...";
            foreach (var s in Servers) { s.LatencyMs = -1; s.RealDelayMs = -1; s.PingMs = -1; }
            using var sem = new SemaphoreSlim(8);
            var tasks = Servers.ToList().Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var isReal = SelectedPingType == PingType.RealDelay;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.TcpPing or PingType.Ping => await LatencyTester.TcpAsync(s.Address, s.Port),
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await RealDelayTester.TestAsync(s, _testCts.Token)
                    };
                    if (!_testCts.Token.IsCancellationRequested)
                        Post(() => { if (isReal) s.RecordRealDelay(ok ? ms : -2); else s.RecordPing(ok ? ms : -2); });
                }
                catch { Post(() => { if (SelectedPingType == PingType.RealDelay) s.RecordRealDelay(-2); else s.RecordPing(-2); }); }
                finally { sem.Release(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
             int okCount = Servers.Count(s => s.LatencyMs >= 0);
             Status = $"پینگ تمام شد: {okCount} از {Servers.Count} پاسخ داد";
             AppendLog(Status);
             AutoSortAfterTest("latency");
             SaveServers();
         }
         finally { IsTesting = false; ConnectCommand.NotifyCanExecuteChanged(); }
     }

     [RelayCommand]
     private async Task TestSpeed()
     {
         if (IsTesting) return;
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : Servers.ToList();
         if (targets.Count == 0) { Status = "سروری نیست"; return; }
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
              Status = $"تست سرعت {targets.Count} سرور (از طریق پروکسی) ...";
              foreach (var s in targets) s.RecordSpeed(-1);
              using var sem = new SemaphoreSlim(3);
              var tasks = targets.Select(async s =>
              {
                  if (_testCts.Token.IsCancellationRequested) return;
                  await sem.WaitAsync(_testCts.Token);
                  try
                  {
                      if (_testCts.Token.IsCancellationRequested) return;
                      Post(() => Status = $"تست سرعت: {s.Name} ...");
                      var (ok, mbps) = await SpeedTester.TestAsync(s, _testCts.Token);
                      Post(() => s.RecordSpeed(ok ? mbps : -2));
                  }
                  catch { Post(() => s.RecordSpeed(-2)); }
                  finally { try { sem.Release(); } catch { } }
              });
              try { await Task.WhenAll(tasks); }
              catch (OperationCanceledException) { }
             foreach (var s in targets)
                 if (s.DownloadMbps == -1) s.RecordSpeed(-2);
             int okCount = targets.Count(s => s.DownloadMbps >= 0);
             Status = $"سرعت تمام شد: {okCount} از {targets.Count} موفق";
             AppendLog(Status);
             AutoSortAfterTest("speed");
             SaveServers();
         }
         finally { IsTesting = false; ConnectCommand.NotifyCanExecuteChanged(); }
     }

    [RelayCommand]
    private async Task TestOneServer(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0) return;
        if (IsTesting) return;
        IsTesting = true;
        try
        {
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var isReal = SelectedPingType == PingType.RealDelay;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.TcpPing or PingType.Ping => await LatencyTester.TcpAsync(s.Address, s.Port),
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await RealDelayTester.TestAsync(s)
                    };
                    Post(() => { if (isReal) s.RecordRealDelay(ok ? ms : -2); else s.RecordPing(ok ? ms : -2); });
                }
                catch { Post(() => { if (SelectedPingType == PingType.RealDelay) s.RecordRealDelay(-2); else s.RecordPing(-2); }); }
                finally { try { sem.Release(); } catch { } }
            });
            await Task.WhenAll(tasks);
        }
        finally { IsTesting = false; SaveServers(); }
    }

    [RelayCommand]
    private async Task TestOneSpeed(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0 || IsTesting) return;
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        try
        {
            Status = $"تست سرعت {targets.Count} سرور ...";
            foreach (var s in targets) s.RecordSpeed(-1);
              using var sem = new SemaphoreSlim(3);
              var tasks = targets.Select(async s =>
              {
                  if (_testCts.Token.IsCancellationRequested) return;
                  await sem.WaitAsync(_testCts.Token);
                  try
                  {
                      if (_testCts.Token.IsCancellationRequested) return;
                      Post(() => Status = $"تست سرعت: {s.Name} ...");
                      var (ok, mbps) = await SpeedTester.TestAsync(s, _testCts.Token);
                      Post(() => s.RecordSpeed(ok ? mbps : -2));
                  }
                  catch { Post(() => s.RecordSpeed(-2)); }
                  finally { try { sem.Release(); } catch { } }
              });
              try { await Task.WhenAll(tasks); }
              catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.DownloadMbps == -1) s.RecordSpeed(-2);
            int okCount = targets.Count(s => s.DownloadMbps >= 0);
            Status = $"سرعت تمام شد: {okCount} از {targets.Count} موفق";
            SaveServers();
        }
        finally { IsTesting = false; }
    }

    // ---------- انتخاب چندتایی ----------
    public string SelectedCountText => SelectedCount > 0 ? string.Format(TranslationManager.Instance["SelectedCount"], SelectedCount) : "";
    private bool HasSelection() => SelectedCount > 0;

    public void UpdateSelectedCount()
    {
        SelectedCount = Servers.Count(s => s.IsChecked);
        OnPropertyChanged(nameof(SelectedCountText));
        TestSelectedCommand.NotifyCanExecuteChanged();
        TestSelectedSpeedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        TestRealDelaySelectedCommand.NotifyCanExecuteChanged();
    }

    public void ClearAllChecks()
    {
        foreach (var s in Servers)
            s.IsChecked = false;
    }

    [RelayCommand]
    private void ClearSelection() => ClearAllChecks();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelected()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        IsTesting = true;
        try
        {
            Status = $"پینگ {targets.Count} سرور منتخب ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.PingMs = -1; }
            using var sem = new SemaphoreSlim(8);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var isReal = SelectedPingType == PingType.RealDelay;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.TcpPing or PingType.Ping => await LatencyTester.TcpAsync(s.Address, s.Port),
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await RealDelayTester.TestAsync(s)
                    };
                    Post(() => { if (isReal) s.RecordRealDelay(ok ? ms : -2); else s.RecordPing(ok ? ms : -2); });
                }
                catch { Post(() => { if (SelectedPingType == PingType.RealDelay) s.RecordRealDelay(-2); else s.RecordPing(-2); }); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
            Status = $"پینگ منتخب‌ها تمام شد ({targets.Count(t => t.LatencyMs >= 0)} موفق)";
            AutoSortAfterTest("latency");
            SaveServers();
        }
        finally { IsTesting = false; TestSelectedCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestSelectedSpeed()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        IsTesting = true;
        try
        {
            Status = $"تست سرعت {targets.Count} سرور منتخب ...";
            foreach (var s in targets) s.RecordSpeed(-1);
            using var sem = new SemaphoreSlim(2);
            var tasks = targets.Select(async s =>
            {
                await sem.WaitAsync();
                try
                {
                    var (ok, mbps) = await SpeedTester.TestAsync(s);
                    Post(() => s.RecordSpeed(ok ? mbps : -2));
                }
                catch { Post(() => s.RecordSpeed(-2)); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
            Status = $"سرعت منتخب‌ها تمام شد ({targets.Count(t => t.DownloadMbps >= 0)} موفق)";
            AutoSortAfterTest("speed");
            SaveServers();
        }
        finally { IsTesting = false; TestSelectedSpeedCommand.NotifyCanExecuteChanged(); }
    }

     [RelayCommand(CanExecute = nameof(HasSelection))]
     private async Task RemoveSelected()
     {
         var doomed = Servers.Where(s => s.IsChecked).ToList();
         if (doomed.Count == 0) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler($"آیا از حذف {doomed.Count} سرور منتخب متمعن هستید؟");
             if (!confirmed) return;
         }
         if (doomed.Any(s => ReferenceEquals(s, SelectedServer)) && IsConnected) Disconnect();
         foreach (var d in doomed)
             Servers.Remove(d);
         if (SelectedServer != null && doomed.Contains(SelectedServer)) SelectedServer = null;
         SaveServers();
         RefreshGroups();
          AppendLog(string.Format(TranslationManager.Instance["Log_ServersDeleted"], doomed.Count));
     }

    [RelayCommand]
    private void ToggleGroupSelection(ServerGroup? group)
    {
        if (group == null) return;
        bool allChecked = group.Servers.Count > 0 && group.Servers.All(s => s.IsChecked);
        foreach (var s in group.Servers)
            s.IsChecked = !allChecked;
    }

     [RelayCommand]
     private async Task TestGroup(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
            Status = $"پینگ گروه «{group!.Title}» ({targets.Count} سرور) ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.PingMs = -1; }
            using var sem = new SemaphoreSlim(SelectedPingType == PingType.RealDelay ? 3 : 8);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var isReal = SelectedPingType == PingType.RealDelay;
                    var (ok, ms) = SelectedPingType switch
                    {
                        PingType.TcpPing or PingType.Ping => await LatencyTester.TcpAsync(s.Address, s.Port),
                        PingType.HttpPing => await LatencyTester.HttpAsync(s.Address, s.Port),
                        _ => await RealDelayTester.TestAsync(s, _testCts.Token)
                    };
                    if (!_testCts.Token.IsCancellationRequested)
                        Post(() => { if (isReal) s.RecordRealDelay(ok ? ms : -2); else s.RecordPing(ok ? ms : -2); });
                }
                catch { Post(() => { if (SelectedPingType == PingType.RealDelay) s.RecordRealDelay(-2); else s.RecordPing(-2); }); }
                finally { sem.Release(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
             int okCount = targets.Count(t => t.LatencyMs >= 0);
             Status = $"پینگ گروه تمام شد ({okCount} موفق)";
             AutoSortAfterTest("latency");
             SaveServers();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand]
     private async Task TestGroupRealDelay(ServerGroup? group)
     {
          var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
            Status = $"تست تأخیر واقعی گروه «{group!.Title}» ({targets.Count} سرور) ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; }
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var (ok, ms) = await RealDelayTester.TestAsync(s, _testCts.Token);
                    if (!_testCts.Token.IsCancellationRequested)
                        Post(() => s.RecordRealDelay(ok ? ms : -2));
                }
                catch { Post(() => s.RecordRealDelay(-2)); }
                finally { sem.Release(); }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
             int okCount = targets.Count(t => t.LatencyMs >= 0);
              Status = $"تأخیر واقعی گروه تمام شد ({okCount} موفق)";
             AutoSortAfterTest("latency");
             SaveServers();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand]
     private async Task TestGroupSpeed(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
              Status = $"تست سرعت گروه «{group!.Title}» ({targets.Count} سرور) ...";
              foreach (var s in targets) s.RecordSpeed(-1);
              using var sem = new SemaphoreSlim(3);
              var tasks = targets.Select(async s =>
              {
                  if (_testCts.Token.IsCancellationRequested) return;
                  await sem.WaitAsync(_testCts.Token);
                  try
                  {
                      if (_testCts.Token.IsCancellationRequested) return;
                      Post(() => Status = $"تست سرعت: {s.Name} ...");
                      var (ok, mbps) = await SpeedTester.TestAsync(s, _testCts.Token);
                      Post(() => s.RecordSpeed(ok ? mbps : -2));
                  }
                  catch { Post(() => s.RecordSpeed(-2)); }
                  finally { try { sem.Release(); } catch { } }
              });
              try { await Task.WhenAll(tasks); }
              catch (OperationCanceledException) { }
             foreach (var s in targets)
                 if (s.DownloadMbps == -1) s.RecordSpeed(-2);
             int okCount = targets.Count(s => s.DownloadMbps >= 0);
              Status = $"سرعت گروه تمام شد ({okCount} موفق)";
             AutoSortAfterTest("speed");
             SaveServers();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand]
     private async Task UpdateGroupServers(ServerGroup? group)
     {
         var targets = group?.Servers.ToList() ?? new List<ServerInfo>();
         if (targets.Count == 0 || IsTesting) return;
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
            Status = $"بروزرسانی گروه «{group!.Title}» ({targets.Count} سرور) ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; s.RecordSpeed(-1); }
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    Post(() => Status = $"بروزرسانی: {s.Name} ...");
                    var (ok, ms) = await RealDelayTester.TestAsync(s, _testCts.Token);
                    Post(() => s.RecordRealDelay(ok ? ms : -2));
                }
                catch { Post(() => s.RecordRealDelay(-2)); }
                finally { try { sem.Release(); } catch { } }
            });
             try { await Task.WhenAll(tasks); }
             catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.LatencyMs == -1) s.RecordRealDelay(-2);
             int okCount = targets.Count(s => s.LatencyMs >= 0);
             Status = $"بروزرسانی گروه تمام شد ({okCount} موفق)";
             AutoSortAfterTest("latency");
             SaveServers();
         }
         catch (Exception ex) { Status = $"خطا: {ex.Message}"; }
         finally { IsTesting = false; }
     }

     // ---------- تب تأخیر واقعی ----------
     [RelayCommand]
     private async Task TestRealDelayAll()
     {
         if (IsTesting) return;
         var checkedServers = Servers.Where(s => s.IsChecked).ToList();
         var targets = checkedServers.Count > 0 ? checkedServers : Servers.ToList();
         if (targets.Count == 0) { Status = "سروری نیست"; return; }
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Dispose();
         IsTesting = true;
         try
         {
            Status = $"تست تأخیر واقعی {targets.Count} سرور (کند ولی دقیق) ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; }
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var (ok, ms) = await RealDelayTester.TestAsync(s, _testCts.Token);
                    Post(() => s.RecordRealDelay(ok ? ms : -2));
                }
                catch { Post(() => s.RecordRealDelay(-2)); }
                finally { try { sem.Release(); } catch { } }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.LatencyMs == -1) s.RecordRealDelay(-2);
            int okCount = targets.Count(t => t.LatencyMs >= 0);
            Status = $"تأخیر واقعی تمام شد ({okCount} موفق)";
            AutoSortAfterTest("latency");
            SaveServers();
         }
         finally { IsTesting = false; }
     }

     [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestRealDelaySelected()
    {
        var targets = Servers.Where(s => s.IsChecked).ToList();
        if (targets.Count == 0 || IsTesting) return;
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        try
        {
             Status = $"تست تأخیر واقعی {targets.Count} سرور منتخب ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; }
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    var (ok, ms) = await RealDelayTester.TestAsync(s, _testCts.Token);
                    Post(() => s.RecordRealDelay(ok ? ms : -2));
                }
                catch { Post(() => s.RecordRealDelay(-2)); }
                finally { try { sem.Release(); } catch { } }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.LatencyMs == -1) s.RecordRealDelay(-2);
            Status = $"تأخیر واقعی منتخب‌ها تمام شد ({targets.Count(t => t.LatencyMs >= 0)} موفق)";
            AutoSortAfterTest("latency");
            SaveServers();
        }
        finally { IsTesting = false; TestRealDelaySelectedCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand]
    private async Task TestOneRealDelay(ServerInfo? server)
    {
        var checkedServers = Servers.Where(s => s.IsChecked).ToList();
        var targets = checkedServers.Count > 0 ? checkedServers : (server != null ? new List<ServerInfo> { server } : new List<ServerInfo>());
        if (targets.Count == 0 || IsTesting) return;
        var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
        oldCts?.Dispose();
        IsTesting = true;
        try
        {
            Status = $"تست تأخیر واقعی {targets.Count} سرور ...";
            foreach (var s in targets) { s.LatencyMs = -1; s.RealDelayMs = -1; }
            using var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async s =>
            {
                if (_testCts.Token.IsCancellationRequested) return;
                await sem.WaitAsync(_testCts.Token);
                try
                {
                    if (_testCts.Token.IsCancellationRequested) return;
                    Post(() => Status = $"تأخیر واقعی: {s.Name} ...");
                    var (ok, ms) = await RealDelayTester.TestAsync(s, _testCts.Token);
                    Post(() => s.RecordRealDelay(ok ? ms : -2));
                }
                catch { Post(() => s.RecordRealDelay(-2)); }
                finally { try { sem.Release(); } catch { } }
            });
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            foreach (var s in targets)
                if (s.LatencyMs == -1) s.RecordRealDelay(-2);
            int okCount = targets.Count(s => s.LatencyMs >= 0);
            Status = $"تأخیر واقعی تمام شد: {okCount} از {targets.Count} موفق";
            SaveServers();
        }
        finally { IsTesting = false; }
    }

    // ---------- اشتراک ----------

    private static string GuessSubName(string url)
    {
        try
        {
            if (UrlParser.IsProtocolLink(url)) return UrlParser.Parse(url).Name;
            var uri = new Uri(url);
            string host = uri.Host.Replace("www.", "");
            int dot = host.IndexOf('.');
            return (dot > 0 ? host[..dot] : host) + "-" + DateTime.Now.ToString("MMdd-HHmm");
        }
        catch { return "sub-" + DateTime.Now.ToString("MMdd-HHmm"); }
    }

    [RelayCommand]
    private async Task AddSubscription()
    {
        string url = (SubUrl ?? "").Trim();
        if (url.Length == 0) { ToastHandler?.Invoke("لینک را وارد کنید"); return; }
        string name = string.IsNullOrWhiteSpace(SubName) ? GuessSubName(url) : SubName.Trim();
        if (Subscriptions.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        { AppendLog(string.Format(TranslationManager.Instance["Log_ServerDup"], name)); return; }
        try
        {
            Status = $"دریافت {name} ...";
            var (servers, subInfo) = await SubscriptionManager.FetchWithInfoAsync(url);
            AppendLog($"تعداد سرور دریافتی: {servers.Count}");
            var info = new SubscriptionInfo { Name = name, Url = url, LastUpdated = DateTime.Now, ServerCount = servers.Count };
            if (subInfo != null)
            {
                info.DataLimit = subInfo.DataLimit;
                info.UsedTraffic = subInfo.UsedTraffic;
                info.ExpireDate = subInfo.ExpireDate;
                AppendLog($"حجم: {info.UsedTraffic}/{info.DataLimit} | انقضا: {info.ExpireDate}");
            }
            else
            {
                AppendLog("اطلاعات اشتراک (حجم/انقضا) از سرور دریافت نشد");
            }
            Subscriptions.Add(info);
            SaveSubs();
            MergeServers(name, servers);
            SubName = "";
            SubUrl = "";
            SelectedSubscriptionName = name;
            Status = $"«{name}» اضافه شد ({servers.Count} سرور)";
        }
        catch (Exception ex) { Status = "خطا در دریافت"; AppendLog($"خطا: {ex.Message}"); ToastHandler?.Invoke($"خطا: {ex.Message}"); }
    }

    private void MergeServers(string subName, List<ServerInfo> fetched)
    {
        string? activeId = SelectedServer?.Id;
        foreach (var o in Servers.Where(s => s.SubscriptionName == subName).ToList())
        {
            if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
            Servers.Remove(o);
        }
        int added = 0;
        foreach (var server in fetched)
        {
            Enrich(server);
            server.SubscriptionName = subName;
            if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
                Servers.Insert(0, server);
                server.IsHighlighted = true;
                added++;
        }
        SaveServers();
        var sub = Subscriptions.FirstOrDefault(s => s.Name == subName);
        if (sub != null)
        {
            sub.LastUpdated = DateTime.Now;
            sub.ServerCount = fetched.Count;
            SaveSubs();
            foreach (var s in Servers.Where(s => s.SubscriptionName == subName))
            {
                s.SubDataLimit = sub.DataLimit;
                s.SubUsedTraffic = sub.UsedTraffic;
                s.SubExpireDate = sub.ExpireDate;
            }
        }
        SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? SelectedServer;
        RefreshGroups();
        AppendLog(string.Format(TranslationManager.Instance["Log_Merged"], subName, added, Servers.Count));
    }

    [RelayCommand]
    private async Task UpdateSubscriptions()
    {
        if (Subscriptions.Count == 0) { AppendLog(TranslationManager.Instance["Log_NoSub"]); return; }
        string? activeId = SelectedServer?.Id;
        int total = 0;
        string? proxyAddress = UseProxyForSubscriptions && IsConnected && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;
        foreach (var sub in Subscriptions.ToList())
        {
            try
            {
                Status = $"دریافت {sub.Name} ...";
                var (servers, subInfo) = proxyAddress != null
                    ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
                    : await SubscriptionManager.FetchWithInfoAsync(sub.Url);
                if (servers.Count == 0)
                {
                    AppendLog($"خطا در {sub.Name}: سروری دریافت نشد — سرورهای قبلی حفظ شد");
                    continue;
                }
                foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
                {
                    if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                    if (ReferenceEquals(SelectedServer, o)) SelectedServer = null;
                    Servers.Remove(o);
                }
                foreach (var server in servers)
                {
                    Enrich(server);
                    server.Owner = this;
                    server.SubscriptionName = sub.Name;
                    if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
                    Servers.Add(server);
                }
                sub.LastUpdated = DateTime.Now;
                sub.ServerCount = servers.Count;
                if (subInfo != null)
                {
                    sub.DataLimit = subInfo.DataLimit;
                    sub.UsedTraffic = subInfo.UsedTraffic;
                    sub.ExpireDate = subInfo.ExpireDate;
                }
                foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
                {
                    s.SubDataLimit = sub.DataLimit;
                    s.SubUsedTraffic = sub.UsedTraffic;
                    s.SubExpireDate = sub.ExpireDate;
                }
                total += servers.Count;
                AppendLog(string.Format(TranslationManager.Instance["Log_SubUpdated"], sub.Name, servers.Count));
            }
            catch (Exception ex) { AppendLog($"خطا در {sub.Name}: {ex.Message}"); }
        }
        SaveSubs();
        SaveServers();
        _settings.LastSubscriptionUpdate = DateTime.Now;
        SaveSettings();
        Post(() => OnPropertyChanged(nameof(LastUpdateDisplay)));
        SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? Servers.FirstOrDefault();
        Status = string.Format(TranslationManager.Instance["Log_Updated"], total, Servers.Count);
        RefreshGroups();
    }

    [RelayCommand]
    private async Task UpdateSingleSubscription(SubscriptionInfo? sub)
    {
        sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
        if (sub == null) return;
        try
        {
            Status = $"دریافت {sub.Name} ...";
            string? proxyAddress = UseProxyForSubscriptions && IsConnected && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;
            var (fetched, subInfo) = proxyAddress != null
                ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
                : await SubscriptionManager.FetchWithInfoAsync(sub.Url);
            if (fetched.Count == 0)
            {
                AppendLog($"خطا در {sub.Name}: سروری دریافت نشد — سرورهای قبلی حفظ شد");
                Status = $"سروری از «{sub.Name}» دریافت نشد";
                return;
            }
            string? activeId = SelectedServer?.Id;
            foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
            {
                if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                Servers.Remove(o);
            }
            int added = 0;
            foreach (var server in fetched)
            {
                Enrich(server);
                server.Owner = this;
                server.SubscriptionName = sub.Name;
                if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
                Servers.Add(server);
                added++;
            }
            SaveServers();
            sub.LastUpdated = DateTime.Now;
            sub.ServerCount = fetched.Count;
            if (subInfo != null)
            {
                sub.DataLimit = subInfo.DataLimit;
                sub.UsedTraffic = subInfo.UsedTraffic;
                sub.ExpireDate = subInfo.ExpireDate;
            }
            foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
            {
                s.SubDataLimit = sub.DataLimit;
                s.SubUsedTraffic = sub.UsedTraffic;
                s.SubExpireDate = sub.ExpireDate;
            }
            SaveSubs();
            SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? SelectedServer;
            foreach (var g in Groups)
                UpdateGroupContent(g);
            _settings.LastSubscriptionUpdate = DateTime.Now;
            SaveSettings();
            Post(() => OnPropertyChanged(nameof(LastUpdateDisplay)));
            Status = $"«{sub.Name}» به‌روز شد ({added} سرور جدید)";
        }
        catch (Exception ex) { Status = "خطا در به‌روزرسانی"; AppendLog(string.Format(TranslationManager.Instance["Log_Error"], ex.Message)); }
    }

     [RelayCommand]
     private async Task RemoveSubscription(SubscriptionInfo? sub)
     {
         sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
         if (sub == null) return;
         if (ConfirmDeleteHandler != null)
         {
             bool confirmed = await ConfirmDeleteHandler($"آیا از حذف اشتراک «{sub.Name}» و تمام سرورهایش متمعن هستید؟");
             if (!confirmed) return;
         }
         Subscriptions.Remove(sub);
         SaveSubs();
         foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
         {
             if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
             Servers.Remove(o);
         }
         SaveServers();
         if (SelectedSubscriptionName == sub.Name)
             SelectedSubscriptionName = Subscriptions.Count > 0 ? Subscriptions[0].Name : "";
         RefreshGroups();
         AppendLog(string.Format(TranslationManager.Instance["Log_SubRemoved"], sub.Name));
     }

    [RelayCommand]
    private async Task CopySubscriptionUrl(SubscriptionInfo? sub)
    {
        if (sub == null || Clipboard == null) return;
         await Clipboard.SetTextAsync(sub.Url);
         ToastHandler?.Invoke("لینک اشتراک کپی شد");
     }

    [RelayCommand]
    private async Task UpdateSingleSubscriptionNoProxy(SubscriptionInfo? sub)
    {
        sub ??= Subscriptions.FirstOrDefault(s => s.Name == SelectedSubscriptionName);
        if (sub == null) return;
        try
        {
            Status = $"دریافت {sub.Name} ...";
            var (fetched, subInfo) = await SubscriptionManager.FetchWithInfoAsync(sub.Url);
            if (fetched.Count == 0)
            {
                AppendLog($"خطا در {sub.Name}: سروری دریافت نشد — سرورهای قبلی حفظ شد");
                Status = $"سروری از «{sub.Name}» دریافت نشد";
                return;
            }
            string? activeId = SelectedServer?.Id;
            foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
            {
                if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                Servers.Remove(o);
            }
            foreach (var s in fetched) { s.SubscriptionName = sub.Name; Servers.Add(s); }
            if (subInfo != null) { sub.DataLimit = subInfo.DataLimit; sub.UsedTraffic = subInfo.UsedTraffic; sub.ExpireDate = subInfo.ExpireDate; }
            sub.ServerCount = fetched.Count;
            sub.LastUpdated = DateTime.Now;
            SaveServers();
            SaveSubs();
            RefreshGroups();
            if (activeId != null) SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId);
            Status = $"{fetched.Count} سرور از «{sub.Name}» دریافت شد";
            AppendLog(string.Format(TranslationManager.Instance["Log_SubUpdated"], sub.Name, fetched.Count));
        }
        catch (Exception ex)
        {
            Status = $"خطا در بروزرسانی {sub.Name}";
            AppendLog($"خطا در بروزرسانی {sub.Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ExportServers()
    {
        string path = Path.Combine(Storage.DataDir, "servers_export.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Servers.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        AppendLog(string.Format(TranslationManager.Instance["Log_Exported"], path));
    }

    [RelayCommand]
    private void ExportShareUrls()
    {
        var urls = Servers.Where(s => !string.IsNullOrEmpty(s.Link)).Select(s => UrlBuilder.Build(s)).ToList();
        if (urls.Count == 0) { AppendLog("سروری نیست."); return; }
        _ = CopyTextAsync(string.Join(Environment.NewLine, urls));
        AppendLog(string.Format(TranslationManager.Instance["Log_LinksCopied"], urls.Count));
    }

    [RelayCommand]
    private void ExportSubscriptionBase64()
    {
        var urls = Servers.Where(s => !string.IsNullOrEmpty(s.Link)).Select(s => UrlBuilder.Build(s)).ToList();
        if (urls.Count == 0) { AppendLog("سروری نیست."); return; }
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", urls)));
        _ = CopyTextAsync(b64);
         AppendLog(TranslationManager.Instance["Log_SubCopied"]);
     }

     [RelayCommand]
     private void StopTest()
     {
         var oldCts = Interlocked.Exchange(ref _testCts, new CancellationTokenSource());
         oldCts?.Cancel();
         oldCts?.Dispose();
         IsTesting = false;
          Status = TranslationManager.Instance["Log_TestStopped"];
          AppendLog(TranslationManager.Instance["Log_TestStopped"]);
      }

      [RelayCommand]
      private void SelectGroupServers(ServerGroup? group)
      {
          if (group == null) return;
          foreach (var s in group.Servers)
              s.IsChecked = true;
          AppendLog($"تمام سرورهای گروه «{group.Title}» انتخاب شد");
      }

      [RelayCommand]
      private async Task RemoveGroupServers(ServerGroup? group)
      {
          if (group == null) return;
          var toRemove = group.Servers.ToList();
          if (ConfirmDeleteHandler != null)
          {
              bool confirmed = await ConfirmDeleteHandler($"آیا از حذف {toRemove.Count} سرور از گروه «{group.Title}» متمعن هستید؟");
              if (!confirmed) return;
          }
          foreach (var s in toRemove)
          {
              if (ReferenceEquals(SelectedServer, s) && IsConnected) Disconnect();
              if (ReferenceEquals(SelectedServer, s)) SelectedServer = null;
              Servers.Remove(s);
          }
          SaveServers();
          RefreshGroups();
          AppendLog($"{toRemove.Count} سرور از گروه «{group.Title}» حذف شد");
      }

      [RelayCommand]
      private void ToggleSortDirection()
      {
          SortAscending = !SortAscending;
      }

      // --- متدهای عمومی برای Tray Menu ---
      public async Task TraySmartConnectAsync()
      {
          if (SmartConnectCommand.CanExecute(null))
              await SmartConnect();
      }

      public void CancelSmartConnect()
      {
          if (IsSmartConnecting || IsFindingBetter)
          {
              _testCts.Cancel();
              IsTesting = false;
              IsSmartConnecting = false;
              IsFindingBetter = false;
              Status = "متوقف شد";
          }
      }

      public void TrayToggleSystemProxy()
      {
          SetSystemProxy = !SetSystemProxy;
      }

      public async Task TrayUpdateSubscriptionsAsync(bool useProxy)
      {
          if (Subscriptions.Count == 0)
          {
              AppendLog(TranslationManager.Instance["Log_NoSub"]);
              return;
          }
          Status = useProxy ? "بروزرسانی با پراکسی ..." : "بروزرسانی بدون پراکسی ...";
          string? activeId = SelectedServer?.Id;
          int total = 0;
          string? proxyAddress = useProxy && _engine.HttpPort > 0 ? $"127.0.0.1:{_engine.HttpPort}" : null;

              foreach (var sub in Subscriptions.ToList())
              {
                  try
                  {
                      Status = $"دریافت {sub.Name} ...";
                      var (servers, subInfo) = proxyAddress != null
                          ? await SubscriptionManager.FetchWithInfoAndProxyAsync(sub.Url, proxyAddress)
                          : await SubscriptionManager.FetchWithInfoAsync(sub.Url);

                      if (servers.Count == 0)
                      {
                          AppendLog($"خطا در {sub.Name}: سروری دریافت نشد — سرورهای قبلی حفظ شد");
                          continue;
                      }

                      foreach (var o in Servers.Where(s => s.SubscriptionName == sub.Name).ToList())
                      {
                          if (ReferenceEquals(SelectedServer, o) && IsConnected) Disconnect();
                          if (ReferenceEquals(SelectedServer, o)) SelectedServer = null;
                          Servers.Remove(o);
                      }
                      foreach (var server in servers)
                      {
                          Enrich(server);
                          server.Owner = this;
                          server.SubscriptionName = sub.Name;
                          if (Servers.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal))) continue;
                          Servers.Add(server);
                      }
                  sub.LastUpdated = DateTime.Now;
                  sub.ServerCount = servers.Count;
                  if (subInfo != null)
                  {
                      sub.DataLimit = subInfo.DataLimit;
                      sub.UsedTraffic = subInfo.UsedTraffic;
                      sub.ExpireDate = subInfo.ExpireDate;
                  }
                  foreach (var s in Servers.Where(s => s.SubscriptionName == sub.Name))
                  {
                      s.SubDataLimit = sub.DataLimit;
                      s.SubUsedTraffic = sub.UsedTraffic;
                      s.SubExpireDate = sub.ExpireDate;
                  }
                  total += servers.Count;
                  AppendLog(string.Format(TranslationManager.Instance["Log_SubUpdated"], sub.Name, servers.Count));
              }
              catch (Exception ex) { AppendLog($"خطا در {sub.Name}: {ex.Message}"); }
          }
          SaveSubs();
          SaveServers();
          _settings.LastSubscriptionUpdate = DateTime.Now;
          SaveSettings();
          Post(() => OnPropertyChanged(nameof(LastUpdateDisplay)));
          SelectedServer = Servers.FirstOrDefault(s => s.Id == activeId) ?? Servers.FirstOrDefault();
          Status = string.Format(TranslationManager.Instance["Log_Updated"], total, Servers.Count);
          RefreshGroups();
      }

      public void Shutdown() => _engine.Dispose();
 }
