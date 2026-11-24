using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Native;
using MouseIA.Services;

namespace MouseIA.UI;

/// <summary>
/// Live translation: keeps a window translated while you use it. A see-through layer sits right over
/// the window (clicks pass through it); whenever the window's content changes, its text is read again
/// and only new pieces go to the AI, everything already seen comes from memory.
/// The layer is kept out of screen captures, so MouseIA never reads its own translation.
/// </summary>
internal sealed class LiveTranslation
{
    public static LiveTranslation? Current { get; private set; }
    public static event Action? StateChanged;

    /// <summary>Follows whatever window is in front (the "always on" setting).</summary>
    public static void StartFollowing(AppController app) => Begin(app, IntPtr.Zero, follow: true);

    /// <summary>Translates one window (started from a selection).</summary>
    public static void StartFor(AppController app, IntPtr window) => Begin(app, window, follow: false);

    public static void Stop()
    {
        Current?.Dispose();
        Current = null;
        StateChanged?.Invoke();
    }

    public bool Following => _follow;

    private static void Begin(AppController app, IntPtr window, bool follow)
    {
        Current?.Dispose();
        Current = new LiveTranslation(app, window, follow);
        StateChanged?.Invoke();
    }

    // ---- state ----

    private readonly AppController _app;
    private readonly bool _follow;
    private IntPtr _target;
    private Win32.RECT _rect;
    private readonly LayerWindow _layer = new();
    private readonly PillWindow _pill;
    private readonly DispatcherTimer _tick;
    private readonly CancellationTokenSource _cts = new();
    private bool _paused, _processing, _translating, _disposed;
    private ulong _lastSignature, _pendingSignature;
    private DateTime _lastProcessed = DateTime.MinValue, _retryAfter = DateTime.MinValue;
    private List<(OcrBox Box, Color Background, Color Foreground)> _blocks = new();

    /// <summary>Translations already made, shared by every window (text → translation).</summary>
    private static readonly Dictionary<string, string> Memory = new();
    private static string _memoryLanguage = "";

    private AppSettings Settings => _app.Settings;
    private LanguageInfo Target => Languages.Resolve(Settings.TranslateTo, CultureInfo.CurrentUICulture);

    private LiveTranslation(AppController app, IntPtr window, bool follow)
    {
        _app = app;
        _follow = follow;
        _target = IntPtr.Zero;
        _fixed = window;
        _pill = new PillWindow(TogglePause, () =>
        {
            // Closing the pill turns the always-on mode off too.
            if (_follow) _app.SetLiveTranslation(false);
            else Stop();
        });
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        Tick();
    }

    private readonly IntPtr _fixed;

    private void TogglePause()
    {
        _paused = !_paused;
        _pill.SetPaused(_paused);
        if (_paused) _layer.Clear();
        else _lastSignature = 0;
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _cts.Cancel();
        _layer.Close();
        _pill.Close();
    }

    // ---- following the window ----

    private bool IsOurs(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Win32.CurrentProcessId;
    }

    private void Tick()
    {
        if (_disposed) return;
        var target = _follow ? Win32.GetForegroundWindow() : _fixed;
        // Clicking our own pill (or MouseIA) doesn't change what's being translated.
        if (_follow && (target == IntPtr.Zero || IsOurs(target))) target = _target;
        if (!_follow && !Win32.IsWindow(_fixed))
        {
            Stop(); // the window was closed
            return;
        }
        if (target == IntPtr.Zero || !Win32.IsWindowVisible(target) || Win32.IsIconic(target) || Win32.IsCloaked(target) ||
            !Win32.VisibleBounds(target, out var rect) || rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 40)
        {
            _layer.Hide();
            _pill.Hide();
            return;
        }

        if (target != _target || !Same(rect, _rect))
        {
            _target = target;
            _rect = rect;
            _layer.Place(target, rect);
            _pill.Place(target, rect, Target);
            // A new window, or the old positions no longer match: read it again.
            _layer.Clear();
            _blocks.Clear();
            _lastSignature = 0;
            _pendingSignature = 0;
        }
        if (_paused || _processing) return;

        _processing = true;
        var area = new Int32Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var capturedFor = target;
        Task.Run(async () =>
        {
            try
            {
                var shot = ScreenCapture.Capture(area);
                ulong signature = Signature(shot);
                bool changed = signature != _lastSignature;
                bool stable = signature == _pendingSignature;
                // Read the window once its content has settled (or every 1.5 s if it keeps changing, like a video).
                bool due = changed && (stable || (DateTime.UtcNow - _lastProcessed).TotalSeconds > 1.5);
                _pendingSignature = signature;
                if (!due) return;
                var blocks = await OcrService.RecognizeBlocksAsync(shot);
                var pixels = ImageUtil.ToBgra(shot, out int w, out int h);
                var painted = blocks.Where(b => Prompts.IsTranslatable(b.Text) && b.Text.Trim().Length >= 2)
                    .Take(150)
                    .Select(b =>
                    {
                        var (bg, fg) = TextPaint.Colors(pixels, w, h, b);
                        return (b, bg, fg);
                    }).ToList();
                _lastSignature = signature;
                _lastProcessed = DateTime.UtcNow;
                await _layer.Dispatcher.InvokeAsync(() =>
                {
                    if (_disposed || capturedFor != _target) return;
                    _blocks = painted;
                    Redraw();
                    _ = TranslateMissingAsync();
                });
            }
            catch (Exception ex) { Log.Write("Live: " + ex.Message); }
            finally { _processing = false; }
        });
    }

    private static bool Same(Win32.RECT a, Win32.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>A rough fingerprint of the picture: a blinking caret or tiny noise doesn't count as a change.</summary>
    private static ulong Signature(BitmapSource shot)
    {
        var small = ImageUtil.Fit(shot, 72, allowUpscale: false);
        var px = ImageUtil.ToBgra(small, out _, out _);
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < px.Length; i += 4)
        {
            int value = (px[i] + px[i + 1] + px[i + 2]) / 3 >> 5;
            hash = (hash ^ (ulong)value) * 1099511628211UL;
        }
        return hash;
    }

    // ---- translating ----

    private static string Key(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private async Task TranslateMissingAsync()
    {
        if (_translating || _disposed || DateTime.UtcNow < _retryAfter) return;
        if (!Settings.HasAi)
        {
            _pill.SetStatus(Loc.T("needs_ai_short"), error: true);
            return;
        }
        var language = Target.Code;
        if (_memoryLanguage != language)
        {
            Memory.Clear();
            _memoryLanguage = language;
        }
        var missing = _blocks.Select(b => Key(b.Box.Text)).Distinct().Where(k => !Memory.ContainsKey(k)).Take(40).ToList();
        if (missing.Count == 0) return;
        _translating = true;
        _pill.SetStatus(Loc.T("live_translating"), error: false);
        try
        {
            var target = Target;
            var request = Prompts.TranslateBatchRequest(target, missing);
            var prepared = await Task.Run(() => AiSetup.PrepareAsync(Settings, Prompts.TranslateSystem(target),
                new[] { new ChatTurn(true, request) }, new List<string>(), Math.Clamp(missing.Sum(m => m.Length) * 3 + 200, 400, 8000),
                null, _cts.Token), _cts.Token);
            var result = await Task.Run(() => AiClient.StreamAsync(prepared.Request, _ => { }, _cts.Token), _cts.Token);
            var parsed = Prompts.ParseBatch(result.Text, missing.Count);
            for (int i = 0; i < missing.Count; i++)
                Memory[missing[i]] = string.IsNullOrWhiteSpace(parsed[i]) ? missing[i] : parsed[i]!.Trim();
            if (Memory.Count > 5000) Memory.Clear();
            _pill.SetStatus(null, error: false);
            Redraw();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = ex is AiException ai ? ai.Detail : ex.Message;
            _pill.SetStatus(message.Length > 60 ? message[..60] + "…" : message, error: true);
            _retryAfter = DateTime.UtcNow.AddSeconds(10);
        }
        finally { _translating = false; }
        // More new text may have shown up meanwhile.
        if (!_disposed && _blocks.Any(b => !Memory.ContainsKey(Key(b.Box.Text)))) _ = TranslateMissingAsync();
    }

    private void Redraw()
    {
        if (_disposed || _paused) return;
        double scale = _layer.Scale;
        var views = new List<UIElement>();
        foreach (var (box, bg, fg) in _blocks)
        {
            var key = Key(box.Text);
            if (!Memory.TryGetValue(key, out var translation)) continue;
            // Already in the chosen language: leave the original alone.
            if (string.Equals(Key(translation), key, StringComparison.OrdinalIgnoreCase)) continue;
            var area = new Rect(box.X / scale, box.Y / scale, box.Width / scale, box.Height / scale);
            views.Add(TextPaint.View(translation, area, box.Lines, bg, fg, _layer.ContentWidth, _layer.ContentHeight));
        }
        _layer.Show(views);
    }

    // ---- the see-through layer ----

    private sealed class LayerWindow : Window
    {
        private readonly Canvas _canvas = new();
        private IntPtr _hwnd;

        public LayerWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Left = -32000;
            Top = -32000;
            Width = 10;
            Height = 10;
            Content = _canvas;
            IsHitTestVisible = false;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
                ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
                if (!Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
                    Log.Write("Live layer: can't exclude from capture on this Windows version");
            };
            Show();
        }

        public double Scale => _hwnd == IntPtr.Zero ? 1 : Math.Max(1, Win32.GetDpiForWindow(_hwnd)) / 96.0;
        public double ContentWidth { get; private set; }
        public double ContentHeight { get; private set; }

        /// <summary>Sits exactly over the window and stays just above it (it belongs to that window).</summary>
        public void Place(IntPtr target, Win32.RECT rect)
        {
            if (_hwnd == IntPtr.Zero) return;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_HWNDPARENT, target);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOZORDER);
            ContentWidth = (rect.Right - rect.Left) / Scale;
            ContentHeight = (rect.Bottom - rect.Top) / Scale;
            _canvas.Width = ContentWidth;
            _canvas.Height = ContentHeight;
        }

        public void Show(List<UIElement> views)
        {
            _canvas.Children.Clear();
            foreach (var view in views) _canvas.Children.Add(view);
            if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        }

        public void Clear() => _canvas.Children.Clear();

        public new void Hide()
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
        }
    }

    // ---- the small control on the window ----

    private sealed class PillWindow : Window
    {
        private readonly TextBlock _label;
        private readonly TextBlock _status;
        private readonly IconButton _pause;
        private IntPtr _hwnd;
        private string _language = "";

        public PillWindow(Action pause, Action close)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.Font;
            Left = -32000;
            Top = -32000;

            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.B("#FF4D6D"), VerticalAlignment = VerticalAlignment.Center };
            _label = Ui.Label("", 12, Theme.Text, FontWeights.SemiBold);
            _label.Margin = new Thickness(7, 0, 6, 0);
            _label.VerticalAlignment = VerticalAlignment.Center;
            _status = Ui.Label("", 11.5, Theme.Muted);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(0, 0, 4, 0);
            _status.MaxWidth = 260;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            _pause = new IconButton(Theme.Glyph.Stop, Loc.T("live_pause"), 24, 10);
            _pause.Click += pause;
            var closeButton = new IconButton(Theme.Glyph.Close, Loc.T("live_stop"), 24, 10);
            closeButton.Click += close;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(dot);
            row.Children.Add(_label);
            row.Children.Add(_status);
            row.Children.Add(_pause);
            row.Children.Add(closeButton);
            Content = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Theme.B("#EE151925"),
                BorderBrush = Theme.FieldStroke,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 3, 4, 3),
                Child = row,
            };
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                var ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
                ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
                Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
                Win32.SetWindowDisplayAffinity(_hwnd, Win32.WDA_EXCLUDEFROMCAPTURE);
            };
            Show();
        }

        public void Place(IntPtr target, Win32.RECT rect, LanguageInfo language)
        {
            _language = language.Display(Loc.IsPortuguese).Split(" · ")[0];
            if (string.IsNullOrEmpty(_status.Text)) _label.Text = Loc.Format("live_label", _language);
            if (_hwnd == IntPtr.Zero) return;
            Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_HWNDPARENT, target);
            UpdateLayout();
            double scale = Math.Max(1, Win32.GetDpiForWindow(_hwnd)) / 96.0;
            int w = (int)Math.Ceiling(ActualWidth * scale);
            // Top centre of the window, just under its edge.
            int x = rect.Left + (rect.Right - rect.Left - w) / 2;
            int y = rect.Top + (int)(4 * scale);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW | Win32.SWP_NOZORDER | 0x0001 /* NOSIZE */);
            if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        }

        public void SetPaused(bool paused)
        {
            _pause.ToolTip = Loc.T(paused ? "live_resume" : "live_pause");
            _label.Text = paused ? Loc.T("live_paused") : Loc.Format("live_label", _language);
        }

        public void SetStatus(string? text, bool error)
        {
            _status.Text = text ?? "";
            _status.Foreground = error ? Theme.Danger : Theme.Muted;
        }

        public new void Hide()
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
        }
    }
}
