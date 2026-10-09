using System.Diagnostics;

namespace Roxa.Core;

public static class UpdateApplier
{
    public static int Run(string payload, int appPid, string targetDir, string exeName)
    {
        bool ok = true;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && IsAlive(appPid))
                Thread.Sleep(800);
            Thread.Sleep(1000);

            if (Directory.Exists(payload) && Directory.EnumerateFileSystemEntries(payload).Any())
            {
                ok = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        CopyTree(payload, targetDir);
                        ok = true;
                        break;
                    }
                    catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 9)
                    {
                        Thread.Sleep(1000);
                    }
                }
            }

            // کپی ناقص نباید برنامه را روی ترکیبی از فایل‌های قدیمی/جدید بالا بیاورد.
            if (ok)
            {
                try
                {
                    string exePath = Path.Combine(targetDir, exeName);
                    if (File.Exists(exePath))
                    {
                        bool reconnect = UpdateInstaller.ConsumeReconnectMarker();
                        var psi = new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true,
                            WorkingDirectory = targetDir,
                        };
                        if (reconnect)
                            psi.Arguments = "--reconnect";
                        Process.Start(psi);
                    }
                }
                catch { }
            }
        }
        catch { ok = false; }
        return ok ? 0 : 1;
    }

    public static void CleanupStaging()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), "RoxaUpdate");
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch
            {
                return;
            }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(destination, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(destination, rel);
            File.Copy(file, dest, true);
        }
    }
}
