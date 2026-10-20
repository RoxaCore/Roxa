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

using Avalonia;
using Avalonia.Media;
using System.Threading;

namespace Roxa;

sealed class Program
{
    // Inter-process names, all in the `Global` namespace.
    //
    // Without `Global\`, every session (Fast User Switching, RDP) would see only
    // its own instance, and one install left the user with two programs running
    // side by side, both fighting over TUN and the system proxy.
    private const string MutexName = @"Global\Roxa.SingleInstance";
    private const string ShowSignalName = @"Global\RoxaShowSignal";
    private const string ExitSignalName = @"Global\RoxaExitSignal";

    public static string[] StartupArgs = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;

        if (args.Length > 0 && (args[0] == "--baseline" || args[0] == "--validate" || args[0] == "--speed"))
        {
            var ct = new CancellationTokenSource().Token;
            int code = args[0] == "--speed"
                ? Core.SpeedRunner.RunAsync(args, ct).GetAwaiter().GetResult()
                : Core.BaselineRunner.RunAsync(args, ct).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        // Auto-update is disabled in this release: the app no longer downloads the
        // update package and never runs this path. Instead of returning silently —
        // which hid the fact that an update had not been applied — we clean the
        // staging leftovers and exit non-zero so the caller knows nothing applied.
        if (args.Length > 0 && args[0] == "--update-apply")
        {
            try { Core.UpdateApplier.CleanupStaging(); } catch { }
            Environment.Exit(2);
            return;
        }

        // Cleanup on uninstall: the installer runs this before deleting files.
        // Without it, if the user crashed right before uninstalling or left a
        // connection open, the 127.0.0.1 DNS, the loopback proxy, orphan routes
        // and the RoxaTunExcl_* firewall rules stay on the machine forever, and
        // nobody cleans them up afterwards. Deliberately before the mutex: the
        // installer may run while no instance is up.
        if (Array.Exists(args, a => a == "--cleanup"))
        {
            try { Core.TunRecovery.RecoverOnStartup(); } catch { }
            try { Core.TunExclusion.CleanupLeftovers(); } catch { }
            try { Core.TunKillSwitch.CleanupLeftovers(); } catch { }
            try { Core.TestRouteBypass.ReleaseOrphanedHostRoutes(); } catch { }
            try { Core.TunRecovery.EmergencyCleanup(); } catch { }
            try { Core.TunManager.FlushLog(); } catch { }
            return;
        }

        // Quiet remote exit: no killing (killing would leave a ghost tray icon).
        if (Array.Exists(args, a => a == "--exit"))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var remoteExit = EventWaitHandle.OpenExisting(ExitSignalName);
                    remoteExit.Set();
                }
            }
            catch { }
            return;
        }

        bool waitExit = Array.Exists(args, a => a == "--wait-exit");

        // Wait for the previous, exiting instance to actually die. A fixed mutex
        // loop alone could not tell "slow" from "never died"; in the second case
        // the new instance silently died after 10 seconds and the app vanished.
        if (waitExit)
        {
            int waitPid = 0;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--wait-pid" && int.TryParse(args[i + 1], out int p)) waitPid = p;
            if (waitPid > 0 && waitPid != Environment.ProcessId)
            {
                try
                {
                    using var oldProc = System.Diagnostics.Process.GetProcessById(waitPid);
                    if (!oldProc.HasExited) oldProc.WaitForExit(30_000);
                }
                catch { /* process already gone or no access; the mutex is free now */ }
            }
        }

        Mutex? mutex = null;
        bool createdNew = false;
        int tries = waitExit ? 100 : 1;
        try
        {
            for (int i = 0; i < tries; i++)
            {
                mutex?.Dispose();
                mutex = new Mutex(true, MutexName, out createdNew);
                if (createdNew) break;
                if (i < tries - 1) Thread.Sleep(100);
            }
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }
        catch
        {
            // Another elevated/non-elevated instance may own the mutex and deny
            // access; rather than crash, behave as if an instance already exists.
            createdNew = false;
        }

        if (!createdNew)
        {
            bool handedOff = false;
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                signal.Set();
                handedOff = true;
            }
            catch { }
            mutex?.Dispose();

            // Another instance is running and we brought its window forward — so
            // there is nothing to report. But if delivery failed too (say the
            // instance belongs to another user, or the mutex owner denies access),
            // a double-click does nothing: no window, no error, not even a hint as
            // to why. That is exactly the blind case that used to be swallowed.
            if (!handedOff)
                WarnInstanceBlocked();

            return;
        }

        try { Core.UpdateApplier.CleanupStaging(); } catch { }

        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        ThreadPool.RegisterWaitForSingleObject(showSignal, static (_, _) =>
        {
            try
            {
                if (Application.Current is App app)
                    app.ShowMainWindow();
            }
            catch { }
        }, null, -1, false);

        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ExitSignalName);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, static (_, _) =>
        {
            try
            {
                if (Application.Current is App app)
                    app.ExitApp();
            }
            catch { }
        }, null, -1, false);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    /// <summary>
    /// When another instance owns the mutex but we could not deliver the
    /// show-window signal to it, the user sees nothing: no window, no error,
    /// not even a clue. That is the only truly blind case, and it must surface.
    ///
    /// We come up before Avalonia, so we need a system message — not a MessageBox
    /// tied to WPF/WinForms. This user32 call is enough.
    /// </summary>
    private static void WarnInstanceBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            string text;
            try { text = Localization.TranslationManager.Get("Msg_InstanceBlocked"); }
            catch { text = "Another instance of Roxa is already running and its window could not be brought up."; }

            const uint MB_OK = 0x0;
            const uint MB_ICONWARNING = 0x30;
            const uint MB_SETFOREGROUND = 0x10000;
            MessageBoxW(IntPtr.Zero, text, "Roxa", MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        }
        catch { /* showing the message failed too; that is fine, it just must not crash */ }
    }

    // ExactSpelling is required: the default of false makes the runtime rewrite
    // the entry name to "MessageBoxWW", which is never found — our message would
    // be lost silently. Unicode is required too: the message text is Persian.
    [System.Runtime.InteropServices.DllImport("user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Glyphs missing from the primary font are filled in from here.
            //
            // Vazirmatn only ships Latin and Arabic/Persian; Chinese, Japanese,
            // Korean, Russian and Indic are missing, so their text showed as boxes.
            //
            // Deliberately not comma-separated inside FontFamily itself: Avalonia
            // does not support multi-font fallback there and silently discards it
            // (AvaloniaUI/Avalonia#19362). FontManagerOptions is the right place:
            // for each missing glyph it searches these in order and takes the
            // first font that has the character, skipping any not installed.
            //
            // Order matters: Segoe UI first because it covers Cyrillic, Greek and
            // Latin; CJK and Indic are read only for their own scripts, so Persian
            // and English still come out as Vazirmatn.
            .With(new FontManagerOptions
            {
                FontFallbacks = new[]
                {
                    new FontFallback { FontFamily = new FontFamily("Segoe UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft JhengHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
                    new FontFallback { FontFamily = new FontFamily("Nirmala UI") },
                },
            })
            .LogToTrace();
}
