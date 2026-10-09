using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>ذخیره‌سازی JSON در پوشه AppData/Roxa. servers.json با DPAPI (CurrentUser) رمزنگاری می‌شود.</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly byte[] ServersEntropy = Encoding.UTF8.GetBytes("Roxa.servers.dpapi.v1");

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
            byte[] raw = File.ReadAllBytes(ServersFile);
            string json;
            if (OperatingSystem.IsWindows())
            {
#pragma warning disable CA1416
                try
                {
                    byte[] plain = ProtectedData.Unprotect(raw, ServersEntropy, DataProtectionScope.CurrentUser);
                    json = Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException)
                {
                    json = Encoding.UTF8.GetString(raw);
                }
#pragma warning restore CA1416
            }
            else
            {
                json = Encoding.UTF8.GetString(raw);
            }
            var list = JsonSerializer.Deserialize<List<ServerInfo>>(json) ?? new List<ServerInfo>();
            return list;
        }
        catch { return new List<ServerInfo>(); }
    }

    public static void SaveServers(List<ServerInfo> servers)
    {
        string json = JsonSerializer.Serialize(servers, Options);
        byte[] plain = Encoding.UTF8.GetBytes(json);
        if (OperatingSystem.IsWindows())
        {
#pragma warning disable CA1416
            byte[] protectedBytes = ProtectedData.Protect(plain, ServersEntropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            File.WriteAllBytes(ServersFile, protectedBytes);
        }
        else
        {
            File.WriteAllBytes(ServersFile, plain);
        }
    }

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
