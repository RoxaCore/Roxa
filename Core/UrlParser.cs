using System.Text;
using System.Text.Json;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>پارس لینک‌های اشتراک‌گذاری به ServerInfo. پشتیبانی: vmess/vless/ss/trojan.</summary>
public static class UrlParser
{
    public static bool IsProtocolLink(string text)
    {
        text = text.Trim();
        return text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase);
    }

    public static ServerInfo Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(text);
        if (text.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(text);
        if (text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(text);
        if (text.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(text);
        throw new FormatException("فقط vmess / vless / ss / trojan پشتیبانی می‌شود.");
    }

    private static ServerInfo ParseVmess(string text)
    {
        string jsonText = DecodeBase64(text["vmess://".Length..]);
        using var doc = JsonDocument.Parse(jsonText);
        var r = doc.RootElement;

        string Get(string key)
        {
            if (!r.TryGetProperty(key, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
        }

        var server = new ServerInfo
        {
            Protocol = ProxyProtocol.VMess,
            Link = text,
            Name = Get("ps"),
            Address = Get("add"),
            Port = int.TryParse(Get("port"), out int port) && port > 0 ? port : 443,
            UserId = Get("id"),
            AlterId = int.TryParse(Get("aid"), out int aid) ? aid : 0,
            Network = NormalizeNetwork(Get("net")),
            Security = NormalizeSecurity(Get("tls")),
            Host = Get("host"),
            Path = Get("path"),
            Flow = Get("flow"),
            Fingerprint = Get("fp"),
            Alpn = Get("alpn"),
            PublicKey = Get("pbk"),
            ShortId = Get("sid"),
            SpiderX = Get("spx"),
            HeaderType = Get("type"),
            ServiceName = Get("serviceName"),
            SNI = Get("sni"),
            Encryption = Get("scy"),
            AllowInsecure = Get("allowInsecure") is "1" or "true"
        };
        if (string.IsNullOrEmpty(server.Name)) server.Name = server.Address;
        if (string.IsNullOrEmpty(server.Encryption)) server.Encryption = "auto";
        return server;
    }

    private static ServerInfo ParseVless(string text)
    {
        string rest = text["vless://".Length..];
        int at = rest.IndexOf('@');
        if (at <= 0) throw new FormatException("لینک VLESS نامعتبر است.");

        var server = new ServerInfo
        {
            Protocol = ProxyProtocol.VLESS,
            Link = text,
            UserId = Uri.UnescapeDataString(rest[..at])
        };
        string hostPort = rest[(at + 1)..];
        string fragment = ExtractFragment(ref hostPort);
        var qp = ExtractQuery(ref hostPort);
        var (address, port) = SplitHostPort(hostPort);
        server.Address = address;
        server.Port = port;
        server.Name = string.IsNullOrEmpty(fragment) ? address : fragment;
        server.Network = NormalizeNetwork(qp.Get("type"));
        server.Security = NormalizeSecurity(qp.Get("security"));
        server.Flow = qp.Get("flow");
        server.Host = qp.Get("host");
        server.Path = qp.Get("path");
        server.ServiceName = qp.Get("serviceName");
        server.SNI = qp.Get("sni");
        server.Fingerprint = qp.Get("fp");
        server.Alpn = qp.Get("alpn");
        server.HeaderType = qp.Get("headerType");
        server.PublicKey = qp.Get("pbk");
        server.ShortId = qp.Get("sid");
        server.SpiderX = qp.Get("spx");
        server.Encryption = string.IsNullOrEmpty(qp.Get("encryption")) ? "none" : qp.Get("encryption");
        server.AllowInsecure = qp.Get("allowInsecure") is "1" or "true";
        return server;
    }

    private static ServerInfo ParseTrojan(string text)
    {
        string rest = text["trojan://".Length..];
        int at = rest.IndexOf('@');
        if (at <= 0) throw new FormatException("لینک Trojan نامعتبر است.");

        var server = new ServerInfo
        {
            Protocol = ProxyProtocol.Trojan,
            Link = text,
            Password = Uri.UnescapeDataString(rest[..at])
        };
        string hostPort = rest[(at + 1)..];
        string fragment = ExtractFragment(ref hostPort);
        var qp = ExtractQuery(ref hostPort);
        var (address, port) = SplitHostPort(hostPort);
        server.Address = address;
        server.Port = port;
        server.Name = string.IsNullOrEmpty(fragment) ? address : fragment;
        server.Security = NormalizeSecurity(qp.Get("security"));
        server.Flow = qp.Get("flow");
        server.Network = NormalizeNetwork(qp.Get("type"));
        server.Host = qp.Get("host");
        server.Path = qp.Get("path");
        server.ServiceName = qp.Get("serviceName");
        server.SNI = qp.Get("sni");
        server.Fingerprint = qp.Get("fp");
        server.Alpn = qp.Get("alpn");
        server.HeaderType = qp.Get("headerType");
        server.AllowInsecure = qp.Get("allowInsecure") is "1" or "true";
        if (string.IsNullOrEmpty(server.Security)) server.Security = "tls";
        return server;
    }

    private static ServerInfo ParseShadowsocks(string text)
    {
        string payload = text["ss://".Length..];
        string fragment = ExtractFragment(ref payload);

        string userinfo;
        string hostPort;
        if (payload.Contains('@'))
        {
            int at = payload.IndexOf('@');
            string encoded = payload[..at];
            userinfo = encoded.Contains(':') ? encoded : DecodeBase64(encoded);
            hostPort = payload[(at + 1)..];
            int q = hostPort.IndexOf('?');
            if (q >= 0) hostPort = hostPort[..q];
        }
        else
        {
            string decoded = DecodeBase64(payload);
            int at = decoded.IndexOf('@');
            if (at <= 0) throw new FormatException("لینک Shadowsocks نامعتبر است.");
            userinfo = decoded[..at];
            hostPort = decoded[(at + 1)..];
        }

        int colon = userinfo.IndexOf(':');
        if (colon <= 0) throw new FormatException("لینک Shadowsocks نامعتبر است.");
        var (address, port) = SplitHostPort(hostPort);

        return new ServerInfo
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Link = text,
            Name = string.IsNullOrEmpty(fragment) ? address : Uri.UnescapeDataString(fragment),
            Address = address,
            Port = port,
            Method = userinfo[..colon],
            Password = userinfo[(colon + 1)..]
        };
    }

    private static string ExtractFragment(ref string s)
    {
        int hash = s.IndexOf('#');
        if (hash < 0) return "";
        string fragment = Uri.UnescapeDataString(s[(hash + 1)..]);
        s = s[..hash];
        return fragment;
    }

    private static Dictionary<string, string> ExtractQuery(ref string s)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = s.IndexOf('?');
        if (q < 0) return result;
        string query = s[(q + 1)..];
        s = s[..q];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq >= 0 ? pair[..eq] : pair;
            string value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : "";
            result[key] = value;
        }
        return result;
    }

    private static (string Address, int Port) SplitHostPort(string hostPort)
    {
        if (hostPort.StartsWith('['))
        {
            int close = hostPort.IndexOf(']');
            if (close > 0)
            {
                string address = hostPort[1..close];
                string rest = hostPort[(close + 1)..].TrimStart(':');
                return (address, int.TryParse(rest, out int p) ? p : 443);
            }
        }
        int idx = hostPort.LastIndexOf(':');
        if (idx < 0) return (hostPort, 443);
        return (hostPort[..idx], int.TryParse(hostPort[(idx + 1)..], out int prt) ? prt : 443);
    }

    public static string NormalizeNetwork(string network)
    {
        string n = (network ?? "").ToLowerInvariant();
        return n is "tcp" or "kcp" or "ws" or "h2" or "http" or "grpc" or "xhttp" or "splithttp" ? n : "tcp";
    }

    public static string NormalizeSecurity(string security)
    {
        string s = (security ?? "").ToLowerInvariant();
        return s is "tls" or "reality" ? s : "none";
    }

    public static string DecodeBase64(string input)
    {
        input = input.Trim().Replace('_', '/').Replace('-', '+');
        input = input.PadRight(input.Length + (4 - input.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(input));
    }
}

internal static class QueryExt
{
    public static string Get(this Dictionary<string, string> qp, string key)
        => qp.TryGetValue(key, out string? value) ? value : "";
}
