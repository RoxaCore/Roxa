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

namespace Roxa.Core;

public static class UpdateApplier
{
    // The backup of the current version is kept next to the app's settings, not in
    // %TEMP%: it lasts longer and is usually on the same install drive, so the copy
    // stays short. We keep a single backup and the next update replaces it; deleting
    // it automatically at startup was dangerous, because "the app came up" is not
    // yet proof it is healthy — it may crash seconds later.
    private static string BackupDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roxa", "update-backup");

    public static int Run(string payload, int appPid, string targetDir, string exeName)
    {
        string exePath = Path.Combine(targetDir, exeName);
        // Both versions (fresh and rolled back) must carry the same "after connect".
        bool reconnect = false;
        try { reconnect = UpdateInstaller.ConsumeReconnectMarker(); } catch { }
        // Also needed inside catch, so it is declared outside try.
        bool backedUp = false;

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && IsAlive(appPid))
                Thread.Sleep(800);
            Thread.Sleep(1000);

            // 1) The fresh package must really be a runnable build. Copying an empty
            // or partial folder over the live install also kills the previously
            // working version, leaving a mixed install of old and new files.
            if (!LooksLikePe(Path.Combine(payload, exeName)))
            {
                Log("payload is not a valid build; keeping the current install.");
                Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // 2) Back up the current version; this is the only thing separating a
            // failed update from a missing app. The copy is deliberate, not a Move:
            // a Move that dies mid-transition leaves the install folder empty.
            backedUp = TryBackup(targetDir);

            // 3) Copy with retries; a locked file (antivirus, a leftover engine)
            // is the most common cause of failure.
            if (!TryCopyTree(payload, targetDir))
            {
                Log("copy failed; restoring the previous build.");
                if (backedUp) TryRestore(targetDir);
                Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // 4) Run it, then verify that it actually stayed.
            bool started = Launch(exePath, targetDir, reconnect, out Process? proc);
            if (!started || proc == null)
            {
                Log("updated build failed to launch; rolling back.");
                if (backedUp && TryRestore(targetDir)) Launch(exePath, targetDir, reconnect);
                return 0;
            }

            // Wait 20 seconds. If it is still alive, or exited with code zero, we assume
            // the update succeeded. A zero exit can mean "another instance is running",
            // which is entirely normal — so only a non-zero code means the new build is broken.
            if (!proc.WaitForExit(20_000)) return 0;
            try { if (proc.ExitCode == 0) return 0; } catch { return 0; }

            Log($"updated build exited with code {SafeExitCode(proc)}; rolling back.");
            if (backedUp && TryRestore(targetDir)) Launch(exePath, targetDir, reconnect);
            return 0;
        }
        catch (Exception ex)
        {
            Log($"update failed: {ex.Message}");
            // Above all: the user must not be left with a closed window and no app.
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
        catch { /* if even the last reporting resort is down, the update must not stop */ }
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
