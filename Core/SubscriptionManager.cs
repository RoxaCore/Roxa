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

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Roxa.Localization;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>Result of a lightweight subscription fetch probe (diagnostics button): no exception is thrown.</summary>
public sealed class FetchProbe
{
    public int StatusCode { get; set; }
    public string Reason { get; set; } = "";
    public int ServerCount { get; set; }
    public string BodyHead { get; set; } = "";
    public long ElapsedMs { get; set; }
    public string? Error { get; set; }
    public bool Success => Error == null && StatusCode >= 200 && StatusCode < 300 && ServerCount > 0;
}

/// <summary>Fetching and parsing a subscription: plain-links / base64 / JSON.</summary>
public static class SubscriptionManager
{
    public static string UserAgent { get; set; } = "v2rayNG/1.8.33";

    /// <summary>Number of attempts for transient network failures (temporary DNS outage, 5xx and …).</summary>
    private const int FetchAttempts = 3;

    private static SocketsHttpHandler CreateHandler(string? proxyAddress = null, bool forceDirect = false)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        if (forceDirect)
        {
            // The "no proxy" mode must really go direct;
            // without this line, UseProxy defaults to true and the system proxy is used.
            handler.UseProxy = false;
        }
        else if (!string.IsNullOrEmpty(proxyAddress))
        {
            handler.Proxy = new WebProxy(proxyAddress);
        }
        return handler;
    }

    public static Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchWithInfoAsync(string url, CancellationToken ct = default)
        => FetchWithRetryAsync(url, forceDirect: false, null, ct);

    /// <summary>Direct fetch: even the configured system proxy is not used.</summary>
    public static Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchWithInfoDirectAsync(string url, CancellationToken ct = default)
        => FetchWithRetryAsync(url, forceDirect: true, null, ct);

    public static Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchWithInfoAndProxyAsync(string url, string proxyAddress, CancellationToken ct = default)
        => FetchWithRetryAsync(url, forceDirect: false, proxyAddress, ct);

    /// <summary>Each smart attempt takes at most this long; shorter than the old path (30s×3)
    /// so the multi-route chain is not stalled for too long.</summary>
    private const int SmartFetchTimeoutSeconds = 20;

    /// <summary>
    /// Fetch with an automatic multi-route chain (like v2rayN): preferred route first, then the rest of the
    /// routes without user intervention — "if it did not work without the proxy, try with the proxy, and vice versa".
    /// Order: preferDirect or the engine proxy (whichever is first) ← the default system proxy.
    /// If the failure is caused by DNS left over from the previous TUN, it is repaired automatically once;
    /// previously such a case was only fixed by a full application restart.
    /// </summary>
    public static async Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchSmartAsync(
        string url, string? engineProxyAddress, bool preferDirect, Action<string>? progress = null, CancellationToken ct = default)
    {
        url = StripFragment(url);
        EnsureHttpUrl(url);

        // If DNS left over from the previous TUN blocks resolution, heal it right at the start.
        await TryHealDeadDnsAsync(progress);

        // Bypassing TUN — ours or a third party like v2rayN/sing-tun: the subscription
        // host's route is pinned straight to the physical gateway so the "direct fetch"
        // really escapes under any TUN; otherwise Iranian panels give no answer via a foreign exit.
        string fetchHost = new Uri(url).Host;
        using var bypass = await TestRouteBypass.BeginAsync(new[] { fetchHost });
        if (bypass.HasRoutes) progress?.Invoke(TranslationManager.Get("Log_SubBypass"));

        var modes = new List<(string Label, bool ForceDirect, string? Proxy)>();
        void AddMode(string label, bool forceDirect, string? proxy)
        {
            foreach (var m in modes)
                if (m.ForceDirect == forceDirect && string.Equals(m.Proxy, proxy, StringComparison.Ordinal)) return;
            modes.Add((label, forceDirect, proxy));
        }

        string directLabel = TranslationManager.Get("SubFetch_Direct");
        string proxyLabel = TranslationManager.Get("SubFetch_Proxy");
        string systemLabel = TranslationManager.Get("SubFetch_SystemProxy");

        if (preferDirect) AddMode(directLabel, forceDirect: true, null);
        if (!string.IsNullOrEmpty(engineProxyAddress)) AddMode(proxyLabel, forceDirect: false, engineProxyAddress);
        if (!preferDirect) AddMode(directLabel, forceDirect: true, null);
        AddMode(systemLabel, forceDirect: false, null);

        Exception? last = null;
        foreach (var mode in modes)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(mode.Label);
            try
            {
                return await FetchOnceAsync(url, mode.ForceDirect, mode.Proxy, ct, SmartFetchTimeoutSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                string path = mode.Proxy != null ? "engine-proxy" : mode.ForceDirect ? "direct" : "system-proxy";
                TunManager.Log($"subscription fetch via {path} failed: {ex.Message}");
                // Total failure is usually the fault of a leftover 127.0.0.1 DNS from a
                // previous TUN session; previously only a restart (via TunRecovery) fixed it.
                await TryHealDeadDnsAsync(progress);
            }
        }
        throw last ?? new InvalidOperationException("subscription fetch failed");
    }

    /// <summary>One-time repair when the adapter DNS points at the dead 127.0.0.1 stub.</summary>
    private static async Task TryHealDeadDnsAsync(Action<string>? progress)
    {
        string? msg = null;
        try
        {
            // SweepDeadAdapters spawns a PowerShell process; it froze the UI thread for seconds.
            bool needsSweep = await Task.Run(() => !TunDns.IsStubListening());
            if (!needsSweep) return;  // the stub is up; DNS is not to blame
            int healed = await Task.Run(() => TunDns.SweepDeadAdapters(out _));
            if (healed > 0)
                msg = string.Format(TranslationManager.Get("Log_SubHealDns"), healed);
        }
        catch { }
        if (msg != null)
        {
            TunManager.Log(msg);
            progress?.Invoke(msg);
        }
    }

    private static async Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchWithRetryAsync(string url, bool forceDirect, string? proxyAddress, CancellationToken ct)
    {
        url = StripFragment(url);
        EnsureHttpUrl(url);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await FetchOnceAsync(url, forceDirect, proxyAddress, ct);
            }
            catch (Exception ex) when (attempt < FetchAttempts && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    private static async Task<(List<ServerInfo> servers, SubscriptionInfo? info)> FetchOnceAsync(string url, bool forceDirect, string? proxyAddress, CancellationToken ct, int timeoutSeconds = 30)
    {
        using var handler = CreateHandler(proxyAddress, forceDirect);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Previously an error body (e.g. an HTML page behind a CDN) was silently taken as "0 servers".
            throw new HttpRequestException(
                string.Format(TranslationManager.Get("Sub_HttpStatus"), (int)response.StatusCode, response.ReasonPhrase ?? ""),
                null, response.StatusCode);
        }
        string content = await response.Content.ReadAsStringAsync(ct);
        var servers = ParseContent(content);
        if (servers.Count == 0)
        {
            throw new FormatException(content.Trim().Length == 0
                ? TranslationManager.Get("Sub_EmptyBody")
                : string.Format(TranslationManager.Get("Sub_EmptyResult"), BodyHead(content)));
        }
        SubscriptionInfo? info = null;
        try { info = ParseHeaders(response.Headers); } catch { }
        return (servers, info);
    }

    /// <summary>Light fetch for diagnostics: it reports the error instead of throwing.</summary>
    public static async Task<FetchProbe> ProbeAsync(string url, string? proxyAddress = null, bool forceDirect = false, CancellationToken ct = default)
    {
        var result = new FetchProbe();
        url = StripFragment(url);
        try { EnsureHttpUrl(url); }
        catch (Exception ex) { result.Error = ex.Message; return result; }
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = CreateHandler(proxyAddress, forceDirect);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            using var response = await http.GetAsync(url, ct);
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.StatusCode = (int)response.StatusCode;
            result.Reason = response.ReasonPhrase ?? "";
            string content = await response.Content.ReadAsStringAsync(ct);
            result.BodyHead = BodyHead(content);
            try { result.ServerCount = ParseContent(content).Count; }
            catch (JsonException) { result.ServerCount = 0; }
        }
        catch (Exception ex)
        {
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.Error = ex.InnerException?.Message ?? ex.Message;
        }
        return result;
    }

    /// <summary>Only transient network errors are retried; content/response errors are final.</summary>
    private static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            if (hre.StatusCode is null) return true;
            int code = (int)hre.StatusCode.Value;
            return code >= 500 || code == 408 || code == 429;
        }
        return ex is IOException or SocketException or TaskCanceledException;
    }

    private static string StripFragment(string url)
    {
        int hash = url.IndexOf('#');
        return hash >= 0 ? url[..hash] : url;
    }

    private static string BodyHead(string content)
    {
        string s = content.TrimStart('\uFEFF', ' ', '\r', '\n').Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 160 ? s : s[..160] + "…";
    }

    private static SubscriptionInfo? ParseHeaders(HttpResponseHeaders headers)
    {
        string? raw = null;

        // Standard format: Subscription-Userinfo: upload=1;download=2;total=3;expire=4
        if (headers.TryGetValues("Subscription-Userinfo", out var vals))
            raw = string.Join(";", vals);

        // Some panels return info in separate headers
        if (string.IsNullOrEmpty(raw))
        {
            string? upload = null, download = null, total = null, expire = null;
            if (headers.TryGetValues("upload", out vals)) upload = string.Join(";", vals);
            if (headers.TryGetValues("download", out vals)) download = string.Join(";", vals);
            if (headers.TryGetValues("total", out vals)) total = string.Join(";", vals);
            if (headers.TryGetValues("expire", out vals)) expire = string.Join(";", vals);

            // Also check Traffic-Info, Profile-Info, etc.
            if (headers.TryGetValues("Traffic-Info", out vals)) raw = string.Join(";", vals);
            else if (headers.TryGetValues("Profile-Info", out vals)) raw = string.Join(";", vals);

            if (raw == null && (upload != null || download != null || total != null || expire != null))
            {
                raw = "";
                if (upload != null) raw += $"upload={upload};";
                if (download != null) raw += $"download={download};";
                if (total != null) raw += $"total={total};";
                if (expire != null) raw += $"expire={expire};";
            }
        }

        // Last resort: check ALL headers for keywords
        if (string.IsNullOrEmpty(raw))
        {
            foreach (var h in headers)
            {
                string key = h.Key.ToLowerInvariant();
                string val = string.Join(";", h.Value);
                if (key.Contains("subscription") && val.Contains("=") && val.Contains(";"))
                {
                    raw = val;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(raw)) return null;

        var info = new SubscriptionInfo();
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            string key = kv[0].Trim().ToLowerInvariant();
            string val = kv[1].Trim();
            switch (key)
            {
                case "upload":
                    if (long.TryParse(val, out long up)) info.UsedTraffic += up;
                    break;
                case "download":
                    if (long.TryParse(val, out long down)) info.UsedTraffic += down;
                    break;
                case "total":
                    if (long.TryParse(val, out long total)) info.DataLimit = total;
                    break;
                case "expire":
                    if (long.TryParse(val, out long ts) && ts > 0)
                        info.ExpireDate = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                    break;
            }
        }
        return info;
    }

    /// <summary>The subscription link must be absolute and http/https; all other schemes are rejected.</summary>
    private static void EnsureHttpUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException(TranslationManager.Get("Sub_OnlyHttps"));
    }

    public static List<ServerInfo> ParseContent(string content)
    {
        content = content.TrimStart('\uFEFF').TrimStart();
        if (content.Length == 0) return new List<ServerInfo>();
        if (LooksLikePlainLinks(content)) return ParseLines(content);
        if (content.StartsWith('{') || content.StartsWith('['))
        {
            try { return ParseSubscriptionJson(content); }
            catch (JsonException) { }
        }
        string decoded = TryDecodeBase64(content);
        if (decoded != content)
        {
            if (LooksLikePlainLinks(decoded)) return ParseLines(decoded);
            if (decoded.TrimStart().StartsWith('{') || decoded.TrimStart().StartsWith('['))
            {
                try { return ParseSubscriptionJson(decoded); }
                catch (JsonException) { }
            }
            return ParseLines(decoded);
        }
        return ParseLines(content);
    }

    public static List<ServerInfo> ParseSubscriptionJson(string content)
    {
        var list = new List<ServerInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    var server = FromConfigObject(item);
                    if (server != null && seen.Add(DupeKey(server))) list.Add(server);
                }
                catch (FormatException) { }
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var server = FromConfigObject(doc.RootElement);
                if (server != null) list.Add(server);
            }
            catch (FormatException) { }
        }
        return list;
    }

    /// <summary>The single duplicate key: ServerInfo.GetDupeKey; the name is not part of it.</summary>
    private static string DupeKey(ServerInfo s) => s.GetDupeKey();

    private static ServerInfo? FromConfigObject(JsonElement item)
    {
        string remarks = GetString(item, "remarks") ?? "";
        if (!item.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
            return null;
        JsonElement proxy = default;
        bool found = false;
        foreach (var ob in outbounds.EnumerateArray())
        {
            string p = GetString(ob, "protocol") ?? "";
            if (p is "vless" or "vmess" or "trojan" or "shadowsocks") { proxy = ob; found = true; break; }
        }
        if (!found) return null;

        string protocol = GetString(proxy, "protocol") ?? "vless";
        var server = new ServerInfo
        {
            Name = remarks,
            Link = item.GetRawText(),
            Protocol = protocol switch
            {
                "vmess" => ProxyProtocol.VMess,
                "trojan" => ProxyProtocol.Trojan,
                "shadowsocks" => ProxyProtocol.Shadowsocks,
                _ => ProxyProtocol.VLESS
            }
        };
        var stream = GetObject(proxy, "streamSettings");
        string security = GetString(stream, "security") ?? "";
        server.Security = security is "tls" or "reality" ? security : "none";
        server.Network = UrlParser.NormalizeNetwork(GetString(stream, "network") ?? "tcp");

        var tls = GetObject(stream, "tlsSettings");
        if (tls.ValueKind == JsonValueKind.Object)
        {
            server.SNI = GetString(tls, "serverName") ?? "";
            server.Fingerprint = GetString(tls, "fingerprint") ?? "";
        }
        var reality = GetObject(stream, "realitySettings");
        if (reality.ValueKind == JsonValueKind.Object)
        {
            server.SNI = GetString(reality, "serverName") ?? "";
            server.Fingerprint = GetString(reality, "fingerprint") ?? "";
            server.PublicKey = GetString(reality, "publicKey") ?? "";
            server.ShortId = GetString(reality, "shortId") ?? "";
            server.SpiderX = GetString(reality, "spiderX") ?? "";
        }
        switch (server.Network)
        {
            case "ws":
                var ws = GetObject(stream, "wsSettings");
                server.Path = GetString(ws, "path") ?? "";
                server.Host = GetString(GetObject(ws, "headers"), "Host") ?? GetString(ws, "host") ?? "";
                break;
            case "grpc":
                server.ServiceName = GetString(GetObject(stream, "grpcSettings"), "serviceName") ?? "";
                break;
            case "xhttp":
            case "splithttp":
                var xhttp = GetObject(stream, "xhttpSettings");
                if (xhttp.ValueKind != JsonValueKind.Object) xhttp = GetObject(stream, "splithttpSettings");
                server.Path = GetString(xhttp, "path") ?? "";
                server.Host = GetString(xhttp, "host") ?? "";
                server.Mode = UrlParser.ValidateXhttpMode(GetString(xhttp, "mode") ?? "");
                if (xhttp.ValueKind == JsonValueKind.Object &&
                    xhttp.TryGetProperty("extra", out var xhttpExtra) &&
                    xhttpExtra.ValueKind == JsonValueKind.Object)
                    server.Extra = xhttpExtra.GetRawText();
                break;
            case "kcp":
                var kcp = GetObject(stream, "kcpSettings");
                server.Seed = GetString(kcp, "seed") ?? "";
                server.HeaderType = GetString(GetObject(kcp, "header"), "type") ?? "";
                break;
            case "httpupgrade":
                var httpUpgrade = GetObject(stream, "httpupgradeSettings");
                server.Path = GetString(httpUpgrade, "path") ?? "";
                server.Host = GetString(httpUpgrade, "host") ?? "";
                break;
            case "h2":
            case "http":
                var httpSt = GetObject(stream, "httpSettings");
                server.Path = GetString(httpSt, "path") ?? "";
                var hosts = GetObject(httpSt, "host");
                if (hosts.ValueKind == JsonValueKind.Array && hosts.GetArrayLength() > 0)
                    server.Host = hosts[0].GetString() ?? "";
                else
                    server.Host = GetString(httpSt, "host") ?? "";
                break;
        }
        switch (server.Protocol)
        {
            case ProxyProtocol.VLESS:
            case ProxyProtocol.VMess:
                var vnext = GetObject(proxy, "settings", "vnext");
                if (vnext.ValueKind == JsonValueKind.Array && vnext.GetArrayLength() > 0)
                {
                    var first = vnext[0];
                    server.Address = GetString(first, "address") ?? "";
                    server.Port = GetInt(first, "port", 443);
                    var users = GetObject(first, "users");
                    if (users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0)
                    {
                        var user = users[0];
                        server.UserId = GetString(user, "id") ?? "";
                        server.AlterId = GetInt(user, "alterId", 0);
                        server.Flow = GetString(user, "flow") ?? "";
                        string enc = GetString(user, "encryption") ?? "";
                        server.Encryption = string.IsNullOrEmpty(enc)
                            ? (server.Protocol == ProxyProtocol.VMess ? "auto" : "none") : enc;
                    }
                }
                break;
            case ProxyProtocol.Trojan:
            case ProxyProtocol.Shadowsocks:
                var servers = GetObject(proxy, "settings", "servers");
                if (servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
                {
                    var first = servers[0];
                    server.Address = GetString(first, "address") ?? "";
                    server.Port = GetInt(first, "port", 443);
                    server.Password = GetString(first, "password") ?? "";
                    server.Method = GetString(first, "method") ?? "";
                }
                break;
        }
        if (string.IsNullOrEmpty(server.Name)) server.Name = server.Address;
        return server;
    }

    private static List<ServerInfo> ParseLines(string text)
    {
        var result = new List<ServerInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || !UrlParser.IsProtocolLink(trimmed)) continue;
            try
            {
                var server = UrlParser.Parse(trimmed);
                if (seen.Add(DupeKey(server)))
                    result.Add(server);
            }
            catch (FormatException) { }
        }
        return result;
    }

    private static string TryDecodeBase64(string content)
    {
        try
        {
            string decoded = UrlParser.DecodeBase64(content);
            if (decoded.Contains("://")) return decoded;
        }
        catch (FormatException) { }
        return content;
    }

    private static bool LooksLikePlainLinks(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0) continue;
            return UrlParser.IsProtocolLink(trimmed);
        }
        return false;
    }

    private static string? GetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static int GetInt(JsonElement obj, string key, int fallback)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return fallback;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int r) ? r : fallback;
    }

    private static JsonElement GetObject(JsonElement obj, params string[] path)
    {
        JsonElement current = obj;
        foreach (string key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
                return default;
            current = next;
        }
        return current;
    }
}
