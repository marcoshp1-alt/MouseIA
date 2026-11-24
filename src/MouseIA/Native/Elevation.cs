using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace MouseIA.Native;

/// <summary>
/// Windows keeps normal programs from reading the mouse over apps opened as administrator.
/// MouseIA can run as administrator too, so shaking the mouse works over every app.
/// </summary>
internal static class Elevation
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TOKEN_QUERY = 0x0008, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TokenElevation = 20;

    public static bool IsSelfElevated { get; } = IsElevated(Process.GetCurrentProcess().Handle);

    private static bool IsElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return false;
        try { return GetTokenInformation(token, TokenElevation, out int elevated, 4, out _) && elevated != 0; }
        finally { CloseHandle(token); }
    }

    /// <summary>True when the window belongs to a program running as administrator.</summary>
    public static bool IsWindowElevated(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return true; // can't even look at it: it's above us
        try { return IsElevated(process); }
        finally { CloseHandle(process); }
    }

    /// <summary>Starts MouseIA again as administrator (Windows asks first). False if refused.</summary>
    public static bool Relaunch(params string[] extraArgs)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith("--after", StringComparison.Ordinal)))
            info.ArgumentList.Add(arg);
        foreach (var arg in extraArgs) info.ArgumentList.Add(arg);
        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception ex)
        {
            Log.Write("Elevation refused: " + ex.Message);
            return false;
        }
    }
}
