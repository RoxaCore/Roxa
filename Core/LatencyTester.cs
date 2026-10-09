using System.Diagnostics;
using System.Net.Sockets;

namespace Roxa.Core;

/// <summary>تست TCP مستقیم (بدون پروکسی) — برای مرتب‌سازی اولیه.</summary>
public static class LatencyTester
{
    public static async Task<(bool Ok, int Ms)> TcpAsync(string host, int port, int timeoutMs = 3000, TestRouteScope? scope = null)
    {
        bool created = false;
        if (scope == null)
        {
            scope = await TestRouteBypass.BeginAsync(new[] { host });
            created = true;
        }
        try
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
        finally
        {
            if (created) scope.Dispose();
        }
    }
}
