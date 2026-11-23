using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MouseIA.Native;

/// <summary>A frozen picture of one monitor, plus the windows that were on it.</summary>
internal sealed class ScreenShot
{
    public required BitmapSource Image { get; init; }
    /// <summary>Monitor bounds in physical pixels (virtual-screen coordinates).</summary>
    public required Int32Rect Bounds { get; init; }
    public required double Scale { get; init; }
    /// <summary>Top-level windows, front to back, in pixels relative to the monitor.</summary>
    public required IReadOnlyList<Int32Rect> Windows { get; init; }
    /// <summary>Cursor position in pixels relative to the monitor.</summary>
    public required Point Cursor { get; init; }
}

internal static class ScreenCapture
{
    public static ScreenShot CaptureMonitorUnderCursor()
    {
        Win32.GetCursorPos(out var cursor);
        var info = Win32.MonitorAt(cursor, out var monitor);
        var r = info.rcMonitor;
        var bounds = new Int32Rect(r.Left, r.Top, r.Width, r.Height);
        return new ScreenShot
        {
            Image = Capture(bounds),
            Bounds = bounds,
            Scale = Win32.MonitorScale(monitor),
            Windows = TopLevelWindows(bounds),
            Cursor = new Point(cursor.X - r.Left, cursor.Y - r.Top),
        };
    }

    public static BitmapSource Capture(Int32Rect area)
    {
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
        IntPtr bitmap = Win32.CreateCompatibleBitmap(screenDc, area.Width, area.Height);
        IntPtr old = Win32.SelectObject(memDc, bitmap);
        try
        {
            Win32.BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Win32.SRCCOPY | Win32.CAPTUREBLT);
            Win32.SelectObject(memDc, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            Win32.DeleteObject(bitmap);
            Win32.DeleteDC(memDc);
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Visible app windows on the monitor, front to back, clipped and relative to it.</summary>
    private static List<Int32Rect> TopLevelWindows(Int32Rect monitor)
    {
        var list = new List<Int32Rect>();
        var ownPid = Win32.CurrentProcessId;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || Win32.IsCloaked(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownPid) return true;
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return true;
            var cls = Win32.WindowClass(hwnd);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (!Win32.VisibleBounds(hwnd, out var rc)) return true;
            int left = Math.Max(rc.Left, monitor.X), top = Math.Max(rc.Top, monitor.Y);
            int right = Math.Min(rc.Right, monitor.X + monitor.Width), bottom = Math.Min(rc.Bottom, monitor.Y + monitor.Height);
            if (right - left < 40 || bottom - top < 40) return true;
            list.Add(new Int32Rect(left - monitor.X, top - monitor.Y, right - left, bottom - top));
            return list.Count < 64;
        }, IntPtr.Zero);
        return list;
    }
}
