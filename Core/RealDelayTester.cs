using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
            catch
            {
                return TestResult.Fail(TestStatus.Error, extra);
            }
            swStartup.Stop();
            extra["startup_ms"] = ((int)swStartup.ElapsedMilliseconds).ToString();

            if (!await WaitTcpAsync(engine.HttpPort, TcpWaitTimeoutMs, ct))
                return TestResult.Fail(TestStatus.Timeout, extra);
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
            foreach (var url in WarmupUrls)
            {
                if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
                if (httpCts.IsCancellationRequested) break;

                int elapsedBudget = (int)warmupBudget.ElapsedMilliseconds;
                int remaining = WarmupBudgetMs - elapsedBudget;
                if (remaining < WarmupSkipThresholdMs) break;

                int urlTimeout = Math.Min(PerRequestTimeoutMs, remaining);
                using var urlCts = CancellationTokenSource.CreateLinkedTokenSource(httpCts.Token);
                urlCts.CancelAfter(urlTimeout);

                var t0 = Stopwatch.GetTimestamp();
                try
                {
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, urlCts.Token);
                    var elapsed = ElapsedMs(t0);
                    if ((int)resp.StatusCode == 204)
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
                catch { /* next url */ }
            }

            extra["warmup_ms"] = warmupMs.ToString();
            if (chosenUrl == null)
                return TestResult.Fail(TestStatus.Timeout, extra);
            extra["url"] = chosenUrl;

            var samples = new List<int>(SampleCount);
            bool reWarmupUsed = false;

            for (int i = 0; i < SampleCount; i++)
            {
                if (ct.IsCancellationRequested) return TestResult.Cancelled(extra);
                if (httpCts.IsCancellationRequested) break;

                var (st, ms) = await MeasureOnceAsync(http, chosenUrl, httpCts.Token);

                if (st == MeasureStatus.Ok)
                {
                    samples.Add(ms);
                    continue;
                }
                if (st == MeasureStatus.Cancelled || ct.IsCancellationRequested)
                    return TestResult.Cancelled(extra);

                if (st == MeasureStatus.Drop && !reWarmupUsed)
                {
                    reWarmupUsed = true;
                    var rw = await MeasureOnceAsync(http, chosenUrl, httpCts.Token);
                    if (rw.St == MeasureStatus.Ok)
                    {
                        samples.Add(rw.Ms);
                        continue;
                    }
                    extra["unstable"] = "1";
                    break;
                }
            }

            if (samples.Count == 0)
                return TestResult.Fail(TestStatus.Timeout, extra);

            samples.Sort();
            int median = samples.Count switch
            {
                3 => samples[1],
                2 => (samples[0] + samples[1]) / 2,
                _ => samples[0],
            };
            int jitter = samples.Max() - samples.Min();

            extra["jitter"] = jitter.ToString();
            extra["ok_count"] = samples.Count.ToString();
            extra["total_samples"] = SampleCount.ToString();
            if (samples.Count < SampleCount)
                extra["unstable"] = "1";

            return TestResult.Success(median, extra);
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
