using System.Diagnostics;
using System.Net;
using System.Net.Http;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>تست سرعت تخمینی دانلود از طریق پروکسی Xray (برای مرتب‌سازی نسبی).</summary>
public static class SpeedTester
{
    public const int MaxConcurrency = 5;

    private static readonly (string Url, bool Primary)[] TestUrls =
    {
        ("https://speed.cloudflare.com/__down?bytes=250000", true),
        ("http://cachefly.cachefly.net/1mb.test", false),
    };

    private const int EngineStartBudgetMs = 12_000;
    private const int PrimaryUrlBudgetMs = 3_000;
    private const int FallbackUrlBudgetMs = 6_000;
    private const int DownloadBudgetMs = 1_800;
    private const long TargetBytes = 250_000;
    private const long MinBytes = 50_000;
    private const int MinElapsedMs = 20;

    public static async Task<TestResult> TestAsync(ServerInfo server, CancellationToken ct = default)
    {
        var extra = new Dictionary<string, string>();
        var engine = new XrayEngine();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EngineStartBudgetMs);
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
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(FallbackUrlBudgetMs / 1000.0) };

            foreach (var (url, primary) in TestUrls)
            {
                if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
                var urlBudgetMs = primary ? PrimaryUrlBudgetMs : FallbackUrlBudgetMs;
                if (cts.IsCancellationRequested) break;

                using var urlCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                urlCts.CancelAfter(urlBudgetMs);
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, urlCts.Token);
                    if (!resp.IsSuccessStatusCode) continue;

                    using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(urlCts.Token);
                    bodyCts.CancelAfter(DownloadBudgetMs);
                    using var stream = await resp.Content.ReadAsStreamAsync(bodyCts.Token);
                    var buffer = new byte[16 * 1024];
                    long total = 0;
                    while (total < TargetBytes)
                    {
                        int n;
                        try
                        {
                            n = await stream.ReadAsync(buffer, bodyCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            break;
                        }
                        if (n <= 0) break;
                        total += n;
                    }
                    sw.Stop();

                    if (total < MinBytes) continue;
                    if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

                    double elapsed = Math.Max(sw.Elapsed.TotalSeconds, MinElapsedMs / 1000.0);
                    double mbps = total * 8.0 / elapsed / 1_000_000.0;
                    extra["bytes"] = total.ToString();
                    extra["seconds"] = elapsed.ToString("F3");
                    extra["url"] = url;
                    extra["early_stop"] = (total < TargetBytes).ToString();
                    return TestResult.Success(Math.Round(mbps, 1), extra);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return TestResult.Cancelled(extra);
                }
                catch
                {
                    // next url
                }
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
