using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Roxa.Models;

namespace Roxa.Core;

/// <summary>تست Real Delay: انجین (تکی یا multi-outbound) + warmup موازی + ۳ نمونه median روی پروکسی محلی.</summary>
public static class RealDelayTester
{
    private static readonly string[] WarmupUrls =
    {
        "https://cp.cloudflare.com/generate_204",
        "https://www.gstatic.com/generate_204",
        "https://www.google.com/generate_204",
    };

    private const int SampleCount = 3;
    private const int PerRequestTimeoutMs = 3000;
    private const int TcpWaitTimeoutMs = 3000;
    private const int HttpPhaseTimeoutMs = 12000;
    private const int WarmupBudgetMs = 6000;
    private const int WarmupSkipThresholdMs = 300;
    private const int EngineStartTimeoutMs = 5000;
    private const int BatchHttpConcurrency = 8;

    /// <summary>
    /// همزمانیِ مسیر جایگزین (تک‌تک). این مسیر برای هر سرور یک پروسهٔ xray جداگانه بالا می‌آورد،
    /// پس باید خیلی کمتر از BatchHttpConcurrency باشد تا CPU/رم یک‌باره اشباع نشود.
    /// </summary>
    private const int FallbackProcessConcurrency = 2;

    /// <summary>مکث بین شروع سرورها در مسیر جایگزین (پلکانی‌شدن اتصال‌ها).</summary>
    private const int FallbackStaggerMs = 75;

    private static readonly TimeSpan SessionUrlTtl = TimeSpan.FromSeconds(90);
    private static readonly object SessionUrlLock = new();
    private static string? _sessionUrl;
    private static DateTime _sessionUrlAtUtc;

    private enum MeasureStatus { Ok, Timeout, Non204, Drop, Cancelled }

    public static void ResetSessionCache()
    {
        lock (SessionUrlLock)
        {
            _sessionUrl = null;
            _sessionUrlAtUtc = default;
        }
    }

    private static string? GetSessionUrl()
    {
        lock (SessionUrlLock)
        {
            if (_sessionUrl is null || DateTime.UtcNow - _sessionUrlAtUtc > SessionUrlTtl)
                return null;
            return _sessionUrl;
        }
    }

    private static void SetSessionUrl(string url)
    {
        lock (SessionUrlLock)
        {
            _sessionUrl = url;
            _sessionUrlAtUtc = DateTime.UtcNow;
        }
    }

    public static async Task<TestResult> TestAsync(ServerInfo server, CancellationToken ct = default)
    {
        using var bypass = await TestRouteBypass.BeginAsync(new[] { server.Address });
        var extra = new Dictionary<string, string>();
        var rawLog = new List<string>();
        var engine = new XrayEngine();
        var swTotal = Stopwatch.StartNew();
        int disposeMs = 0;
        try
        {
            if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

            var swStartup = Stopwatch.StartNew();
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromMilliseconds(EngineStartTimeoutMs));
            try
            {
                await engine.StartAsync(server, setSystemProxy: false, null, startCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return TestResult.Cancelled(extra);
            }
            catch (OperationCanceledException)
            {
                return TestResult.Fail(TestStatus.Timeout, extra);
            }
            catch (Exception ex)
            {
                extra["startup_error"] = Trunc(ex.Message);
                return TestResult.Fail(TestStatus.Error, extra);
            }
            swStartup.Stop();
            int startupMs = (int)swStartup.ElapsedMilliseconds;

            return await MeasureOnPortAsync(engine.HttpPort, extra, rawLog, startupMs, swTotal, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TestResult.Cancelled(extra);
        }
        catch (Exception ex)
        {
            extra["error"] = Trunc(ex.Message);
            extra["fail_reason"] = "exception";
            extra["total_ms"] = ((int)swTotal.ElapsedMilliseconds).ToString();
            return TestResult.Fail(TestStatus.Error, extra);
        }
        finally
        {
            var swDisp = Stopwatch.StartNew();
            engine.Dispose();
            swDisp.Stop();
            disposeMs = (int)swDisp.ElapsedMilliseconds;
            extra["phase_dispose_ms"] = disposeMs.ToString();
            if (!extra.ContainsKey("total_ms"))
                extra["total_ms"] = ((int)swTotal.ElapsedMilliseconds).ToString();
            if (rawLog.Count > 0 && !extra.ContainsKey("warmup_log"))
                extra["warmup_log"] = string.Join(" | ", rawLog);
        }
    }

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

        var swStartup = Stopwatch.StartNew();
        XrayEngine? engine = null;
        bool multiStarted = false;

        try
        {
            engine = new XrayEngine();
            var (cfg, portMap) = XrayConfigBuilder.BuildMultiServerConfig(servers.ToList());
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(TimeSpan.FromMilliseconds(EngineStartTimeoutMs + servers.Count * 250L));
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

            swStartup.Stop();
            int startupMs = (int)swStartup.ElapsedMilliseconds;

            var portByServer = new Dictionary<ServerInfo, int>(servers.Count);
            foreach (var kv in portMap)
                portByServer[kv.Value] = kv.Key;

            using var sem = new SemaphoreSlim(BatchHttpConcurrency);
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
                        var extra = new Dictionary<string, string>();
                        var rawLog = new List<string>();
                        var swTotal = Stopwatch.StartNew();
                        result = await MeasureOnPortAsync(port, extra, rawLog, startupMs, swTotal, ct);
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
                        ["error"] = Trunc(ex.Message),
                        ["fail_reason"] = "exception"
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
            if (engine != null)
            {
                var swDisp = Stopwatch.StartNew();
                engine.Dispose();
                swDisp.Stop();
                int disposeMs = (int)swDisp.ElapsedMilliseconds;
                foreach (var r in results.Values)
                    r.Extra["phase_dispose_ms"] = disposeMs.ToString();
            }
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
        // برخلاف Task.WhenAll روی کل لیست، اینجا هر سرور پس از آزادشدن جای خالی
        // و گذشت مکث کوتاه شروع می‌شود؛ چون هر کدام یک پروسهٔ xray بالا می‌آورد.
        using var sem = new SemaphoreSlim(FallbackProcessConcurrency);
        var tasks = new List<Task>(servers.Count);
        foreach (var s in servers)
        {
            if (results.ContainsKey(s.Id) || ct.IsCancellationRequested) continue;
            try { await sem.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            var captured = s;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        var cancelledEarly = TestResult.Cancelled();
                        lock (results) results[captured.Id] = cancelledEarly;
                        onResult?.Invoke(captured, cancelledEarly);
                        return;
                    }

                    var r = await TestAsync(captured, ct);
                    lock (results) results[captured.Id] = r;
                    onResult?.Invoke(captured, r);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    var cancelled = TestResult.Cancelled();
                    lock (results) results[captured.Id] = cancelled;
                    onResult?.Invoke(captured, cancelled);
                }
                catch (Exception ex)
                {
                    var fail = TestResult.Fail(TestStatus.Error, new Dictionary<string, string>
                    {
                        ["error"] = Trunc(ex.Message)
                    });
                    lock (results) results[captured.Id] = fail;
                    onResult?.Invoke(captured, fail);
                }
                finally
                {
                    try { sem.Release(); } catch { }
                }
            }, CancellationToken.None));

            if (FallbackStaggerMs > 0)
            {
                try { await Task.Delay(FallbackStaggerMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
    }

    private static async Task<TestResult> MeasureOnPortAsync(
        int port,
        Dictionary<string, string> extra,
        List<string> rawLog,
        int startupMs,
        Stopwatch swTotal,
        CancellationToken ct)
    {
        int tcpWaitMs = 0, warmupTotalMs = 0, samplesPhaseMs = 0;
        extra["startup_ms"] = startupMs.ToString();

        if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

        var swTcp = Stopwatch.StartNew();
        bool tcpOk = await WaitTcpAsync(port, TcpWaitTimeoutMs, ct);
        swTcp.Stop();
        tcpWaitMs = (int)swTcp.ElapsedMilliseconds;
        if (!tcpOk)
            return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, "tcp_wait_timeout");
        if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

        using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        httpCts.CancelAfter(TimeSpan.FromMilliseconds(HttpPhaseTimeoutMs));

        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(PerRequestTimeoutMs),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(PerRequestTimeoutMs),
        };

        string? chosenUrl = null;
        int warmupMs = 0;
        var swWarmup = Stopwatch.StartNew();

        string? cachedUrl = GetSessionUrl();
        if (cachedUrl != null && !httpCts.IsCancellationRequested)
        {
            int remaining = WarmupBudgetMs - (int)swWarmup.ElapsedMilliseconds;
            if (remaining >= WarmupSkipThresholdMs)
            {
                using var cacheCts = CancellationTokenSource.CreateLinkedTokenSource(httpCts.Token);
                cacheCts.CancelAfter(Math.Min(PerRequestTimeoutMs, remaining));
                var (st, ms) = await MeasureOnceAsync(http, cachedUrl, cacheCts.Token);
                rawLog.Add($"warmup cache {cachedUrl} => {st}/{ms}ms");
                if (st == MeasureStatus.Ok)
                {
                    chosenUrl = cachedUrl;
                    warmupMs = ms;
                }
                else if (st == MeasureStatus.Cancelled && ct.IsCancellationRequested)
                {
                    return TestResult.Cancelled(extra);
                }
                else
                {
                    ResetSessionCache();
                }
            }
        }

        if (chosenUrl == null && !httpCts.IsCancellationRequested)
        {
            var (url, ms) = await WarmupParallelAsync(http, httpCts.Token, WarmupBudgetMs, rawLog);
            chosenUrl = url;
            warmupMs = ms;
            if (chosenUrl != null)
                SetSessionUrl(chosenUrl);
        }

        swWarmup.Stop();
        warmupTotalMs = (int)swWarmup.ElapsedMilliseconds;

        extra["warmup_ms"] = warmupMs.ToString();
        if (chosenUrl == null)
            return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, "no_warmup_204");
        extra["url"] = chosenUrl;
        if (ct.IsCancellationRequested || httpCts.IsCancellationRequested)
            return ct.IsCancellationRequested ? TestResult.Cancelled(extra) : FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, "http_phase_timeout");

        var okSamples = new List<int>(SampleCount);
        var rawSamples = new List<string>(SampleCount);
        bool reWarmupUsed = false;
        var swSamples = Stopwatch.StartNew();

        for (int i = 0; i < SampleCount; i++)
        {
            if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
            if (httpCts.IsCancellationRequested) break;

            var t0 = Stopwatch.GetTimestamp();
            var (st, ms) = await MeasureOnceAsync(http, chosenUrl, httpCts.Token);
            int el = ElapsedMs(t0);
            rawSamples.Add($"s{i + 1}={st}/{ms}ms/{el}ms");

            if (st == MeasureStatus.Ok)
            {
                okSamples.Add(ms);
                continue;
            }
            if (st == MeasureStatus.Cancelled || ct.IsCancellationRequested)
                return TestResult.Cancelled(extra);

            if (st == MeasureStatus.Drop && !reWarmupUsed)
            {
                reWarmupUsed = true;
                rawSamples.Add("rewarm");
                var rwT0 = Stopwatch.GetTimestamp();
                var rw = await MeasureOnceAsync(http, chosenUrl, httpCts.Token);
                int rwEl = ElapsedMs(rwT0);
                rawSamples.Add($"rw={rw.St}/{rw.Ms}ms/{rwEl}ms");
                if (rw.St == MeasureStatus.Ok)
                {
                    okSamples.Add(rw.Ms);
                    continue;
                }
                extra["unstable"] = "1";
                break;
            }
        }
        swSamples.Stop();
        samplesPhaseMs = (int)swSamples.ElapsedMilliseconds;

        extra["raw_samples"] = string.Join(";", rawSamples);
        extra["phase_tcp_ms"] = tcpWaitMs.ToString();
        extra["phase_warmup_ms"] = warmupTotalMs.ToString();
        extra["phase_samples_ms"] = samplesPhaseMs.ToString();

        if (okSamples.Count == 0)
            return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, "no_samples");

        okSamples.Sort();
        int median = okSamples.Count switch
        {
            3 => okSamples[1],
            2 => (okSamples[0] + okSamples[1]) / 2,
            _ => okSamples[0],
        };
        int jitter = okSamples.Max() - okSamples.Min();

        extra["jitter"] = jitter.ToString();
        extra["ok_count"] = okSamples.Count.ToString();
        extra["total_samples"] = SampleCount.ToString();
        if (okSamples.Count < SampleCount)
            extra["unstable"] = "1";
        extra["warmup_log"] = string.Join(" | ", rawLog);
        extra["total_ms"] = ((int)swTotal.ElapsedMilliseconds).ToString();

        return TestResult.Success(median, extra);
    }

    private static async Task<(string? Url, int Ms)> WarmupParallelAsync(
        HttpClient http,
        CancellationToken ct,
        int budgetMs,
        List<string> rawLog)
    {
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(budgetMs);

        var tasks = WarmupUrls.Select(async url =>
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budgetCts.Token);
                var elapsed = ElapsedMs(t0);
                int code = (int)resp.StatusCode;
                lock (rawLog) rawLog.Add($"warmup {url} => {code} in {elapsed}ms");
                if (code == 204) return (url, elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (rawLog) rawLog.Add($"warmup {url} => {ex.GetType().Name}: {Trunc(ex.Message)} in {ElapsedMs(t0)}ms");
            }
            return ((string?)null, 0);
        }).ToList();

        string? chosen = null;
        int chosenMs = 0;
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var (url, ms) = await done;
            if (url != null && chosen is null)
            {
                chosen = url;
                chosenMs = ms;
                try { budgetCts.Cancel(); } catch { }
                break;
            }
        }

        if (tasks.Count > 0)
        {
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        return (chosen, chosenMs);
    }

    private static TestResult FailWithPhases(
        Dictionary<string, string> extra,
        List<string> rawLog,
        Stopwatch swTotal,
        int tcpWaitMs,
        int warmupMs,
        int samplesMs,
        string failReason)
    {
        extra["fail_reason"] = failReason;
        extra["phase_tcp_ms"] = tcpWaitMs.ToString();
        extra["phase_warmup_ms"] = warmupMs.ToString();
        extra["phase_samples_ms"] = samplesMs.ToString();
        extra["total_ms"] = ((int)swTotal.ElapsedMilliseconds).ToString();
        if (rawLog.Count > 0)
            extra["warmup_log"] = string.Join(" | ", rawLog);
        return TestResult.Fail(TestStatus.Timeout, extra);
    }

    private static string Trunc(string s)
        => s.Length <= 200 ? s : s[..200];

    private static int ElapsedMs(long startTimestamp)
        => (int)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);

    private static async Task<bool> WaitTcpAsync(int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var done = await Task.WhenAny(connectTask, Task.Delay(50, ct));
                if (done == connectTask)
                {
                    await connectTask;
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch { /* retry */ }
        }
        return false;
    }

    private static async Task<(MeasureStatus St, int Ms)> MeasureOnceAsync(HttpClient http, string url, CancellationToken ct)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            int ms = ElapsedMs(t0);
            if ((int)resp.StatusCode == 204) return (MeasureStatus.Ok, ms);
            return (MeasureStatus.Non204, ms);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (MeasureStatus.Cancelled, 0);
        }
        catch (OperationCanceledException)
        {
            return (MeasureStatus.Timeout, 0);
        }
        catch (HttpRequestException)
        {
            return (MeasureStatus.Drop, 0);
        }
        catch (IOException)
        {
            return (MeasureStatus.Drop, 0);
        }
        catch
        {
            return (MeasureStatus.Timeout, 0);
        }
    }
}
