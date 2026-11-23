using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MouseIA.Native;

/// <summary>
/// Low-level mouse hook on its own thread. Windows silently removes low-level hooks whose
/// thread is too busy to answer, so this never shares the UI thread.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Win32.POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_QUIT = 0x0012;

    private readonly Win32.LowLevelProc _proc; // Kept alive: the OS calls it.
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;

    /// <summary>Called on the hook thread with screen x, y and a millisecond timestamp. Must return quickly.</summary>
    public event Action<int, int, long>? Moved;

    public MouseHook()
    {
        _proc = HookProc;
    }

    public bool IsInstalled => _thread != null;

    public bool Install()
    {
        if (_thread != null) return true;
        bool ok = false;
        using var started = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            _hook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _proc, Win32.GetModuleHandle(null), 0);
            ok = _hook != IntPtr.Zero;
            started.Set();
            if (!ok) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "MouseIA.MouseHook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        started.Wait(TimeSpan.FromSeconds(5));
        if (!ok)
        {
            _thread = null;
            return false;
        }
        return true;
    }

    public void Uninstall()
    {
        var thread = _thread;
        if (thread == null) return;
        _thread = null;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == Win32.WM_MOUSEMOVE && Moved != null)
        {
            var data = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            const uint LLMHF_INJECTED = 0x1;
            if ((data.flags & LLMHF_INJECTED) == 0)
            {
                try { Moved(data.pt.X, data.pt.Y, data.time); }
                catch (Exception) { /* never break the system's mouse */ }
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
