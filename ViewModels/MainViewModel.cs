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
     public Action<ServerInfo>? FocusServerHandler { get; set; }

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
      [ObservableProperty] private string _selectedSubscriptionName = "";
      [ObservableProperty] private string _sortField = TranslationManager.Instance["Sort_None"];
      [ObservableProperty] private bool _globalSort;
      [ObservableProperty] private int _selectedCount;
      [ObservableProperty] private bool _selectAllChecked;
      [ObservableProperty] private bool _sortAscending = true;
      [ObservableProperty] private string _customDnsServers = "";
      [ObservableProperty] private string _customDnsAdvancedJson = "";

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


    public event Action? RestartRequested;

    public string HomeServerName => SelectedServer?.Name ?? TranslationManager.Instance["Home_NoServer"];
    public string HomeShortName => SelectedServer?.ShortName ?? "";
    public string HomeProtocol => SelectedServer != null ? SelectedServer.Protocol.ToString().ToLowerInvariant() : "";
    public string HomeIp => SelectedServer?.Address ?? "";
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
    private DispatcherTimer? _blinkTimer;
    private double _blinkPhase;

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

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _blinkTimer.Tick += (_, _) => BlinkTick();
        _blinkTimer.Start();
    }

    private void BlinkTick()
    {
        if (!IsConnected) { SpeedBlinkOpacity = 1.0; _blinkPhase = 0; return; }
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
    partial void OnTestTestedCountChanged(int value) { OnPropertyChanged(nameof(TestProgressPercent)); OnPropertyChanged(nameof(TestProgressText)); }
    partial void OnTestTotalCountChanged(int value) { OnPropertyChanged(nameof(TestProgressPercent)); OnPropertyChanged(nameof(TestProgressText)); }


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
                AppendLog(TranslationManager.Instance["Log_AutoConnectNoServer"]);
                ToastHandler?.Invoke(TranslationManager.Instance["Toast_AutoConnectSelect"]);
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
                var f when f == TranslationManager.Instance["Filter_Errors"] => LogMatches(l, "Status_Error") || l.Contains("error", StringComparison.OrdinalIgnoreCase),
                var f when f == TranslationManager.Instance["Filter_Connection"] => LogMatches(l, "Status_Connected") || LogMatches(l, "Status_Disconnected") || LogMatches(l, "Log_Connected") || LogMatches(l, "Log_Disconnected") || LogMatches(l, "Ctx_Connect") || l.Contains("connect", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
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
    private void SaveSettings() => Storage.SaveSettings(_settings);
    private void SaveSubs() => Storage.SaveSubscriptions(Subscriptions.ToList());

     partial void OnIsTestingChanged(bool value) => ConnectCommand.NotifyCanExecuteChanged();


    public Action? ShowAdminWarningHandler { get; set; }


    private async Task CopyTextAsync(string text)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(text);
    }

    // ---------- سرور ----------


      public void Shutdown() => _engine.Dispose();
 }
