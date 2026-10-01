namespace Roxa.Core;

public sealed class TestRouteScope : IDisposable
{
    private readonly List<string> _ips = new();
    private bool _disposed;

    internal void Track(string ip) { lock (_ips) _ips.Add(ip); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        string[] ips;
        lock (_ips) { ips = _ips.ToArray(); _ips.Clear(); }
        foreach (var ip in ips) TestRouteBypass.Release(ip);
    }
}

public static class TestRouteBypass
{
    private sealed class Held
    {
        public string Via = "";
        public int Refs;
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Held> _held = new(StringComparer.OrdinalIgnoreCase);
    private static string? _via;
    private static long _viaAt;

    public static bool TestsViaTun { get; set; }

    public static bool IsBypassing => !TestsViaTun && AdminHelper.IsRunningAsAdmin() && TunManager.IsCaptureActive;

    public static async Task<TestRouteScope> BeginAsync(IEnumerable<string?>? hosts)
    {
        var scope = new TestRouteScope();
        try
        {
            if (TestsViaTun || hosts == null) return scope;
            if (!AdminHelper.IsRunningAsAdmin()) return scope;
            if (!TunManager.IsCaptureActive) return scope;

            var wanted = new List<string>();
            foreach (var raw in hosts)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string h = raw.Trim();
                if (!wanted.Contains(h, StringComparer.OrdinalIgnoreCase)) wanted.Add(h);
            }
            if (wanted.Count == 0) return scope;

            string? via = Gateway();
            if (via == null) return scope;

            var guard = TunManager.CaptureHostRoutes();
            var ips = await TunManager.ResolveIpsAsync(wanted);

            foreach (var ip in ips)
            {
                if (guard.Contains(ip) || TunExclusion.IsPinned(ip)) continue;
                bool ours = false;
                lock (_lock)
                {
                    if (_held.TryGetValue(ip, out var held))
                    {
                        held.Refs++;
                        ours = true;
                    }
                    else
                    {
                        TunManager.RunRoute($"delete {ip} mask 255.255.255.255");
                        if (TunManager.RunRoute($"add {ip} mask 255.255.255.255 {via}") == 0)
                        {
                            _held[ip] = new Held { Via = via, Refs = 1 };
                            ours = true;
                        }
                    }
                }
                if (ours) scope.Track(ip);
            }
        }
        catch { }
        return scope;
    }

    internal static void Release(string ip)
    {
        lock (_lock)
        {
            if (!_held.TryGetValue(ip, out var held)) return;
            held.Refs--;
            if (held.Refs > 0) return;
            _held.Remove(ip);
            if (TunManager.CaptureHostRoutes().Contains(ip)) return;
            if (TunExclusion.IsPinned(ip)) return;
            try { TunManager.RunRoute($"delete {ip} mask 255.255.255.255"); } catch { }
        }
    }

    private static string? Gateway()
    {
        long now = Environment.TickCount64;
        if (_via != null && now - _viaAt < 5000) return _via;
        _via = TunManager.GetDefaultGatewayVia();
        _viaAt = now;
        return _via;
    }
}
