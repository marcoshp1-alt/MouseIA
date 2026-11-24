using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MouseIA.Core;
using MouseIA.Native;
using MouseIA.Services;

namespace MouseIA.UI;

/// <summary>
/// Translation drawn right into the screen, like Circle to Search: each piece of text is covered with
/// its own background colour and rewritten in the target language, same place, similar size.
/// One small toolbar under the selection copies, reads aloud, shows the original or closes.
/// </summary>
internal sealed class InlineTranslation : Canvas
{
    private sealed class Piece
    {
        public required OcrBox Box;
        public required Rect Area;          // where the original text is, in DIPs
        public required Color Background;
        public required Color Foreground;
        public Border? View;
        public Rectangle? Shimmer;
        public string? Translation;
    }

    private readonly OverlayWindow _host;
    private readonly AppSettings _settings;
    private readonly List<Piece> _pieces = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly LanguageInfo _target;
    private readonly Rect _selection;
    private readonly bool _speak;
    private readonly Border _toolbar;
    private readonly TextBlock _status;
    private readonly StackPanel _buttons;
    private readonly TranslateTransform _shimmerMove = new();
    private bool _showingOriginal;

    /// <param name="image">The captured area (physical pixels).</param>
    /// <param name="imageArea">Where that image sits on the screen, in DIPs.</param>
    /// <param name="scale">Physical pixels per DIP.</param>
    /// <param name="blocks">Recognized text blocks, in pixels of <paramref name="image"/>.</param>
    public InlineTranslation(OverlayWindow host, AppSettings settings, BitmapSource image, Rect imageArea, double scale,
        IReadOnlyList<OcrBox> blocks, Rect selection, bool speak)
    {
        _host = host;
        _settings = settings;
        _selection = selection;
        _speak = speak;
        _target = Languages.Resolve(settings.TranslateTo, CultureInfo.CurrentUICulture);
        Width = host.ScreenWidthDip;
        Height = host.ScreenHeightDip;
        IsHitTestVisible = true;
        Background = null; // only the toolbar takes clicks

        var pixels = ImageUtil.ToBgra(image, out int w, out int h);
        foreach (var block in blocks.Where(b => Prompts.IsTranslatable(b.Text)))
        {
            var (bg, fg) = Colors(pixels, w, h, block);
            var area = new Rect(imageArea.X + block.X / scale, imageArea.Y + block.Y / scale, block.Width / scale, block.Height / scale);
            _pieces.Add(new Piece { Box = block, Area = area, Background = bg, Foreground = fg });
        }

        // A soft light sweeps over each piece while it's being translated.
        var sweep = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            RelativeTransform = _shimmerMove,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 0x9F, 0xD0, 0xFF), 0.2),
                new GradientStop(Color.FromArgb(0x55, 0x9F, 0xD0, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0, 0x9F, 0xD0, 0xFF), 0.8),
            },
        };
        foreach (var piece in _pieces)
        {
            piece.Shimmer = new Rectangle
            {
                Width = piece.Area.Width + 4,
                Height = piece.Area.Height + 4,
                RadiusX = 4,
                RadiusY = 4,
                Fill = sweep,
                IsHitTestVisible = false,
            };
            SetLeft(piece.Shimmer, piece.Area.X - 2);
            SetTop(piece.Shimmer, piece.Area.Y - 2);
            Children.Add(piece.Shimmer);
        }
        _shimmerMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });

        // ---- toolbar ----
        _status = Ui.Label(Loc.Format("translating_to_x", _target.Display(Loc.IsPortuguese).Split(" · ")[0]), 12.5, Theme.Muted);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(6, 0, 8, 0);
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        var copy = ToolButton(Theme.Glyph.Copy, Loc.T("copy"));
        copy.Click += () => Clipboard2.SetText(AllText());
        var listen = ToolButton(Theme.Glyph.Volume, Loc.T("listen"));
        listen.Click += () => _ = SpeakAsync();
        var original = ToolButton(Theme.Glyph.Text, Loc.T("show_original"));
        original.Click += ToggleOriginal;
        _buttons.Children.Add(copy);
        _buttons.Children.Add(listen);
        _buttons.Children.Add(original);
        var close = ToolButton(Theme.Glyph.Close, Loc.T("close"));
        close.Click += Remove;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Sparkle(12));
        row.Children.Add(_status);
        row.Children.Add(_buttons);
        row.Children.Add(close);
        _toolbar = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = Theme.B("#EE151925"),
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 4, 4),
            Child = row,
            Effect = Theme.Shadow(),
        };
        Children.Add(_toolbar);
        _toolbar.SizeChanged += (_, _) => PlaceToolbar();

        Loaded += (_, _) => _ = RunAsync();
    }

    /// <summary>True when there was text worth translating here.</summary>
    public bool HasText => _pieces.Count > 0;

    private static IconButton ToolButton(string glyph, string tip) => new(glyph, tip, 28, 11)
    {
        NormalBrush = Theme.B("#00000000"),
        Margin = new Thickness(2, 0, 0, 0),
    };

    private void PlaceToolbar()
    {
        double w = _toolbar.ActualWidth, h = _toolbar.ActualHeight;
        double x = Math.Clamp(_selection.X + (_selection.Width - w) / 2, 8, Math.Max(8, Width - w - 8));
        double y = _selection.Bottom + 10;
        if (y + h > Height - 8) y = Math.Max(8, _selection.Y - h - 10);
        if (y + h > Height - 8) y = Height - h - 8;
        SetLeft(_toolbar, x);
        SetTop(_toolbar, y);
    }

    // ---- colours ----

    /// <summary>The background around a piece of text (median of a thin ring outside it) and the text colour.</summary>
    private static (Color Background, Color Foreground) Colors(byte[] px, int w, int h, OcrBox box)
    {
        int x0 = (int)Math.Floor(box.X), y0 = (int)Math.Floor(box.Y);
        int x1 = (int)Math.Ceiling(box.Right), y1 = (int)Math.Ceiling(box.Bottom);
        var ring = new List<(byte B, byte G, byte R)>();
        void Take(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = (y * w + x) * 4;
            ring.Add((px[i], px[i + 1], px[i + 2]));
        }
        for (int d = 2; d <= 4; d += 2)
        {
            for (int x = x0 - d; x <= x1 + d; x += 2) { Take(x, y0 - d); Take(x, y1 + d); }
            for (int y = y0 - d; y <= y1 + d; y += 2) { Take(x0 - d, y); Take(x1 + d, y); }
        }
        if (ring.Count == 0) return (Color.FromRgb(30, 30, 30), System.Windows.Media.Colors.White);
        byte Median(Func<(byte B, byte G, byte R), byte> pick)
        {
            var values = ring.Select(pick).OrderBy(v => v).ToList();
            return values[values.Count / 2];
        }
        var bg = Color.FromRgb(Median(p => p.R), Median(p => p.G), Median(p => p.B));

        // The text colour: the pixels inside that differ most from the background.
        var far = new List<(int Dist, byte R, byte G, byte B)>();
        int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1.0, (x1 - x0) * (double)(y1 - y0) / 4000)));
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y += step)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x += step)
            {
                int i = (y * w + x) * 4;
                int dist = Math.Abs(px[i + 2] - bg.R) + Math.Abs(px[i + 1] - bg.G) + Math.Abs(px[i] - bg.B);
                if (dist > 90) far.Add((dist, px[i + 2], px[i + 1], px[i]));
            }
        Color fg;
        if (far.Count >= 4)
        {
            var top = far.OrderByDescending(f => f.Dist).Take(Math.Max(3, far.Count / 3)).ToList();
            fg = Color.FromRgb((byte)top.Average(t => t.R), (byte)top.Average(t => t.G), (byte)top.Average(t => t.B));
        }
        else fg = Luma(bg) > 0.5 ? Color.FromRgb(20, 20, 20) : System.Windows.Media.Colors.White;
        if (Math.Abs(Luma(fg) - Luma(bg)) < 0.3) fg = Luma(bg) > 0.5 ? Color.FromRgb(20, 20, 20) : System.Windows.Media.Colors.White;
        return (bg, fg);
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    // ---- translating ----

    private async Task RunAsync()
    {
        if (_pieces.Count == 0) return;
        if (!_settings.HasAi)
        {
            StopShimmer();
            _status.Text = Loc.T("needs_ai_short");
            return;
        }
        try
        {
            const int batch = 40;
            for (int start = 0; start < _pieces.Count; start += batch)
            {
                var group = _pieces.Skip(start).Take(batch).ToList();
                await TranslateGroupAsync(group);
            }
            // Anything the model skipped goes again on its own.
            foreach (var piece in _pieces.Where(p => p.Translation == null).ToList())
                await TranslateGroupAsync(new List<Piece> { piece });
            StopShimmer();
            _status.Text = "→ " + _target.Display(Loc.IsPortuguese).Split(" · ")[0];
            _buttons.Visibility = Visibility.Visible;
            if (_speak) await SpeakAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StopShimmer();
            _status.Foreground = Theme.Danger;
            var message = ex is AiException ai ? ai.Detail : ex.Message;
            _status.Text = message.Length > 90 ? message[..90] + "…" : message;
            if (_pieces.Any(p => p.Translation != null)) _buttons.Visibility = Visibility.Visible;
        }
    }

    private async Task TranslateGroupAsync(List<Piece> group)
    {
        var items = group.Select(p => p.Box.Text).ToList();
        var request = Prompts.TranslateBatchRequest(_target, items);
        var prepared = await Task.Run(() => AiSetup.PrepareAsync(_settings, Prompts.TranslateSystem(_target),
            new[] { new ChatTurn(true, request) }, new List<string>(), Math.Clamp(items.Sum(i => i.Length) * 3 + 200, 400, 8000),
            null, _cts.Token), _cts.Token);
        var reply = new StringBuilder();
        var dispatcher = Dispatcher;
        var result = await Task.Run(() => AiClient.StreamAsync(prepared.Request, piece => dispatcher.BeginInvoke(() =>
        {
            reply.Append(piece);
            // A line is complete once the next one has started: show it right away.
            var parsed = Prompts.ParseBatch(reply.ToString(), group.Count);
            int last = Array.FindLastIndex(parsed, p => p != null);
            for (int i = 0; i < last; i++) Show(group[i], parsed[i]);
        }), _cts.Token), _cts.Token);
        var final = Prompts.ParseBatch(result.Text, group.Count);
        for (int i = 0; i < group.Count; i++) Show(group[i], final[i]);
    }

    private void Show(Piece piece, string? text)
    {
        if (piece.View != null || string.IsNullOrWhiteSpace(text)) return;
        piece.Translation = text.Trim();
        if (piece.Shimmer != null)
        {
            Children.Remove(piece.Shimmer);
            piece.Shimmer = null;
        }
        piece.View = BuildView(piece);
        Children.Insert(0, piece.View);
        if (_showingOriginal) piece.View.Visibility = Visibility.Collapsed;
        piece.View.Opacity = 0;
        Ui.Fade(piece.View, 1, 220);
    }

    /// <summary>The translated text over its own background, sized to sit where the original was.</summary>
    private Border BuildView(Piece piece)
    {
        var area = piece.Area;
        double lineHeight = area.Height / Math.Max(1, piece.Box.Lines);
        double baseSize = Math.Clamp(lineHeight * 0.92, 9, 64);
        bool singleLine = piece.Box.Lines <= 1;
        var text = new TextBlock
        {
            Text = piece.Translation,
            FontFamily = Theme.Font,
            Foreground = new SolidColorBrush(piece.Foreground),
            TextWrapping = singleLine ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
        };
        double width = area.Width + 4, height = area.Height + 4;
        double size = baseSize;
        double min = baseSize * (singleLine ? 0.72 : 0.6);
        while (true)
        {
            text.FontSize = size;
            text.Measure(new Size(singleLine ? double.PositiveInfinity : width - 4, double.PositiveInfinity));
            bool fits = singleLine ? text.DesiredSize.Width <= width - 4 : text.DesiredSize.Height <= height - 2;
            if (fits || size <= min) break;
            size = Math.Max(min, size - 0.5);
        }
        // Still too big at the smallest readable size: let the background grow instead.
        if (singleLine) width = Math.Max(width, text.DesiredSize.Width + 6);
        else height = Math.Max(height, text.DesiredSize.Height + 4);

        double x = area.X - 2, y = area.Y - 2;
        if (x + width > Width - 2) x = Math.Max(2, Width - 2 - width);
        if (y + height > Height - 2) y = Math.Max(2, Height - 2 - height);
        var view = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(piece.Background),
            Padding = new Thickness(2, 0, 2, 0),
            Child = text,
            IsHitTestVisible = false,
        };
        SetLeft(view, x);
        SetTop(view, y);
        return view;
    }

    private void StopShimmer()
    {
        _shimmerMove.BeginAnimation(TranslateTransform.XProperty, null);
        foreach (var piece in _pieces.Where(p => p.Shimmer != null))
        {
            Children.Remove(piece.Shimmer);
            piece.Shimmer = null;
        }
    }

    private void ToggleOriginal()
    {
        _showingOriginal = !_showingOriginal;
        foreach (var piece in _pieces)
            if (piece.View != null) piece.View.Visibility = _showingOriginal ? Visibility.Collapsed : Visibility.Visible;
    }

    private string AllText() => string.Join("\n", _pieces.Where(p => p.Translation != null).Select(p => p.Translation));

    private async Task SpeakAsync()
    {
        if (SpeechService.IsSpeaking)
        {
            SpeechService.Stop();
            return;
        }
        try { await SpeechService.SpeakAsync(AllText(), _target.Code, _settings.VoiceRate, _settings.VoiceId); }
        catch (Exception ex) { Log.Write("Inline speak: " + ex.Message); }
    }

    public void Cancel()
    {
        _cts.Cancel();
        StopShimmer();
    }

    public void Remove()
    {
        Cancel();
        Ui.Fade(this, 0, 140, () => (Parent as Panel)?.Children.Remove(this));
    }
}
