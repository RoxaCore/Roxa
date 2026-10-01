using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Roxa.Models;

namespace Roxa.Core;

public static class SpeedRunner
{
    public sealed class Sample
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Ok { get; set; }
        public double Mbps { get; set; }
        public string Status { get; set; } = "";
        public int TotalMs { get; set; }
        public string Url { get; set; } = "";
        public long Bytes { get; set; }
        public double Seconds { get; set; }
    }

    public sealed class Report
    {
        public string Mode { get; set; } = "speed";
        public DateTime CreatedAt { get; set; }
        public int Concurrency { get; set; }
        public int WallMs { get; set; }
        public List<string> ServerIds { get; set; } = new();
        public List<Sample> Results { get; set; } = new();
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        int concurrency = SpeedTester.MaxConcurrency;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--concurrency" && int.TryParse(args[i + 1], out var c) && c > 0)
                concurrency = c;

        string outPath = Path.Combine(Path.GetTempPath(), "roxa-speed.json");
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out")
                outPath = args[i + 1];

        string idsFile = "";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--ids-file")
                idsFile = args[i + 1];

        int take = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--take" && int.TryParse(args[i + 1], out var t) && t > 0)
                take = t;

        Console.WriteLine($"[speed] concurrency={concurrency} loading servers...");
        var all = Storage.LoadServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Address) && s.Port > 0)
            .ToList();
        if (all.Count == 0)
        {
            Console.WriteLine("No servers.");
            return 1;
        }

        List<ServerInfo> picked;
        if (!string.IsNullOrEmpty(idsFile) && File.Exists(idsFile))
            picked = PickByIds(all, idsFile);
        else if (take > 0)
            picked = all.Take(take).ToList();
        else
            picked = PickServerSet(all);
        Console.WriteLine($"Using {picked.Count} servers:");
        foreach (var s in picked)
            Console.WriteLine($"  {s.Name} ({s.Address}:{s.Port})");

        var report = new Report
        {
            CreatedAt = DateTime.UtcNow,
            Concurrency = concurrency,
            ServerIds = picked.Select(s => s.Id).ToList(),
        };

        var results = new List<Sample>();
        var sw = Stopwatch.StartNew();
        using var sem = new SemaphoreSlim(concurrency);
        var tasks = picked.Select(async s =>
        {
            try { await sem.WaitAsync(ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                var r = await SpeedTester.TestAsync(s, ct);
                var sample = new Sample
                {
                    Id = s.Id,
                    Name = s.Name,
                    Ok = r.Status == TestStatus.Ok,
                    Mbps = r.Status == TestStatus.Ok ? Math.Round(r.Value, 1) : -1,
                    Status = r.Status.ToString(),
                };
                if (r.Extra.TryGetValue("url", out var u)) sample.Url = u;
                if (r.Extra.TryGetValue("bytes", out var b) && long.TryParse(b, out var bl)) sample.Bytes = bl;
                if (r.Extra.TryGetValue("seconds", out var sec) && double.TryParse(sec, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sd)) sample.Seconds = sd;
                lock (results) results.Add(sample);
                Console.WriteLine($"  [{(sample.Ok ? "OK" : "FAIL")}] {sample.Name}: {sample.Mbps}Mb/s status={sample.Status} bytes={sample.Bytes} sec={sample.Seconds:F3}");
            }
            catch (Exception ex)
            {
                lock (results) results.Add(new Sample { Id = s.Id, Name = s.Name, Ok = false, Mbps = -1, Status = ex.GetType().Name });
                Console.WriteLine($"  [ERR] {s.Name}: {ex.GetType().Name}");
            }
            finally { sem.Release(); }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        sw.Stop();

        report.WallMs = (int)sw.ElapsedMilliseconds;
        report.Results = results
            .OrderBy(x => picked.FindIndex(p => p.Id == x.Id))
            .ToList();

        int okCount = report.Results.Count(r => r.Ok);
        Console.WriteLine($"WALL_MS={report.WallMs} ok={okCount}/{report.Results.Count}");
        Console.WriteLine("speed order (desc):");
        foreach (var r in report.Results.OrderByDescending(x => x.Mbps))
            Console.WriteLine($"  {r.Mbps,6} Mb/s  {r.Name}");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outPath, json);
        Console.WriteLine($"Saved: {outPath}");
        return okCount > 0 ? 0 : 2;
    }

    private static List<ServerInfo> PickByIds(List<ServerInfo> all, string idsFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(idsFile));
            if (doc.RootElement.TryGetProperty("ServerIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                var idList = ids.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).Cast<string>().ToList();
                var picked = idList.Select(id => all.FirstOrDefault(s => s.Id == id)).Where(s => s != null).Cast<ServerInfo>().ToList();
                if (picked.Count > 0)
                {
                    Console.WriteLine($"Reusing {picked.Count} servers from ids file.");
                    return picked;
                }
            }
        }
        catch { }
        Console.WriteLine("Ids file not usable; picking default set.");
        return PickServerSet(all);
    }

    private static List<ServerInfo> PickServerSet(List<ServerInfo> all)
    {
        string[] candidates =
        {
            Path.Combine(Storage.DataDir, "real-delay-validate.json"),
            Path.Combine(Storage.DataDir, "real-delay-baseline.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("ServerIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                    continue;
                var idList = ids.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).Cast<string>().ToList();
                var picked = idList.Select(id => all.FirstOrDefault(s => s.Id == id)).Where(s => s != null).Cast<ServerInfo>().ToList();
                if (picked.Count > 0)
                {
                    Console.WriteLine($"Reusing {picked.Count} servers from {Path.GetFileName(path)}.");
                    return picked;
                }
            }
            catch { }
        }
        return all.Take(12).ToList();
    }
}
