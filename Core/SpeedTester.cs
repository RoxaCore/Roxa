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

    public static async Task<TestResult> TestAsync(ServerInfo server, CancellationToken ct = default)
    {
        var extra = new Dictionary<string, string>();
        var engine = new XrayEngine();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await engine.StartAsync(server, setSystemProxy: false, null, cts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return TestResult.Cancelled(extra);
            }
            catch (OperationCanceledException)
            {
                return TestResult.Fail(TestStatus.Timeout, extra);
            }
            catch
            {
                return TestResult.Fail(TestStatus.Error, extra);
            }

            if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{engine.HttpPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };

            foreach (var url in TestUrls)
            {
                if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
                if (cts.IsCancellationRequested) break;
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (!resp.IsSuccessStatusCode) continue;
                    byte[] data = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                    sw.Stop();
                    if (data.Length < 50_000) continue;
                    double mbps = data.Length * 8.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.01) / 1_000_000.0;
                    extra["bytes"] = data.Length.ToString();
                    extra["seconds"] = sw.Elapsed.TotalSeconds.ToString("F3");
                    return TestResult.Success(Math.Round(mbps, 1), extra);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return TestResult.Cancelled(extra);
                }
                catch { /* next url */ }
            }

            return cts.IsCancellationRequested && !ct.IsCancellationRequested
                ? TestResult.Fail(TestStatus.Timeout, extra)
                : TestResult.Fail(TestStatus.Error, extra);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TestResult.Cancelled(extra);
        }
        catch
        {
            return TestResult.Fail(TestStatus.Error, extra);
        }
        finally
        {
            engine.Dispose();
        }
    }
}
