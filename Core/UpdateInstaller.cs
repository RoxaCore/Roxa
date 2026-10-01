using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Roxa.Core;

public static class UpdateInstaller
{
    private static string? _stagedRoot;
    private static string? _stagedPayload;

    public static bool IsStaged => _stagedRoot != null && _stagedPayload != null;

    public static async Task StageAsync(UpdateInfo info, int? proxyPort, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.Url))
            throw new InvalidOperationException("update url is empty");

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
                await input.CopyToAsync(fs, ct);
            }

            if (!string.IsNullOrWhiteSpace(info.Sha256))
            {
                await using var hs = File.OpenRead(zipPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(hs, ct)).ToLowerInvariant();
                if (!hash.Equals(info.Sha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
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

            WriteUpdaterScript(root, payload);
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

        var script = Path.Combine(_stagedRoot, "update.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("updater script missing");

        var target = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exe = Path.GetFileName(Environment.ProcessPath ?? "Roxa.exe");
        var pid = Environment.ProcessId;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Payload \"{_stagedPayload}\" -AppPid {pid} -Target \"{target}\" -Exe \"{exe}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        Process.Start(psi);
    }

    private static void WriteUpdaterScript(string root, string payload)
    {
        var sb = new StringBuilder();
        sb.AppendLine("param([string]$Payload,[int]$AppPid,[string]$Target,[string]$Exe)");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("Set-Location $env:TEMP");
        sb.AppendLine("$deadline = (Get-Date).AddSeconds(120)");
        sb.AppendLine("while ((Get-Date) -lt $deadline) {");
        sb.AppendLine("  $p = Get-Process -Id $AppPid -ErrorAction SilentlyContinue");
        sb.AppendLine("  if (-not $p) { break }");
        sb.AppendLine("  Start-Sleep -Milliseconds 800");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Seconds 1");
        sb.AppendLine("Copy-Item -Path (Join-Path $Payload '*') -Destination $Target -Recurse -Force");
        sb.AppendLine("Start-Process -FilePath (Join-Path $Target $Exe)");
        sb.AppendLine("$rootDir = Split-Path -Parent $Payload");
        sb.AppendLine("Remove-Item -LiteralPath $rootDir -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");

        var script = Path.Combine(root, "update.ps1");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));
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
}
