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

using System.Text;
using System.Text.Json.Nodes;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>ساخت لینک اشتراک‌گذاری از روی ServerInfo (معکوس UrlParser).</summary>
public static class UrlBuilder
{
    public static string Build(ServerInfo s) => s.Protocol switch
    {
        ProxyProtocol.VLESS => BuildVless(s),
        ProxyProtocol.VMess => BuildVmess(s),
        ProxyProtocol.Trojan => BuildTrojan(s),
        ProxyProtocol.Shadowsocks => BuildSs(s),
        _ => s.Link
    };

    private static string Enc(string v) => Uri.EscapeDataString(v ?? "");

    private static string BuildQuery(Dictionary<string, string> qp)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in qp)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (sb.Length > 0) sb.Append('&');
            sb.Append(k).Append('=').Append(Enc(v));
        }
        return sb.ToString();
    }

    public static string BuildVless(ServerInfo s)
    {
        var qp = new Dictionary<string, string>
        {
            ["encryption"] = string.IsNullOrEmpty(s.Encryption) ? "none" : s.Encryption,
            ["security"] = s.Security,
            ["type"] = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network,
        };
        if (!string.IsNullOrEmpty(s.Flow)) qp["flow"] = s.Flow;
        if (!string.IsNullOrEmpty(s.SNI)) qp["sni"] = s.SNI;
        if (!string.IsNullOrEmpty(s.Fingerprint)) qp["fp"] = s.Fingerprint;
        if (!string.IsNullOrEmpty(s.Alpn)) qp["alpn"] = s.Alpn;
        if (!string.IsNullOrEmpty(s.Host)) qp["host"] = s.Host;
        if (!string.IsNullOrEmpty(s.Path)) qp["path"] = s.Path;
        if (!string.IsNullOrEmpty(s.ServiceName)) qp["serviceName"] = s.ServiceName;
        if (!string.IsNullOrEmpty(s.PublicKey)) qp["pbk"] = s.PublicKey;
        if (!string.IsNullOrEmpty(s.ShortId)) qp["sid"] = s.ShortId;
        if (!string.IsNullOrEmpty(s.SpiderX)) qp["spx"] = s.SpiderX;
        if (!string.IsNullOrEmpty(s.HeaderType)) qp["headerType"] = s.HeaderType;
        if (!string.IsNullOrEmpty(s.Mode)) qp["mode"] = s.Mode;
        if (!string.IsNullOrEmpty(s.Extra)) qp["extra"] = s.Extra;
        if (!string.IsNullOrEmpty(s.Seed)) qp["seed"] = s.Seed;
        if (s.AllowInsecure) qp["allowInsecure"] = "1";
        return $"vless://{Enc(s.UserId)}@{s.Address}:{s.Port}?{BuildQuery(qp)}#{Enc(s.Name)}";
    }

    public static string BuildVmess(ServerInfo s)
    {
        var obj = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = s.Name,
            ["add"] = s.Address,
            ["port"] = s.Port.ToString(),
            ["id"] = s.UserId,
            ["aid"] = s.AlterId.ToString(),
            ["scy"] = string.IsNullOrEmpty(s.Encryption) ? "auto" : s.Encryption,
            ["net"] = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network,
            ["type"] = string.IsNullOrEmpty(s.HeaderType) ? "none" : s.HeaderType,
            ["host"] = s.Host ?? "",
            ["path"] = s.Path ?? "",
            ["tls"] = s.Security == "tls" ? "tls" : "",
            ["sni"] = s.SNI ?? "",
            ["fp"] = s.Fingerprint ?? "",
            ["alpn"] = s.Alpn ?? "",
            ["pbk"] = s.PublicKey ?? "",
            ["sid"] = s.ShortId ?? "",
            ["spx"] = s.SpiderX ?? "",
            ["serviceName"] = s.ServiceName ?? "",
            ["flow"] = s.Flow ?? ""
        };
        if (!string.IsNullOrEmpty(s.Mode)) obj["mode"] = s.Mode;
        if (!string.IsNullOrEmpty(s.Extra)) obj["extra"] = s.Extra;
        if (!string.IsNullOrEmpty(s.Seed)) obj["seed"] = s.Seed;
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(obj.ToJsonString()));
    }

    public static string BuildTrojan(ServerInfo s)
    {
        var qp = new Dictionary<string, string>
        {
            ["security"] = string.IsNullOrEmpty(s.Security) ? "tls" : s.Security,
            ["type"] = string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network,
        };
        if (!string.IsNullOrEmpty(s.SNI)) qp["sni"] = s.SNI;
        if (!string.IsNullOrEmpty(s.Fingerprint)) qp["fp"] = s.Fingerprint;
        if (!string.IsNullOrEmpty(s.Host)) qp["host"] = s.Host;
        if (!string.IsNullOrEmpty(s.Path)) qp["path"] = s.Path;
        if (!string.IsNullOrEmpty(s.ServiceName)) qp["serviceName"] = s.ServiceName;
        if (!string.IsNullOrEmpty(s.Alpn)) qp["alpn"] = s.Alpn;
        if (!string.IsNullOrEmpty(s.Mode)) qp["mode"] = s.Mode;
        if (!string.IsNullOrEmpty(s.Extra)) qp["extra"] = s.Extra;
        if (!string.IsNullOrEmpty(s.Seed)) qp["seed"] = s.Seed;
        if (s.AllowInsecure) qp["allowInsecure"] = "1";
        return $"trojan://{Enc(s.Password)}@{s.Address}:{s.Port}?{BuildQuery(qp)}#{Enc(s.Name)}";
    }

    public static string BuildSs(ServerInfo s)
    {
        string userinfo = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Method}:{s.Password}"));
        return $"ss://{userinfo}@{s.Address}:{s.Port}#{Enc(s.Name)}";
    }
}
