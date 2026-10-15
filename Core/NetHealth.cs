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

using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Roxa.Core;

/// <summary>
/// تشخیص «قطع بودن اینترنت» مستقیم و بدون DNSِ سیستم و بدون پروکسی.
/// فقط دست‌دادنِ TCP کافی نیست (پورت‌های hijack/captive هم SYN-ACK می‌فرستند)؛
/// باید یک مبادلهٔ HTTP واقعی با پاسخِ موردِ انتظار دیده شود:
/// gstatic → 204 ، msftconnecttest → بدنهٔ Microsoft Connect Test ،
/// و 1.1.1.1 → پاسخِ HTTP که خودِ 1.1.1.1 را معرفی کند (بدون نیاز به DNS).
/// برای اینکه هنگام قطع‌بودن، پیام‌های «هیچ سروری پاسخ نداد»
/// با «اینترنت قطع است» عوض شوند و کاربر گمراه نشود.
/// </summary>
public static class NetHealth
{
    /// <summary>میزبان‌های پروب — برای ساختِ بایپسِ مستقیم (TestRouteBypass) هم استفاده می‌شوند.</summary>
    public static readonly string[] ProbeHosts = { "www.gstatic.com", "www.msftconnecttest.com", "1.1.1.1" };

    /// <summary>
    /// اگر هرکدام از پروب‌ها پاسخ درست بگیرند آنلاین است؛ موازی و با یک بودجهٔ زمانی مشترک.
    /// وقتی پروکسی داخلی موتور سالم است (proxyPort) پروب‌های HTTPS از همان مسیر هم
    /// می‌روند: در حالت TUN پروب‌های خامِ پورت ۸۰ وارد تونل می‌شوند و خروجی ۸۰ِ
    /// سرورهای VPN اغلب بسته است، در حالی که ۴۴۳ کاملاً سالم است.
    /// </summary>
    public static async Task<bool> IsInternetAvailableAsync(int timeoutMs = 2500, int? proxyPort = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var tasks = new List<Task<bool>>
        {
            ProbeHttpAsync("www.gstatic.com", "/generate_204",
                firstLine => firstLine.Contains(" 204 ", StringComparison.Ordinal), null, cts.Token),
            ProbeHttpAsync("www.msftconnecttest.com", "/connecttest.txt",
                firstLine => firstLine.Contains(" 200 ", StringComparison.Ordinal), "Microsoft Connect Test", cts.Token),
            ProbeHttpAsync("1.1.1.1", "/cdn-cgi/trace",
                firstLine => firstLine.StartsWith("HTTP/", StringComparison.Ordinal), "1.1.1.1", cts.Token),
            // پوشش پورت 443: شبکه‌ای که فقط 80 را hijack/blocks کند نباید «اینترنت قطع» گزارش شود.
            ProbeTlsAsync("www.gstatic.com", "/generate_204",
                firstLine => firstLine.Contains(" 204 ", StringComparison.Ordinal), null, cts.Token),
            // پوشش بدون DNS: با IP خام (گواهی 1.1.1.1 شامل IP-SAN است) تا اگر
            // resolve سیستم/تونل مرده باشد باز هم بتوان سلامت مسیر مستقیم را فهمید.
            ProbeTlsAsync("1.1.1.1", "/cdn-cgi/trace",
                firstLine => firstLine.StartsWith("HTTP/", StringComparison.Ordinal), "1.1.1.1", cts.Token),
        };
        if (proxyPort is > 0)
        {
            tasks.Add(ProxyProbeAsync(proxyPort.Value, "https://www.gstatic.com/generate_204", 204, null, cts.Token));
            tasks.Add(ProxyProbeAsync(proxyPort.Value, "https://www.msftconnecttest.com/connecttest.txt", 200, "Microsoft Connect Test", cts.Token));
            tasks.Add(ProxyProbeAsync(proxyPort.Value, "https://1.1.1.1/cdn-cgi/trace", null, "1.1.1.1", cts.Token));
        }
        var pending = tasks;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            if (await done)
            {
                try { cts.Cancel(); } catch { }
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> ProxyProbeAsync(int proxyPort, string url, int? expectStatus, string? bodyMustContain, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (expectStatus.HasValue && (int)resp.StatusCode != expectStatus.Value) return false;
            if (bodyMustContain != null)
            {
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (!text.Contains(bodyMustContain, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> ProbeHttpAsync(string host, string path, Func<string, bool> statusOk, string? bodyMustContain, CancellationToken ct)
    {
        try
        {
            IPAddress? target = null;
            if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                target = literal;
            }
            else
            {
                var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(ct);
                target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            if (target == null) return false;

            using var client = new TcpClient();
            await client.ConnectAsync(target, 80, ct);
            using var stream = client.GetStream();

            string request =
                $"GET {path} HTTP/1.0\r\n" +
                $"Host: {host}\r\n" +
                "User-Agent: Mozilla/5.0\r\n" +
                "Connection: close\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(request);
            await stream.WriteAsync(reqBytes, ct);

            var buffer = new byte[700];
            var ms = new MemoryStream();
            // HTTP/1.0 + Connection: close → سرور بعد از پاسخ می‌بندد؛ تا انتها می‌خوانیم
            // تا بدنه (مثل Microsoft Connect Test) هم به دست آید.
            while (ms.Length < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n <= 0) break;
                ms.Write(buffer, 0, n);
            }
            string text = Encoding.ASCII.GetString(ms.ToArray());
            int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            string firstLine = lineEnd > 0 ? text.Substring(0, lineEnd) : text;
            if (!statusOk(firstLine)) return false;
            if (bodyMustContain != null && !text.Contains(bodyMustContain, StringComparison.Ordinal)) return false;
            return true;
        }
        catch { return false; }
    }

    private static async Task<bool> ProbeTlsAsync(string host, string path, Func<string, bool> statusOk, string? bodyMustContain, CancellationToken ct)
    {
        try
        {
            IPAddress? target = null;
            if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                target = literal;
            }
            else
            {
                var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(ct);
                target = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            if (target == null) return false;

            using var client = new TcpClient();
            await client.ConnectAsync(target, 443, ct);
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            // اعتبارسنجی گواهی پیش‌فرض: هجوم/hijack روی 443 بدون گواهی معتبر رد می‌شود.
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);

            string request =
                $"GET {path} HTTP/1.0\r\n" +
                $"Host: {host}\r\n" +
                "User-Agent: Mozilla/5.0\r\n" +
                "Connection: close\r\n\r\n";
            byte[] reqBytes = Encoding.ASCII.GetBytes(request);
            await ssl.WriteAsync(reqBytes, ct);

            var buffer = new byte[700];
            var ms = new MemoryStream();
            while (ms.Length < buffer.Length)
            {
                int n = await ssl.ReadAsync(buffer, ct);
                if (n <= 0) break;
                ms.Write(buffer, 0, n);
                if (Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length).Contains("\r\n", StringComparison.Ordinal))
                    break;
            }
            string text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            string firstLine = lineEnd > 0 ? text.Substring(0, lineEnd) : text;
            if (!statusOk(firstLine)) return false;
            if (bodyMustContain != null && !text.Contains(bodyMustContain, StringComparison.Ordinal)) return false;
            return true;
        }
        catch { return false; }
    }
}
