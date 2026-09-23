using System.Text.Json;
using System.Text.Json.Serialization;
using XrayClient.Models;

namespace XrayClient.Core;

/// <summary>اجرای چندباره تست تأخیر واقعی روی ۱۰ سرور با تأخیر پخش‌شده و ذخیره نتایج برای مقایسه.</summary>
public static class BaselineRunner
{
    public sealed class Sample
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public int Ms { get; set; }
        public string Status { get; set; } = "";
        public double Jitter { get; set; }
        public int WarmupMs { get; set; }
        public int SuccessCount { get; set; }
    }

    public sealed class Run
    {
        public int Index { get; set; }
        public DateTime At { get; set; }
        public List<Sample> Samples { get; set; } = new();
    }

    public sealed class Report
    {
        public string Algorithm { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int Runs { get; set; }
        public int SampleCount { get; set; }
        public List<string> ServerIds { get; set; } = new();
        public List<Run> Data { get; set; } = new();
        public Dictionary<string, object> Analysis { get; set; } = new();
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        int runs = 5;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--baseline-runs" && int.TryParse(args[i + 1], out var n) && n > 0)
                runs = n;

        string algorithm = args.Contains("--validate") ? "v2" : "baseline-v1";
        string baselinePath = Path.Combine(Storage.DataDir, "real-delay-baseline.json");
        string outPath = args.Contains("--validate")
            ? Path.Combine(Storage.DataDir, "real-delay-validate.json")
            : baselinePath;

        Console.WriteLine($"[{algorithm}] loading servers...");
        var all = Storage.LoadServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Address) && s.Port > 0)
            .ToList();
        if (all.Count == 0)
        {
            Console.WriteLine("No servers.");
            return 1;
        }

        List<ServerInfo> picked;
        if (args.Contains("--validate") && File.Exists(baselinePath))
        {
            var baseline = JsonSerializer.Deserialize<Report>(await File.ReadAllTextAsync(baselinePath));
            var ids = baseline?.ServerIds ?? new List<string>();
            picked = ids.Select(id => all.FirstOrDefault(s => s.Id == id)).Where(s => s != null).ToList()!;
            if (picked.Count == 0)
            {
                Console.WriteLine("Baseline servers not found; picking spread.");
                picked = await PickSpreadServersAsync(all, 10, ct);
            }
            else
            {
                Console.WriteLine($"Reusing {picked.Count} baseline servers.");
            }
        }
        else
        {
            picked = await PickSpreadServersAsync(all, 10, ct);
        }
        Console.WriteLine($"Using {picked.Count} servers:");
        foreach (var s in picked)
            Console.WriteLine($"  {s.Name} ({s.Address}:{s.Port})");

        var report = new Report
        {
            Algorithm = algorithm,
            CreatedAt = DateTime.UtcNow,
            Runs = runs,
            SampleCount = picked.Count,
            ServerIds = picked.Select(s => s.Id).ToList(),
        };

        using var sem = new SemaphoreSlim(3);
        for (int r = 1; r <= runs; r++)
        {
            if (ct.IsCancellationRequested) break;
            Console.WriteLine($"--- run {r}/{runs} ---");
            var run = new Run { Index = r, At = DateTime.UtcNow };
            var tasks = picked.Select(async s =>
            {
                await sem.WaitAsync(ct);
                Sample sample;
                try
                {
                    sample = await SampleOneAsync(s, ct);
                }
                finally
                {
                    sem.Release();
                }
                lock (run.Samples) run.Samples.Add(sample);
                Console.WriteLine($"  [{(sample.Ok ? "OK" : "FAIL")}] {sample.Name}: {sample.Ms}ms status={sample.Status}");
            });
            await Task.WhenAll(tasks);
            run.Samples = run.Samples
                .OrderBy(x => picked.FindIndex(p => p.Id == x.Id))
                .ToList();
            report.Data.Add(run);
        }

        report.Analysis = Analyze(report, picked);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        Console.WriteLine($"Saved: {outPath}");
        PrintAnalysis(report.Analysis);
        return 0;
    }

    private static async Task<Sample> SampleOneAsync(ServerInfo s, CancellationToken ct)
    {
        try
        {
            var r = await RealDelayTester.TestAsync(s, ct);
            var sample = new Sample
            {
                Id = s.Id,
                Name = s.Name,
                Ok = r.Status == TestStatus.Ok,
                Ms = r.Status == TestStatus.Ok ? (int)Math.Round(r.Value) : -1,
                Status = r.Status.ToString(),
            };
            if (r.Extra.TryGetValue("jitter", out var j) && double.TryParse(j, out var jd)) sample.Jitter = jd;
            if (r.Extra.TryGetValue("warmup_ms", out var w) && int.TryParse(w, out var wi)) sample.WarmupMs = wi;
            if (r.Extra.TryGetValue("ok_count", out var o) && int.TryParse(o, out var oi)) sample.SuccessCount = oi;
            if (r.Extra.TryGetValue("startup_ms", out var su)) sample.Status += $";startup={su}";
            if (r.Extra.TryGetValue("unstable", out _)) sample.Status += ";unstable";
            return sample;
        }
        catch (Exception ex)
        {
            return new Sample { Id = s.Id, Name = s.Name, Ok = false, Ms = -1, Status = ex.GetType().Name };
        }
    }

    private static async Task<List<ServerInfo>> PickSpreadServersAsync(List<ServerInfo> all, int take, CancellationToken ct)
    {
        if (all.Count <= take) return all;

        Console.WriteLine("TCP-probing for latency spread...");
        using var sem = new SemaphoreSlim(8);
        var probed = new List<(ServerInfo S, int Ms)>();
        var tasks = all.Select(async s =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var (ok, ms) = await LatencyTester.TcpAsync(s.Address, s.Port, 2500);
                lock (probed) probed.Add((s, ok ? ms : int.MaxValue));
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        var okList = probed.Where(p => p.Ms < int.MaxValue).OrderBy(p => p.Ms).ToList();
        if (okList.Count == 0) return all.Take(take).ToList();
        if (okList.Count <= take) return okList.Select(p => p.S).ToList();

        var picked = new List<ServerInfo> { okList[0].S };
        var remaining = okList.Skip(1).ToList();
        while (picked.Count < take && remaining.Count > 0)
        {
            ServerInfo? best = null;
            int bestDist = -1;
            (ServerInfo S, int Ms) bestEntry = default;
            foreach (var cand in remaining)
            {
                int minDist = int.MaxValue;
                foreach (var p in picked)
                {
                    int ms = okList.First(x => x.S.Id == p.Id).Ms;
                    minDist = Math.Min(minDist, Math.Abs(cand.Ms - ms));
                }
                if (minDist > bestDist) { bestDist = minDist; best = cand.S; bestEntry = cand; }
            }
            if (best == null) break;
            picked.Add(best);
            remaining.Remove(bestEntry);
        }
        return picked;
    }

    private static Dictionary<string, object> Analyze(Report report, List<ServerInfo> picked)
    {
        var analysis = new Dictionary<string, object>();
        var validRuns = report.Data.Where(r => r.Samples.Count > 0).ToList();
        if (validRuns.Count < 2) return analysis;

        var rankings = validRuns.Select(run =>
        {
            var ordered = run.Samples
                .OrderByDescending(s => s.SuccessCount > 0 ? s.SuccessCount : (s.Ok ? 1 : 0))
                .ThenBy(s => s.Ok ? s.Ms + 2.0 * s.Jitter : double.MaxValue)
                .Select(s => s.Id)
                .ToList();
            return ordered;
        }).ToList();

        var top3Sets = rankings.Select(r => string.Join(",", r.Take(3))).ToList();
        analysis["top3_per_run"] = top3Sets;
        analysis["top3_stable"] = top3Sets.Distinct().Count() == 1;

        var rhos = new List<double>();
        for (int i = 0; i < rankings.Count; i++)
        for (int j = i + 1; j < rankings.Count; j++)
            rhos.Add(Spearman(rankings[i], rankings[j]));
        analysis["spearman_pairs"] = rhos;
        analysis["spearman_mean"] = rhos.Count > 0 ? Math.Round(rhos.Average(), 4) : 0;
        analysis["spearman_min"] = rhos.Count > 0 ? Math.Round(rhos.Min(), 4) : 0;
        return analysis;
    }

    private static double Spearman(List<string> a, List<string> b)
    {
        var ids = a.Union(b).ToList();
        var ra = ids.ToDictionary(id => id, id => a.IndexOf(id) + 1);
        var rb = ids.ToDictionary(id => id, id => b.IndexOf(id) + 1);
        int n = ids.Count;
        if (n < 2) return 1;
        double sumD2 = 0;
        foreach (var id in ids)
        {
            double d = ra[id] - rb[id];
            sumD2 += d * d;
        }
        return 1.0 - (6.0 * sumD2) / (n * (n * n - 1));
    }

    private static void PrintAnalysis(Dictionary<string, object> analysis)
    {
        Console.WriteLine("=== analysis ===");
        if (analysis.TryGetValue("top3_stable", out var st)) Console.WriteLine($"top3_stable: {st}");
        if (analysis.TryGetValue("top3_per_run", out var t3) && t3 is IEnumerable<string> list)
            foreach (var x in list) Console.WriteLine($"  top3: {x}");
        else if (analysis.TryGetValue("top3_per_run", out var raw)) Console.WriteLine($"top3_per_run: {raw}");
        if (analysis.TryGetValue("spearman_mean", out var m)) Console.WriteLine($"spearman_mean: {m}");
        if (analysis.TryGetValue("spearman_min", out var mi)) Console.WriteLine($"spearman_min: {mi}");
    }
}
