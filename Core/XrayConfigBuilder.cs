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

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>ساخت کانفیگ JSON موتور xray از روی یک سرور.</summary>
public static class XrayConfigBuilder
{
    public static bool LastDnsStub { get; private set; }

    private static readonly string[] PrivateIpCidrs =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
        "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
        "224.0.0.0/4", "240.0.0.0/4",
        "::1/128", "64:ff9b::/96", "100::/64", "2001::/32", "2001:db8::/32",
        "2002::/16", "fc00::/7", "fe80::/10", "ff00::/8"
    ];

    private static JsonArray PrivateIpArray() =>
        new(PrivateIpCidrs.Select(c => (JsonNode)c).ToArray());

    /// <summary>
    /// ثانیه‌های بی‌حرکتی پیش از بسته‌شدن یک اتصال/نشست در موتور.
    /// پیش‌فرضِ xray سیصد ثانیه است؛ در حالت TUN نشست‌های UDP کشفِ شبکه
    /// تا آن موقع باز می‌مانند و هندل/حافظه انباشته می‌کنند.
    /// مقدار عمداً شصت ثانیه گذاشته شده تا QUIC/HTTP3 (که فاصله‌های
    /// بی‌حرکتِ طبیعی دارد) آسیب نبیند.
    /// </summary>
    private const int ConnIdleSeconds = 60;

    /// <summary>فقط <c>connIdle</c> تنظیم می‌شود؛ بقیهٔ فیلدها پیش‌فرض خودِ موتور می‌مانند.</summary>
    private static JsonObject BuildPolicy() =>
        new()
        {
            ["levels"] = new JsonObject
            {
                ["0"] = new JsonObject
                {
                    ["connIdle"] = ConnIdleSeconds
                }
            }
        };

    public static string Build(ServerInfo server, int socksPort, int httpPort, AppSettings? settings = null, bool? useTunOverride = null)
    {
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["policy"] = BuildPolicy(),
            ["inbounds"] = new JsonArray(),
            ["outbounds"] = new JsonArray
            {
                BuildOutbound(server),
                new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" },
                new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" }
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray
                {
                    // پورت‌های کشفِ شبکه را از همان اول می‌بندیم. این‌ها بِرودکستِ
                    // لوکال‌اند و نباید هرگز واردِ تونل شوند: 137/138 (NBNS)،
                    // 1900 (SSDP)، 5353 (mDNS)، 5355 (LLMNR) و 1947 (بِرودکستِ
                    // دانگِلِ HASP). مسیریابیِ ویندوز را نمی‌شد به‌طورِ قطعی
                    // درستش کرد — subnet-broadcast از رویِ آدرسِ آداپتور محاسبه
                    // می‌شود، نه از رویِ جدولِ مسیر — و هر بار که آن بسته‌ها
                    // وارد می‌شدند xray به‌ازایِ هر کدام یک نشستِ UDP باز می‌کرد:
                    // در یک نوبت ۱۶٬۳۰۹ بسته در یک دقیقه ثبت شد و حافظه‌اش تا
                    // ۹۴۱ مگابایت با ۱۶٬۷۰۰ هندل بالا رفت. اینجا قبل از هر قانونِ
                    // دیگری می‌آیند تا blackhole آن‌ها را بی‌آنکه socketی باز کند
                    // دور بریزد.
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["network"] = "udp",
                        ["port"] = "137,138,1900,5353,5355,1947",
                        ["outboundTag"] = "block"
                    },
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["ip"] = PrivateIpArray()
                    }
                }
            }
        };

        var inbounds = (JsonArray)config["inbounds"]!;

        bool useTun = useTunOverride ?? settings?.UseTunMode == true;
        bool dnsStub = useTun && TunDns.IsStubFree();
        LastDnsStub = dnsStub;
        if (useTun)
        {
            if (dnsStub)
            {
                inbounds.Add(new JsonObject
                {
                    ["tag"] = "dns-in",
                    ["listen"] = TunDns.StubIp,
                    ["port"] = TunDns.StubPort,
                    ["protocol"] = "dokodemo-door",
                    ["settings"] = new JsonObject
                    {
                        ["address"] = "8.8.8.8",
                        ["port"] = 53,
                        ["network"] = "udp,tcp"
                    }
                });
                ((JsonArray)config["routing"]!["rules"]!).Insert(0, new JsonObject
                {
                    ["type"] = "field",
                    ["inboundTag"] = new JsonArray("dns-in"),
                    ["outboundTag"] = "proxy"
                });
            }

            inbounds.Add(new JsonObject
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["name"] = "roxa0",
                    ["MTU"] = 1420
                },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls")
                }
            });
            inbounds.Add(new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") }
            });
            inbounds.Add(new JsonObject
            {
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["port"] = httpPort,
                ["protocol"] = "http"
            });
        }
        else
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") }
            });
            inbounds.Add(new JsonObject
            {
                ["tag"] = "http-in",
                ["listen"] = "127.0.0.1",
                ["port"] = httpPort,
                ["protocol"] = "http"
            });
        }

        var dns = BuildDnsObject(settings);
        if (dns is null && dnsStub)
            dns = new JsonObject
            {
                ["servers"] = new JsonArray(TunDns.PickUpstreamDns().Select(s => (JsonNode)s).ToArray())
            };
        if (dns is not null) config["dns"] = dns;

        return config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    public static JsonObject? BuildDnsObject(AppSettings? settings)
    {
        if (settings is null) return null;

        if (!string.IsNullOrWhiteSpace(settings.CustomDnsAdvancedJson))
        {
            try
            {
                var parsed = JsonNode.Parse(settings.CustomDnsAdvancedJson);
                if (parsed is JsonObject obj)
                {
                    return obj;
                }
            }
            catch { }
            return null;
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomDnsServers))
        {
            var servers = new JsonArray();
            foreach (var part in settings.CustomDnsServers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                servers.Add(part);
            }
            if (servers.Count > 0)
                return new JsonObject { ["servers"] = servers };
        }

        return null;
    }

    private static JsonObject BuildOutbound(ServerInfo s)
    {
        var outbound = new JsonObject { ["tag"] = "proxy" };
        var stream = BuildStreamSettings(s);
        if (stream is not null) outbound["streamSettings"] = stream;

        switch (s.Protocol)
        {
            case ProxyProtocol.VMess:
                outbound["protocol"] = "vmess";
                outbound["settings"] = new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["users"] = new JsonArray(new JsonObject
                        {
                            ["id"] = s.UserId,
                            ["alterId"] = s.AlterId,
                            ["security"] = string.IsNullOrEmpty(s.Encryption) ? "auto" : s.Encryption
                        })
                    })
                };
                break;
            case ProxyProtocol.VLESS:
                var vlessUser = new JsonObject
                {
                    ["id"] = s.UserId,
                    ["encryption"] = string.IsNullOrEmpty(s.Encryption) ? "none" : s.Encryption
                };
                if (!string.IsNullOrEmpty(s.Flow)) vlessUser["flow"] = s.Flow;
                outbound["protocol"] = "vless";
                outbound["settings"] = new JsonObject
                {
                    ["vnext"] = new JsonArray(new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["users"] = new JsonArray(vlessUser)
                    })
                };
                break;
            case ProxyProtocol.Shadowsocks:
                outbound["protocol"] = "shadowsocks";
                outbound["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray(new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["method"] = string.IsNullOrEmpty(s.Method) ? "aes-256-gcm" : s.Method,
                        ["password"] = s.Password ?? ""
                    })
                };
                break;
            case ProxyProtocol.Trojan:
                outbound["protocol"] = "trojan";
                outbound["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray(new JsonObject
                    {
                        ["address"] = s.Address,
                        ["port"] = s.Port,
                        ["password"] = s.Password ?? ""
                    })
                };
                break;
        }
        return outbound;
    }

    private static JsonObject BuildStreamSettings(ServerInfo s)
    {
        string network = s.Network switch
        {
            "h2" => "http",
            "splithttp" => "xhttp",
            _ => string.IsNullOrEmpty(s.Network) ? "tcp" : s.Network
        };
        var stream = new JsonObject { ["network"] = network };

        switch (network)
        {
            case "tcp" when string.Equals(s.HeaderType, "http", StringComparison.OrdinalIgnoreCase):
                stream["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["request"] = new JsonObject
                        {
                            ["path"] = new JsonArray(string.IsNullOrEmpty(s.Path) ? "/" : s.Path),
                            ["headers"] = new JsonObject
                            {
                                ["Host"] = new JsonArray(string.IsNullOrEmpty(s.Host) ? s.Address : s.Host)
                            }
                        }
                    }
                };
                break;
            case "kcp":
                stream["kcpSettings"] = new JsonObject();
                var kcpMasks = new JsonArray();
                if (!string.IsNullOrEmpty(s.Seed))
                    kcpMasks.Add(new JsonObject
                    {
                        ["type"] = "mkcp-aes128gcm",
                        ["settings"] = new JsonObject { ["password"] = s.Seed }
                    });
                else
                    kcpMasks.Add(new JsonObject
                    {
                        ["type"] = "mkcp-original",
                        ["settings"] = new JsonObject()
                    });
                string? kcpHeader = MapKcpHeader(s.HeaderType);
                if (kcpHeader != null)
                    kcpMasks.Add(new JsonObject
                    {
                        ["type"] = kcpHeader,
                        ["settings"] = new JsonObject()
                    });
                stream["finalmask"] = new JsonObject { ["udp"] = kcpMasks };
                break;
            case "ws":
                stream["wsSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["headers"] = new JsonObject { ["Host"] = string.IsNullOrEmpty(s.Host) ? (string.IsNullOrEmpty(s.SNI) ? s.Address : s.SNI) : s.Host }
                };
                break;
            case "http":
                stream["httpSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["host"] = new JsonArray(string.IsNullOrEmpty(s.Host) ? (string.IsNullOrEmpty(s.SNI) ? s.Address : s.SNI) : s.Host)
                };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = s.ServiceName ?? "" };
                break;
            case "xhttp":
                var xhttp = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["host"] = string.IsNullOrEmpty(s.Host) ? (string.IsNullOrEmpty(s.SNI) ? s.Address : s.SNI) : s.Host
                };
                if (!string.IsNullOrEmpty(s.Mode)) xhttp["mode"] = s.Mode;
                if (!string.IsNullOrEmpty(s.Extra))
                {
                    try
                    {
                        if (JsonNode.Parse(s.Extra) is JsonObject extra) xhttp["extra"] = extra.DeepClone();
                    }
                    catch (JsonException) { }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(s.Path) ? "/" : s.Path,
                    ["host"] = string.IsNullOrEmpty(s.Host) ? (string.IsNullOrEmpty(s.SNI) ? s.Address : s.SNI) : s.Host
                };
                break;
        }

        switch (s.Security)
        {
            case "tls":
                stream["security"] = "tls";
                var tls = new JsonObject { ["allowInsecure"] = s.AllowInsecure };
                tls["serverName"] = !string.IsNullOrEmpty(s.SNI) ? s.SNI
                    : !string.IsNullOrEmpty(s.Host) ? s.Host : s.Address;
                if (!string.IsNullOrEmpty(s.Fingerprint)) tls["fingerprint"] = s.Fingerprint;
                if (!string.IsNullOrEmpty(s.Alpn)) tls["alpn"] = SplitList(s.Alpn);
                stream["tlsSettings"] = tls;
                break;
            case "reality":
                stream["security"] = "reality";
                var reality = new JsonObject
                {
                    ["show"] = false,
                    ["fingerprint"] = string.IsNullOrEmpty(s.Fingerprint) ? "chrome" : s.Fingerprint,
                    ["serverName"] = string.IsNullOrEmpty(s.SNI) ? s.Address : s.SNI,
                    ["publicKey"] = s.PublicKey ?? "",
                    ["shortId"] = s.ShortId ?? "",
                    ["spiderX"] = s.SpiderX ?? ""
                };
                stream["realitySettings"] = reality;
                break;
            default:
                stream["security"] = "none";
                break;
        }

        // TLS Fragment - bypass DPI (only for TLS/Reality connections)
        if (s.Security is "tls" or "reality")
        {
            stream["sockopt"] = new JsonObject
            {
                ["fragment"] = new JsonObject
                {
                    ["packets"] = "1-1",
                    ["length"] = "100-200",
                    ["interval"] = "50-100"
                }
            };
        }

        return stream;
    }

    private static string? MapKcpHeader(string? headerType)
    {
        string t = (headerType ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "" or "none" => null,
            "srp" or "srtp" => "header-srtp",
            "utp" => "header-utp",
            "dtls" => "header-dtls",
            "dns" => "header-dns",
            "wechat" or "wechat-video" => "header-wechat",
            "wireguard" => "header-wireguard",
            // مقادیر خام finalmask که خودِ لینک صریحا با پیشوند header- نوشته
            // (مثلا از سمت سرور با نسخه‌ی جدیدتر موتور) همان‌طور عبور داده می‌شوند
            // تا بی‌صدا حذف نشوند و اتصال به خاطر ناهماهنگیِ obfuscation نشکند.
            _ => IsRawFinalmaskId(t) ? t : null
        };
    }

    private static bool IsRawFinalmaskId(string t)
    {
        if (!t.StartsWith("header-", StringComparison.Ordinal) || t.Length is < 8 or > 40) return false;
        foreach (char c in t.AsSpan(7))
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        return true;
    }

    private static JsonArray SplitList(string value)
    {
        var arr = new JsonArray();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            arr.Add(item);
        return arr;
    }

    public static (string Json, Dictionary<int, ServerInfo> PortMap) BuildMultiServerConfig(
        List<ServerInfo> servers, AppSettings? settings = null)
    {
        var portMap = new Dictionary<int, ServerInfo>();
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var ports = new List<int>(servers.Count);

        for (int i = 0; i < servers.Count; i++)
        {
            int port = XrayEngine.FindFreePort();
            ports.Add(port);
            portMap[port] = servers[i];

            inbounds.Add(new JsonObject
            {
                ["tag"] = $"http-{i}",
                ["protocol"] = "http",
                ["listen"] = "127.0.0.1",
                ["port"] = port,
                ["settings"] = new JsonObject()
            });

            var outbound = BuildOutbound(servers[i]);
            outbound["tag"] = $"proxy-{i}";
            outbounds.Add(outbound);
        }

        outbounds.Add(new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" });
        outbounds.Add(new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" });

        var routingRules = new JsonArray
        {
            // همان قانونِ بستنِ بِرودکست‌های کشفِ شبکه (NBNS/SSDP/mDNS/LLMNR/HASP)
            // که در سازندهٔ تک‌سرور هم هست؛ اینجا هم لازم است.
            new JsonObject
            {
                ["type"] = "field",
                ["network"] = "udp",
                ["port"] = "137,138,1900,5353,5355,1947",
                ["outboundTag"] = "block"
            },
            new JsonObject
            {
                ["type"] = "field",
                ["outboundTag"] = "direct",
                ["ip"] = PrivateIpArray()
            }
        };
        for (int i = 0; i < servers.Count; i++)
        {
            routingRules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = $"http-{i}",
                ["outboundTag"] = $"proxy-{i}"
            });
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "none" },
            ["policy"] = BuildPolicy(),
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = routingRules
            }
        };

        var dns = BuildDnsObject(settings);
        if (dns is not null) config["dns"] = dns;

        string json = config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });

        return (json, portMap);
    }
}
