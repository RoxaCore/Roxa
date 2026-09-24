using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>تست Real Delay: انجین موقت + warmup + ۳ نمونه median روی پروکسی محلی.</summary>
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

    private enum MeasureStatus { Ok, Timeout, Non204, Drop, Cancelled }

    public static async Task<TestResult> TestAsync(ServerInfo server, CancellationToken ct = default)
    {
        var extra = new Dictionary<string, string>();
        var engine = new XrayEngine();
        var rawLog = new List<string>();
        var swTotal = Stopwatch.StartNew();
        int tcpWaitMs = 0, warmupTotalMs = 0, samplesPhaseMs = 0, disposeMs = 0;
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
            extra["startup_ms"] = ((int)swStartup.ElapsedMilliseconds).ToString();

            var swTcp = Stopwatch.StartNew();
            if (!await WaitTcpAsync(engine.HttpPort, TcpWaitTimeoutMs, ct))
            {
                swTcp.Stop();
                tcpWaitMs = (int)swTcp.ElapsedMilliseconds;
                return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, disposeMs, "tcp_wait_timeout");
            }
            swTcp.Stop();
            tcpWaitMs = (int)swTcp.ElapsedMilliseconds;
            if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);

            using var httpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            httpCts.CancelAfter(TimeSpan.FromMilliseconds(HttpPhaseTimeoutMs));

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{engine.HttpPort}"),
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
            var warmupBudget = Stopwatch.StartNew();
            var swWarmup = Stopwatch.StartNew();
            foreach (var url in WarmupUrls)
            {
                if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
                if (httpCts.IsCancellationRequested) break;

                int elapsedBudget = (int)warmupBudget.ElapsedMilliseconds;
                int remaining = WarmupBudgetMs - elapsedBudget;
                if (remaining < WarmupSkipThresholdMs)
                {
                    rawLog.Add($"warmup skip remaining={remaining}");
                    break;
                }

                int urlTimeout = Math.Min(PerRequestTimeoutMs, remaining);
                using var urlCts = CancellationTokenSource.CreateLinkedTokenSource(httpCts.Token);
                urlCts.CancelAfter(urlTimeout);

                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, urlCts.Token);
                    var elapsed = ElapsedMs(t0);
                    int code = (int)resp.StatusCode;
                    rawLog.Add($"warmup {url} => {code} in {elapsed}ms");
                    if (code == 204)
                    {
                        chosenUrl = url;
                        warmupMs = elapsed;
                        break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return TestResult.Cancelled(extra);
                }
                catch (Exception ex)
                {
                    rawLog.Add($"warmup {url} => {ex.GetType().Name}: {Trunc(ex.Message)} in {ElapsedMs(t0)}ms");
                }
            }
            swWarmup.Stop();
            warmupTotalMs = (int)swWarmup.ElapsedMilliseconds;

            extra["warmup_ms"] = warmupMs.ToString();
            if (chosenUrl == null)
                return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, disposeMs, "no_warmup_204");
            extra["url"] = chosenUrl;

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
                return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, disposeMs, "no_samples");

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return TestResult.Cancelled(extra);
        }
        catch (Exception ex)
        {
            extra["error"] = Trunc(ex.Message);
            return FailWithPhases(extra, rawLog, swTotal, tcpWaitMs, warmupTotalMs, samplesPhaseMs, disposeMs, "exception");
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
            extra["phase_tcp_ms"] = tcpWaitMs.ToString();
            extra["phase_warmup_ms"] = warmupTotalMs.ToString();
            extra["phase_samples_ms"] = samplesPhaseMs.ToString();
        }
    }

    private static TestResult FailWithPhases(
        Dictionary<string, string> extra,
        List<string> rawLog,
        Stopwatch swTotal,
        int tcpWaitMs,
        int warmupMs,
        int samplesMs,
        int disposeMs,
        string failReason)
    {
        extra["fail_reason"] = failReason;
        extra["phase_tcp_ms"] = tcpWaitMs.ToString();
        extra["phase_warmup_ms"] = warmupMs.ToString();
        extra["phase_samples_ms"] = samplesMs.ToString();
        extra["phase_dispose_ms"] = disposeMs.ToString();
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
