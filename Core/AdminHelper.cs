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

using System.Security.Principal;

namespace Roxa.Core;

public static class AdminHelper
{
    public static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestartAsAdmin()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "",
            Arguments = "--wait-exit",
            Verb = "runas",
            UseShellExecute = true
        };
        try
        {
            System.Diagnostics.Process.Start(psi);
            // بعد از موفقیت Process.Start (اگر UAC رد شده باشد اینجا نمی‌آییم)؛
            // آیکون سینی باید قبل از Environment.Exit روی رشتهٔ UI پاک شود.
            try
            {
                if (Avalonia.Application.Current is Roxa.App app)
                    app.DisposeTray();
            }
            catch { }
            Environment.Exit(0);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC
        }
    }
}