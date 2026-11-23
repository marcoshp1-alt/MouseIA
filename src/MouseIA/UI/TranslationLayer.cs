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
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Services;

namespace MouseIA.UI;

/// <summary>
/// A translation laid right over the area it came from, like a sticker of glass,
/// without opening the card. Several can sit on the screen at once.
/// </summary>
internal sealed class TranslationLayer : Grid
{
    private readonly OverlayWindow _host;
    private readonly AppSettings _settings;
    private readonly BitmapSource _image;
    private readonly bool _speak;
    private readonly TextBlock _text;
    private readonly Border _panel;
    private readonly StackPanel _tools;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _translation = new();
    private readonly DispatcherTimer _fit;
    private LanguageInfo _target;

    public TranslationLayer(OverlayWindow host, AppSettings settings, BitmapSource image, Rect area, bool speak)
    {
        _host = host;
        _settings = settings;
        _image = image;
        _speak = speak;
        _target = Languages.Resolve(settings.TranslateTo, CultureInfo.CurrentUICulture);
        Width = Math.Max(area.Width, 60);
        Canvas.SetLeft(this, area.X);
        Canvas.SetTop(this, area.Y);

        // Glass: the blurred screen behind the area, so the original text disappears under it.
        var backdrop = new Image
        {
            Source = host.Shot.Image,
            Stretch = Stretch.Fill,
            Width = host.ScreenWidthDip,
            Height = host.ScreenHeightDip,
            Effect = new BlurEffect { Radius = 30, RenderingBias = RenderingBias.Performance },
        };
        var backdropHost = new Canvas { ClipToBounds = true };
        Canvas.SetLeft(backdrop, -area.X);
        Canvas.SetTop(backdrop, -area.Y);
        backdropHost.Children.Add(backdrop);
        var clip = new RectangleGeometry { RadiusX = 10, RadiusY = 10 };
        var glass = new Grid { Clip = clip };
        glass.Children.Add(backdropHost);
        glass.Children.Add(new Border
        {
            Background = settings.GlassEffect ? Theme.GlassTint : Theme.SolidSurface,
            Opacity = settings.GlassEffect ? Math.Max(0.75, settings.GlassOpacity) : 1,
        });

        _text = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 18,
            Foreground = Theme.Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 7, 10, 8),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var typing = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 8) };
        typing.Children.Add(Ui.Sparkle(13));
        var status = Ui.Label(Loc.T("translating_here"), 12.5, Theme.Muted);
        status.Margin = new Thickness(8, 0, 0, 0);
        typing.Children.Add(status);
        var content = new Grid();
        content.Children.Add(typing);
        content.Children.Add(_text);

        _panel = new Border
        {
            MinHeight = Math.Max(area.Height, 34),
            CornerRadius = new CornerRadius(10),
            BorderBrush = Theme.PointerStroke,
            BorderThickness = new Thickness(1.5),
            Effect = Theme.Glow(Theme.PointerGlow, 22, 0.8),
            Child = content,
        };
        var stack = new Grid();
        stack.Children.Add(glass);
        stack.Children.Add(_panel);
        stack.SizeChanged += (_, _) => clip.Rect = new Rect(0, 0, stack.ActualWidth, stack.ActualHeight);

        // Small toolbar under the translation.
        _tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var copy = ToolButton(Theme.Glyph.Copy, Loc.T("copy"));
        copy.Click += () => Clipboard2.SetText(_translation.ToString().Trim());
        var listen = ToolButton(Theme.Glyph.Volume, Loc.T("listen"));
        listen.Click += () => _ = SpeakAsync();
        var close = ToolButton(Theme.Glyph.Close, Loc.T("close"));
        close.Click += Remove;
        _tools.Children.Add(copy);
        _tools.Children.Add(listen);
        _tools.Children.Add(close);

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(stack);
        SetRow(_tools, 1);
        Children.Add(_tools);

        _fit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _fit.Tick += (_, _) => Fit(area.Height);
        Loaded += (_, _) =>
        {
            Ui.PopIn(this, 0.97);
            _ = RunAsync(typing, area.Height);
        };
    }

    private static IconButton ToolButton(string glyph, string tip)
    {
        var button = new IconButton(glyph, tip, 28, 11)
        {
            NormalBrush = Theme.B("#E6151925"),
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 0, 0),
        };
        return button;
    }

    /// <summary>Shrinks the text until it fits the original area (but never below a readable size).</summary>
    private void Fit(double areaHeight)
    {
        double size = 22;
        double width = Math.Max(40, Width - 20);
        while (size > 11)
        {
            _text.FontSize = size;
            _text.Measure(new Size(width, double.PositiveInfinity));
            if (_text.DesiredSize.Height + 15 <= Math.Max(areaHeight, 34)) break;
            size -= 1;
        }
    }

    private async Task RunAsync(FrameworkElement typing, double areaHeight)
    {
        string source;
        try { source = await Task.Run(() => OcrService.RecognizeAsync(_image)); }
        catch (Exception) { source = ""; }

        if (!_settings.HasAi)
        {
            typing.Visibility = Visibility.Collapsed;
            _text.FontSize = 13;
            _text.Text = Loc.T("needs_ai_short");
            _tools.Visibility = Visibility.Visible;
            return;
        }

        var images = new List<string>();
        bool useImage = source.Length == 0 || _settings.Provider != ProviderIds.Local;
        if (useImage) images.Add(await Task.Run(() => ImageUtil.ToJpegBase64(_image, 1280)));
        var system = Prompts.TranslateSystem(_target);
        _fit.Start();
        try
        {
            var text = await StreamAsync(system, Prompts.TranslateRequest(_target, source, useImage), images, typing);
            if (source.Length > 0 && Prompts.LooksUntranslated(source, text))
            {
                _translation.Clear();
                _text.Text = "";
                text = await StreamAsync(system, Prompts.TranslateRequest(_target, source, false, insist: true), new List<string>(), typing);
            }
            _fit.Stop();
            Fit(areaHeight);
            _tools.Visibility = Visibility.Visible;
            if (_speak) await SpeakAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _fit.Stop();
            typing.Visibility = Visibility.Collapsed;
            _text.FontSize = 12.5;
            _text.Foreground = Theme.Danger;
            _text.Text = ex is AiException ai ? Shorten(ai.Detail) : ex.Message;
            _tools.Visibility = Visibility.Visible;
        }
    }

    private async Task<string> StreamAsync(string system, string request, IReadOnlyList<string> images, FrameworkElement typing)
    {
        var prepared = await Task.Run(() => AiSetup.PrepareAsync(_settings, system, new[] { new ChatTurn(true, request) },
            images, 2048, null, _cts.Token), _cts.Token);
        var dispatcher = Dispatcher;
        var result = await Task.Run(() => AiClient.StreamAsync(prepared.Request, piece => dispatcher.BeginInvoke(() =>
        {
            typing.Visibility = Visibility.Collapsed;
            _translation.Append(piece);
            _text.Text = MiniMarkdown.ToPlain(_translation.ToString());
        }), _cts.Token), _cts.Token);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        return result.Text;
    }

    private async Task SpeakAsync()
    {
        if (SpeechService.IsSpeaking)
        {
            SpeechService.Stop();
            return;
        }
        try
        {
            await SpeechService.SpeakAsync(MiniMarkdown.ToPlain(_translation.ToString()), _target.Code, _settings.VoiceRate, _settings.VoiceId);
        }
        catch (Exception ex)
        {
            Native.Log.Write("Overlay speak: " + ex.Message);
        }
    }

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + "…" : text;

    public void Remove()
    {
        _cts.Cancel();
        _fit.Stop();
        Ui.Fade(this, 0, 140, () => (Parent as Panel)?.Children.Remove(this));
    }

    public void Cancel()
    {
        _cts.Cancel();
        _fit.Stop();
    }
}
