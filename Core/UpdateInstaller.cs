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
            using var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using (var fs = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long total = resp.Content.Headers.ContentLength ?? 0;
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    copied += read;
                    if (total > 0)
                        progress?.Report(copied * 100.0 / total);
                }
                progress?.Report(100);
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
