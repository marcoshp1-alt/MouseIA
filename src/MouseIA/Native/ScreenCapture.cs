using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>The window handle for each entry of Windows.</summary>
    public IReadOnlyList<IntPtr> Handles { get; init; } = Array.Empty<IntPtr>();
    /// <summary>Cursor position in pixels relative to the monitor (may lie outside it).</summary>
    public required Point Cursor { get; init; }

    public bool HasCursor => Cursor.X >= 0 && Cursor.Y >= 0 && Cursor.X < Bounds.Width && Cursor.Y < Bounds.Height;
}

internal sealed record MonitorInfo(int Number, string Device, Int32Rect Bounds, bool Primary, IntPtr Handle)
{
    public string Label => $"{Number}{(Primary ? " ★" : "")} · {Bounds.Width}×{Bounds.Height}";
}

internal static class ScreenCapture
{
    /// <summary>All monitors, numbered left to right like Windows' display settings usually show them.</summary>
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var found = new List<(string Device, Int32Rect Bounds, bool Primary, IntPtr Handle)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = "" };
            if (Win32.GetMonitorInfoEx(monitor, ref info))
            {
                var r = info.rcMonitor;
                found.Add((info.szDevice, new Int32Rect(r.Left, r.Top, r.Width, r.Height), (info.dwFlags & 1) != 0, monitor));
            }
            return true;
        }, IntPtr.Zero);
        return found.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)
            .Select((m, i) => new MonitorInfo(i + 1, m.Device, m.Bounds, m.Primary, m.Handle)).ToList();
    }

    /// <summary>Monitors the pointer should cover for a setting: "all", "cursor" or a device name.</summary>
    public static IReadOnlyList<MonitorInfo> MonitorsFor(string mode)
    {
        var all = Monitors();
        if (all.Count == 0) return all;
        Win32.GetCursorPos(out var cursor);
        var underCursor = Win32.MonitorFromPoint(cursor, Win32.MONITOR_DEFAULTTONEAREST);
        if (mode == "all") return all;
        if (mode != "cursor")
        {
            var chosen = all.Where(m => string.Equals(m.Device, mode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count > 0) return chosen;
        }
        return all.Where(m => m.Handle == underCursor).DefaultIfEmpty(all[0]).ToList();
    }

    public static ScreenShot CaptureMonitor(MonitorInfo monitor)
    {
        Win32.GetCursorPos(out var cursor);
        var bounds = monitor.Bounds;
        return new ScreenShot
        {
            Image = Capture(bounds),
            Bounds = bounds,
            Scale = Win32.MonitorScale(monitor.Handle),
            Windows = TopLevelWindows(bounds, out var handles),
            Handles = handles,
            Cursor = new Point(cursor.X - bounds.X, cursor.Y - bounds.Y),
        };
    }

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
            Windows = TopLevelWindows(bounds, out var handles),
            Handles = handles,
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
    private static List<Int32Rect> TopLevelWindows(Int32Rect monitor, out List<IntPtr> handles)
    {
        var list = new List<Int32Rect>();
        var hwnds = new List<IntPtr>();
        handles = hwnds;
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
            hwnds.Add(hwnd);
            return list.Count < 64;
        }, IntPtr.Zero);
        return list;
    }
}
