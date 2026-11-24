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
            var (bg, fg) = TextPaint.Colors(pixels, w, h, block);
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
        // Live: keep this whole window translated while you use it.
        var live = ToolButton(Theme.Glyph.Play, Loc.T("live_here"));
        live.Click += () => _host.StartLiveHere(_selection);
        _buttons.Children.Add(copy);
        _buttons.Children.Add(listen);
        _buttons.Children.Add(original);
        _buttons.Children.Add(live);
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

    private Border BuildView(Piece piece) =>
        TextPaint.View(piece.Translation ?? "", piece.Area, piece.Box.Lines, piece.Background, piece.Foreground, Width, Height);

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

    /// <summary>Raised when the user closes it.</summary>
    public event Action? Removed;

    public void Remove()
    {
        Removed?.Invoke();
        Cancel();
        Ui.Fade(this, 0, 140, () => (Parent as Panel)?.Children.Remove(this));
    }
}
