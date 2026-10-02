using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Roxa.Core;

public static class ProcessStarter
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const int STD_INPUT_HANDLE = -10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    public static Process StartSuspendedInJob(string exePath, IReadOnlyList<string> args,
        JobObject? job, Action<string> onOutput)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        IntPtr hStdoutRead = IntPtr.Zero, hStdoutWrite = IntPtr.Zero;
        IntPtr hStderrRead = IntPtr.Zero, hStderrWrite = IntPtr.Zero;
        IntPtr hProcess = IntPtr.Zero, hThread = IntPtr.Zero;

        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            if (!CreatePipe(out hStdoutRead, out hStdoutWrite, ref sa, 0) ||
                !CreatePipe(out hStderrRead, out hStderrWrite, ref sa, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            SetHandleInformation(hStdoutRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(hStderrRead, HANDLE_FLAG_INHERIT, 0);

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = (int)STARTF_USESTDHANDLES,
                hStdInput = GetStdHandle(STD_INPUT_HANDLE),
                hStdOutput = hStdoutWrite,
                hStdError = hStderrWrite
            };
            var cmd = BuildCommandLine(exePath, args);
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, true,
                    CREATE_SUSPENDED | CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            hProcess = pi.hProcess;
            hThread = pi.hThread;

            CloseHandle(hStdoutWrite); hStdoutWrite = IntPtr.Zero;
            CloseHandle(hStderrWrite); hStderrWrite = IntPtr.Zero;

            bool assigned = job?.Assign(hProcess) ?? false;
            if (job != null && !assigned)
                TunManager.Log($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");

            if (ResumeThread(hThread) == 0xFFFFFFFF)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(hThread); hThread = IntPtr.Zero;

            Process process;
            try { process = Process.GetProcessById(pi.dwProcessId); }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(Roxa.Localization.TranslationManager.Get("Eng_ExitedUnexpectedly"));
            }

            IntPtr stdoutRead = hStdoutRead; hStdoutRead = IntPtr.Zero;
            IntPtr stderrRead = hStderrRead; hStderrRead = IntPtr.Zero;
            Task.Factory.StartNew(() => ReadLines(stdoutRead, onOutput), TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(() => ReadLines(stderrRead, onOutput), TaskCreationOptions.LongRunning);
            return process;
        }
        catch
        {
            if (hProcess != IntPtr.Zero)
            {
                try { TerminateProcess(hProcess, 1); } catch { }
                try { CloseHandle(hProcess); } catch { }
            }
            if (hThread != IntPtr.Zero) { try { CloseHandle(hThread); } catch { } }
            throw;
        }
        finally
        {
            if (hStdoutWrite != IntPtr.Zero) CloseHandle(hStdoutWrite);
            if (hStderrWrite != IntPtr.Zero) CloseHandle(hStderrWrite);
            if (hStdoutRead != IntPtr.Zero) CloseHandle(hStdoutRead);
            if (hStderrRead != IntPtr.Zero) CloseHandle(hStderrRead);
        }
    }

    private static StringBuilder BuildCommandLine(string exePath, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(Quote(exePath));
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(Quote(arg));
        }
        return sb;
    }

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return arg;
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static void ReadLines(IntPtr handle, Action<string> onOutput)
    {
        try
        {
            using var stream = new FileStream(new SafeFileHandle(handle, ownsHandle: true),
                FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                try { onOutput(line); }
                catch { }
            }
        }
        catch { }
    }
}
