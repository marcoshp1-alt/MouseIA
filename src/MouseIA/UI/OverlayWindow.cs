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
using System.Windows.Media.Imaging;
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
    private readonly DispatcherTimer _idle;
    private ElementProbe? _probe;
    private CardView? _card;
    private Mode _mode = Mode.Pointing;
    private Point _dragStart;
    private bool _mouseDown;
    private bool _rightDown;
    private bool _dragging;
    private readonly List<FrameworkElement> _layers = new();
    private bool _acted;

    /// <summary>Nothing has been selected, translated or opened on this monitor.</summary>
    internal bool Untouched => _mode == Mode.Pointing && !_acted && _layers.Count == 0 && _card == null;
    private IntPtr _previousForeground;
    private DateTime _openedAt = DateTime.UtcNow;
    private bool _hintFollows = true;
    private Rect _hover = Rect.Empty;
    private Rect _selection = Rect.Empty;
    private double _scale = 1;
    private bool _modalOpen;
    private bool _closing;
    private RectProxy? _frameProxy;

    public ScreenShot Shot { get; }

    private BitmapSource? _frosted;

    /// <summary>The screen, pre-blurred once, for glass surfaces.</summary>
    public BitmapSource Frosted => _frosted ??= ImageUtil.Frosted(Shot.Image);
    public double ScreenWidthDip => Shot.Bounds.Width / _scale;
    public double ScreenHeightDip => Shot.Bounds.Height / _scale;

    private readonly bool _primary;

    /// <param name="primary">The overlay that takes the keyboard and shows the opening ripple.</param>
    public OverlayWindow(AppController app, ScreenShot shot, bool primary)
    {
        _primary = primary;
        _app = app;
        _settings = app.Settings;
        Shot = shot;
        _scale = shot.Scale;

        Title = "MouseIA";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        // Behind everything is the screen itself, so no moment of the window can ever show black.
        var screenBrush = new ImageBrush(shot.Image) { Stretch = Stretch.Fill };
        if (screenBrush.CanFreeze) screenBrush.Freeze();
        Background = screenBrush;
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

        // The glow is drawn with soft outer rings rather than a blur effect, so it moves at full frame rate.
        _frame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Theme.PointerStroke,
            Background = Theme.B("#0E9FD0FF"),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = GlowRings(),
        };
        _hint = BuildHint();
        _pointer = BuildPointer();

        _root.Children.Add(_screen);
        _root.Children.Add(_frame);
        _root.Children.Add(_hint);
        _root.Children.Add(_pointer);
        Content = _root;

        _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        // Only an untouched pointer goes away by itself; never one with a selection or translations on it.
        // An untouched pointer goes away by itself, but only if no monitor has anything going on:
        // a quiet monitor must never close a translation or card on another one.
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (Untouched && _app.AllOverlaysUntouched) CloseOverlay();
        };

        SourceInitialized += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) => OnLoaded();
        Deactivated += (_, _) => OnDeactivated();
        // WPF applies its own suggested size after a DPI change; put the window back afterwards.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceOnMonitor);
        Drop += (_, e) => OnFileDrop(e);
        MouseEnter += (_, _) =>
        {
            if (_mode is Mode.Pointing or Mode.Adding)
            {
                SetPointerVisible(true);
                // The keyboard follows the pointer between monitors.
                if (!IsActive && !_closing) Activate();
            }
        };
        MouseLeave += (_, _) => { if (_mode is Mode.Pointing or Mode.Adding) _pointer.Visibility = Visibility.Collapsed; };
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private static Grid GlowRings()
    {
        var grid = new Grid { IsHitTestVisible = false };
        foreach (var (inset, thickness, color) in new[] { (-4.0, 3.0, "#665AA9FF"), (-9.0, 5.0, "#305AA9FF"), (-16.0, 7.0, "#145AA9FF") })
        {
            grid.Children.Add(new Border
            {
                Margin = new Thickness(inset),
                BorderThickness = new Thickness(thickness),
                CornerRadius = new CornerRadius(14 - inset),
                BorderBrush = Theme.B(color),
            });
        }
        return grid;
    }

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
        _probe = new ElementProbe((point, rect) => Dispatcher.BeginInvoke(() => OnProbed(point, rect)));
        // Prepare the frosted glass in the background.
        var image = Shot.Image;
        System.Threading.Tasks.Task.Run(() => ImageUtil.Frosted(image)).ContinueWith(t =>
        {
            if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion) _frosted ??= t.Result;
        });
        if (_primary && _settings.Provider == ProviderIds.Local && _settings.UsesModelFile)
        {
            // Wake the local AI while you're still choosing what to select: the first answer comes sooner.
            var settings = _settings;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await LocalEngine.EnsureRunningAsync(settings, null, System.Threading.CancellationToken.None); }
                catch (Exception ex) { Log.Write("Engine warm-up: " + ex.Message); }
            });
        }
        if (_primary)
        {
            _previousForeground = Win32.GetForegroundWindow();
            _openedAt = DateTime.UtcNow;
            Win32.ForceForeground(Handle);
            Activate();
            Focus();
            Keyboard.Focus(this);
        }
        if (!Shot.HasCursor)
        {
            // Another monitor has the pointer: wait quietly until it comes here.
            _pointer.Visibility = Visibility.Collapsed;
            _hint.Visibility = Visibility.Collapsed;
            _idle.Start();
            return;
        }
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

    /// <summary>A classic arrow pointer; its tip is the hot spot at (0, 0).</summary>
    private static readonly Geometry Arrow = Geometry.Parse(
        "M 0,0 L 0,21 L 5.2,16.2 L 8.6,24 L 12,22.6 L 8.7,14.9 L 15.6,14.9 Z");

    private Canvas BuildPointer()
    {
        var canvas = new Canvas { IsHitTestVisible = false, CacheMode = new BitmapCache { RenderAtScale = 1.5 } };
        var halo = new Path
        {
            Data = Arrow,
            Fill = Theme.B("#995AA9FF"),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14 },
            RenderTransform = new ScaleTransform(1.15, 1.15),
        };
        var arrow = new Path
        {
            Data = Arrow,
            Fill = Brushes.White,
            Stroke = Theme.B("#FF1B2A44"),
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = Theme.Glow(Theme.PointerGlow, 18, 1),
        };
        canvas.Children.Add(halo);
        canvas.Children.Add(arrow);
        halo.Opacity = 0.85;
        return canvas;
    }

    private void MovePointer(Point point)
    {
        Canvas.SetLeft(_pointer, point.X);
        Canvas.SetTop(_pointer, point.Y);
        if (_hintFollows && _hint.Opacity > 0) PlaceHint(point, animate: false);
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
        int insertAt = _root.Children.IndexOf(_frame);
        // Rings are fixed-size ellipses filled with a ring-shaped gradient and scaled on the GPU.
        foreach (var (delay, strength, width) in new[] { (0, 1.0, 0.035), (90, 0.55, 0.08) })
        {
            var ring = new Ellipse
            {
                Width = reach * 2,
                Height = reach * 2,
                IsHitTestVisible = false,
                Fill = RingBrush(strength, width),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.01, 0.01),
                Opacity = 0,
            };
            Canvas.SetLeft(ring, center.X - reach);
            Canvas.SetTop(ring, center.Y - reach);
            _root.Children.Insert(insertAt, ring);
            var begin = TimeSpan.FromMilliseconds(delay);
            var grow = new DoubleAnimation(0.01, 1, TimeSpan.FromMilliseconds(1000))
            {
                BeginTime = begin,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            fade.Completed += (_, _) => _root.Children.Remove(ring);
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, fade);
        }

        var flash = new Ellipse
        {
            Fill = new RadialGradientBrush(Theme.C("#88CFE6FF"), Theme.C("#00CFE6FF")),
            Width = 260,
            Height = 260,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(flash, center.X - 130);
        Canvas.SetTop(flash, center.Y - 130);
        _root.Children.Insert(insertAt, flash);
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
    }

    /// <summary>A soft bright band near the edge of a circle, fading in and out.</summary>
    private static Brush RingBrush(double strength, double width)
    {
        byte a = (byte)(255 * strength);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0x9F, 0xD0, 0xFF), 0),
                new GradientStop(Color.FromArgb(0, 0x9F, 0xD0, 0xFF), 1 - width * 2),
                new GradientStop(Color.FromArgb((byte)(a * 0.55), 0x5A, 0xA9, 0xFF), 1 - width),
                new GradientStop(Color.FromArgb(a, 0xF2, 0xF8, 0xFF), 1 - width * 0.35),
                new GradientStop(Color.FromArgb(0, 0xF2, 0xF8, 0xFF), 1),
            },
        };
        brush.Freeze();
        return brush;
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
            CacheMode = new BitmapCache(),
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
        // With translations on screen, nothing is outlined until you drag a new selection.
        if (_layers.Count > 0)
        {
            if (!_dragging) _frame.Visibility = Visibility.Collapsed;
            return;
        }
        // Keep the element outline while the pointer stays on it.
        if (!_hover.IsEmpty && _hover.Contains(point) && _hoverIsElement) return;
        var px = new Point(point.X * _scale, point.Y * _scale);
        int index = -1;
        for (int i = 0; i < Shot.Windows.Count; i++)
        {
            var r = Shot.Windows[i];
            if (px.X >= r.X && px.Y >= r.Y && px.X < r.X + r.Width && px.Y < r.Y + r.Height) { index = i; break; }
        }
        var rect = index >= 0 ? ToDip(Shot.Windows[index]) : new Rect(0, 0, ScreenWidthDip, ScreenHeightDip);
        _hoverIsElement = false;
        SetHover(rect);
        if (index >= 0 && index < Shot.Handles.Count)
        {
            var screen = new Win32.POINT { X = (int)px.X + Shot.Bounds.X, Y = (int)px.Y + Shot.Bounds.Y };
            _probe?.Request(screen, Shot.Handles[index]);
        }
    }

    private bool _hoverIsElement;

    private void OnProbed(Win32.POINT point, Int32Rect? rect)
    {
        if (_mode is not (Mode.Pointing or Mode.Adding) || _dragging || rect == null || _layers.Count > 0) return;
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
        if (rect == _hover && _frame.Visibility == Visibility.Visible) return;
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
        // Every new selection shows the frame again (it hides while a translation is drawn).
        _frame.Visibility = Visibility.Visible;
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
                if ((_mouseDown || _rightDown) && !_dragging && (Math.Abs(point.X - _dragStart.X) > 5 || Math.Abs(point.Y - _dragStart.Y) > 5))
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
        if (_mode == Mode.Adding) Select(chosen);
        else RunAction(_settings.LeftDragAction, chosen);
    }

    /// <summary>Does what the user set for a mouse gesture on an area of the screen.</summary>
    private void RunAction(string action, Rect area)
    {
        _acted = true;
        _idle.Stop();
        area.Intersect(new Rect(0, 0, ScreenWidthDip, ScreenHeightDip));
        if (area.IsEmpty || area.Width < 2 || area.Height < 2) return;
        switch (action)
        {
            case "ask":
                Select(area);
                break;
            case "translate":
            case "translate_speak":
                TranslateHere(area, action == "translate_speak" || _settings.SpeakOverlayTranslation);
                break;
            case "copy_text":
                _ = CopyTextHereAsync(area);
                break;
            case "qr":
                _ = ShowCodesAsync(Crop(area), area, announceEmpty: true);
                break;
            case "lens":
                try
                {
                    LensService.Search(Crop(area));
                    Toast(area, Loc.T("lens_opened"));
                }
                catch (Exception ex) { Log.Write("Lens: " + ex.Message); }
                break;
        }
    }

    private BitmapSource Crop(Rect areaDip) => ImageUtil.Crop(Shot.Image, new Int32Rect(
        (int)Math.Round(areaDip.X * _scale), (int)Math.Round(areaDip.Y * _scale),
        (int)Math.Round(areaDip.Width * _scale), (int)Math.Round(areaDip.Height * _scale)));

    private void TranslateHere(Rect area, bool speak) => _ = TranslateHereAsync(area, speak);

    /// <summary>
    /// Translates in place, like Circle to Search: every piece of text in the area is rewritten
    /// right where it is, over its own background colour.
    /// </summary>
    private async System.Threading.Tasks.Task TranslateHereAsync(Rect area, bool speak)
    {
        _frame.Visibility = Visibility.Collapsed;
        // A little margin around the area, so the background around the text can be sampled.
        const double margin = 6;
        var padded = new Rect(area.X - margin, area.Y - margin, area.Width + margin * 2, area.Height + margin * 2);
        padded.Intersect(new Rect(0, 0, ScreenWidthDip, ScreenHeightDip));
        var pixelRect = new Int32Rect((int)Math.Round(padded.X * _scale), (int)Math.Round(padded.Y * _scale),
            (int)Math.Round(padded.Width * _scale), (int)Math.Round(padded.Height * _scale));
        pixelRect.Width = Math.Min(pixelRect.Width, Shot.Image.PixelWidth - pixelRect.X);
        pixelRect.Height = Math.Min(pixelRect.Height, Shot.Image.PixelHeight - pixelRect.Y);
        var image = ImageUtil.Crop(Shot.Image, pixelRect);
        var imageArea = new Rect(pixelRect.X / _scale, pixelRect.Y / _scale, pixelRect.Width / _scale, pixelRect.Height / _scale);

        List<OcrBox> blocks;
        try { blocks = await System.Threading.Tasks.Task.Run(() => OcrService.RecognizeBlocksAsync(image)); }
        catch (Exception) { blocks = new List<OcrBox>(); }
        if (_closing) return;

        if (blocks.Any(b => Prompts.IsTranslatable(b.Text)))
        {
            var inline = new InlineTranslation(this, _settings, image, imageArea, _scale, blocks, area,
                speak || _settings.SpeakOverlayTranslation);
            _layers.Add(inline);
            inline.Removed += () => _layers.Remove(inline);
            _root.Children.Insert(_root.Children.IndexOf(_hint), inline);
            return;
        }
        // No readable text (a photo, a drawing): the AI looks at the picture instead.
        AddLayer(new TranslationLayer(this, _settings, Crop(area), area, speak));
    }

    private void AddLayer(TranslationLayer layer)
    {
        _layers.Add(layer);
        layer.Removed += () => _layers.Remove(layer);
        _root.Children.Insert(_root.Children.IndexOf(_hint), layer);
    }

    private async System.Threading.Tasks.Task CopyTextHereAsync(Rect area)
    {
        var image = Crop(area);
        string text;
        try { text = await System.Threading.Tasks.Task.Run(() => OcrService.RecognizeAsync(image)); }
        catch (Exception) { text = ""; }
        if (text.Length == 0)
        {
            Toast(area, Loc.T("no_text_found"));
            return;
        }
        Clipboard2.SetText(text);
        Toast(area, Loc.T("text_copied"));
    }

    /// <summary>A small glass note that appears over an area and fades away.</summary>
    private void Toast(Rect area, string message)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Icon(Theme.Glyph.Check, 12, Brushes.White));
        var label = Ui.Label(message, 12.5, Brushes.White);
        label.Margin = new Thickness(8, 0, 0, 1);
        label.TextWrapping = TextWrapping.NoWrap;
        row.Children.Add(label);
        var toast = new Border
        {
            Background = Theme.B("#D8162030"),
            BorderBrush = Theme.B("#88CFE6FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 14, 7),
            Child = row,
            Effect = Theme.Glow(Theme.PointerGlow, 18, 0.7),
            IsHitTestVisible = false,
        };
        toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(toast, Math.Clamp(area.X + (area.Width - toast.DesiredSize.Width) / 2, 8, ScreenWidthDip - toast.DesiredSize.Width - 8));
        Canvas.SetTop(toast, Math.Clamp(area.Y + (area.Height - toast.DesiredSize.Height) / 2, 8, ScreenHeightDip - toast.DesiredSize.Height - 8));
        _root.Children.Add(toast);
        Ui.PopIn(toast, 0.9);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Ui.Fade(toast, 0, 300, () => _root.Children.Remove(toast));
        };
        timer.Start();
    }

    // ---- codes on screen ----

    /// <summary>Puts an "open link" button right on top of each code found in the area (like circle to search).</summary>
    internal async System.Threading.Tasks.Task ShowCodesAsync(BitmapSource image, Rect area, bool announceEmpty,
        System.Threading.Tasks.Task<IReadOnlyList<CodeResult>>? pending = null)
    {
        IReadOnlyList<CodeResult> codes;
        try { codes = await (pending ?? System.Threading.Tasks.Task.Run(() => QrService.Decode(image))); }
        catch (Exception) { codes = Array.Empty<CodeResult>(); }
        if (_closing) return;
        if (codes.Count == 0)
        {
            if (announceEmpty) Toast(area, Loc.T("no_code_found"));
            return;
        }
        double toDip = area.Width / Math.Max(1, image.PixelWidth);
        foreach (var code in codes)
        {
            var box = code.Box is { } b
                ? new Rect(area.X + b.X * toDip, area.Y + b.Y * toDip, b.Width * toDip, b.Height * toDip)
                : area;
            AddCodeChip(code, box);
        }
    }

    private void AddCodeChip(CodeResult code, Rect box)
    {
        // A glowing outline around the code itself.
        var ring = new Border
        {
            Width = Math.Max(24, box.Width),
            Height = Math.Max(24, box.Height),
            CornerRadius = new CornerRadius(10),
            BorderBrush = Theme.PointerStroke,
            BorderThickness = new Thickness(2),
            Effect = Theme.Glow(Theme.PointerGlow, 20, 0.9),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, box.X);
        Canvas.SetTop(ring, box.Y);

        string glyph, label;
        Action action;
        if (code.IsLink)
        {
            glyph = Theme.Glyph.Link;
            var host = Uri.TryCreate(code.Text, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", "") : code.Text;
            label = Loc.Format("open_link_to", host);
            action = () => { OpenLink(code.Text); };
        }
        else if (QrService.ParseWifi(code.Text) is { } wifi)
        {
            glyph = Theme.Glyph.Wifi;
            label = Loc.Format("copy_wifi_password", wifi.Ssid);
            action = () => { Clipboard2.SetText(wifi.Password); Toast(box, Loc.T("copied")); };
        }
        else
        {
            glyph = Theme.Glyph.Copy;
            label = Loc.Format("copy_code_text", code.Text.Length > 28 ? code.Text[..28] + "…" : code.Text);
            action = () => { Clipboard2.SetText(code.Text); Toast(box, Loc.T("copied")); };
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Icon(glyph, 13, Brushes.White));
        var text = Ui.Label(label, 13, Brushes.White, FontWeights.SemiBold);
        text.TextWrapping = TextWrapping.NoWrap;
        text.Margin = new Thickness(8, 0, 0, 1);
        row.Children.Add(text);
        var chip = new Clickable
        {
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 0, 16, 0),
            NormalBrush = Theme.B("#E61A2438"),
            HoverBrush = Theme.B("#FF24324D"),
            BorderBrush = Theme.B("#AACFE6FF"),
            BorderThickness = new Thickness(1),
            Effect = Theme.Glow(Theme.PointerGlow, 22, 0.9),
            Child = row,
            Cursor = Cursors.Hand,
        };
        chip.Click += action;
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = box.X + (box.Width - chip.DesiredSize.Width) / 2;
        double y = box.Y + (box.Height - chip.DesiredSize.Height) / 2;
        Canvas.SetLeft(chip, Math.Clamp(x, 8, ScreenWidthDip - chip.DesiredSize.Width - 8));
        Canvas.SetTop(chip, Math.Clamp(y, 8, ScreenHeightDip - chip.DesiredSize.Height - 8));
        int at = _card != null ? _root.Children.IndexOf(_card) : _root.Children.IndexOf(_hint);
        _root.Children.Insert(at, ring);
        _root.Children.Insert(at + 1, chip);
        _layers.Add(ring);
        _layers.Add(chip);
        Ui.PopIn(chip, 0.85);
        // While the pointer is out, the chip needs the normal cursor.
        chip.MouseEnter += (_, _) => { if (_pointer.Visibility == Visibility.Visible) _pointer.Opacity = 0; Cursor = Cursors.Hand; };
        chip.MouseLeave += (_, _) => { _pointer.Opacity = 1; Cursor = _pointer.Visibility == Visibility.Visible ? Cursors.None : Cursors.Arrow; };
    }

    private void OpenLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Open {url}: {ex.Message}"); }
        CloseOverlay();
    }

    private void ClearLayers()
    {
        foreach (var layer in _layers)
        {
            if (layer is TranslationLayer t) t.Cancel();
            if (layer is InlineTranslation i) i.Cancel();
            _root.Children.Remove(layer);
        }
        _layers.Clear();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_mode != Mode.Pointing || e.Handled) return;
        _rightDown = true;
        _dragging = false;
        _dragStart = e.GetPosition(_root);
        CaptureMouse();
        _hintFollows = false;
        Ui.Fade(_hint, 0, 120);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_rightDown)
        {
            _rightDown = false;
            ReleaseMouseCapture();
            bool dragged = _dragging;
            _dragging = false;
            if (_mode == Mode.Dragging) _mode = Mode.Pointing;
            if (dragged)
            {
                var area = Normalize(_dragStart, e.GetPosition(_root));
                if (area.Width >= 8 && area.Height >= 8) RunAction(_settings.RightDragAction, area);
                return;
            }
        }
        if (e.Handled) return;
        if (_mode == Mode.Adding) { BackToCard(); return; }
        if (_card != null && _card.IsMouseOver) return;
        CloseOverlay();
    }

    private DateTime _lastWheel = DateTime.MinValue;

    /// <summary>A wheel turn at a screen point (physical pixels). True when it was on this monitor.</summary>
    internal bool WheelAt(int screenX, int screenY)
    {
        var b = Shot.Bounds;
        if (screenX < b.X || screenY < b.Y || screenX >= b.X + b.Width || screenY >= b.Y + b.Height) return false;
        // One action per turn of the wheel, not one per notch.
        if ((DateTime.UtcNow - _lastWheel).TotalMilliseconds < 700) return true;
        if (_mode != Mode.Pointing || _settings.ScrollAction == "none") return true;
        var point = new Point((screenX - b.X) / _scale, (screenY - b.Y) / _scale);
        if (_hover.IsEmpty || !_hover.Contains(point)) UpdateHover(point);
        if (_hover.IsEmpty) return true;
        _lastWheel = DateTime.UtcNow;
        RunAction(_settings.ScrollAction, _hover);
        return true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Delivered by the mouse hook (WheelAt) when it's on; this is the fallback.
        e.Handled = true;
        var screen = PointToScreen(e.GetPosition(this));
        WheelAt((int)screen.X, (int)screen.Y);
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
            if (_settings.AutoQr && attachment.Codes != null)
                _ = ShowCodesAsync(image, rectDip, announceEmpty: false, attachment.Codes);
            return;
        }
        OpenCard(rectDip, attachment, null);
        if (_settings.AutoQr && attachment.Codes != null)
            _ = ShowCodesAsync(image, rectDip, announceEmpty: false, attachment.Codes);
    }

    // ---- card ----

    private void OpenCard(Rect anchor, Attachment? attachment, string? typed)
    {
        _app.CloseOtherOverlays(this);
        _mode = Mode.Card;
        _idle.Stop();
        SetPointerVisible(false);
        _hintFollows = false;
        Ui.Fade(_hint, 0, 100);
        _card = new CardView(this, _settings);
        if (attachment != null) _card.AddAttachment(attachment);
        _root.Children.Add(_card);
        if (TryRememberedPosition(out var savedLeft, out var savedTop)) _card.PlaceRemembered(savedLeft, savedTop);
        _card.Measure(new Size(CardView.CardWidth, double.PositiveInfinity));
        PositionCard(anchor);
        _card.SizeChanged += (_, _) => PositionCard(_selection.IsEmpty ? anchor : _selection);
        Ui.PopIn(_card);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _card?.FocusPrompt(typed));
    }

    private string MonitorKey => $"{Shot.Bounds.X},{Shot.Bounds.Y},{Shot.Bounds.Width},{Shot.Bounds.Height}";

    /// <summary>Remembers where the card was dragged on this monitor, for next time.</summary>
    internal void RememberCardPosition(double left, double top)
    {
        _settings.CardPositions[MonitorKey] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{left:0},{top:0}");
        _app.SaveSettings();
    }

    private bool TryRememberedPosition(out double left, out double top)
    {
        left = top = 0;
        if (!_settings.CardPositions.TryGetValue(MonitorKey, out var value)) return false;
        var parts = value.Split(',');
        return parts.Length == 2 &&
               double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out left) &&
               double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out top);
    }

    private void PositionCard(Rect anchor)
    {
        if (_card == null) return;
        double w = CardView.CardWidth;
        double h = _card.ActualHeight > 0 ? _card.ActualHeight : _card.DesiredSize.Height;
        double sw = ScreenWidthDip, sh = ScreenHeightDip;
        const double gap = 14, margin = 12;
        if (_card.UserMoved)
        {
            // Stay where the user put it; just keep it on the screen as it grows.
            _card.MoveTo(Math.Clamp(_card.Left, 0, Math.Max(0, sw - w)), Math.Clamp(_card.Top, 0, Math.Max(0, sh - h)));
            return;
        }
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
        // Read the keys now: by the time we check, a quick Alt+Tab is already released.
        bool userSwitch = Win32.IsKeyDown(0x12) || Win32.IsKeyDown(0x5B) || Win32.IsKeyDown(0x5C) || Win32.IsKeyDown(0x11);
        // Give system flyouts (voice typing, emoji panel) a moment; they don't count as leaving.
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_modalOpen || _closing || IsActive) return;
            var foreground = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Win32.CurrentProcessId) return;
            // Right after opening, the app underneath sometimes takes the focus back by itself:
            // take it back instead of closing.
            // With a translation or the card on screen, only the user leaves (Alt+Tab, Win key, Esc, right-click):
            // another program grabbing the focus (the local AI engine starting, a notification) doesn't close it.
            userSwitch |= Win32.IsKeyDown(0x12) || Win32.IsKeyDown(0x5B) || Win32.IsKeyDown(0x5C);
            bool holding = _layers.Count > 0 || _card != null || _acted;
            if ((holding && !userSwitch) ||
                (foreground == _previousForeground && (DateTime.UtcNow - _openedAt).TotalSeconds < 2.5))
            {
                Win32.ForceForeground(Handle);
                Activate();
                return;
            }
            var cls = Win32.WindowClass(foreground);
            if (cls is "Windows.UI.Core.CoreWindow" or "Shell_InputSwitchTopLevelWindow" or "Xaml_WindowedPopupClass") return;
            if (_card?.IsBusy == true) return;
            CloseOverlay();
        };
        check.Start();
    }

    /// <summary>Puts the pointer away on every monitor.</summary>
    internal void CloseOverlay() => _app.CloseAllOverlays();

    /// <summary>Closes just this monitor's layer.</summary>
    internal void CloseSelf()
    {
        if (_closing) return;
        _closing = true;
        _idle.Stop();
        _probe?.Dispose();
        _card?.CancelAll();
        foreach (var layer in _layers)
        {
            if (layer is TranslationLayer t) t.Cancel();
            if (layer is InlineTranslation i) i.Cancel();
        }
        // The frozen picture is identical to the real screen, so only our own things fade out;
        // fading the picture itself would show the black window behind it for a moment.
        _screenPulse.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _screenPulse.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _screenPulse.ScaleX = _screenPulse.ScaleY = 1;
        var parts = _root.Children.OfType<UIElement>().Where(c => !ReferenceEquals(c, _screen)).ToList();
        if (parts.Count == 0) { Close(); return; }
        foreach (var part in parts) part.IsHitTestVisible = false;
        int left = parts.Count;
        foreach (var part in parts) Ui.Fade(part, 0, 110, () => { if (--left == 0) Close(); });
    }

    internal void OpenSettingsAndClose()
    {
        CloseOverlay();
        _app.OpenSettings();
    }

    internal void SaveSettingsQuietly() => _app.SaveSettings();

    /// <summary>Keeps the window under this area translated live, and puts the pointer away.</summary>
    internal void StartLiveHere(Rect area)
    {
        var center = new Point((area.X + area.Width / 2) * _scale, (area.Y + area.Height / 2) * _scale);
        IntPtr window = IntPtr.Zero;
        for (int i = 0; i < Shot.Windows.Count && i < Shot.Handles.Count; i++)
        {
            var r = Shot.Windows[i];
            if (center.X >= r.X && center.Y >= r.Y && center.X < r.X + r.Width && center.Y < r.Y + r.Height)
            {
                window = Shot.Handles[i];
                break;
            }
        }
        if (window == IntPtr.Zero) return;
        CloseOverlay();
        LiveTranslation.StartFor(_app, window);
    }

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
