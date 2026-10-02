using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Roxa.Core;

public static class ProxyRouter
{
    private const int HeadMaxBytes = 64 * 1024;
    private const int HeadTimeoutMs = 30000;
    private const int ConnectTimeoutMs = 15000;
    private const long PidCacheTtlMs = 15000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TcpTableOwnerPidAll = 5;
    private const int AfInet = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint LoopbackNet = 0x0100007F;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    private sealed class ReqHead
    {
        public string Method = "";
        public string Target = "";
        public string Version = "HTTP/1.1";
        public List<string> Headers = new();
    }

    private static readonly object _lock = new();
    private static readonly ConcurrentDictionary<int, (TcpClient Client, string Exe)> _clients = new();
    private static readonly Dictionary<int, (string Name, long Ticks)> _pidCache = new();
    private static string[] _bypass = Array.Empty<string>();
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static int _upstreamPort;
    private static int _port;
    private static int _clientSeq;

    public static int Port { get { lock (_lock) return _port; } }

    public static bool Start(IEnumerable<string>? bypassPaths, int upstreamPort)
    {
        if (upstreamPort <= 0) return false;
        lock (_lock)
        {
            if (_listener != null)
            {
                if (_upstreamPort == upstreamPort)
                {
                    ApplyBypassLocked(Normalize(bypassPaths));
                    return true;
                }
                StopLocked();
            }
            _bypass = Normalize(bypassPaths);
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listener = listener;
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _upstreamPort = upstreamPort;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                TunManager.Log($"proxy-router started port={_port} upstream={upstreamPort} bypass={_bypass.Length} app(s)");
                _ = Task.Run(() => AcceptLoopAsync(listener, token));
                return true;
            }
            catch (Exception ex)
            {
                TunManager.Log($"proxy-router start failed: {ex.Message}");
                StopLocked();
                return false;
            }
        }
    }

    public static void Stop()
    {
        lock (_lock) StopLocked();
    }

    public static void StopForUpstream(int upstreamPort)
    {
        lock (_lock)
        {
            if (_listener != null && _upstreamPort == upstreamPort) StopLocked();
        }
    }

    private static void StopLocked()
    {
        bool was = _listener != null;
        var listener = _listener;
        var cts = _cts;
        _listener = null;
        _cts = null;
        _port = 0;
        _upstreamPort = 0;
        _bypass = Array.Empty<string>();
        try { listener?.Stop(); } catch { }
        try { cts?.Cancel(); } catch { }
        try { cts?.Dispose(); } catch { }
        foreach (var kv in _clients) { try { kv.Value.Client.Close(); } catch { } }
        _clients.Clear();
        lock (_pidCache) _pidCache.Clear();
        if (was) TunManager.Log("proxy-router stopped");
    }

    private static void ApplyBypassLocked(string[] next)
    {
        var prev = _bypass;
        _bypass = next;
        foreach (var kv in _clients)
        {
            string exe = kv.Value.Exe;
            bool was = exe.Length > 0 && Array.IndexOf(prev, exe) >= 0;
            bool now = exe.Length > 0 && Array.IndexOf(next, exe) >= 0;
            if (exe.Length == 0 || was != now)
            {
                try { kv.Value.Client.Close(); } catch { }
                TunManager.Log($"proxy-router bypass change closed {exe} conn");
            }
        }
    }

    private static string[] Normalize(IEnumerable<string>? paths)
    {
        if (paths == null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string n = Path.GetFileName(p.Trim()).ToLowerInvariant();
            if (n.Length > 0 && !list.Contains(n)) list.Add(n);
        }
        return list.ToArray();
    }

    private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(token); }
            catch { break; }
            int id = Interlocked.Increment(ref _clientSeq);
            _clients[id] = (client, "");
            _ = Task.Run(() => HandleClientAsync(client, id, token));
        }
    }

    private static async Task HandleClientAsync(TcpClient client, int id, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;
            var ep = client.Client.RemoteEndPoint as IPEndPoint;
            int clientPort = ep?.Port ?? 0;
            string[] bypass;
            int upstream;
            int routerPort;
            lock (_lock) { bypass = _bypass; upstream = _upstreamPort; routerPort = _port; }

            bool direct = false;
            string exe = "";
            int pid = 0;
            if (bypass.Length > 0 && clientPort > 0)
            {
                pid = ResolvePid(clientPort, routerPort);
                exe = ResolveExeName(pid).ToLowerInvariant();
                if (exe.Length > 0 && Array.IndexOf(bypass, exe) >= 0) direct = true;
            }
            if (_clients.ContainsKey(id)) _clients[id] = (client, exe);
            if (bypass.Length > 0 && pid == 0)
                TunManager.Log($"proxy-router resolve miss port={clientPort} router={routerPort}");

            var stream = client.GetStream();
            var reader = new BufReader(stream);
            string? headText;
            using (var to = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                to.CancelAfter(HeadTimeoutMs);
                try { headText = await reader.ReadHeadAsync(to.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
            }
            if (headText == null) return;
            if (!TryParseRequest(headText, out var req)) return;

            if (direct && req.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                ParseAuthority(req.Target, out string host, out int port);
                if (host.Length > 0)
                {
                    await DirectTunnelAsync(reader, stream, host, port, token);
                    return;
                }
            }
            if (direct && Uri.TryCreate(req.Target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp)
            {
                await DirectHttpAsync(reader, stream, req, token);
                return;
            }
            await ForwardAsync(reader, stream, upstream, headText, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            TunManager.Log($"proxy-router conn failed: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            try { client.Close(); } catch { }
        }
    }

    private static async Task ForwardAsync(BufReader reader, Stream client, int upstream, string head, CancellationToken token)
    {
        if (upstream <= 0) return;
        using var server = new TcpClient();
        await server.ConnectAsync(IPAddress.Loopback, upstream)
            .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), token);
        server.NoDelay = true;
        var ss = server.GetStream();
        if (head.Length > 0)
            await ss.WriteAsync(Encoding.Latin1.GetBytes(head), token);
        await PumpPairAsync(reader, client, ss, token);
    }

    private static async Task DirectTunnelAsync(BufReader reader, Stream client, string host, int port, CancellationToken token)
    {
        using var target = new TcpClient();
        await target.ConnectAsync(host, port)
            .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), token);
        target.NoDelay = true;
        var ts = target.GetStream();
        await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
        await PumpPairAsync(reader, client, ts, token);
    }

    private static async Task DirectHttpAsync(BufReader reader, Stream client, ReqHead first, CancellationToken token)
    {
        var req = first;
        while (true)
        {
            if (!Uri.TryCreate(req.Target, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return;
            using var target = new TcpClient();
            int port = uri.IsDefaultPort ? 80 : uri.Port;
            try
            {
                await target.ConnectAsync(uri.Host, port)
                    .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), token);
            }
            catch { return; }
            target.NoDelay = true;
            var ts = target.GetStream();

            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            var sb = new StringBuilder();
            sb.Append(req.Method).Append(' ').Append(path).Append(' ').Append(req.Version).Append("\r\n");
            bool hasHost = false;
            foreach (var h in req.Headers)
            {
                int c = h.IndexOf(':');
                string name = c > 0 ? h[..c].Trim() : "";
                if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
                sb.Append(h).Append("\r\n");
            }
            if (!hasHost)
                sb.Append("Host: ").Append(uri.Host).Append(port == 80 ? "" : ":" + port).Append("\r\n");
            sb.Append("\r\n");
            await ts.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), token);

            string? te = HeaderValue(req, "Transfer-Encoding");
            bool chunked = te != null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            long cl = 0;
            if (!chunked)
            {
                string? cls = HeaderValue(req, "Content-Length");
                if (cls != null) long.TryParse(cls.Trim(), out cl);
            }
            string? expect = HeaderValue(req, "Expect");
            if (expect != null && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token);
            if (chunked)
                await CopyChunksAsync(reader, ts, token);
            else if (cl > 0)
                await CopyExactAsync(reader, ts, cl, token);

            var rs = new BufReader(ts);
            bool clientWantsClose = HeaderValue(req, "Connection")?
                .Contains("close", StringComparison.OrdinalIgnoreCase) == true;
            bool close = await RelayResponseAsync(rs, client, req.Method, clientWantsClose, token);
            if (close) return;

            string? nextHead;
            using (var to = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                to.CancelAfter(HeadTimeoutMs);
                try { nextHead = await reader.ReadHeadAsync(to.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
            }
            if (nextHead == null) return;
            if (!TryParseRequest(nextHead, out var next)) return;
            if (next.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)) return;
            req = next;
        }
    }

    private static async Task<bool> RelayResponseAsync(BufReader rs, Stream client, string reqMethod, bool clientWantsClose, CancellationToken token)
    {
        while (true)
        {
            string? head;
            using (var to = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                to.CancelAfter(HeadTimeoutMs);
                try { head = await rs.ReadHeadAsync(to.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return true; }
            }
            if (head == null) return true;
            int firstEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
            string statusLine = firstEnd >= 0 ? head[..firstEnd] : head.TrimEnd('\r', '\n');
            var sp = statusLine.Split(' ', 3);
            if (sp.Length < 2 || !int.TryParse(sp[1], out int code)) return true;
            await client.WriteAsync(Encoding.Latin1.GetBytes(head), token);
            if (code >= 100 && code < 200 && code != 101) continue;
            if (code == 101) return true;
            bool connClose = clientWantsClose
                || HeadValue(head, "Connection")?.Contains("close", StringComparison.OrdinalIgnoreCase) == true
                || statusLine.StartsWith("HTTP/1.0", StringComparison.Ordinal);
            if (reqMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || code == 204 || code == 304)
                return connClose;
            string? te = HeadValue(head, "Transfer-Encoding");
            if (te != null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                await CopyChunksAsync(rs, client, token);
                if (connClose) return true;
                continue;
            }
            long cl = -1;
            string? cls = HeadValue(head, "Content-Length");
            if (cls != null) long.TryParse(cls.Trim(), out cl);
            if (cl >= 0)
            {
                await CopyExactAsync(rs, client, cl, token);
                if (connClose) return true;
                continue;
            }
            await PumpFromAsync(rs, client, token);
            return true;
        }
    }

    private static async Task PumpPairAsync(BufReader clientReader, Stream clientRaw, Stream remote, CancellationToken token)
    {
        var up = PumpFromAsync(clientReader, remote, token);
        var down = PumpAsync(remote, clientRaw, token);
        await Task.WhenAny(up, down);
        try { clientRaw.Close(); } catch { }
        try { remote.Close(); } catch { }
        try { await Task.WhenAll(up, down); } catch { }
    }

    private static async Task PumpFromAsync(BufReader src, Stream dst, CancellationToken token)
    {
        var buf = new byte[16 * 1024];
        while (true)
        {
            int n = await src.ReadAsync(buf, token);
            if (n <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), token);
        }
    }

    private static async Task PumpAsync(Stream src, Stream dst, CancellationToken token)
    {
        var buf = new byte[16 * 1024];
        while (true)
        {
            int n = await src.ReadAsync(buf, token);
            if (n <= 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), token);
        }
    }

    private static async Task CopyExactAsync(BufReader src, Stream dst, long count, CancellationToken token)
    {
        var buf = new byte[16 * 1024];
        long left = count;
        while (left > 0)
        {
            int want = (int)Math.Min(buf.Length, left);
            int n = await src.ReadAsync(buf.AsMemory(0, want), token);
            if (n <= 0) return;
            await dst.WriteAsync(buf.AsMemory(0, n), token);
            left -= n;
        }
    }

    private static async Task CopyChunksAsync(BufReader src, Stream dst, CancellationToken token)
    {
        while (true)
        {
            string? sizeLine = await src.ReadLineAsync(token);
            if (sizeLine == null) return;
            await WriteAsciiAsync(dst, sizeLine + "\r\n", token);
            string sz = sizeLine.Split(';', 2)[0].Trim();
            if (!int.TryParse(sz, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0) return;
            if (size == 0)
            {
                while (true)
                {
                    string? t = await src.ReadLineAsync(token);
                    if (t == null) return;
                    await WriteAsciiAsync(dst, t + "\r\n", token);
                    if (t.Length == 0) return;
                }
            }
            await CopyExactAsync(src, dst, size + 2L, token);
        }
    }

    private static Task WriteAsciiAsync(Stream dst, string text, CancellationToken token) =>
        dst.WriteAsync(Encoding.Latin1.GetBytes(text), token).AsTask();

    private static bool TryParseRequest(string head, out ReqHead req)
    {
        req = new ReqHead();
        int lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        string first = lineEnd >= 0 ? head[..lineEnd] : head.TrimEnd('\r', '\n');
        var parts = first.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        req.Method = parts[0];
        req.Target = parts[1];
        req.Version = parts[2];
        string rest = lineEnd >= 0 ? head[(lineEnd + 2)..] : "";
        foreach (var ln in rest.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            if (ln.Contains(':')) req.Headers.Add(ln);
        return true;
    }

    private static string? HeaderValue(ReqHead req, string name)
    {
        foreach (var h in req.Headers)
        {
            int c = h.IndexOf(':');
            if (c > 0 && h[..c].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return h[(c + 1)..].Trim();
        }
        return null;
    }

    private static string? HeadValue(string head, string name)
    {
        int pos = head.IndexOf("\r\n", StringComparison.Ordinal);
        while (pos >= 0 && pos + 2 < head.Length)
        {
            int end = head.IndexOf("\r\n", pos + 2, StringComparison.Ordinal);
            string ln = end > pos ? head[(pos + 2)..end] : head[(pos + 2)..];
            int c = ln.IndexOf(':');
            if (c > 0 && ln[..c].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return ln[(c + 1)..].Trim();
            if (end < 0) break;
            pos = end;
        }
        return null;
    }

    private static void ParseAuthority(string target, out string host, out int port)
    {
        host = "";
        port = 443;
        if (string.IsNullOrWhiteSpace(target)) return;
        string t = target.Trim();
        if (t.StartsWith('['))
        {
            int close = t.IndexOf(']');
            if (close <= 0) return;
            host = t[1..close];
            if (close + 1 < t.Length && t[close + 1] == ':' && int.TryParse(t[(close + 2)..], out int p))
                port = p;
            return;
        }
        int ci = t.LastIndexOf(':');
        if (ci > 0 && int.TryParse(t[(ci + 1)..], out int pp))
        {
            host = t[..ci];
            port = pp;
        }
        else host = t;
    }

    private static int ResolvePid(int clientPort, int routerPort)
    {
        if (clientPort <= 0 || routerPort <= 0) return 0;
        int size = 0;
        uint r = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        if (r != ErrorInsufficientBuffer || size <= 0) return 0;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            r = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (r != 0) return 0;
            int count = Marshal.ReadInt32(buf);
            IntPtr rows = IntPtr.Add(buf, 4);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rows, i * rowSize));
                if (row.OwningPid == 0 || row.OwningPid == (uint)Environment.ProcessId) continue;
                if (row.LocalAddr != LoopbackNet || row.RemoteAddr != LoopbackNet) continue;
                if (MatchPort(row.LocalPort, clientPort) && MatchPort(row.RemotePort, routerPort))
                    return (int)row.OwningPid;
            }
            return 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool MatchPort(uint stored, int port)
    {
        int v = (int)(stored & 0xFFFF);
        if (v == port) return true;
        int swapped = ((v & 0xFF) << 8) | ((v >> 8) & 0xFF);
        return swapped == port;
    }

    private static string ResolveExeName(int pid)
    {
        if (pid <= 0) return "";
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (_pidCache.TryGetValue(pid, out var cached) && now - cached.Ticks < PidCacheTtlMs)
                return cached.Name;
        }
        string path = "";
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint len = (uint)sb.Capacity;
                if (QueryFullProcessImageName(handle, 0, sb, ref len)) path = sb.ToString(0, (int)len);
            }
            finally { CloseHandle(handle); }
        }
        string name = path.Length > 0 ? Path.GetFileName(path) : "";
        if (name.Length > 0)
            lock (_lock) _pidCache[pid] = (name, now);
        return name;
    }

    private sealed class BufReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[HeadMaxBytes];
        private int _start;
        private int _end;

        public BufReader(Stream stream) => _stream = stream;

        public async Task<string?> ReadHeadAsync(CancellationToken ct)
        {
            while (true)
            {
                for (int i = _start; i + 3 < _end; i++)
                {
                    if (_buf[i] == '\r' && _buf[i + 1] == '\n' && _buf[i + 2] == '\r' && _buf[i + 3] == '\n')
                    {
                        string head = Encoding.Latin1.GetString(_buf, _start, i + 4 - _start);
                        _start = i + 4;
                        return head;
                    }
                }
                if (_end - _start >= HeadMaxBytes) return null;
                if (_end == _buf.Length)
                {
                    if (_start == 0) return null;
                    Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                    _end -= _start;
                    _start = 0;
                }
                int n = await _stream.ReadAsync(_buf.AsMemory(_end, _buf.Length - _end), ct);
                if (n <= 0) return null;
                _end += n;
            }
        }

        public async Task<int> ReadAsync(Memory<byte> dst, CancellationToken ct)
        {
            if (_start < _end)
            {
                int n = Math.Min(dst.Length, _end - _start);
                _buf.AsSpan(_start, n).CopyTo(dst.Span);
                _start += n;
                return n;
            }
            return await _stream.ReadAsync(dst, ct);
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var ms = new MemoryStream();
            byte[] one = new byte[1];
            int prev = -1;
            while (true)
            {
                int n = await ReadAsync(one, ct);
                if (n <= 0) return null;
                byte b = one[0];
                if (prev == '\r' && b == '\n')
                {
                    var arr = ms.ToArray();
                    return Encoding.Latin1.GetString(arr, 0, arr.Length - 1);
                }
                if (ms.Length >= HeadMaxBytes) return null;
                ms.WriteByte(b);
                prev = b;
            }
        }
    }
}
