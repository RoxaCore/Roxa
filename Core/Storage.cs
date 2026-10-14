// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

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
        catch { Quarantine(ServersFile); return new List<ServerInfo>(); }
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
            WriteAtomic(ServersFile, protectedBytes);
        }
        else
        {
            WriteAtomic(ServersFile, plain);
        }
    }

    public static List<SubscriptionInfo> LoadSubscriptions()
    {
        try
        {
            if (!File.Exists(SubscriptionsFile)) return new List<SubscriptionInfo>();
            return JsonSerializer.Deserialize<List<SubscriptionInfo>>(File.ReadAllText(SubscriptionsFile)) ?? new List<SubscriptionInfo>();
        }
        catch { Quarantine(SubscriptionsFile); return new List<SubscriptionInfo>(); }
    }

    public static void SaveSubscriptions(List<SubscriptionInfo> subs)
        => WriteAtomic(SubscriptionsFile, JsonSerializer.Serialize(subs, Options));

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch { Quarantine(SettingsFile); return new AppSettings(); }
    }

    public static void SaveSettings(AppSettings settings)
        => WriteAtomic(SettingsFile, JsonSerializer.Serialize(settings, Options));

    /// <summary>
    /// نوشتنِ اتمیک: اول در فایلِ موقت، بعد جایگزینیِ اتمیک.
    ///
    /// File.WriteAllText معمولی اول طولِ فایلِ مقصد را صفر می‌کند و بعد می‌نویسد؛
    /// پس یک کرش یا kill در همان لحظه یک فایلِ ناقص می‌گذارد و Load در سکوت
    /// همه‌چیز را به پیش‌فرض برمی‌گرداند — یعنی زبان، کلیدها، سرورها، اشتراک‌ها و
    /// تنظیماتِ کیل‌سوییچِ کاربر بی‌صدا گم می‌شد و کسی هم خبر نمی‌شد.
    /// </summary>
    private static void WriteAtomic(string path, byte[] content)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, true);
    }

    private static void WriteAtomic(string path, string content)
        => WriteAtomic(path, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// فایلِ خراب را دور نمی‌ریزیم؛ کنارِ خودش نگه می‌داریم تا قابلِ بررسی و
    /// بازیابی باشد. بی‌صدا حذف‌کردنِ دادهٔ کاربر قابلِ قبول نیست.
    /// </summary>
    private static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Copy(path, path + ".bad", true);
            TunManager.Log($"Storage: '{Path.GetFileName(path)}' was unreadable; kept a copy as '.bad'.");
        }
        catch { }
    }
}
