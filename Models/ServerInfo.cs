using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XrayClient.Models;

public enum ProxyProtocol { VMess, VLESS, Shadowsocks, Trojan }

public partial class ServerInfo : ObservableObject
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Link { get; set; } = "";
    public ProxyProtocol Protocol { get; set; }
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string UserId { get; set; } = "";
    public string Password { get; set; } = "";
    public string Method { get; set; } = "";
    public string Encryption { get; set; } = "auto";
    public int AlterId { get; set; }
    public string Flow { get; set; } = "";
    public string Security { get; set; } = "none";
    public string Network { get; set; } = "tcp";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string SNI { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Alpn { get; set; } = "";
    public string HeaderType { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string SpiderX { get; set; } = "";
    public bool AllowInsecure { get; set; }
    public string SubscriptionName { get; set; } = "";
    public DateTime LastTested { get; set; } = DateTime.MinValue;

    [ObservableProperty] private long _subDataLimit;
    [ObservableProperty] private long _subUsedTraffic;
    [ObservableProperty] private DateTime _subExpireDate = DateTime.MinValue;

    public string SubTrafficDisplay
    {
        get
        {
            if (SubDataLimit <= 0) return "";
            return $"{FormatBytes(SubUsedTraffic)} / {FormatBytes(SubDataLimit)}";
        }
    }

    public string SubExpireDisplay
    {
        get
        {
            if (SubExpireDate == DateTime.MinValue) return "";
            if (SubExpireDate < DateTime.Now) return "!";
            var remaining = SubExpireDate - DateTime.Now;
            if (remaining.TotalDays < 1) return $"{(int)remaining.TotalHours}h";
            if (remaining.TotalDays < 30) return $"{(int)remaining.TotalDays}d";
            return SubExpireDate.ToString("yy/MM/dd");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double val = bytes;
        int unit = 0;
        while (val >= 1024 && unit < Units.Length - 1) { val /= 1024; unit++; }
        return $"{val:F1}{Units[unit]}";
    }

    [ObservableProperty] private int _latencyMs = -1;
    [ObservableProperty] private double _downloadMbps = -1;
    [ObservableProperty] private string _countryCode = "";
    [ObservableProperty] [property: JsonIgnore] private bool _isSelected;
    [ObservableProperty] [property: JsonIgnore] private bool _isActiveConnection;
    [ObservableProperty] [property: JsonIgnore] private bool _isChecked;

    [JsonIgnore] public ViewModels.MainViewModel? Owner { get; set; }

    [JsonIgnore]
    public string LatencyDisplay => LatencyMs switch
    {
        -1 => "—",
        -2 => Localization.TranslationManager.Instance["Status_Failed"],
        _ => $"{LatencyMs} ms"
    };

    [JsonIgnore]
    public string SpeedDisplay => DownloadMbps switch
    {
        < -1.5 => Localization.TranslationManager.Instance["Status_Error"],
        < -0.5 => "—",
        _ => $"{DownloadMbps:F1} Mb/s"
    };

    [JsonIgnore] public string Flag => Core.GeoFlags.FlagEmoji(CountryCode);

    [JsonIgnore]
    public string StatusColor
    {
        get
        {
            if (IsActiveConnection) return "#22C55E";
            if (LatencyMs == -1) return "#EF4444";
            if (LatencyMs == -2) return "#F59E0B";
            return "#22C55E";
        }
    }

    [JsonIgnore]
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set
        {
            if (_isHighlighted == value) return;
            _isHighlighted = value;
            OnPropertyChanged();
            if (value)
            {
                _highlightTimer?.Stop();
                _highlightTimer?.Dispose();
                _highlightTimer = new System.Timers.Timer(5000) { AutoReset = false };
                _highlightTimer.Elapsed += OnHighlightElapsed;
                _highlightTimer.Start();
            }
        }
    }
    private bool _isHighlighted;
    private System.Timers.Timer? _highlightTimer;

    private void OnHighlightElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        IsHighlighted = false;
        _highlightTimer?.Dispose();
        _highlightTimer = null;
    }

    [JsonIgnore]
    public string ProtocolDisplay => Protocol switch
    {
        ProxyProtocol.VMess => "VMess",
        ProxyProtocol.VLESS => "VLESS",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        ProxyProtocol.Trojan => "Trojan",
        _ => Protocol.ToString()
    };

    [JsonIgnore] public List<int> LatencyHistory { get; } = new();

    [JsonIgnore]
    public string HistoryDisplay => LatencyHistory.Count == 0
        ? ""
        : string.Join(" ← ", LatencyHistory.TakeLast(5).Reverse());

    [JsonIgnore] public string LastTestedDisplay => LastTested == DateTime.MinValue ? "" : LastTested.ToString("HH:mm dd/MM");

    partial void OnLatencyMsChanged(int value) { OnPropertyChanged(nameof(LatencyDisplay)); OnPropertyChanged(nameof(StatusColor)); }
    partial void OnDownloadMbpsChanged(double value) => OnPropertyChanged(nameof(SpeedDisplay));
    partial void OnCountryCodeChanged(string value) => OnPropertyChanged(nameof(Flag));
    partial void OnIsCheckedChanged(bool value) => Owner?.UpdateSelectedCount();
    partial void OnIsActiveConnectionChanged(bool value) => OnPropertyChanged(nameof(StatusColor));

    public void RecordLatency(int ms)
    {
        LatencyMs = ms;
        if (ms >= 0)
        {
            LatencyHistory.Add(ms);
            if (LatencyHistory.Count > 20) LatencyHistory.RemoveAt(0);
            OnPropertyChanged(nameof(HistoryDisplay));
        }
        LastTested = DateTime.Now;
        OnPropertyChanged(nameof(LastTestedDisplay));
    }

    public void RecordSpeed(double mbps)
    {
        DownloadMbps = mbps;
        LastTested = DateTime.Now;
        OnPropertyChanged(nameof(LastTestedDisplay));
    }

    public ServerInfo Clone(string? newName = null) => new()
    {
        Name = newName ?? Name + " (copy)",
        Link = Link,
        Protocol = Protocol,
        Address = Address,
        Port = Port,
        UserId = UserId,
        Password = Password,
        Method = Method,
        Encryption = Encryption,
        AlterId = AlterId,
        Flow = Flow,
        Security = Security,
        Network = Network,
        Host = Host,
        Path = Path,
        ServiceName = ServiceName,
        SNI = SNI,
        Fingerprint = Fingerprint,
        Alpn = Alpn,
        HeaderType = HeaderType,
        PublicKey = PublicKey,
        ShortId = ShortId,
        SpiderX = SpiderX,
        AllowInsecure = AllowInsecure,
        SubscriptionName = ""
    };

    public static int CompareByPriority(ServerInfo a, ServerInfo b)
    {
        int ra = a.LatencyMs < 0 ? int.MaxValue : a.LatencyMs;
        int rb = b.LatencyMs < 0 ? int.MaxValue : b.LatencyMs;
        int c = ra.CompareTo(rb);
        if (c != 0) return c;

        double sa = a.DownloadMbps < 0 ? -1 : a.DownloadMbps;
        double sb = b.DownloadMbps < 0 ? -1 : b.DownloadMbps;
        c = sb.CompareTo(sa);
        if (c != 0) return c;

        return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
    }
}
