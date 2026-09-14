namespace XrayClient.Models;

public class SubscriptionInfo
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
    public int ServerCount { get; set; }
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
    public string SortField { get; set; } = "بدون مرتب";
    public bool SortAscending { get; set; } = true;
    public Dictionary<string, string> GroupSortFields { get; set; } = new();
}
