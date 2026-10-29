// Roxa - a VPN client for Windows built on Xray-core
// Copyright (C) 2026 Roxa contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
//
// Additional terms under section 7 of that license: the "Roxa" name and
// visual identity are not licensed. See "The name and logo are not
// covered" in README.md.

using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Roxa.Localization;

namespace Roxa.Core;

public static class UpdateInstaller
{
    private static string? _stagedRoot;
    private static string? _stagedPayload;

    public static bool IsStaged => _stagedRoot != null && _stagedPayload != null;

    /// <summary>True once the second-stage updater has been started. Launching it
    /// twice would run two updaters over the same install directory, so every
    /// caller (the banner button and the exit-time automatic path) checks this.</summary>
    public static bool UpdaterLaunched { get; private set; }

    public static Task StageAsync(UpdateInfo info, int? proxyPort, CancellationToken ct)
        => StageAsync(info, proxyPort, null, ct);

    public static async Task StageAsync(UpdateInfo info, int? proxyPort, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.Url))
            throw new InvalidOperationException("update url is empty");

        // The downloaded zip is extracted and executed, so it must arrive over TLS
        // and be pinned to the hash the update feed published.
        // The only local-test exception: a test server on loopback without TLS (ROXA_TEST_FEED).
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(TranslationManager.Get("Upd_RequireHttps"));
        bool loopbackHost = uri.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
        if (uri.Scheme != Uri.UriSchemeHttps && !(loopbackHost && uri.Scheme == Uri.UriSchemeHttp))
            throw new InvalidOperationException(TranslationManager.Get("Upd_RequireHttps"));

        string sha256 = (info.Sha256 ?? "").Trim();
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException(TranslationManager.Get("Upd_RequireSha256"));

        var handler = new HttpClientHandler();
        if (proxyPort is > 0)
        {
            handler.Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort.Value}");
            handler.UseProxy = true;
        }

        string zipPath = Path.Combine(Path.GetTempPath(), "roxa-update.zip");
        try
        {
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Roxa/3.0");

            // A quiet connection is the failure mode that used to hang the
            // progress bar at some percent forever: after ResponseHeadersRead
            // the body stream has no timeout of its own, so a stalled link
            // produced neither an error nor any progress. Every read now
            // carries a stall budget; when it trips (or the connection is
            // reset), the download retries — resuming with a Range request
            // when the server supports it — before ever giving up.
            const int stallSeconds = 45;
            const int maxAttempts = 4;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadWithStallGuardAsync(http, info.Url, zipPath, stallSeconds, progress, ct);
                    break;
                }
                catch (Exception ex) when (
                    !ct.IsCancellationRequested
                    && attempt < maxAttempts
                    && ex is OperationCanceledException or IOException or HttpRequestException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            await using (var hs = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(hs, ct));
                if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("sha256 mismatch");
            }

            var root = Path.Combine(Path.GetTempPath(), "RoxaUpdate", Sanitize(info.Latest));
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, true); } catch { }
            }
            var payload = Path.Combine(root, "app");
            Directory.CreateDirectory(payload);
            ZipFile.ExtractToDirectory(zipPath, payload, overwriteFiles: true);

            if (!Directory.EnumerateFiles(payload, "*.exe").Any())
            {
                var subDirs = Directory.GetDirectories(payload);
                if (subDirs.Length == 1 && Directory.EnumerateFiles(subDirs[0], "*.exe").Any())
                    payload = subDirs[0];
            }

            _stagedRoot = root;
            _stagedPayload = payload;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    /// <summary>One download pass: GET (with a Range resume when a partial file
    /// is left over from an interrupted attempt), then stream to disk with a
    /// per-read stall budget. Throws on stall/reset so the caller retries.</summary>
    private static async Task DownloadWithStallGuardAsync(
        HttpClient http, string url, string zipPath, int stallSeconds, IProgress<double>? progress, CancellationToken ct)
    {
        long have = File.Exists(zipPath) ? new FileInfo(zipPath).Length : 0;

        HttpResponseMessage resp;
        bool resumed = false;
        if (have > 0)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The saved prefix is unusable (larger than the file, or the file
                // changed on the server): drop it and fetch from scratch.
                resp.Dispose();
                try { File.Delete(zipPath); } catch { }
                throw new IOException("update range not satisfiable");
            }
            resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!resumed)
            {
                // The server ignored the Range header and sent the full body.
                resp.Dispose();
                have = 0;
                resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }
        else
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            long remaining = resp.Content.Headers.ContentLength ?? 0;
            long total = resumed && remaining > 0 ? have + remaining : remaining;

            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(
                zipPath,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);

            var buffer = new byte[81920];
            long copied = have;
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                // CancelAfter restarts on every read, so this trips only when no
                // new byte has arrived for stallSeconds — a live but slow transfer
                // is never interrupted.
                stallCts.CancelAfter(TimeSpan.FromSeconds(stallSeconds));
                int read;
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(), stallCts.Token);
                    if (read <= 0)
                        break;
                    await fs.WriteAsync(buffer.AsMemory(0, read), stallCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Stall, not a user cancel: surface it as a retryable IO error.
                    throw new IOException("update download stalled");
                }
                copied += read;
                if (total > 0)
                    progress?.Report(Math.Min(100, copied * 100.0 / total));
            }
            progress?.Report(100);
        }
    }

    public static void LaunchUpdater()
    {
        if (_stagedRoot is null || _stagedPayload is null)
            throw new InvalidOperationException("update is not staged");

        var target = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exe = Path.GetFileName(Environment.ProcessPath ?? "Roxa.exe");
        var pid = Environment.ProcessId;

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_stagedPayload, exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("--update-apply");
        psi.ArgumentList.Add(_stagedPayload);
        psi.ArgumentList.Add(pid.ToString());
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(exe);
        Process.Start(psi);
        UpdaterLaunched = true;
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "update";
    }

    private static string ReconnectMarkerPath => Path.Combine(Path.GetTempPath(), "roxa-update-reconnect");

    /// <summary>Before the update restart: if we were connected, reconnect to the same server once the update is applied.</summary>
    public static void MarkReconnectAfterUpdate()
    {
        try { File.WriteAllText(ReconnectMarkerPath, "1"); } catch { }
    }

    /// <summary>Returns whether the marker exists and clears it (it is single-use).</summary>
    public static bool ConsumeReconnectMarker()
    {
        try
        {
            if (!File.Exists(ReconnectMarkerPath)) return false;
            File.Delete(ReconnectMarkerPath);
            return true;
        }
        catch { return false; }
    }
}
