using System.Net;
using System.Net.Http;
using System.Text.Json;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>دریافت و پارس سابسکریپشن: plain-links / base64 / JSON.</summary>
public static class SubscriptionManager
{
    public static string UserAgent { get; set; } = "v2rayNG/1.8.33";

    public static async Task<List<ServerInfo>> FetchAsync(string url, CancellationToken ct = default)
    {
        int hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];
        string content = await FetchRawAsync(url, ct);
        return ParseContent(content);
    }

    public static async Task<string> FetchRawAsync(string url, CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return await http.GetStringAsync(url, ct);
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
        using var doc = JsonDocument.Parse(content);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var server = FromConfigObject(item);
                if (server != null) list.Add(server);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var server = FromConfigObject(doc.RootElement);
            if (server != null) list.Add(server);
        }
        return list;
    }

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
        foreach (var line in text.Split('\n'))
        {
            string trimmed = line.Trim().TrimStart('\uFEFF');
            if (trimmed.Length == 0 || !UrlParser.IsProtocolLink(trimmed)) continue;
            try
            {
                var server = UrlParser.Parse(trimmed);
                if (!result.Any(s => string.Equals(s.Link, server.Link, StringComparison.Ordinal)))
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
