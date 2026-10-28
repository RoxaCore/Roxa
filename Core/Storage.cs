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
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>
/// JSON storage in the AppData/Roxa folder.
/// servers.json and subscriptions.json are encrypted with DPAPI (CurrentUser);
/// settings.json is plain text (settings-heavy, and the install ID must be readable
/// before the engine loads).
/// </summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly byte[] ServersEntropy = Encoding.UTF8.GetBytes("Roxa.servers.dpapi.v1");
    private static readonly byte[] SubscriptionsEntropy = Encoding.UTF8.GetBytes("Roxa.subscriptions.dpapi.v1");

    private static readonly string _dataDir;
    static Storage()
    {
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa");
        Directory.CreateDirectory(_dataDir);
    }

    public static string DataDir => _dataDir;

    /// <summary>
    /// Announced once when DPAPI fails and subscriptions.json gets written as
    /// plain text instead of being encrypted.
    ///
    /// Storage builds no UI; it only reports. Silence is the worst case: the user
    /// must learn right away that their subscription links lost Windows account
    /// protection, not find out later themselves. Announced once per run so that
    /// consecutive saves don't create back-to-back toasts.
    /// </summary>
    public static event Action? SubscriptionsWrittenInClearText;

    private static bool _clearTextAnnounced;

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

    /// <summary>
    /// Detects whether the bytes start out looking like JSON.
    ///
    /// This is our discriminator between «old plain-text version» and «encrypted
    /// text», so it must be definitive: a DPAPI blob never starts with `[` or `{`,
    /// so if it does, the file is plain text and must migrate.
    /// </summary>
    private static bool LooksLikeJson(byte[] raw)
    {
        foreach (byte b in raw)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b is (byte)'[' or (byte)'{';
        }
        return false;
    }

    public static List<SubscriptionInfo> LoadSubscriptions()
    {
        try
        {
            if (!File.Exists(SubscriptionsFile)) return new List<SubscriptionInfo>();
            byte[] raw = File.ReadAllBytes(SubscriptionsFile);
            if (raw.Length == 0) return new List<SubscriptionInfo>();

            string json;
            // The old file is plain text; a successful load means it must be encrypted
            // right now, else the subscription link (holding a token) stays plain on disk.
            bool migrate = false;

            if (OperatingSystem.IsWindows() && !LooksLikeJson(raw))
            {
#pragma warning disable CA1416
                try
                {
                    byte[] plain = ProtectedData.Unprotect(raw, SubscriptionsEntropy, DataProtectionScope.CurrentUser);
                    json = Encoding.UTF8.GetString(plain);
                }
                catch (CryptographicException)
                {
                    // Neither our plain text nor our cipher — e.g. a profile copied
                    // from another user or machine. We still read it as text; if it
                    // isn't JSON, the outer catch quarantines it and keeps the copy.
                    json = Encoding.UTF8.GetString(raw);
                }
#pragma warning restore CA1416
            }
            else
            {
                json = Encoding.UTF8.GetString(raw);
                migrate = OperatingSystem.IsWindows();
            }

            var list = JsonSerializer.Deserialize<List<SubscriptionInfo>>(json) ?? new List<SubscriptionInfo>();
            if (migrate) SaveSubscriptions(list);
            return list;
        }
        catch { Quarantine(SubscriptionsFile); return new List<SubscriptionInfo>(); }
    }

    public static void SaveSubscriptions(List<SubscriptionInfo> subs)
    {
        string json = JsonSerializer.Serialize(subs, Options);
        byte[] plain = Encoding.UTF8.GetBytes(json);
        if (!OperatingSystem.IsWindows())
        {
            WriteAtomic(SubscriptionsFile, plain);
            return;
        }
#pragma warning disable CA1416
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(plain, SubscriptionsEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Losing the user's subscription link is worse than writing plain text.
            // We write it plainly, raise a warning, and the next successful save
            // re-encrypts it — the old-data migration follows the same path.
            TunManager.Log("Storage: DPAPI failed for subscriptions.json; wrote it in plain text. It will be re-encrypted on the next successful save.");
            WriteAtomic(SubscriptionsFile, plain);
            AnnounceClearText();
            return;
        }
#pragma warning restore CA1416
        WriteAtomic(SubscriptionsFile, protectedBytes);
    }

    /// <summary>
    /// One-time announcement of «فایل متنی نوشته شد». The one-shot guard is here, not in the caller,
    /// because the call path is single and every later save lands here too.
    /// </summary>
    private static void AnnounceClearText()
    {
        if (_clearTextAnnounced) return;
        _clearTextAnnounced = true;
        SubscriptionsWrittenInClearText?.Invoke();
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch { Quarantine(SettingsFile); return new AppSettings(); }
    }

    /// <summary>
    /// True only when settings.json has never existed: a genuinely fresh install.
    /// The language picker is shown exactly then; an existing file (even an empty
    /// or broken one) means the user has run the app before and their choice —
    /// or the default — must be kept, not asked again.
    /// </summary>
    public static bool FirstRun => !File.Exists(SettingsFile);

    public static void SaveSettings(AppSettings settings)
        => WriteAtomic(SettingsFile, JsonSerializer.Serialize(settings, Options));

    /// <summary>
    /// Atomic write: first into a temp file, then an atomic replace.
    ///
    /// Ordinary File.WriteAllText first truncates the destination file to zero and
    /// then writes; so a crash or kill at that instant leaves a partial file and Load
    /// silently restores every default — the user's language, keys, servers,
    /// subscriptions and kill-switch settings silently vanished and nobody noticed.
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
    /// We don't throw a corrupt file away; we keep it next to its place so it can be
    /// inspected and recovered. Silently deleting user data is not acceptable.
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
