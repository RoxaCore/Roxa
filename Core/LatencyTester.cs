using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace XrayClient.Core;

/// <summary>تست TCP/HTTP مستقیم (بدون پروکسی) — برای مرتب‌سازی اولیه.</summary>
public static class LatencyTester
{
    public static async Task<(bool Ok, int Ms)> TcpAsync(string host, int port, int timeoutMs = 3000)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return (true, (int)sw.Elapsed.TotalMilliseconds);
        }
        catch { return (false, 0); }
    }

    public static async Task<(bool Ok, int Ms)> HttpAsync(string host, int port, int timeoutMs = 3000)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync($"http://{host}:{port}");
            sw.Stop();
            return (true, (int)sw.Elapsed.TotalMilliseconds);
        }
        catch { return (false, 0); }
    }
}
