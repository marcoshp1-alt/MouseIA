using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using MouseIA.Native;

namespace MouseIA.UI;

/// <summary>
/// Finds the on-screen element under the pointer (a paragraph, button, image…) with UI Automation,
/// on a background thread so the pointer never stutters. It walks the accessibility tree of the
/// window under the pointer directly, so MouseIA's own layer never has to step aside.
/// </summary>
internal sealed class ElementProbe : IDisposable
{
    private readonly Action<Win32.POINT, Int32Rect?> _found;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly object _gate = new();
    private Win32.POINT _pending;
    private IntPtr _pendingWindow;
    private bool _hasPending;
    private volatile bool _stopped;

    /// <summary>found receives the probed point and the element's bounds in screen pixels (null: nothing useful).</summary>
    public ElementProbe(Action<Win32.POINT, Int32Rect?> found)
    {
        _found = found;
        _thread = new Thread(Run) { IsBackground = true, Name = "MouseIA.ElementProbe" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <param name="window">The top-level window under the point.</param>
    public void Request(Win32.POINT screenPoint, IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        lock (_gate)
        {
            _pending = screenPoint;
            _pendingWindow = window;
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
            Thread.Sleep(40);
            Win32.POINT point;
            IntPtr window;
            lock (_gate)
            {
                if (!_hasPending) continue;
                point = _pending;
                window = _pendingWindow;
                _hasPending = false;
            }
            if (point.X == last.X && point.Y == last.Y) continue;
            last = point;
            Int32Rect? rect = null;
            try { rect = Probe(point, window); }
            catch (Exception ex) { Log.Write("Probe: " + ex.Message); }
            if (!_stopped) _found(point, rect);
        }
    }

    private static Int32Rect? Probe(Win32.POINT point, IntPtr window)
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        var path = new List<(AutomationElement Element, Rect Bounds, ControlType Type)>();
        using (request.Activate())
        {
            var current = AutomationElement.FromHandle(window);
            var at = new Point(point.X, point.Y);
            for (int depth = 0; depth < 40 && current != null; depth++)
            {
                path.Add((current, current.Cached.BoundingRectangle, current.Cached.ControlType));
                // Of the children under the point, the smallest is the most specific.
                AutomationElement? next = null;
                double nextArea = double.MaxValue;
                var children = current.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                foreach (AutomationElement child in children)
                {
                    var bounds = child.Cached.BoundingRectangle;
                    if (bounds.IsEmpty || !bounds.Contains(at)) continue;
                    double area = bounds.Width * bounds.Height;
                    if (area < nextArea)
                    {
                        nextArea = area;
                        next = child;
                    }
                }
                current = next;
            }
        }
        if (path.Count <= 1) return null;

        // From the deepest element, step up to something worth outlining.
        var windowBounds = path[0].Bounds;
        double windowArea = Math.Max(1, windowBounds.Width * windowBounds.Height);
        int index = path.Count - 1;
        for (int steps = 0; steps < 7 && index > 1; steps++)
        {
            var (_, bounds, type) = path[index];
            if (IsWholeThing(type, bounds)) break;
            var parent = path[index - 1];
            double area = Math.Max(1, bounds.Width * bounds.Height);
            double parentArea = parent.Bounds.Width * parent.Bounds.Height;
            // Stop before jumping to something huge (a whole page or window).
            if (parent.Bounds.IsEmpty || (parentArea > 12 * area && bounds.Height >= 22)) break;
            if (parentArea > windowArea * 0.6) break;
            index--;
        }
        var best = path[index].Bounds;
        if (best.IsEmpty || best.Width < 8 || best.Height < 8) return null;
        if (best.Width * best.Height > windowArea * 0.85) return null; // that's just the window
        return new Int32Rect((int)best.X, (int)best.Y, (int)best.Width, (int)best.Height);
    }

    /// <summary>Elements worth outlining as they are: controls, pictures, and text blocks of a couple of lines.</summary>
    private static bool IsWholeThing(ControlType type, Rect bounds)
    {
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
