using System.Text.Json;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>ذخیره‌سازی JSON در پوشه AppData/Roxa.</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly string _dataDir;
    static Storage()
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa");
        Directory.CreateDirectory(_dataDir);
    }

    public static string DataDir => _dataDir;

    private static string ServersFile => Path.Combine(_dataDir, "servers.json");
    private static string SubscriptionsFile => Path.Combine(_dataDir, "subscriptions.json");
    private static string SettingsFile => Path.Combine(_dataDir, "settings.json");

    public static List<ServerInfo> LoadServers()
    {
        try
        {
            if (!File.Exists(ServersFile)) return new List<ServerInfo>();
            return JsonSerializer.Deserialize<List<ServerInfo>>(File.ReadAllText(ServersFile)) ?? new List<ServerInfo>();
        }
        catch { return new List<ServerInfo>(); }
    }

    public static void SaveServers(List<ServerInfo> servers)
        => File.WriteAllText(ServersFile, JsonSerializer.Serialize(servers, Options));

    public static List<SubscriptionInfo> LoadSubscriptions()
    {
        try
        {
            if (!File.Exists(SubscriptionsFile)) return new List<SubscriptionInfo>();
            return JsonSerializer.Deserialize<List<SubscriptionInfo>>(File.ReadAllText(SubscriptionsFile)) ?? new List<SubscriptionInfo>();
        }
        catch { return new List<SubscriptionInfo>(); }
    }

    public static void SaveSubscriptions(List<SubscriptionInfo> subs)
        => File.WriteAllText(SubscriptionsFile, JsonSerializer.Serialize(subs, Options));

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public static void SaveSettings(AppSettings settings)
        => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, Options));
}
