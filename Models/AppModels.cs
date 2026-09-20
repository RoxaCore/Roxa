namespace XrayClient.Models;

public class SubscriptionInfo : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public string Name { get; set; } = "";
    public string Url { get; set; } = "";

    private DateTime _lastUpdated = DateTime.MinValue;
    public DateTime LastUpdated { get => _lastUpdated; set { _lastUpdated = value; OnPropertyChanged(nameof(LastUpdated)); } }

    private int _serverCount;
    public int ServerCount { get => _serverCount; set { _serverCount = value; OnPropertyChanged(nameof(ServerCount)); } }

    private long _dataLimit;
    public long DataLimit { get => _dataLimit; set { _dataLimit = value; OnPropertyChanged(nameof(DataLimit)); OnPropertyChanged(nameof(TrafficPercent)); OnPropertyChanged(nameof(TrafficDisplay)); } }

    private long _usedTraffic;
    public long UsedTraffic { get => _usedTraffic; set { _usedTraffic = value; OnPropertyChanged(nameof(UsedTraffic)); OnPropertyChanged(nameof(TrafficPercent)); OnPropertyChanged(nameof(TrafficDisplay)); } }

    private DateTime _expireDate = DateTime.MinValue;
    public DateTime ExpireDate { get => _expireDate; set { _expireDate = value; OnPropertyChanged(nameof(ExpireDate)); OnPropertyChanged(nameof(ExpireText)); OnPropertyChanged(nameof(ExpireDisplay)); } }

    public string TrafficDisplay
    {
        get
        {
            if (DataLimit <= 0) return "";
            return $"{FormatBytes(UsedTraffic)} / {FormatBytes(DataLimit)}";
        }
    }

    public double TrafficPercent
    {
        get
        {
            if (DataLimit <= 0) return 0;
            return Math.Min(1.0, (double)UsedTraffic / DataLimit);
        }
    }

    public string ExpireText
    {
        get
        {
            if (ExpireDate == DateTime.MinValue) return "";
            if (ExpireDate < DateTime.Now) return "منقضی شده";
            var remaining = ExpireDate - DateTime.Now;
            if (remaining.TotalDays < 1) return $"{(int)remaining.TotalHours} ساعت باقی‌مانده";
            return $"{(int)remaining.TotalDays} روز باقی‌مانده";
        }
    }

    public string ExpireDisplay
    {
        get
        {
            if (ExpireDate == DateTime.MinValue) return "";
            if (ExpireDate < DateTime.Now) return "منقضی شده";
            var remaining = ExpireDate - DateTime.Now;
            if (remaining.TotalDays < 1) return $"{(int)remaining.TotalHours} ساعت باقی‌مانده";
            if (remaining.TotalDays < 30) return $"{(int)remaining.TotalDays} روز باقی‌مانده";
            return ExpireDate.ToString("yyyy/MM/dd");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double val = bytes;
        int unit = 0;
        while (val >= 1024 && unit < Units.Length - 1) { val /= 1024; unit++; }
        return $"{val:F2} {Units[unit]}";
    }
}

public class AppSettings
{
    public string? ActiveServerId { get; set; }
    public bool SetSystemProxy { get; set; } = true;
    public bool AutoConnect { get; set; } = false;
    public bool AutoUpdateSubs { get; set; } = false;
    public int AutoUpdateIntervalHours { get; set; } = 12;
    public DateTime LastSubscriptionUpdate { get; set; } = DateTime.MinValue;
    public HashSet<string> CollapsedGroups { get; set; } = new();
    public string SortField { get; set; } = "none";
    public bool SortAscending { get; set; } = true;
    public bool GlobalSort { get; set; }
    public Dictionary<string, string> GroupSortFields { get; set; } = new();
    public string CustomDnsServers { get; set; } = "";
    public string? CustomDnsAdvancedJson { get; set; }
    public bool UseTunMode { get; set; }
    public bool LeaveSystemProxyAsIs { get; set; }
    public bool ClearProxyOnDisconnect { get; set; } = true;
    public bool UseProxyForSubscriptions { get; set; } = true;
    public string Language { get; set; } = "fa";
}
