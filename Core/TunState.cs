using System.Text.Json;

namespace Roxa.Core;

public sealed class TunRouteRecord
{
    public string Dest { get; set; } = "";
    public string Mask { get; set; } = "";
    public string? Via { get; set; }
}

public sealed class TunState
{
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public int EnginePid { get; set; }
    public long EngineStartTicks { get; set; }
    public string EngineExePath { get; set; } = "";
    public string AdapterName { get; set; } = TunManager.AdapterName;
    public List<TunRouteRecord> Routes { get; set; } = new();
    public bool TunActive { get; set; }
    public string? ProxySet { get; set; }
    public bool ProxyPrevEnable { get; set; }
    public string? ProxyPrevServer { get; set; }
    public string? ProxyPrevOverride { get; set; }
    public DateTime SavedUtc { get; set; }

    public static string ProgramDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Roxa", "tun-state.json");

    public static string LocalAppDataPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa", "tun-state.json");

    public static TunState? Load()
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (_lock)
        {
            TunState? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var path in new[] { ProgramDataPath, LocalAppDataPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var st = JsonSerializer.Deserialize<TunState>(File.ReadAllText(path));
                    if (st == null) continue;
                    var time = File.GetLastWriteTimeUtc(path);
                    if (best == null || time > bestTime)
                    {
                        best = st;
                        bestTime = time;
                    }
                }
                catch { }
            }
            return best;
        }
    }

    public static void Save(TunState state)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            state.SavedUtc = DateTime.UtcNow;
            string json;
            try { json = JsonSerializer.Serialize(state, _jsonOptions); }
            catch { return; }
            if (!TryWrite(ProgramDataPath, json))
                TryWrite(LocalAppDataPath, json);
        }
    }

    public static void Delete()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            foreach (var path in new[] { ProgramDataPath, LocalAppDataPath })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
            }
        }
    }

    private static bool TryWrite(string path, string json)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
            return true;
        }
        catch { return false; }
    }
}
