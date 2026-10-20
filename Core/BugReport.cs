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

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Roxa.Core;

/// <summary>
/// Reports bugs/crashes to the panel (api/report.php).
/// A crash is written to the queue folder the moment it happens, and an immediate send is
/// best-effort; otherwise FlushPending sends it on the next run (or after connecting).
/// Every report has two parts: the app's automatic part (version, OS, id, error/log)
/// and the user's optional text.
/// </summary>
public static class BugReport
{
    private static readonly string PendingDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roxa", "reports-pending");

    private const int PendingCap = 30;

    /// <summary>
    /// A queued report does not live longer than this.
    ///
    /// The count cap existed from the start, but the deadline did not — and the missing deadline
    /// meant that if the user never gave consent, that very first crash report would stay on
    /// disk forever. 30 days matches the count cap and is also worth more than a crash report
    /// for debugging: after a month, its log and stack have lost their usefulness for the
    /// current version.
    /// </summary>
    private const int PendingMaxAgeDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public sealed class Payload
    {
        public string Type { get; set; } = "manual";
        public string Ver { get; set; } = RemoteConfig.AppVersion;
        public string Os { get; set; } = "";
        public string Id { get; set; } = "";
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
        public string Log { get; set; } = "";
    }

    public static string OsInfo()
    {
        try
        {
            return RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
        }
        catch
        {
            return "";
        }
    }

    public static string DeviceId()
    {
        try
        {
            return Storage.LoadSettings().ClientId ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static Payload Create(string type) => new() { Type = type, Os = OsInfo(), Id = DeviceId() };

    /// <summary>
    /// Is sending a report allowed?
    ///
    /// Local writing to the queue always happens — it is harmless, and if the user gives consent
    /// later, the crash report of that very first run (the most valuable one) is not lost.
    /// This key only controls "going to the server".
    ///
    /// Without this gate, the error and stack and the last 40 log lines and ClientId would be sent
    /// silently, contrary to what we told the user in settings — the crash report also passed through
    /// the FlushPendingAsync path and never looked at settings at all.
    /// </summary>
    private static bool ConsentAllowsSend()
    {
        try { return Storage.LoadSettings().SendUsageStats; }
        catch { return false; } // couldn't read consent ⇒ no consent ⇒ don't send
    }

    /// <summary>Crash: the file is written to the queue and an immediate send is best-effort.</summary>
    public static void ReportCrash(Exception? ex, string logTail = "")
    {
        if (ex == null) return;
        var p = Create("crash");
        p.Error = Cut($"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}", 4000);
        p.Log = Cut(logTail, 4000);
        string? file = Enqueue(p);
        if (file == null) return;
        if (!ConsentAllowsSend()) return; // stays in the queue; only goes if the user consents
        _ = Task.Run(async () =>
        {
            try
            {
                if (await SendAsync(p, null)) File.Delete(file);
            }
            catch { }
        });
    }

    public static string? Enqueue(Payload p)
    {
        try
        {
            Directory.CreateDirectory(PendingDir);
            var files = Directory.GetFiles(PendingDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            while (files.Count >= PendingCap)
            {
                try { File.Delete(files[0]); } catch { }
                files.RemoveAt(0);
            }
            string file = Path.Combine(PendingDir, $"{DateTime.Now:yyyyMMddHHmmssfff}-{p.Type}.json");
            // The sanitized version is what stays on disk too, lest the queue file itself
            // later expose someone without anyone having sent it.
            File.WriteAllText(file, JsonSerializer.Serialize(Sanitized(p), JsonOptions), Encoding.UTF8);
            return file;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clears the queue of old reports.
    ///
    /// Deliberately runs before the consent check: this is local hygiene, not sending, and
    /// precisely in the no-consent state the queue grows and, if anyone is keeping an
    /// eye on it, it must go stale.
    /// </summary>
    private static void SweepExpired()
    {
        try
        {
            if (!Directory.Exists(PendingDir)) return;
            DateTime cutoff = DateTime.Now.AddDays(-PendingMaxAgeDays);
            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// The user turned off usage statistics sending ⇒ whatever was previously collected in the queue must go.
    ///
    /// Local writing of future reports deliberately continues (it is harmless, and if the user
    /// gives consent later, the most valuable report is that very first run after the crash); but
    /// what remained without consent is pointless after explicitly turning that same option off.
    /// </summary>
    public static void DiscardPending()
    {
        try
        {
            if (!Directory.Exists(PendingDir)) return;
            foreach (var file in Directory.GetFiles(PendingDir, "*.json"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    public static async Task<bool> SendAsync(Payload p, int? proxyPort, CancellationToken ct = default)
    {
        try
        {
            using var handler = new HttpClientHandler();
            if (proxyPort is > 0)
            {
                handler.Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort.Value}");
                handler.UseProxy = true;
            }
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Roxa/3.0");
            using var content = new StringContent(JsonSerializer.Serialize(Sanitized(p), JsonOptions), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"{RemoteConfig.BaseUrl}/api/report.php", content, ct);
            if (!resp.IsSuccessStatusCode) return false;
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends all queued reports; stops at the first failure (network outage).
    ///
    /// This is the most important consent control point: it is called both at app startup and after
    /// every connection, so if it had no gate here, the entire crash report would go out
    /// regardless of the settings checkbox — exactly the thing we told the user
    /// counts as usage statistics. Without consent, we only count the queue.
    /// </summary>
    public static async Task<int> FlushPendingAsync(int? proxyPort)
    {
        // Before the consent gate: the expiry sweep is not sending, it is local hygiene and
        // must run even in the no-consent state where the queue grows.
        SweepExpired();

        if (!ConsentAllowsSend()) return 0;

        int sent = 0;
        try
        {
            if (!Directory.Exists(PendingDir)) return 0;
            foreach (var file in Directory.GetFiles(PendingDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Payload>(File.ReadAllText(file, Encoding.UTF8), JsonOptions);
                    if (p == null || (p.Message == "" && p.Error == "" && p.Log == ""))
                    {
                        File.Delete(file);
                        continue;
                    }
                    if (!await SendAsync(p, proxyPort)) break;
                    File.Delete(file);
                    sent++;
                }
                catch
                {
                    break;
                }
            }
        }
        catch { }
        return sent;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly Regex UrlRe = new(
        @"\bhttps?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UuidRe = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    private static readonly Regex Ipv4Re = new(
        @"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);

    /// <summary>
    /// Removes the things a crash report must not carry: the subscription link (whose path holds a token),
    /// the UUID (server id) and IP addresses.
    ///
    /// Applied to Log and Error, not Message: we do not alter the user's manual text,
    /// because someone who wrote something themselves may want exactly that
    /// sent for debugging — and SECURITY.md explicitly asks them not to write such things.
    /// For a crash report Message is also empty, so these two parts are the whole report.
    ///
    /// Notes:
    ///  - A link is only scrubbed when it has a non-empty path or query;
    ///    a home-page link like https://example.com, valuable in a log for debugging
    ///    updates, stays, but any path that could be a subscription
    ///    token goes.
    ///  - 127.x.x.x and 0.0.0.0 are not removed: loopback is nobody's address and without it
    ///    the local proxy error is the most common thing a report contains.
    ///  - IPv6 is deliberately not removed. No trustworthy pattern can be
    ///    written for it that also doesn't match the short three-part time text (07:11:53), and wrongly
    ///    removing times from a debugging report is a real cost. If a reliable way
    ///    is found it must be added.
    /// </summary>
    public static string Scrub(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = UrlRe.Replace(s, static m => IsSecretUrl(m.Value) ? "<url>" : m.Value);
        s = UuidRe.Replace(s, "<uuid>");
        s = Ipv4Re.Replace(s, static m => IsPublicIpv4(m.Value) ? "<ip>" : m.Value);
        return s;
    }

    /// <summary>A link with a path or query ⇒ it can be a subscription token.</summary>
    private static bool IsSecretUrl(string raw)
    {
        string t = raw.TrimEnd('.', ',', ')', ']', ';', '>', '"');
        if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
        string path = u.AbsolutePath;
        return !string.IsNullOrEmpty(u.Query) || (path.Length > 1 && path != "/");
    }

    /// <summary>A meaningful, non-loopback IPv4 ⇒ an address that must go.</summary>
    private static bool IsPublicIpv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        int first = -1;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out int v) || v > 255) return false;
            if (first < 0) first = v;
        }
        return first is not (0 or 127);
    }

    /// <summary>
    /// The scrubbed version of the report, before serialization. We do not modify the Payload itself
    /// so the caller sees exactly what they gave; only what goes to disk or
    /// the network is scrubbed.
    /// </summary>
    private static Payload Sanitized(Payload p)
    {
        if (p.Error.Length == 0 && p.Log.Length == 0) return p;
        return new Payload
        {
            Type = p.Type,
            Ver = p.Ver,
            Os = p.Os,
            Id = p.Id,
            Message = p.Message,
            Error = Scrub(p.Error),
            Log = Scrub(p.Log),
        };
    }
}
