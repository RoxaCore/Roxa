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