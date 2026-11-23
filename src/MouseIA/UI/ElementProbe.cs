using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using MouseIA.Native;

namespace MouseIA.UI;

/// <summary>
/// Finds the on-screen element under the pointer (a paragraph, button, image…) with UI Automation,
/// on a background thread so the pointer never stutters.
/// </summary>
internal sealed class ElementProbe : IDisposable
{
    private readonly IntPtr _overlay;
    private readonly Action<Win32.POINT, Int32Rect?> _found;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private Win32.POINT _pending;
    private bool _hasPending;
    private volatile bool _stopped;

    /// <summary>found receives the probed point and the element's bounds in screen pixels (null: nothing useful).</summary>
    public ElementProbe(IntPtr overlay, Action<Win32.POINT, Int32Rect?> found)
    {
        _overlay = overlay;
        _found = found;
        _thread = new Thread(Run) { IsBackground = true, Name = "MouseIA.ElementProbe" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Request(Win32.POINT screenPoint)
    {
        lock (_gate)
        {
            _pending = screenPoint;
            _hasPending = true;
        }
        _signal.Set();
    }

    private void Run()
    {
        Win32.POINT last = new() { X = int.MinValue };
        while (!_stopped)
        {
            _signal.WaitOne();
            if (_stopped) break;
            // Let the pointer settle a moment; only the latest position matters.
            Thread.Sleep(45);
            Win32.POINT point;
            lock (_gate)
            {
                if (!_hasPending) continue;
                point = _pending;
                _hasPending = false;
            }
            if (point.X == last.X && point.Y == last.Y) continue;
            last = point;
            Int32Rect? rect = null;
            try { rect = Probe(point); }
            catch (Exception ex) { Log.Write("Probe: " + ex.Message); }
            if (!_stopped) _found(point, rect);
        }
    }

    private Int32Rect? Probe(Win32.POINT point)
    {
        AutomationElement? element;
        // The overlay covers the screen; disabled windows are skipped by hit testing,
        // so for this instant the element underneath is found instead.
        Win32.EnableWindow(_overlay, false);
        try
        {
            element = AutomationElement.FromPoint(new Point(point.X, point.Y));
        }
        finally
        {
            Win32.EnableWindow(_overlay, true);
        }
        if (element == null) return null;
        if (element.Current.ProcessId == Environment.ProcessId) return null;

        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        Rect best = element.Current.BoundingRectangle;
        for (int level = 0; level < 7; level++)
        {
            var bounds = current.Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Width > 0) best = bounds;
            if (IsWholeThing(current, bounds)) break;
            var parent = walker.GetParent(current);
            if (parent == null || parent == AutomationElement.RootElement) break;
            var parentBounds = parent.Current.BoundingRectangle;
            // Stop before jumping to something huge (a whole page or window).
            if (parentBounds.IsEmpty ||
                (parentBounds.Width * parentBounds.Height > 12 * Math.Max(1, bounds.Width * bounds.Height) && bounds.Height >= 22)) break;
            if (parent.Current.ControlType == ControlType.Window ||
                (parent.Current.ControlType == ControlType.Pane && parentBounds.Width * parentBounds.Height > 600_000)) break;
            current = parent;
        }
        if (best.IsEmpty || best.Width < 8 || best.Height < 8) return null;
        return new Int32Rect((int)best.X, (int)best.Y, (int)best.Width, (int)best.Height);
    }

    /// <summary>Elements worth outlining as they are: controls, pictures, and text blocks of a couple of lines.</summary>
    private static bool IsWholeThing(AutomationElement element, Rect bounds)
    {
        var type = element.Current.ControlType;
        if (type == ControlType.Button || type == ControlType.Hyperlink || type == ControlType.Image ||
            type == ControlType.Edit || type == ControlType.CheckBox || type == ControlType.RadioButton ||
            type == ControlType.MenuItem || type == ControlType.TabItem || type == ControlType.ListItem ||
            type == ControlType.ComboBox || type == ControlType.SplitButton || type == ControlType.TreeItem ||
            type == ControlType.DataItem)
            return bounds.Height >= 14;
        return bounds.Height >= 40 && bounds.Width >= 80;
    }

    public void Dispose()
    {
        _stopped = true;
        _signal.Set();
    }
}
