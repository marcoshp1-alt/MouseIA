using System;
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
/// Full-monitor layer over a frozen screenshot: point at a window or drag an area,
/// then the card opens next to the selection.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private enum Mode { Pointing, Dragging, Card, Adding }

    private readonly AppController _app;
    private readonly AppSettings _settings;
    private readonly Canvas _root = new();
    private readonly Image _screen = new() { Stretch = Stretch.Fill };
    private readonly Path _dim;
    private readonly RectangleGeometry _hole = new() { RadiusX = 14, RadiusY = 14 };
    private readonly RectangleGeometry _full = new();
    private readonly Border _frame;
    private readonly Border _hint;
    private readonly DispatcherTimer _idle;
    private CardView? _card;
    private Mode _mode = Mode.Pointing;
    private Point _dragStart;
    private bool _mouseDown;
    private Rect _hover = Rect.Empty;
    private Rect _selection = Rect.Empty;
    private double _scale = 1;
    private bool _modalOpen;
    private bool _closing;

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
        Cursor = Cursors.Cross;
        AllowDrop = true;

        _screen.Source = shot.Image;
        RenderOptions.SetBitmapScalingMode(_screen, BitmapScalingMode.NearestNeighbor);
        _dim = new Path
        {
            Fill = Theme.B("#8C05070D"),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, _full, _hole),
            IsHitTestVisible = false,
        };
        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            BorderBrush = Theme.AnimatedAurora(),
            Effect = Theme.Glow(Theme.AccentA, 28, 0.85),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _hint = BuildHint();

        _root.Children.Add(_screen);
        _root.Children.Add(_dim);
        _root.Children.Add(_frame);
        _root.Children.Add(_hint);
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

    private void PlaceOnMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = Shot.Bounds;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Win32.SWP_SHOWWINDOW);
        uint dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi > 0) _scale = dpi / 96.0;
        Layout();
    }

    private void Layout()
    {
        double w = ScreenWidthDip, h = ScreenHeightDip;
        _root.Width = w;
        _root.Height = h;
        _screen.Width = w;
        _screen.Height = h;
        _full.Rect = new Rect(0, 0, w, h);
    }

    private void OnLoaded()
    {
        PlaceOnMonitor();
        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.ForceForeground(hwnd);
        Activate();
        Focus();
        Keyboard.Focus(this);
        _dim.Opacity = 0;
        Ui.Fade(_dim, 1, 180);
        var cursor = ToDip(Shot.Cursor);
        UpdateHover(cursor);
        PlaceHint(cursor);
        _idle.Start();
    }

    private Point ToDip(Point pixels) => new(pixels.X / _scale, pixels.Y / _scale);

    private Rect ToDip(Int32Rect r) => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

    // ---- hint ----

    private Border BuildHint()
    {
        var text = new StackPanel { Orientation = Orientation.Horizontal };
        text.Children.Add(Ui.Sparkle(13));
        var label = Ui.Label(Loc.T(_settings.TypeToAsk ? "hint_typing" : "hint"), 12.5);
        label.TextWrapping = TextWrapping.NoWrap;
        label.Margin = new Thickness(8, 0, 0, 1);
        text.Children.Add(label);
        return new Border
        {
            Background = Theme.B("#E0141825"),
            BorderBrush = Theme.GlassStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 7, 14, 8),
            Child = text,
            IsHitTestVisible = false,
            Effect = Theme.Shadow(),
        };
    }

    private void PlaceHint(Point cursor)
    {
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _hint.DesiredSize;
        double x = Math.Clamp(cursor.X + 22, 12, ScreenWidthDip - size.Width - 12);
        double y = Math.Clamp(cursor.Y + 26, 12, ScreenHeightDip - size.Height - 12);
        Canvas.SetLeft(_hint, x);
        Canvas.SetTop(_hint, y);
        Ui.PopIn(_hint);
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            Ui.Fade(_hint, 0, 400);
        };
        hide.Start();
    }

    // ---- pointing and dragging ----

    private void UpdateHover(Point point)
    {
        var px = new Point(point.X * _scale, point.Y * _scale);
        var window = Shot.Windows.FirstOrDefault(r => px.X >= r.X && px.Y >= r.Y && px.X < r.X + r.Width && px.Y < r.Y + r.Height);
        var rect = window.Width > 0 ? ToDip(window) : new Rect(0, 0, ScreenWidthDip, ScreenHeightDip);
        if (rect == _hover) return;
        _hover = rect;
        ShowFrame(rect, animate: true);
    }

    private void ShowFrame(Rect rect, bool animate)
    {
        _frame.Visibility = Visibility.Visible;
        var inset = rect;
        if (rect.Width >= ScreenWidthDip - 1 && rect.Height >= ScreenHeightDip - 1) inset.Inflate(-3, -3);
        if (!animate)
        {
            SetFrame(inset);
            return;
        }
        // Glide between windows.
        var from = new Rect(Canvas.GetLeft(_frame), Canvas.GetTop(_frame), _frame.Width, _frame.Height);
        if (double.IsNaN(from.X) || double.IsNaN(from.Width) || from.Width <= 0)
        {
            SetFrame(inset);
            return;
        }
        _frameProxy?.BeginAnimation(RectProxy.ValueProperty, null);
        var animation = new RectAnimation(from, inset, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var proxy = new RectProxy(SetFrame);
        proxy.BeginAnimation(RectProxy.ValueProperty, animation);
        _frameProxy = proxy;
    }

    private RectProxy? _frameProxy;

    private void SetFrame(Rect rect)
    {
        Canvas.SetLeft(_frame, rect.X);
        Canvas.SetTop(_frame, rect.Y);
        _frame.Width = Math.Max(0, rect.Width);
        _frame.Height = Math.Max(0, rect.Height);
        _hole.Rect = rect;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _idle.Stop();
        _idle.Start();
        var point = e.GetPosition(_root);
        switch (_mode)
        {
            case Mode.Pointing:
            case Mode.Adding:
                if (_mouseDown && (Math.Abs(point.X - _dragStart.X) > 5 || Math.Abs(point.Y - _dragStart.Y) > 5))
                {
                    _mode = _mode == Mode.Adding ? Mode.Adding : Mode.Dragging;
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

    private bool _dragging;

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
        Ui.Fade(_hint, 0, 120);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_mouseDown) return;
        _mouseDown = false;
        ReleaseMouseCapture();
        var point = e.GetPosition(_root);
        Rect chosen;
        if (_dragging)
        {
            chosen = Normalize(_dragStart, point);
            if (chosen.Width < 8 || chosen.Height < 8) chosen = _hover;
        }
        else
        {
            chosen = _hover;
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
        SetFrame(rectDip);
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
        Cursor = Cursors.Arrow;
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
        Cursor = Cursors.Cross;
        _hover = Rect.Empty;
        UpdateHover(Mouse.GetPosition(_root));
    }

    private void BackToCard()
    {
        if (_card == null) return;
        _mode = Mode.Card;
        _card.Visibility = Visibility.Visible;
        Cursor = Cursors.Arrow;
        SetFrame(_selection);
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
        _hole.Rect = Rect.Empty;
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
        _hole.Rect = Rect.Empty;
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
