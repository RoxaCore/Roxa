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

/// <summary>
/// The "Roxa Boot Recovery" scheduled task.
///
/// A TUN session writes 127.0.0.1 into the physical adapter's DNS. That value
/// lives in the registry, so a reboot while the session is open survives the
/// reboot: after the next boot nothing listens on :53, every name lookup fails
/// and the machine looks offline until the user happens to open Roxa - whose
/// startup recovery was the only thing that ever swept the dead DNS back.
///
/// This task closes that window. At every logon it runs "Roxa.exe --recover",
/// the same startup recovery, headless. The program creates the task itself
/// whenever it runs elevated (a TUN user always passes through an elevated
/// instance, because connecting asks for UAC), re-creating it on every such
/// start so the action keeps pointing at this very exe even after a portable
/// copy is moved. Uninstall removes it through "Roxa.exe --cleanup".
///
/// The principal is the interactive logged-on user with the highest run level:
/// no password is stored, no UAC prompt appears at logon, and the repair runs
/// under that user's own registry hive, which is where the system proxy lives.
/// </summary>
public static class BootRecoveryTask
{
    public const string TaskName = "Roxa Boot Recovery";

    /// <summary>
    /// Creates or refreshes the logon task. A no-op without admin rights and
    /// never throws: a task that could not be created costs one recovery on the
    /// next app start, which is exactly what happened before it existed.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!AdminHelper.IsRunningAsAdmin()) return;
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;
        try
        {
            string script =
                "$exe = '" + PsQuote(exe) + "'; " +
                "$user = \"$env:USERDOMAIN\\$env:USERNAME\"; " +
                "$action = New-ScheduledTaskAction -Execute $exe -Argument '--recover'; " +
                "$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user; " +
                "$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest; " +
                "$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10); " +
                "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null";
            RunPowerShell(script);
            TunManager.Log("Boot recovery task registered for " + exe);
        }
        catch (Exception ex)
        {
            TunManager.Log("Boot recovery task registration failed: " + ex.Message);
        }
    }

    /// <summary>Uninstall path: the task must not outlive the program.</summary>
    public static void Remove()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            RunPowerShell("Unregister-ScheduledTask -TaskName '" + TaskName + "' -Confirm:$false -ErrorAction SilentlyContinue");
            TunManager.Log("Boot recovery task removed.");
        }
        catch { }
    }

    // A single quote inside a PowerShell string literal is written twice.
    private static string PsQuote(string s) => s.Replace("'", "''");

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \""
                + script.Replace("\"", "\\\"") + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException("powershell did not start");
        // Read the pipes before waiting: an unattended child that fills the
        // 4 KB buffer would otherwise never finish, and killing it is not an
        // option - this runs at startup where taking process names lightly is
        // how a repair turns into an outage.
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
            throw new TimeoutException("powershell did not finish in 15s");
        string err = errTask.Result.Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(err.Length > 0 ? err : ("exit code " + p.ExitCode));
    }
}
