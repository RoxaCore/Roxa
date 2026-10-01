using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Roxa.Models;

namespace Roxa.Core;

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
        using var bypass = await TestRouteBypass.BeginAsync(new[] { server.Address });
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

            return await MeasureUrlsAsync(http, cts, ct, extra);
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

    /// <summary>تست سرعت دسته‌ای با یک پروسه Xray مشترک (multi-outbound)؛ در صورت شکست به تست تکی برمی‌گردد.</summary>
    public static async Task<Dictionary<string, TestResult>> TestBatchAsync(
        IReadOnlyList<ServerInfo> servers,
        CancellationToken ct = default,
        Action<ServerInfo, TestResult>? onResult = null)
    {
        var results = new Dictionary<string, TestResult>();
        if (servers.Count == 0) return results;
        using var bypass = await TestRouteBypass.BeginAsync(servers.Select(s => s.Address));

        if (servers.Count == 1)
        {
            var single = await TestAsync(servers[0], ct);
            results[servers[0].Id] = single;
            onResult?.Invoke(servers[0], single);
            return results;
        }

        XrayEngine? engine = null;
        bool multiStarted = false;

        try
        {
            engine = new XrayEngine();
            var (cfg, portMap) = XrayConfigBuilder.BuildMultiServerConfig(servers.ToList());
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromMilliseconds(EngineStartBudgetMs + servers.Count * 250L));
            try
            {
                await engine.StartMultiAsync(cfg, portMap, startCts.Token);
                multiStarted = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                multiStarted = false;
            }

            if (!multiStarted)
            {
                engine.Dispose();
                engine = null;
                await RunSequentialFallbackAsync(servers, results, onResult, ct);
                return results;
            }

            var portByServer = new Dictionary<ServerInfo, int>(servers.Count);
            foreach (var kv in portMap)
                portByServer[kv.Value] = kv.Key;

            using var sem = new SemaphoreSlim(MaxConcurrency);
            var tasks = servers.Select(async s =>
            {
                try { await sem.WaitAsync(ct); }
                catch (OperationCanceledException)
                {
                    var cancelled = TestResult.Cancelled();
                    lock (results) results[s.Id] = cancelled;
                    onResult?.Invoke(s, cancelled);
                    return;
                }
                try
                {
                    TestResult result;
                    if (ct.IsCancellationRequested)
                    {
                        result = TestResult.Cancelled();
                    }
                    else if (!portByServer.TryGetValue(s, out int port))
                    {
                        result = TestResult.Fail(TestStatus.Error);
                    }
                    else
                    {
                        result = await MeasureOnPortAsync(port, ct);
                    }

                    lock (results) results[s.Id] = result;
                    onResult?.Invoke(s, result);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var cancelled = TestResult.Cancelled();
                    lock (results) results[s.Id] = cancelled;
                    onResult?.Invoke(s, cancelled);
                }
                catch (Exception ex)
                {
                    var fail = TestResult.Fail(TestStatus.Error, new Dictionary<string, string>
                    {
                        ["error"] = ex.Message
                    });
                    lock (results) results[s.Id] = fail;
                    onResult?.Invoke(s, fail);
                }
                finally
                {
                    try { sem.Release(); } catch { }
                }
            });

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            engine?.Dispose();
        }

        foreach (var s in servers)
        {
            if (!results.ContainsKey(s.Id))
            {
                var fail = TestResult.Fail(TestStatus.Error);
                results[s.Id] = fail;
                onResult?.Invoke(s, fail);
            }
        }

        return results;
    }

    private static async Task RunSequentialFallbackAsync(
        IReadOnlyList<ServerInfo> servers,
        Dictionary<string, TestResult> results,
        Action<ServerInfo, TestResult>? onResult,
        CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(MaxConcurrency);
        var tasks = servers.Select(async s =>
        {
            if (results.ContainsKey(s.Id)) return;
            try { await sem.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                var cancelled = TestResult.Cancelled();
                lock (results) results[s.Id] = cancelled;
                onResult?.Invoke(s, cancelled);
                return;
            }
            try
            {
                if (ct.IsCancellationRequested)
                {
                    var cancelled = TestResult.Cancelled();
                    lock (results) results[s.Id] = cancelled;
                    onResult?.Invoke(s, cancelled);
                    return;
                }

                var r = await TestAsync(s, ct);
                lock (results) results[s.Id] = r;
                onResult?.Invoke(s, r);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                var cancelled = TestResult.Cancelled();
                lock (results) results[s.Id] = cancelled;
                onResult?.Invoke(s, cancelled);
            }
            catch (Exception ex)
            {
                var fail = TestResult.Fail(TestStatus.Error, new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
                lock (results) results[s.Id] = fail;
                onResult?.Invoke(s, fail);
            }
            finally
            {
                try { sem.Release(); } catch { }
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private static async Task<TestResult> MeasureOnPortAsync(int httpPort, CancellationToken ct)
    {
        var extra = new Dictionary<string, string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(EngineStartBudgetMs);

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
                UseProxy = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(FallbackUrlBudgetMs / 1000.0) };

            return await MeasureUrlsAsync(http, cts, ct, extra);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TestResult.Cancelled(extra);
        }
        catch
        {
            return TestResult.Fail(TestStatus.Error, extra);
        }
    }

    private static async Task<TestResult> MeasureUrlsAsync(
        HttpClient http,
        CancellationTokenSource cts,
        CancellationToken ct,
        Dictionary<string, string> extra)
    {
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
}
