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

using System.Diagnostics;

namespace Roxa.Core;

public static class UpdateApplier
{
    // بکاپِ نسخهٔ فعلی کنارِ تنظیماتِ برنامه نگه داشته می‌شود، نه در %TEMP%:
    // هم دوامش بیشتر است و هم معمولاً همان درایوِ نصب است، پس کپی کوتاه می‌ماند.
    // فقط یک بکاپ نگه می‌داریم و آپدیتِ بعدی همان را جایگزین می‌کند؛ حذفِ خودکارِ
    // آن در شروعِ برنامه خطرناک بود، چون «برنامه بالا آمد» هنوز دلیلِ سالم‌بودن
    // نیست — ممکن است ثانیه‌ها بعد کرش کند.
    private static string BackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa", "update-backup");

    public static int Run(string payload, int appPid, string targetDir, string exeName)
    {
        string exePath = Path.Combine(targetDir, exeName);
        // هر دو نسخه (تازه و برگشت‌خورده) باید همان «بعد از اتصال» را داشته باشند.
        bool reconnect = false;
        try { reconnect = UpdateInstaller.ConsumeReconnectMarker(); } catch { }
        // در catch هم لازم است، پس بیرون از try تعریف می‌شود.
        bool backedUp = false;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && IsAlive(appPid))
                Thread.Sleep(800);
            Thread.Sleep(1000);

            // ۱) بستهٔ تازه باید واقعاً یک نسخهٔ قابلِ اجرا باشد. کپیکردنِ پوشهٔ
            // خالی یا ناقص رویِ نصبِ زنده، نسخهٔ کارکردِ قبلی را هم می‌کشت و کاربر
            // می‌ماند با نصبِ ترکیبیِ فایل‌های قدیمی/جدید.
            if (!LooksLikePe(Path.Combine(payload, exeName)))
            {
                Log("payload is not a valid build; keeping the current install.");
                Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // ۲) از نسخهٔ فعلی بکاپ بگیر؛ این تنها چیزی است که بینِ یک آپدیتِ ناقص و
            // یک برنامهٔ گم‌شده فرق می‌اندازد. کپی عمدی است نه Move: اگر Move کنیم
            // و در همان گذار بمیریم، پوشهٔ نصب خالی می‌ماند.
            backedUp = TryBackup(targetDir);

            // ۳) کپی با تلاشِ چندباره؛ قفل‌بودنِ فایل (آنتی‌ویروس، موتورِ جامانده)
            // شایع‌ترین دلیلِ شکست است.
            if (!TryCopyTree(payload, targetDir))
            {
                Log("copy failed; restoring the previous build.");
                if (backedUp) TryRestore(targetDir);
                Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // ۴) اجرا، و بعد بررسیِ اینکه واقعاً ماندگار شد.
            bool started = Launch(exePath, targetDir, reconnect, out Process? proc);
            if (!started || proc == null)
            {
                Log("updated build failed to launch; rolling back.");
                if (backedUp && TryRestore(targetDir)) Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // ۲۰ ثانیه صبر می‌کنیم. اگر هنوز زنده بود، یا با کدِ صفر خارج شد، آپدیت
            // را موفق فرض می‌کنیم. خروجِ صفر می‌تواند «نمونهٔ دیگری در حالِ اجراست»
            // باشد که کاملاً عادی است — پس فقط کدِ غیرصفر یعنی نسخهٔ تازه خراب است.
            if (!proc.WaitForExit(20_000)) return 0;
            try { if (proc.ExitCode == 0) return 0; } catch { return 0; }

            Log($"updated build exited with code {SafeExitCode(proc)}; rolling back.");
            if (backedUp && TryRestore(targetDir)) Launch(exePath, targetDir, reconnect);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"update failed: {ex.Message}");
            // مهم‌تر از همه: کاربر نباید با یک پنجرهٔ بسته و بدونِ برنامه بماند.
            try { if (backedUp) TryRestore(targetDir); } catch { }
            try { Launch(exePath, targetDir, reconnect); } catch { }
            return 0;
        }
    }

    private static bool LooksLikePe(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = File.OpenRead(path);
            Span<byte> hdr = stackalloc byte[2];
            return fs.Read(hdr) == 2 && hdr[0] == (byte)'M' && hdr[1] == (byte)'Z';
        }
        catch { return false; }
    }

    private static bool TryBackup(string targetDir)
    {
        try
        {
            if (!Directory.Exists(targetDir)) return false;
            string backup = BackupDir;
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
            CopyTree(targetDir, backup);
            Log($"backed up current build to {backup}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"backup failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryRestore(string targetDir)
    {
        try
        {
            string backup = BackupDir;
            if (!Directory.Exists(backup)) { Log("no backup available to restore."); return false; }
            CopyTree(backup, targetDir);
            Log("previous build restored.");
            return true;
        }
        catch (Exception ex)
        {
            Log($"rollback failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryCopyTree(string source, string destination)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                CopyTree(source, destination);
                return true;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 9)
            {
                Log($"copy attempt {attempt + 1}/10 failed: {ex.Message}");
                Thread.Sleep(1000);
            }
            catch { return false; }
        }
        return false;
    }

    private static bool Launch(string exePath, string targetDir, bool reconnect, out Process? proc)
    {
        proc = null;
        try
        {
            if (!File.Exists(exePath)) return false;
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = targetDir,
            };
            if (reconnect) psi.Arguments = "--reconnect";
            proc = Process.Start(psi);
            return proc != null;
        }
        catch (Exception ex)
        {
            Log($"launch failed: {ex.Message}");
            return false;
        }
    }

    private static void Launch(string exePath, string targetDir, bool reconnect)
        => Launch(exePath, targetDir, reconnect, out _);

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; } catch { return -1; }
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { /* آخرین راهِ گزارش هم از کار افتاده باشد، آپدیت نباید بایستد */ }
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
