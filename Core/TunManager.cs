using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XrayClient.Core;

public static class TunManager
{
    private const string AdapterName = "RoxaTun";
    private const string TUN_GUID = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    public static string WintunDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "engine");
    public static string WintunPath => Path.Combine(WintunDirectory, RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "wintun-arm64.dll" : "wintun-x64.dll");

    public static bool IsWintunAvailable()
    {
        bool exists = File.Exists(WintunPath);
        if (!exists)
            Log($"wintun.dll not found at: {WintunPath}");
        return exists;
    }

    public static async Task<bool> EnsureWintunDownloadedAsync(Action<string>? log = null)
    {
        if (IsWintunAvailable()) return true;

        Log("Downloading wintun driver...");
        log?.Invoke("دانلود درایور wintun...");

        try
        {
            Directory.CreateDirectory(WintunDirectory);
            string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            string url = $"https://www.wintun.net/builds/wintun-{arch}-0.14.1.zip";
            string zipPath = Path.Combine(WintunDirectory, "wintun.zip");

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(zipPath, data);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, WintunDirectory, true);
            File.Delete(zipPath);

            Log("wintun driver downloaded successfully.");
            log?.Invoke("درایور wintun با موفقیت دانلود شد.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Failed to download wintun: {ex.Message}");
            log?.Invoke($"خطا در دانلود wintun: {ex.Message}");
            return false;
        }
    }

    public static bool CreateTunAdapter(Action<string>? log = null)
    {
        if (!AdminHelper.IsRunningAsAdmin())
        {
            log?.Invoke("نیاز به دسترسی Administrator برای ساخت TUN adapter");
            return false;
        }

        Log("Creating TUN adapter...");
        log?.Invoke("ساخت TUN adapter...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface add subinterface \"{AdapterName}\" mtu=1420 store=persistent",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                proc.WaitForExit(5000);
                string output = proc.StandardOutput.ReadToEnd();
                string error = proc.StandardError.ReadToEnd();

                if (proc.ExitCode == 0)
                {
                    Log("TUN adapter created.");
                    log?.Invoke("TUN adapter ساخته شد.");
                    return true;
                }

                if (error.Contains("already exists") || output.Contains("already exists"))
                {
                    Log("TUN adapter already exists.");
                    log?.Invoke("TUN adapter از قبل وجود دارد.");
                    return true;
                }

                Log($"TUN adapter creation failed: {error}");
                log?.Invoke($"خطا در ساخت adapter: {error}");
                return false;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log($"TUN adapter error: {ex.Message}");
            log?.Invoke($"خطا: {ex.Message}");
            return false;
        }
    }

    public static void CleanupTunAdapter(Action<string>? log = null)
    {
        Log("Cleaning up TUN adapter...");
        log?.Invoke("پاکسازی TUN adapter...");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"interface delete subinterface \"{AdapterName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            Log("TUN adapter cleanup done.");
        }
        catch (Exception ex)
        {
            Log($"TUN cleanup error: {ex.Message}");
            log?.Invoke($"خطا در پاکسازی: {ex.Message}");
        }
    }

    public static bool IsTunAdapterActive()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "interface show subinterface",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                return output.Contains(AdapterName);
            }
        }
        catch { }
        return false;
    }

    private static void Log(string message)
    {
        try
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "tun.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}