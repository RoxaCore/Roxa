using System.Diagnostics;
using System.Net;
using System.Net.Http;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>تست Real Delay: بالا آوردن موقت انجین و درخواست واقعی از طریق پروکسی.</summary>
public static class RealDelayTester
{
    private static readonly string[] TestUrls =
    {
        "https://www.google.com/generate_204",
        "http://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204"
    };

     public static async Task<(bool Ok, int Ms)> TestAsync(ServerInfo server, CancellationToken ct = default)
     {
         var engine = new XrayEngine();
         try
         {
             using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
              cts.CancelAfter(TimeSpan.FromSeconds(6));
              await engine.StartAsync(server, setSystemProxy: false, null, cts.Token);

             using var handler = new HttpClientHandler
             {
                 Proxy = new WebProxy($"http://127.0.0.1:{engine.HttpPort}"),
                 UseProxy = true
             };
             using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

             foreach (var url in TestUrls)
             {
                 if (ct.IsCancellationRequested) return (false, 0);
                 try
                 {
                     var sw = Stopwatch.StartNew();
                     using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                     if (resp.IsSuccessStatusCode)
                     {
                         sw.Stop();
                         return (true, (int)sw.ElapsedMilliseconds);
                     }
                 }
                 catch { /* url بعدی */ }
             }
             return (false, 0);
         }
         catch { return (false, 0); }
         finally { engine.Dispose(); }
     }
}
