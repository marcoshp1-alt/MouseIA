using System;
using System.Runtime.InteropServices;

namespace MouseIA.Native;

/// <summary>Notification-area icon built directly on Shell_NotifyIcon (no WinForms).</summary>
internal sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const int CallbackMessage = Win32.WM_USER + 77;

    private readonly MessageWindow _window;
    private readonly int _taskbarCreated;
    private readonly IntPtr _icon;
    private string _tip;
    private bool _added;

    public event Action? LeftClick;
    public event Action? RightClick;

    public TrayIcon(MessageWindow window, IntPtr icon, string tip)
    {
        _window = window;
        _icon = icon;
        _tip = tip;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window.Message += OnMessage;
    }

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        if (!_added) return;
        var data = NewData(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Notify(string title, string text)
    {
        if (!_added) return;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
        data.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated)
        {
            // Explorer restarted: add the icon again.
            Show();
            return false;
        }
        if (msg != CallbackMessage) return false;
        int evt = (int)(lParam.ToInt64() & 0xFFFF);
        switch (evt)
        {
            // With NOTIFYICON_VERSION_4 a click arrives as NIN_SELECT (plus raw button messages,
            // which are ignored so one click never counts twice).
            case Win32.NIN_SELECT:
            case Win32.NIN_KEYSELECT:
                LeftClick?.Invoke();
                return true;
            case Win32.WM_CONTEXTMENU:
                RightClick?.Invoke();
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
    }
}
