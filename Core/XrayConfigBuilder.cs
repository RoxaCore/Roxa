using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>ساخت کانفیگ JSON موتور xray از روی یک سرور.</summary>
public static class XrayConfigBuilder
{
    public static string Build(ServerInfo server, int socksPort, int httpPort, AppSettings? settings = null)
    {
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
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
                    new JsonObject
                    {
                        ["type"] = "field",
                        ["outboundTag"] = "direct",
                        ["ip"] = new JsonArray("geoip:private")
                    }
                }
            }
        };

        var inbounds = (JsonArray)config["inbounds"]!;

        bool useTun = settings?.UseTunMode == true;
        if (useTun)
        {
            inbounds.Add(new JsonObject
            {
                ["tag"] = "tun-in",
                ["protocol"] = "tun",
                ["settings"] = new JsonObject
                {
                    ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                    ["mtu"] = 1420,
                    ["autoRoute"] = false,
                    ["stack"] = "system"
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
                stream["kcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["type"] = string.IsNullOrEmpty(s.HeaderType) ? "none" : s.HeaderType },
                    ["seed"] = s.Path ?? ""
                };
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
                stream["xhttpSettings"] = new JsonObject
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

    private static JsonArray SplitList(string value)
    {
        var arr = new JsonArray();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            arr.Add(item);
        return arr;
    }

    public static (string Json, Dictionary<int, ServerInfo> PortMap) BuildMultiServerConfig(List<ServerInfo> servers)
    {
        var portMap = new Dictionary<int, ServerInfo>();
        var inbounds = new JsonArray();
        var outbounds = new JsonArray();

        int basePort = 20000;

        for (int i = 0; i < servers.Count; i++)
        {
            int port = basePort + i;
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

        var routingRules = new JsonArray();
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
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = routingRules
            }
        };

        string json = config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });

        return (json, portMap);
    }
}
