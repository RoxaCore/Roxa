namespace XrayClient.Models;

public class SubscriptionInfo : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    private static readonly string[] UnitsFa = ["بایت", "کیلوبایت", "مگابایت", "گیگابایت", "ترابایت"];

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
            int totalUnit = UnitIndex(DataLimit);
            string totalVal = ValueText(DataLimit, totalUnit);
            if (UsedTraffic <= 0)
                return $"حجم مصرفی 0 از {totalVal} {UnitsFa[totalUnit]}";
            int usedUnit = UnitIndex(UsedTraffic);
            string usedVal = ValueText(UsedTraffic, usedUnit);
            return usedUnit == totalUnit
                ? $"حجم مصرفی {usedVal} از {totalVal} {UnitsFa[totalUnit]}"
                : $"حجم مصرفی {usedVal} {UnitsFa[usedUnit]} از {totalVal} {UnitsFa[totalUnit]}";
        }
    }

    private static int UnitIndex(long bytes)
    {
        double val = bytes;
        int unit = 0;
        while (val >= 1024 && unit < Units.Length - 1) { val /= 1024; unit++; }
        return unit;
    }

    private static string ValueText(long bytes, int unit)
    {
        double val = bytes;
        for (int i = 0; i < unit; i++) val /= 1024;
        return val.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
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
