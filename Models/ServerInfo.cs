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

    [ObservableProperty] [property: JsonIgnore] private int _latencyMs = -1;
    [ObservableProperty] private int _realDelayMs = -1;
    [ObservableProperty] [property: JsonIgnore] private int _pingMs = -1;
    [ObservableProperty] [property: JsonIgnore] private double _downloadMbps = -1;
    [ObservableProperty] [property: JsonIgnore] private double _realDelaySuccessRate = -1;
    [ObservableProperty] [property: JsonIgnore] private int _realDelayJitter;
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
        _ => $"{LatencyMs}ms"
    };

    [JsonIgnore]
    public string PingDisplay => PingMs switch
    {
        -1 => "—",
        -2 => Localization.TranslationManager.Instance["Status_Failed"],
        _ => $"{PingMs}ms"
    };

    [JsonIgnore]
    public string RealDelayDisplay => RealDelayMs switch
    {
        -1 => "—",
        -2 => Localization.TranslationManager.Instance["Status_Failed"],
        _ => $"{RealDelayMs}ms"
    };

    [JsonIgnore]
    public int PingSortKey => PingMs < 0 ? int.MaxValue : PingMs;

    [JsonIgnore]
    public int RealDelaySortKey => RealDelayMs < 0 ? int.MaxValue : RealDelayMs;

    [JsonIgnore]
    public string LatencyColor => LatencyMs switch
    {
        -1 => "#EF4444",
        -2 => "#EF4444",
        _ => "#AAAAAA"
    };

    [JsonIgnore]
    public bool HasLatency => LatencyMs > 0;

    [JsonIgnore]
    public string LatencyBgColor => LatencyMs switch
    {
        > 0 and < 450 => "#4ADE80",
        >= 450 and <= 800 => "#FDE047",
        > 800 => "#FB923C",
        -2 => "#FCA5A5",
        _ => "Transparent"
    };

    [JsonIgnore]
    public string LatencyTextColor => LatencyMs switch
    {
        > 0 and < 450 => "#052E16",
        >= 450 and <= 800 => "#422006",
        > 800 => "#431407",
        -2 => "#7F1D1D",
        _ => "#AAAAAA"
    };

    [JsonIgnore]
    public string PingBgColor => PingMs switch
    {
        > 0 and < 450 => "#4ADE80",
        >= 450 and <= 800 => "#FDE047",
        > 800 => "#FB923C",
        -2 => "#FCA5A5",
        _ => "Transparent"
    };

    [JsonIgnore]
    public string PingTextColor => PingMs switch
    {
        > 0 and < 450 => "#052E16",
        >= 450 and <= 800 => "#422006",
        > 800 => "#431407",
        -2 => "#7F1D1D",
        _ => "#AAAAAA"
    };

    [JsonIgnore]
    public string RealDelayBgColor => RealDelayMs switch
    {
        > 0 and < 600 => "#4ADE80",
        >= 600 and < 1000 => "#FEF9C3",
        >= 1000 => "#FB923C",
        -2 => "#FCA5A5",
        _ => "Transparent"
    };

    [JsonIgnore]
    public string RealDelayTextColor => RealDelayMs switch
    {
        > 0 and < 600 => "#052E16",
        >= 600 and < 1000 => "#713F12",
        >= 1000 => "#431407",
        -2 => "#7F1D1D",
        _ => "#AAAAAA"
    };

    [JsonIgnore]
    public string SpeedDisplay => DownloadMbps < 0 ? "—" : $"{DownloadMbps:F1} Mb/s";

    [JsonIgnore]
    public bool ShowSpeedTest => DownloadMbps < 0;

    [JsonIgnore]
    public bool ShowPingTest => PingMs == -1;

    [JsonIgnore]
    public bool ShowRealDelayTest => RealDelayMs == -1;

    [JsonIgnore] public string Flag => Core.GeoFlags.FlagEmoji(CountryCode);

    [JsonIgnore]
    public string ShortName
    {
        get
        {
            string name = Name;
            if (string.IsNullOrWhiteSpace(name)) return CountryCode.Length == 2 ? CountryCode : "Unknown";

            string shortName = RemoveCommonParts(name);

            if (!string.IsNullOrEmpty(CountryCode) && shortName.StartsWith(CountryCode, StringComparison.OrdinalIgnoreCase))
                return shortName;

            if (!string.IsNullOrEmpty(CountryCode))
                return $"{CountryCode} {shortName}";

            return shortName;
        }
    }

    private string RemoveCommonParts(string name)
    {
        var myTokens = name.Split(Core.GeoFlags.SplitChars, StringSplitOptions.RemoveEmptyEntries);
        if (myTokens.Length <= 1) return name;

        var noCc = myTokens.Where(t => !IsCountryToken(t)).ToArray();
        if (noCc.Length == 0) return name;

        var siblings = Owner?.Servers?
            .Where(s => s != this && s.SubscriptionName == SubscriptionName)
            .Select(s => s.Name.Split(Core.GeoFlags.SplitChars, StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        if (siblings == null || siblings.Count == 0) return string.Join(" ", noCc);

        int prefixLen = 0;
        int maxPrefix = Math.Min(myTokens.Length, siblings.Min(s => s.Length));
        while (prefixLen < maxPrefix && siblings.All(s => s[prefixLen].Equals(myTokens[prefixLen], StringComparison.OrdinalIgnoreCase)))
            prefixLen++;

        int suffixLen = 0;
        int maxSuffix = Math.Min(myTokens.Length - prefixLen, siblings.Min(s => s.Length - Math.Min(prefixLen, s.Length)));
        while (suffixLen < maxSuffix &&
               myTokens.Length - 1 - suffixLen >= prefixLen &&
               siblings.All(s => s.Length - 1 - suffixLen >= 0 && s[s.Length - 1 - suffixLen].Equals(myTokens[myTokens.Length - 1 - suffixLen], StringComparison.OrdinalIgnoreCase)))
            suffixLen++;

        int keepFrom = prefixLen;
        int keepTo = myTokens.Length - suffixLen;
        if (keepTo <= keepFrom) return name;

        var kept = myTokens[keepFrom..keepTo]
            .Where(t => !IsCountryToken(t))
            .ToArray();

        return kept.Length > 0 ? string.Join(" ", kept) : name;
    }

    private bool IsCountryToken(string token)
    {
        if (string.Equals(token, CountryCode, StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Length < 2) return false;
        int i = 0;
        while (i < token.Length)
        {
            int cp = char.ConvertToUtf32(token, i);
            i += char.IsSurrogatePair(token, i) ? 2 : 1;
            if (cp < 0x1F1E6 || cp > 0x1F1FF) return false;
        }
        return true;
    }

    [JsonIgnore]
    public string ToolTipText => $"{Name}\n{Address}:{Port}\n{Protocol} · {LatencyDisplay} · {SpeedDisplay}";

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

    partial void OnLatencyMsChanged(int value) { OnPropertyChanged(nameof(LatencyDisplay)); OnPropertyChanged(nameof(LatencyColor)); OnPropertyChanged(nameof(HasLatency)); OnPropertyChanged(nameof(LatencyBgColor)); OnPropertyChanged(nameof(StatusColor)); }
    partial void OnPingMsChanged(int value) { OnPropertyChanged(nameof(PingDisplay)); OnPropertyChanged(nameof(PingSortKey)); OnPropertyChanged(nameof(PingBgColor)); OnPropertyChanged(nameof(PingTextColor)); OnPropertyChanged(nameof(ShowPingTest)); }
    partial void OnRealDelayMsChanged(int value) { OnPropertyChanged(nameof(RealDelayDisplay)); OnPropertyChanged(nameof(RealDelaySortKey)); OnPropertyChanged(nameof(RealDelayBgColor)); OnPropertyChanged(nameof(RealDelayTextColor)); OnPropertyChanged(nameof(ShowRealDelayTest)); }
    partial void OnDownloadMbpsChanged(double value) { OnPropertyChanged(nameof(SpeedDisplay)); OnPropertyChanged(nameof(ShowSpeedTest)); }
    partial void OnCountryCodeChanged(string value) { OnPropertyChanged(nameof(Flag)); OnPropertyChanged(nameof(ShortName)); }
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

    public void RecordRealDelay(int ms)
    {
        RealDelayMs = ms;
        if (ms >= 0)
        {
            RealDelaySuccessRate = 1.0;
            RealDelayJitter = 0;
        }
        else if (ms == -2)
        {
            RealDelaySuccessRate = 0;
            RealDelayJitter = 0;
        }
        else
        {
            RealDelaySuccessRate = -1;
            RealDelayJitter = 0;
        }
        RecordLatency(ms);
    }

    public void RecordRealDelay(TestResult result)
    {
        if (result.Status == TestStatus.Cancelled) return;

        if (result.Status != TestStatus.Ok)
        {
            RealDelayMs = -2;
            RealDelaySuccessRate = 0;
            RealDelayJitter = 0;
            RecordLatency(-2);
            return;
        }

        RealDelayMs = (int)Math.Round(result.Value);
        double rate = 1.0;
        if (result.Extra.TryGetValue("ok_count", out var oc) && int.TryParse(oc, out var okc))
        {
            int total = SampleCountOrDefault(result);
            rate = total > 0 ? Math.Clamp(okc / (double)total, 0, 1) : 1.0;
        }
        RealDelaySuccessRate = rate;
        if (result.Extra.TryGetValue("jitter", out var j) && int.TryParse(j, out var ji))
            RealDelayJitter = Math.Max(0, ji);
        RecordLatency(RealDelayMs);
    }

    private static int SampleCountOrDefault(TestResult result)
        => result.Extra.TryGetValue("total_samples", out var t) && int.TryParse(t, out var n) && n > 0 ? n : 3;

    public void RecordPing(TestResult result)
    {
        if (result.Status == TestStatus.Cancelled) return;
        RecordPing(result.Status == TestStatus.Ok ? (int)Math.Round(result.Value) : -2);
    }

    public void RecordSpeed(TestResult result)
    {
        if (result.Status == TestStatus.Cancelled) return;
        RecordSpeed(result.Status == TestStatus.Ok ? result.Value : -2);
    }

    public void RecordPing(int ms)
    {
        PingMs = ms;
        RecordLatency(ms);
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
        bool aTested = a.RealDelaySuccessRate >= 0 && a.RealDelayMs >= 0;
        bool bTested = b.RealDelaySuccessRate >= 0 && b.RealDelayMs >= 0;
        if (aTested && bTested)
        {
            int c = b.RealDelaySuccessRate.CompareTo(a.RealDelaySuccessRate);
            if (c != 0) return c;

            double scoreA = a.RealDelayMs;
            double scoreB = b.RealDelayMs;
            c = scoreA.CompareTo(scoreB);
            if (c != 0) return c;
        }
        else if (aTested != bTested)
        {
            return aTested ? -1 : 1;
        }

        int ra = a.RealDelayMs < 0 ? int.MaxValue : a.RealDelayMs;
        int rb = b.RealDelayMs < 0 ? int.MaxValue : b.RealDelayMs;
        int r = ra.CompareTo(rb);
        if (r != 0) return r;

        int pa = a.PingMs < 0 ? int.MaxValue : a.PingMs;
        int pb = b.PingMs < 0 ? int.MaxValue : b.PingMs;
        r = pa.CompareTo(pb);
        if (r != 0) return r;

        double sa = a.DownloadMbps < 0 ? -1 : a.DownloadMbps;
        double sb = b.DownloadMbps < 0 ? -1 : b.DownloadMbps;
        r = sb.CompareTo(sa);
        if (r != 0) return r;

        return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
    }
}
