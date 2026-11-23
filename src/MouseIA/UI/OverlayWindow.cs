using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Native;
using MouseIA.Services;

namespace MouseIA.UI;

/// <summary>
/// The pointer layer over a frozen picture of the screen. A ripple spreads from the cursor,
/// the cursor turns into a glowing star, and whatever is under it (a paragraph, button,
/// image or window) is outlined in light. Click to pick it, drag to pick an area, or type.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private enum Mode { Pointing, Dragging, Card, Adding }

    private readonly AppController _app;
    private readonly AppSettings _settings;
    private readonly Canvas _root = new();
    private readonly Image _screen = new() { Stretch = Stretch.Fill };
    private readonly ScaleTransform _screenPulse = new(1, 1);
    private readonly Border _frame;
    private readonly Border _hint;
    private readonly Canvas _pointer;
    private readonly List<FrameworkElement> _trail = new();
    private readonly DispatcherTimer _idle;
    private ElementProbe? _probe;
    private CardView? _card;
    private Mode _mode = Mode.Pointing;
    private Point _dragStart;
    private Point _lastTrail;
    private bool _mouseDown;
    private bool _dragging;
    private bool _hintFollows = true;
    private Rect _hover = Rect.Empty;
    private Rect _selection = Rect.Empty;
    private double _scale = 1;
    private bool _modalOpen;
    private bool _closing;
    private RectProxy? _frameProxy;

    public ScreenShot Shot { get; }
    public double ScreenWidthDip => Shot.Bounds.Width / _scale;
    public double ScreenHeightDip => Shot.Bounds.Height / _scale;

    public OverlayWindow(AppController app, ScreenShot shot)
    {
        _app = app;
        _settings = app.Settings;
        Shot = shot;
        _scale = shot.Scale;

        Title = "MouseIA";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;
        Cursor = Cursors.None;
        AllowDrop = true;

        _screen.Source = shot.Image;
        _screen.RenderTransform = _screenPulse;
        RenderOptions.SetBitmapScalingMode(_screen, BitmapScalingMode.HighQuality);

        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Theme.PointerStroke,
            Background = Theme.B("#0E9FD0FF"),
            Effect = Theme.Glow(Theme.PointerGlow, 26, 0.95),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _hint = BuildHint();
        _pointer = BuildPointer();

        _root.Children.Add(_screen);
        _root.Children.Add(_frame);
        _root.Children.Add(_hint);
        _root.Children.Add(_pointer);
        Content = _root;

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        _idle.Tick += (_, _) => { if (_mode == Mode.Pointing) CloseOverlay(); };

        SourceInitialized += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) => OnLoaded();
        Deactivated += (_, _) => OnDeactivated();
        // WPF applies its own suggested size after a DPI change; put the window back afterwards.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceOnMonitor);
        Drop += (_, e) => OnFileDrop(e);
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void PlaceOnMonitor()
    {
        var b = Shot.Bounds;
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Win32.SWP_SHOWWINDOW);
        uint dpi = Win32.GetDpiForWindow(Handle);
        if (dpi > 0) _scale = dpi / 96.0;
        double w = ScreenWidthDip, h = ScreenHeightDip;
        _root.Width = w;
        _root.Height = h;
        _screen.Width = w;
        _screen.Height = h;
    }

    private void OnLoaded()
    {
        PlaceOnMonitor();
        Win32.ForceForeground(Handle);
        Activate();
        Focus();
        Keyboard.Focus(this);
        _probe = new ElementProbe(Handle, (point, rect) => Dispatcher.BeginInvoke(() => OnProbed(point, rect)));
        var cursor = ToDip(Shot.Cursor);
        MovePointer(cursor);
        Ripple(cursor);
        PlaceHint(cursor, animate: true);
        UpdateHover(cursor);
        var hideHint = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        hideHint.Tick += (_, _) =>
        {
            hideHint.Stop();
            _hintFollows = false;
            Ui.Fade(_hint, 0, 450);
        };
        hideHint.Start();
        _idle.Start();
    }

    private Point ToDip(Point pixels) => new(pixels.X / _scale, pixels.Y / _scale);

    private Rect ToDip(Int32Rect r) => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

    // ---- the star pointer ----

    private static readonly Geometry Star = Geometry.Parse(
        "M 0,-11 C 1.2,-2.4 2.4,-1.2 11,0 C 2.4,1.2 1.2,2.4 0,11 C -1.2,2.4 -2.4,1.2 -11,0 C -2.4,-1.2 -1.2,-2.4 0,-11 Z");

    private static Path StarShape(double size, double opacity)
    {
        var star = new Path
        {
            Data = Star,
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size,
            Opacity = opacity,
            IsHitTestVisible = false,
        };
        return star;
    }

    private Canvas BuildPointer()
    {
        var canvas = new Canvas { IsHitTestVisible = false, Effect = Theme.Glow(Theme.PointerGlow, 16, 1) };
        var main = StarShape(24, 1);
        Canvas.SetLeft(main, -12);
        Canvas.SetTop(main, -12);
        var small = StarShape(10, 0.9);
        Canvas.SetLeft(small, 9);
        Canvas.SetTop(small, -17);
        canvas.Children.Add(main);
        canvas.Children.Add(small);
        // A gentle twinkle.
        var twinkle = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        small.BeginAnimation(OpacityProperty, twinkle);
        return canvas;
    }

    private void MovePointer(Point point)
    {
        Canvas.SetLeft(_pointer, point.X);
        Canvas.SetTop(_pointer, point.Y);
        if (_hintFollows && _hint.Opacity > 0) PlaceHint(point, animate: false);
        // Leave a short trail of sparkles when moving quickly.
        if ((point - _lastTrail).Length > 26)
        {
            _lastTrail = point;
            SpawnSparkle(point);
        }
    }

    private void SpawnSparkle(Point point)
    {
        if (_trail.Count > 14) return;
        var size = 5 + Random.Shared.NextDouble() * 6;
        var sparkle = StarShape(size, 0.9);
        sparkle.Effect = Theme.Glow(Theme.PointerGlow, 8, 1);
        Canvas.SetLeft(sparkle, point.X - size / 2 + Random.Shared.Next(-8, 9));
        Canvas.SetTop(sparkle, point.Y - size / 2 + Random.Shared.Next(-8, 9));
        _root.Children.Insert(_root.Children.IndexOf(_pointer), sparkle);
        _trail.Add(sparkle);
        Ui.Fade(sparkle, 0, 520, () =>
        {
            _root.Children.Remove(sparkle);
            _trail.Remove(sparkle);
        });
    }

    private void SetPointerVisible(bool visible)
    {
        _pointer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Cursor = visible ? Cursors.None : Cursors.Arrow;
    }

    // ---- ripple ----

    /// <summary>A ring of light spreads across the screen from the pointer, and the picture breathes with it.</summary>
    private void Ripple(Point center)
    {
        double reach = Math.Sqrt(ScreenWidthDip * ScreenWidthDip + ScreenHeightDip * ScreenHeightDip);
        var rings = new[]
        {
            new Ellipse
            {
                Stroke = Theme.PointerStroke,
                StrokeThickness = 3,
                Effect = Theme.Glow(Theme.PointerGlow, 30, 1),
                IsHitTestVisible = false,
            },
            new Ellipse
            {
                Stroke = Theme.B("#669FD0FF"),
                StrokeThickness = 14,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 18 },
                IsHitTestVisible = false,
            },
        };
        var flash = new Ellipse
        {
            Fill = new RadialGradientBrush(Theme.C("#88CFE6FF"), Theme.C("#00CFE6FF")),
            Width = 260,
            Height = 260,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(flash, center.X - 130);
        Canvas.SetTop(flash, center.Y - 130);
        int insertAt = _root.Children.IndexOf(_frame);
        _root.Children.Insert(insertAt, flash);
        foreach (var ring in rings) _root.Children.Insert(insertAt, ring);
        Ui.Fade(flash, 0, 600, () => _root.Children.Remove(flash));

        _screenPulse.CenterX = center.X;
        _screenPulse.CenterY = center.Y;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.012, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.997, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        _screenPulse.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        _screenPulse.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);

        var started = DateTime.UtcNow;
        const double duration = 950;
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / duration);
            double eased = 1 - Math.Pow(1 - t, 3);
            for (int i = 0; i < rings.Length; i++)
            {
                double radius = 10 + reach * eased * (i == 0 ? 1 : 0.92);
                rings[i].Width = rings[i].Height = radius * 2;
                Canvas.SetLeft(rings[i], center.X - radius);
                Canvas.SetTop(rings[i], center.Y - radius);
                rings[i].Opacity = (1 - t) * (i == 0 ? 0.95 : 0.6);
            }
            if (t >= 1)
            {
                CompositionTarget.Rendering -= frame;
                foreach (var ring in rings) _root.Children.Remove(ring);
            }
        };
        CompositionTarget.Rendering += frame;
    }

    // ---- hint ----

    private Border BuildHint()
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        var spark = Ui.Label("✦", 13, Brushes.White);
        text.Children.Add(spark);
        var label = Ui.Label(Loc.T(_settings.TypeToAsk ? "hint_typing" : "hint"), 12.5, Brushes.White);
        label.TextWrapping = TextWrapping.NoWrap;
        label.Margin = new Thickness(8, 0, 0, 1);
        text.Children.Add(label);
        return new Border
        {
            Background = Theme.B("#B8162030"),
            BorderBrush = Theme.B("#88CFE6FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 7, 14, 8),
            Child = text,
            IsHitTestVisible = false,
            Effect = Theme.Glow(Theme.PointerGlow, 20, 0.7),
        };
    }

    private void PlaceHint(Point cursor, bool animate)
    {
        if (animate) _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _hint.DesiredSize;
        double x = Math.Clamp(cursor.X + 26, 12, Math.Max(12, ScreenWidthDip - size.Width - 12));
        double y = Math.Clamp(cursor.Y + 14, 12, Math.Max(12, ScreenHeightDip - size.Height - 12));
        Canvas.SetLeft(_hint, x);
        Canvas.SetTop(_hint, y);
        if (animate) Ui.PopIn(_hint, 0.9);
    }

    // ---- outlining ----

    private void UpdateHover(Point point)
    {
        // Keep the element outline while the pointer stays on it.
        if (!_hover.IsEmpty && _hover.Contains(point) && _hoverIsElement) return;
        var px = new Point(point.X * _scale, point.Y * _scale);
        var window = Shot.Windows.FirstOrDefault(r => px.X >= r.X && px.Y >= r.Y && px.X < r.X + r.Width && px.Y < r.Y + r.Height);
        var rect = window.Width > 0 ? ToDip(window) : new Rect(0, 0, ScreenWidthDip, ScreenHeightDip);
        _hoverIsElement = false;
        SetHover(rect);
        var screen = new Win32.POINT { X = (int)px.X + Shot.Bounds.X, Y = (int)px.Y + Shot.Bounds.Y };
        _probe?.Request(screen);
    }

    private bool _hoverIsElement;

    private void OnProbed(Win32.POINT point, Int32Rect? rect)
    {
        if (_mode is not (Mode.Pointing or Mode.Adding) || _dragging || rect == null) return;
        var r = rect.Value;
        var local = new Int32Rect(r.X - Shot.Bounds.X, r.Y - Shot.Bounds.Y, r.Width, r.Height);
        var dip = ToDip(local);
        dip.Intersect(new Rect(0, 0, ScreenWidthDip, ScreenHeightDip));
        if (dip.IsEmpty) return;
        var mouse = Mouse.GetPosition(_root);
        if (!dip.Contains(mouse)) return;
        _hoverIsElement = true;
        var padded = dip;
        padded.Inflate(4, 3);
        SetHover(padded);
    }

    private void SetHover(Rect rect)
    {
        if (rect == _hover) return;
        _hover = rect;
        ShowFrame(rect, animate: true);
    }

    private void ShowFrame(Rect rect, bool animate)
    {
        _frame.Visibility = Visibility.Visible;
        var inset = rect;
        if (rect.Width >= ScreenWidthDip - 1 && rect.Height >= ScreenHeightDip - 1) inset.Inflate(-4, -4);
        var from = new Rect(Canvas.GetLeft(_frame), Canvas.GetTop(_frame), _frame.Width, _frame.Height);
        _frameProxy?.BeginAnimation(RectProxy.ValueProperty, null);
        if (!animate || double.IsNaN(from.X) || double.IsNaN(from.Width) || from.Width <= 0)
        {
            SetFrame(inset);
            return;
        }
        var animation = new RectAnimation(from, inset, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var proxy = new RectProxy(SetFrame);
        proxy.BeginAnimation(RectProxy.ValueProperty, animation);
        _frameProxy = proxy;
    }

    private void SetFrame(Rect rect)
    {
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
        _frame.Width = Math.Max(0, rect.Width);
        _frame.Height = Math.Max(0, rect.Height);
        _frame.CornerRadius = new CornerRadius(Math.Min(12, Math.Min(rect.Width, rect.Height) / 3));
    }

    // ---- mouse ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _idle.Stop();
        _idle.Start();
        var point = e.GetPosition(_root);
        if (_pointer.Visibility == Visibility.Visible) MovePointer(point);
        switch (_mode)
        {
            case Mode.Pointing:
            case Mode.Adding:
                if (_mouseDown && !_dragging && (Math.Abs(point.X - _dragStart.X) > 5 || Math.Abs(point.Y - _dragStart.Y) > 5))
                {
                    if (_mode == Mode.Pointing) _mode = Mode.Dragging;
                    _dragging = true;
                    _frameProxy?.BeginAnimation(RectProxy.ValueProperty, null);
                }
                if (_dragging) SetFrame(Normalize(_dragStart, point));
                else UpdateHover(point);
                break;
            case Mode.Dragging:
                SetFrame(Normalize(_dragStart, point));
                break;
        }
    }

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_mode is not (Mode.Pointing or Mode.Adding)) return;
        _mouseDown = true;
        _dragging = false;
        _dragStart = e.GetPosition(_root);
        CaptureMouse();
        _hintFollows = false;
        Ui.Fade(_hint, 0, 120);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_mouseDown) return;
        _mouseDown = false;
        ReleaseMouseCapture();
        var point = e.GetPosition(_root);
        Rect chosen = _hover;
        if (_dragging)
        {
            var dragged = Normalize(_dragStart, point);
            if (dragged.Width >= 8 && dragged.Height >= 8) chosen = dragged;
        }
        _dragging = false;
        if (_mode == Mode.Dragging) _mode = Mode.Pointing;
        Select(chosen);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (e.Handled) return;
        if (_mode == Mode.Adding) { BackToCard(); return; }
        if (_card != null && _card.IsMouseOver) return;
        CloseOverlay();
    }

    private void Select(Rect rectDip)
    {
        rectDip.Intersect(new Rect(0, 0, ScreenWidthDip, ScreenHeightDip));
        if (rectDip.IsEmpty || rectDip.Width < 2 || rectDip.Height < 2) return;
        var pixels = new Int32Rect(
            (int)Math.Round(rectDip.X * _scale), (int)Math.Round(rectDip.Y * _scale),
            (int)Math.Round(rectDip.Width * _scale), (int)Math.Round(rectDip.Height * _scale));
        var image = ImageUtil.Crop(Shot.Image, pixels);
        var attachment = new Attachment { Name = Loc.T("selection"), Image = image };
        bool adding = _mode == Mode.Adding;
        _selection = rectDip;
        ShowFrame(rectDip, animate: false);
        if (adding && _card != null)
        {
            _card.AddAttachment(attachment);
            BackToCard();
            return;
        }
        OpenCard(rectDip, attachment, null);
    }

    // ---- card ----

    private void OpenCard(Rect anchor, Attachment? attachment, string? typed)
    {
        _mode = Mode.Card;
        _idle.Stop();
        SetPointerVisible(false);
        _hintFollows = false;
        Ui.Fade(_hint, 0, 100);
        _card = new CardView(this, _settings);
        if (attachment != null) _card.AddAttachment(attachment);
        _root.Children.Add(_card);
        _card.Measure(new Size(CardView.CardWidth, double.PositiveInfinity));
        PositionCard(anchor);
        _card.SizeChanged += (_, _) => PositionCard(_selection.IsEmpty ? anchor : _selection);
        Ui.PopIn(_card);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _card?.FocusPrompt(typed));
    }

    private void PositionCard(Rect anchor)
    {
        if (_card == null) return;
        double w = CardView.CardWidth;
        double h = _card.ActualHeight > 0 ? _card.ActualHeight : _card.DesiredSize.Height;
        double sw = ScreenWidthDip, sh = ScreenHeightDip;
        const double gap = 14, margin = 12;
        double x, y;
        if (anchor.Bottom + gap + h <= sh - margin)
        {
            x = anchor.Left; y = anchor.Bottom + gap;              // below
        }
        else if (anchor.Top - gap - h >= margin)
        {
            x = anchor.Left; y = anchor.Top - gap - h;             // above
        }
        else if (anchor.Right + gap + w <= sw - margin)
        {
            x = anchor.Right + gap; y = anchor.Top;                // right
        }
        else if (anchor.Left - gap - w >= margin)
        {
            x = anchor.Left - gap - w; y = anchor.Top;             // left
        }
        else
        {
            x = anchor.Left + (anchor.Width - w) / 2;              // inside, near the bottom
            y = anchor.Bottom - h - 24;
        }
        x = Math.Clamp(x, margin, Math.Max(margin, sw - w - margin));
        y = Math.Clamp(y, margin, Math.Max(margin, sh - h - margin));
        _card.MoveTo(Math.Round(x), Math.Round(y));
    }

    internal void BeginAddSelection()
    {
        if (_card == null) return;
        _mode = Mode.Adding;
        _card.Visibility = Visibility.Hidden;
        SetPointerVisible(true);
        _hover = Rect.Empty;
        var point = Mouse.GetPosition(_root);
        MovePointer(point);
        UpdateHover(point);
    }

    private void BackToCard()
    {
        if (_card == null) return;
        _mode = Mode.Card;
        _card.Visibility = Visibility.Visible;
        SetPointerVisible(false);
        ShowFrame(_selection, animate: true);
        PositionCard(_selection);
        _card.FocusPrompt();
    }

    private void OnFileDrop(DragEventArgs e)
    {
        if (_card != null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var point = e.GetPosition(_root);
        var anchor = new Rect(point.X, point.Y, 1, 1);
        _selection = anchor;
        _frame.Visibility = Visibility.Collapsed;
        OpenCard(anchor, null, null);
        if (_card != null) _ = _card.ImportFilesAsync(files);
    }

    // ---- keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.Escape) return;
        if (_card?.HasPopupOpen == true) return;
        e.Handled = true;
        if (_mode == Mode.Adding) BackToCard();
        else CloseOverlay();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_mode != Mode.Pointing || !_settings.TypeToAsk || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        e.Handled = true;
        // Typing without a selection: ask about nothing in particular, at the pointer.
        var cursor = Mouse.GetPosition(_root);
        _frame.Visibility = Visibility.Collapsed;
        _selection = new Rect(cursor.X, cursor.Y, 1, 1);
        OpenCard(_selection, null, e.Text);
    }

    // ---- lifetime ----

    internal void SetModalOpen(bool open) => _modalOpen = open;

    private void OnDeactivated()
    {
        if (_modalOpen || _closing) return;
        // Give system flyouts (voice typing, emoji panel) a moment; they don't count as leaving.
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_modalOpen || _closing || IsActive) return;
            var foreground = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Win32.CurrentProcessId) return;
            var cls = Win32.WindowClass(foreground);
            if (cls is "Windows.UI.Core.CoreWindow" or "Shell_InputSwitchTopLevelWindow" or "Xaml_WindowedPopupClass") return;
            if (_card?.IsBusy == true) return;
            CloseOverlay();
        };
        check.Start();
    }

    internal void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        _idle.Stop();
        _probe?.Dispose();
        _card?.CancelAll();
        Ui.Fade(_root, 0, 120, Close);
    }

    internal void OpenSettingsAndClose()
    {
        CloseOverlay();
        _app.OpenSettings();
    }

    internal void SaveSettingsQuietly() => _app.SaveSettings();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _probe?.Dispose();
        _card?.CancelAll();
        _app.OverlayClosed(this);
    }
}

/// <summary>Lets a RectAnimation drive any setter.</summary>
internal sealed class RectProxy : Animatable
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(Rect), typeof(RectProxy),
        new PropertyMetadata(Rect.Empty, (d, e) => ((RectProxy)d)._apply((Rect)e.NewValue)));

    private readonly Action<Rect> _apply;

    public RectProxy(Action<Rect> apply) => _apply = apply;

    public Rect Value
    {
        get => (Rect)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new RectProxy(_apply);
}
