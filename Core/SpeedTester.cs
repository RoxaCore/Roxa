using System.Diagnostics;
using System.Net;
using System.Net.Http;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>تست سرعت دانلود از طریق پروکسی Xray (نه اینترنت مستقیم).</summary>
public static class SpeedTester
{
    private static readonly string[] TestUrls =
    {
        "https://speed.cloudflare.com/__down?bytes=1000000",
        "http://cachefly.cachefly.net/1mb.test",
    };

     public static async Task<(bool Ok, double Mbps)> TestAsync(ServerInfo server, CancellationToken ct = default)
     {
         var engine = new XrayEngine();
         try
         {
             using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
             cts.CancelAfter(TimeSpan.FromSeconds(12));
              await engine.StartAsync(server, setSystemProxy: false, null, cts.Token);

             using var handler = new HttpClientHandler
             {
                 Proxy = new WebProxy($"http://127.0.0.1:{engine.HttpPort}"),
                 UseProxy = true
             };
             using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

             foreach (var url in TestUrls)
             {
                 if (ct.IsCancellationRequested) return (false, -2);
                 try
                 {
                     var sw = Stopwatch.StartNew();
                     using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                     if (!resp.IsSuccessStatusCode) continue;
                     byte[] data = await resp.Content.ReadAsByteArrayAsync(ct);
                     sw.Stop();
                     if (data.Length < 50_000) continue;
                     double mbps = data.Length * 8.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.01) / 1_000_000.0;
                     return (true, Math.Round(mbps, 1));
                 }
                 catch { /* url بعدی */ }
             }
             return (false, -2);
         }
         catch { return (false, -2); }
         finally { engine.Dispose(); }
     }
}
