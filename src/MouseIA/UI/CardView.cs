using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Services;

namespace MouseIA.UI;

internal sealed class Attachment
{
    public BitmapSource? Image { get; init; }
    public required string Name { get; init; }
    public string Glyph { get; init; } = Theme.Glyph.Picture;
    public string? FileText { get; init; }
    public Task<string>? Ocr { get; set; }
    public Task<IReadOnlyList<CodeResult>>? Codes { get; set; }
}

/// <summary>The glass card that opens next to a selection.</summary>
internal sealed class CardView : Grid
{
    public const double CardWidth = 468;

    private readonly OverlayWindow _host;
    private readonly AppSettings _settings;
    private readonly List<Attachment> _attachments = new();
    private readonly List<ChatTurn> _history = new();
    private string _historySystem = "";

    private readonly Grid _glass;
    private readonly Image _backdrop;
    private readonly RectangleGeometry _clip = new() { RadiusX = 24, RadiusY = 24 };
    private readonly StackPanel _thumbs;
    private readonly IconButton _add;
    private readonly WrapPanel _tools;
    private readonly PillButton _translate;
    private readonly Picker _language;
    private readonly PillButton _copyText;
    private readonly PillButton _qr;
    private readonly PillButton _voice;
    private readonly PillButton _file;
    private readonly Border _banner;
    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _prompt;
    private readonly IconButton _mic;
    private readonly Clickable _send;
    private readonly TextBlock _sendIcon;
    private readonly WrapPanel _chips;

    private CancellationTokenSource? _cts;
    private bool _busy;

    public CardView(OverlayWindow host, AppSettings settings)
    {
        _host = host;
        _settings = settings;
        Width = CardWidth;
        UseLayoutRounding = true;
        AllowDrop = true;

        // ---- glass layers ----
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = Theme.B("#01000000"),
            Effect = Theme.Shadow(),
        };
        var glow = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = Theme.B("#01000000"),
            Effect = Theme.Glow(Theme.PointerGlow, 46, 0.38),
        };
        _backdrop = new Image
        {
            Source = host.Shot.Image,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new BlurEffect { Radius = 42, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        };
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.LowQuality);
        // In a Canvas the full-screen backdrop doesn't take part in the card's size.
        var backdropHost = new Canvas { ClipToBounds = true };
        backdropHost.Children.Add(_backdrop);
        _glass = new Grid { Clip = _clip, ClipToBounds = true };
        _glass.Children.Add(backdropHost);
        var tint = new Border { Background = Theme.GlassTint };
        _glass.Children.Add(tint);
        if (settings.GlassEffect)
        {
            // Lower opacity lets more of the blurred screen show through.
            tint.Opacity = settings.GlassOpacity;
        }
        else
        {
            backdropHost.Visibility = Visibility.Collapsed;
            tint.Background = Theme.SolidSurface;
        }
        _glass.Children.Add(new Border { Background = Theme.GlassSheen });
        var stroke = new Border
        {
            CornerRadius = new CornerRadius(24),
            BorderBrush = Theme.GlassStroke,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };

        // ---- header: attachments + actions ----
        _thumbs = new StackPanel { Orientation = Orientation.Horizontal };
        _add = new IconButton(Theme.Glyph.Add, Loc.T("add_selection"), 44, 15)
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            NormalBrush = Theme.Field,
            Margin = new Thickness(0, 0, 0, 0),
        };
        _add.Click += () => _host.BeginAddSelection();
        var thumbRow = new StackPanel { Orientation = Orientation.Horizontal };
        thumbRow.Children.Add(_thumbs);
        thumbRow.Children.Add(_add);

        var settingsButton = new IconButton(Theme.Glyph.Settings, Loc.T("settings"));
        settingsButton.Click += () => _host.OpenSettingsAndClose();
        var closeButton = new IconButton(Theme.Glyph.Close, Loc.T("close_esc"));
        closeButton.Click += () => _host.CloseOverlay();
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        headerActions.Children.Add(settingsButton);
        headerActions.Children.Add(closeButton);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(headerActions, Dock.Right);
        header.Children.Add(headerActions);
        header.Children.Add(thumbRow);

        // ---- tools ----
        var target = Languages.Resolve(_settings.TranslateTo, CultureInfo.CurrentUICulture);
        _translate = new PillButton(Theme.Glyph.Globe, Loc.T("translate"), Loc.T("translate_tip"));
        _translate.Click += () => _ = TranslateAsync();
        _language = new Picker(LanguageItems(), target.Code, compact: true)
        {
            Prefix = "→ ",
            Margin = new Thickness(4, 0, 6, 6),
            ToolTip = Loc.T("translate_to"),
        };
        _language.Selected = target.Code;
        _language.Changed += code =>
        {
            _settings.TranslateTo = code;
            _host.SaveSettingsQuietly();
        };
        _copyText = new PillButton(Theme.Glyph.Text, Loc.T("copy_text"), Loc.T("copy_text_tip"));
        _copyText.Click += () => _ = CopyTextAsync();
        _qr = new PillButton(Theme.Glyph.Qr, Loc.T("qr_code"), Loc.T("qr_tip"));
        _qr.Click += () => _ = ReadCodesAsync(showEmpty: true);
        _voice = new PillButton(Theme.Glyph.Volume, Loc.T("listen_translation"), Loc.T("listen_translation_tip"));
        _voice.Click += () => _ = TranslateAsync(speak: true);
        _file = new PillButton(Theme.Glyph.Folder, Loc.T("open_file"), Loc.T("open_file_tip"));
        _file.Click += () => _ = OpenFilesAsync();
        _tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var pill in new FrameworkElement[] { _translate })
        {
            pill.Margin = new Thickness(0, 0, 0, 6);
            _tools.Children.Add(pill);
        }
        _tools.Children.Add(_language);
        foreach (var pill in new FrameworkElement[] { _copyText, _qr, _voice, _file })
        {
            pill.Margin = new Thickness(0, 0, 6, 6);
            _tools.Children.Add(pill);
        }

        // ---- banner (status, QR found, setup hints) ----
        _banner = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Theme.B("#1422D3EE"),
            BorderBrush = Theme.B("#3322D3EE"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 4, 0, 6),
            Visibility = Visibility.Collapsed,
        };

        // ---- conversation ----
        _messages = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _messages,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(220, host.ScreenHeightDip * 0.5),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 8),
            Padding = new Thickness(0, 0, 6, 0),
        };

        // ---- prompt ----
        _prompt = GlassField.CreateBox("", multiline: true);
        _prompt.AcceptsReturn = false;
        _prompt.MaxHeight = 120;
        _prompt.FontSize = 13.5;
        _prompt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _prompt.Margin = new Thickness(4, 0, 4, 0);
        var promptHint = Ui.Label(Loc.T("prompt_hint"), 13.5, Theme.Faint);
        promptHint.IsHitTestVisible = false;
        promptHint.TextWrapping = TextWrapping.NoWrap;
        promptHint.Margin = new Thickness(7, 0, 0, 0);
        _prompt.TextChanged += (_, _) =>
        {
            promptHint.Visibility = _prompt.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSendState();
        };
        _prompt.PreviewKeyDown += OnPromptKeyDown;
        _mic = new IconButton(Theme.Glyph.Mic, Loc.T("dictate"), 34, 14);
        _mic.Click += () => _ = DictateIntoPromptAsync();
        _sendIcon = Ui.Icon(Theme.Glyph.Send, 13, Brushes.White);
        _send = new Clickable
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            NormalBrush = Theme.Accent,
            HoverBrush = Theme.Gradient(45, Theme.C("#9D74FF"), Theme.C("#45DDF2")),
            Child = _sendIcon,
            ToolTip = Loc.T("send"),
            Margin = new Thickness(2, 0, 0, 0),
        };
        _send.Click += () =>
        {
            if (_busy) Cancel();
            else Submit();
        };
        var promptGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        promptGrid.Children.Add(promptHint);
        promptGrid.Children.Add(_prompt);
        var promptRow = new DockPanel();
        var promptButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        promptButtons.Children.Add(_mic);
        promptButtons.Children.Add(_send);
        DockPanel.SetDock(promptButtons, Dock.Right);
        promptRow.Children.Add(promptButtons);
        promptRow.Children.Add(promptGrid);
        var promptBox = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = Theme.B("#1AFFFFFF"),
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 4, 4),
            MinHeight = 44,
            Child = promptRow,
        };
        _prompt.GotKeyboardFocus += (_, _) => promptBox.BorderBrush = Theme.Accent;
        _prompt.LostKeyboardFocus += (_, _) => promptBox.BorderBrush = Theme.FieldStroke;

        // ---- suggestions ----
        _chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };

        var content = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };
        content.Children.Add(header);
        content.Children.Add(_tools);
        content.Children.Add(_banner);
        content.Children.Add(_scroll);
        content.Children.Add(promptBox);
        content.Children.Add(_chips);

        Children.Add(shadow);
        Children.Add(glow);
        Children.Add(_glass);
        Children.Add(stroke);
        Children.Add(content);

        SizeChanged += (_, _) => UpdateGlass();
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };

        UpdateAttachmentsUi();
        ResetChips();
        UpdateSendState();
        if (!_settings.HasAi) ShowNeedsAiBanner();
    }

    public bool IsBusy => _busy;

    public bool HasPopupOpen => _language.IsOpen;

    public void FocusPrompt(string? initialText = null)
    {
        if (initialText != null)
        {
            _prompt.Text = initialText;
            _prompt.CaretIndex = _prompt.Text.Length;
        }
        _prompt.Focus();
        Keyboard.Focus(_prompt);
    }

    // ---- geometry ----

    private double _left, _top;

    public void MoveTo(double left, double top)
    {
        _left = left;
        _top = top;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        UpdateGlass();
    }

    private void UpdateGlass()
    {
        _clip.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        _backdrop.Width = _host.ScreenWidthDip;
        _backdrop.Height = _host.ScreenHeightDip;
        Canvas.SetLeft(_backdrop, -_left);
        Canvas.SetTop(_backdrop, -_top);
    }

    // ---- attachments ----

    public void AddAttachment(Attachment attachment)
    {
        if (attachment.Image != null)
        {
            if (_settings.AutoOcr || attachment.Ocr == null)
                attachment.Ocr ??= RunOcrAsync(attachment.Image);
            attachment.Codes ??= Task.Run(() => QrService.Decode(attachment.Image));
        }
        _attachments.Add(attachment);
        UpdateAttachmentsUi();
        ResetChips();
        if (attachment.Image != null && _settings.AutoQr) _ = ShowFoundCodesAsync(attachment);
    }

    private static Task<string> RunOcrAsync(BitmapSource image) => Task.Run(async () =>
    {
        try { return await OcrService.RecognizeAsync(image); }
        catch (Exception ex)
        {
            Native.Log.Write("OCR failed: " + ex.Message);
            return "";
        }
    });

    private void UpdateAttachmentsUi()
    {
        _thumbs.Children.Clear();
        foreach (var attachment in _attachments)
            _thumbs.Children.Add(Thumbnail(attachment));
        _add.Visibility = _attachments.Count < 6 ? Visibility.Visible : Visibility.Collapsed;
        bool hasImage = _attachments.Any(a => a.Image != null);
        _copyText.IsEnabled = hasImage || _attachments.Any(a => a.FileText != null);
        _qr.IsEnabled = hasImage;
        _translate.IsEnabled = _attachments.Count > 0 || _history.Count > 0;
    }

    private FrameworkElement Thumbnail(Attachment attachment)
    {
        var face = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Theme.GlassStroke,
            BorderThickness = new Thickness(1),
            ToolTip = attachment.Name,
        };
        if (attachment.Image != null)
        {
            face.Background = new ImageBrush(attachment.Image) { Stretch = Stretch.UniformToFill };
        }
        else
        {
            face.Background = Theme.Field;
            face.Child = Ui.Icon(attachment.Glyph, 18, Theme.Muted);
        }
        var remove = new Clickable
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            NormalBrush = Theme.B("#E6151925"),
            HoverBrush = Theme.B("#FF2A2F3D"),
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0),
            Child = Ui.Icon(Theme.Glyph.Close, 7, Theme.Text),
            Visibility = Visibility.Hidden,
            ToolTip = Loc.T("remove"),
        };
        remove.Click += () =>
        {
            _attachments.Remove(attachment);
            UpdateAttachmentsUi();
            ResetChips();
        };
        var holder = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        holder.Children.Add(face);
        holder.Children.Add(remove);
        holder.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
        holder.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
        return holder;
    }

    private async Task<string> AllOcrTextAsync(int timeoutMs = 15000)
    {
        var parts = new List<string>();
        foreach (var attachment in _attachments)
        {
            if (attachment.Image == null) continue;
            attachment.Ocr ??= RunOcrAsync(attachment.Image);
            var finished = await Task.WhenAny(attachment.Ocr, Task.Delay(timeoutMs));
            if (finished == attachment.Ocr && attachment.Ocr.Result.Length > 0) parts.Add(attachment.Ocr.Result);
        }
        return string.Join("\n\n", parts);
    }

    private string AllFileText() =>
        string.Join("\n\n", _attachments.Where(a => !string.IsNullOrWhiteSpace(a.FileText))
            .Select(a => $"[{a.Name}]\n{a.FileText}"));

    // ---- chips ----

    private void ResetChips()
    {
        _chips.Children.Clear();
        if (_history.Count > 0)
        {
            _chips.Visibility = Visibility.Collapsed;
            return;
        }
        _chips.Visibility = Visibility.Visible;
        bool hasImage = _attachments.Any(a => a.Image != null);
        bool hasText = _attachments.Any(a => a.FileText != null);
        IEnumerable<string> keys = hasImage || hasText
            ? new[] { "chip_what", "chip_explain", "chip_summarize", "chip_fix" }
            : new[] { "chip_idea", "chip_write", "chip_how" };
        foreach (var key in keys)
        {
            var chip = new ChipButton(Loc.T(key), Loc.T(key + "_prompt"));
            chip.Click += () => Ask(chip.Prompt, Loc.T(key));
            _chips.Children.Add(chip);
        }
    }

    // ---- banner ----

    private void ShowBanner(string glyph, string text, params (string Label, Action Action)[] actions)
    {
        var row = new DockPanel();
        var icon = Ui.Icon(glyph, 14, Theme.B("#7DE6F5"));
        icon.Margin = new Thickness(0, 1, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var close = new IconButton(Theme.Glyph.Close, Loc.T("close"), 24, 9);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Click += HideBanner;
        DockPanel.SetDock(close, Dock.Right);
        row.Children.Add(close);
        var body = new StackPanel();
        var label = Ui.Label(text, 12.5);
        body.Children.Add(label);
        if (actions.Length > 0)
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (caption, action) in actions)
            {
                var button = new SecondaryButton(caption) { Height = 28, Margin = new Thickness(0, 0, 6, 0) };
                button.Click += action;
                buttons.Children.Add(button);
            }
            body.Children.Add(buttons);
        }
        row.Children.Add(body);
        _banner.Child = row;
        _banner.Visibility = Visibility.Visible;
        Ui.Fade(_banner, 1, 150);
    }

    private void HideBanner() => _banner.Visibility = Visibility.Collapsed;

    private void ShowNeedsAiBanner() =>
        ShowBanner(Theme.Glyph.Info, Loc.T("needs_ai"), (Loc.T("configure_ai"), () => _host.OpenSettingsAndClose()));

    // ---- prompt ----

    private void OnPromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            int caret = _prompt.CaretIndex;
            _prompt.Text = _prompt.Text.Insert(caret, "\n");
            _prompt.CaretIndex = caret + 1;
        }
    }

    private void UpdateSendState()
    {
        _sendIcon.Text = _busy ? Theme.Glyph.Stop : Theme.Glyph.Send;
        _send.ToolTip = Loc.T(_busy ? "stop" : "send");
        _send.IsEnabled = _busy || _prompt.Text.Trim().Length > 0;
    }

    private void Submit()
    {
        var text = _prompt.Text.Trim();
        if (text.Length == 0 || _busy) return;
        _prompt.Text = "";
        Ask(text, text);
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    public void CancelAll()
    {
        _cts?.Cancel();
        SpeechService.Stop();
    }

    // ---- asking ----

    private LanguageInfo UserLanguage => Languages.Resolve("auto", Loc.Culture);

    private async void Ask(string prompt, string display)
    {
        if (_busy) return;
        if (!_settings.HasAi)
        {
            ShowNeedsAiBanner();
            return;
        }
        var ocr = await AllOcrTextAsync(4000);
        var images = _attachments.Where(a => a.Image != null).Select(a => a.Image!).ToList();
        _historySystem = Prompts.Assistant(UserLanguage, ocr, AllFileText(), images.Count > 0);
        _history.Add(new ChatTurn(true, prompt));
        AddUserBubble(display);
        var answer = AddAnswer(UserLanguage.Code);
        var text = await RunAsync(answer, _historySystem, _history.ToList(), images);
        if (text != null) _history.Add(new ChatTurn(false, text));
        else _history.RemoveAt(_history.Count - 1);
    }

    /// <summary>Translates the selection (or the last answer); with speak, also reads the translation aloud.</summary>
    private async Task TranslateAsync(bool speak = false)
    {
        if (_busy) return;
        var target = Languages.Find(_language.Selected) ?? UserLanguage;
        var lastAnswer = _history.LastOrDefault(t => !t.FromUser)?.Text;
        var ocr = await AllOcrTextAsync();
        var fileText = AllFileText();
        string source = _attachments.Count > 0 ? string.Join("\n\n", new[] { ocr, fileText }.Where(s => s.Length > 0)) : lastAnswer ?? "";
        // Cloud models also see the picture: Windows OCR can misread scripts it doesn't know.
        bool useImages = _attachments.Any(a => a.Image != null) &&
                         (source.Length == 0 || _settings.Provider != ProviderIds.Local);

        if (!_settings.HasAi)
        {
            if (source.Length == 0)
            {
                ShowNeedsAiBanner();
                return;
            }
            if (speak)
            {
                // Without an AI there's no translation, but the text can still be read aloud.
                _ = SpeakAsync(source, DetectSpeechLanguage(source), null);
                ShowNeedsAiBanner();
                return;
            }
            OpenGoogleTranslate(source, target.Code);
            return;
        }

        var name = target.Display(Loc.IsPortuguese).Split(" · ")[0];
        var display = Loc.Format(speak ? "listen_translation_x" : "translate_to_x", name);
        AddUserBubble((speak ? "🔊 " : "🌐 ") + display);
        var answer = AddAnswer(target.Code);
        var images = useImages ? _attachments.Where(a => a.Image != null).Select(a => a.Image!).ToList() : new List<BitmapSource>();
        var system = Prompts.TranslateSystem(target);
        var text = await RunAsync(answer, system, new[] { new ChatTurn(true, Prompts.TranslateRequest(target, source, useImages)) }, images);
        if (text != null && source.Length > 0 && Prompts.LooksUntranslated(source, text))
        {
            // The model repeated the original: ask once more, firmly and with text only.
            Native.Log.Write("Translation echoed the source; retrying.");
            answer.Reset();
            text = await RunAsync(answer, system,
                new[] { new ChatTurn(true, Prompts.TranslateRequest(target, source, hasImage: false, insist: true)) },
                new List<BitmapSource>());
        }
        if (text == null) return;
        // Follow-up questions can refer to the translation.
        if (_historySystem.Length == 0)
            _historySystem = Prompts.Assistant(UserLanguage, ocr, fileText, _attachments.Any(a => a.Image != null));
        _history.Add(new ChatTurn(true, display));
        _history.Add(new ChatTurn(false, text));
        if (speak || _settings.SpeakTranslation) await SpeakAsync(MiniMarkdown.ToPlain(text), target.Code, answer);
        UpdateAttachmentsUi();
    }

    /// <summary>Streams one answer into the view. Returns the text, or null when nothing usable came back.</summary>
    private async Task<string?> RunAsync(AnswerView answer, string system, IReadOnlyList<ChatTurn> turns, List<BitmapSource> images)
    {
        _busy = true;
        UpdateSendState();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var provider = _settings.Provider;
        try
        {
            var encoded = await Task.Run(() => images.Select(i => ImageUtil.ToJpegBase64(i, 1536)).ToList(), ct);
            var dispatcher = Dispatcher;
            var progress = new Progress<string>(status => answer.SetStatus(status));
            var prepared = await Task.Run(() => AiSetup.PrepareAsync(_settings, system, turns, encoded, 4096, progress, ct), ct);
            answer.SetStatus(null);
            var result = await Task.Run(() => AiClient.StreamAsync(prepared.Request,
                piece => dispatcher.BeginInvoke(DispatcherPriority.Background, () => answer.Append(piece)), ct), ct);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); // let queued pieces land
            string? note = result.Truncated ? Loc.T("answer_truncated")
                : prepared.DroppedImages ? Loc.T("no_vision_note")
                : result.UsedFallback ? Loc.Format("used_fallback", result.Model) : null;
            answer.Complete(note);
            return result.Text;
        }
        catch (OperationCanceledException)
        {
            answer.Complete(Loc.T("stopped"));
            return answer.Text.Length > 0 ? answer.Text : null;
        }
        catch (LocalEngineException ex)
        {
            Native.Log.Write("Local engine: " + ex.Message);
            answer.Fail(ex.Message);
            ShowBanner(Theme.Glyph.Pc, Loc.T("check_local"), (Loc.T("open_settings"), () => _host.OpenSettingsAndClose()));
            return null;
        }
        catch (AiException ex)
        {
            Native.Log.Write($"AI error {ex.Kind}: {ex.Detail}");
            answer.Fail(ErrorText(ex, provider));
            if (ex.Kind is AiErrorKind.InvalidKey or AiErrorKind.Forbidden or AiErrorKind.NoCredit)
                ShowBanner(Theme.Glyph.Warning, Loc.T("check_settings"), (Loc.T("open_settings"), () => _host.OpenSettingsAndClose()));
            return null;
        }
        catch (Exception ex)
        {
            Native.Log.Write("Unexpected: " + ex);
            answer.Fail(ex.Message);
            return null;
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            UpdateSendState();
            UpdateAttachmentsUi();
            ResetChips();
        }
    }

    private string ErrorText(AiException ex, string provider)
    {
        var name = provider == ProviderIds.Local ? Loc.T("local_ai") : ModelCatalog.DisplayName(provider);
        if (provider == ProviderIds.Local && ex.Kind is AiErrorKind.Other or AiErrorKind.Server &&
            (ex.Detail.Contains("image", StringComparison.OrdinalIgnoreCase) ||
             ex.Detail.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
             ex.Detail.Contains("multimodal", StringComparison.OrdinalIgnoreCase)))
            return Loc.Format("err_local_vision", _settings.LocalModel);
        return ex.Kind switch
        {
            AiErrorKind.Network when provider == ProviderIds.Local => Loc.Format("err_local_offline", _settings.LocalUrl),
            AiErrorKind.Network => Loc.Format("err_network", name),
            AiErrorKind.Timeout => Loc.Format("err_timeout", name),
            AiErrorKind.InvalidKey => Loc.Format("err_key", name),
            AiErrorKind.Forbidden => Loc.Format("err_forbidden", name),
            AiErrorKind.ModelMissing when provider == ProviderIds.Local => Loc.Format("err_local_model",
                _settings.UsesModelFile ? System.IO.Path.GetFileName(_settings.LocalModelFile) : _settings.LocalModel),
            AiErrorKind.ModelMissing => Loc.Format("err_model", name),
            AiErrorKind.RateLimit => Loc.Format("err_rate", name),
            AiErrorKind.NoCredit => Loc.Format("err_credit", name),
            AiErrorKind.Server => Loc.Format("err_server", name) + (ex.Detail.Length > 0 ? "\n" + Short(ex.Detail) : ""),
            _ => Loc.Format("err_other", name) + (ex.Detail.Length > 0 ? "\n" + Short(ex.Detail) : ""),
        };
    }

    private static string Short(string text) => text.Length > 220 ? text[..220] + "…" : text;

    // ---- messages ----

    private void ShowConversation()
    {
        _scroll.Visibility = Visibility.Visible;
        _chips.Visibility = Visibility.Collapsed;
    }

    private void AddUserBubble(string text)
    {
        ShowConversation();
        var bubble = new Border
        {
            Background = Theme.B("#2AFFFFFF"),
            CornerRadius = new CornerRadius(16, 16, 4, 16),
            Padding = new Thickness(12, 7, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = CardWidth * 0.78,
            Margin = new Thickness(40, 4, 0, 10),
            Child = Ui.Label(text, 13.5),
        };
        _messages.Children.Add(bubble);
        Ui.PopIn(bubble, 0.98);
        _scroll.ScrollToEnd();
    }

    private AnswerView AddAnswer(string speakLanguage)
    {
        ShowConversation();
        var view = new AnswerView(speakLanguage, this);
        view.SizeChanged += (_, _) => { if (_busy) _scroll.ScrollToEnd(); };
        _messages.Children.Add(view);
        _scroll.ScrollToEnd();
        return view;
    }

    private void AddInfo(string glyph, string title, string? body, params (string Label, string Glyph, Action Action)[] actions)
    {
        ShowConversation();
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 12) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon(glyph, 13, Theme.Accent);
        icon.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(icon);
        head.Children.Add(Ui.Label(title, 13, Theme.Text, FontWeights.SemiBold));
        panel.Children.Add(head);
        if (!string.IsNullOrEmpty(body))
        {
            var box = new TextBox
            {
                Text = body,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Text,
                SelectionBrush = Theme.Selection,
                FontFamily = Theme.Font,
                FontSize = 13,
                Margin = new Thickness(0, 6, 0, 0),
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FocusVisualStyle = null,
            };
            panel.Children.Add(new Border
            {
                Background = Theme.B("#14FFFFFF"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 8),
                Margin = new Thickness(0, 6, 0, 0),
                Child = box,
            });
        }
        if (actions.Length > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (label, actionGlyph, action) in actions)
            {
                var button = new SecondaryButton(label, actionGlyph) { Height = 30, Margin = new Thickness(0, 0, 6, 6) };
                button.Click += action;
                row.Children.Add(button);
            }
            panel.Children.Add(row);
        }
        _messages.Children.Add(panel);
        Ui.PopIn(panel, 0.98);
        _scroll.ScrollToEnd();
    }

    // ---- tools ----

    private async Task CopyTextAsync()
    {
        if (_attachments.Count == 0) return;
        _copyText.Text = Loc.T("reading");
        var ocr = await AllOcrTextAsync();
        var text = string.Join("\n\n", new[] { ocr, string.Join("\n\n", _attachments.Where(a => a.FileText != null).Select(a => a.FileText)) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        _copyText.Text = Loc.T("copy_text");
        if (text.Length == 0)
        {
            AddInfo(Theme.Glyph.Text, Loc.T("no_text_found"), OcrService.Available ? null : Loc.T("ocr_missing"));
            return;
        }
        Clipboard2.SetText(text);
        AddInfo(Theme.Glyph.Check, Loc.T("text_copied"), text,
            (Loc.T("translate"), Theme.Glyph.Globe, () => _ = TranslateAsync()),
            (Loc.T("listen"), Theme.Glyph.Volume, () => _ = SpeakAsync(text, DetectSpeechLanguage(text), null)));
    }

    private async Task ShowFoundCodesAsync(Attachment attachment)
    {
        if (attachment.Codes == null) return;
        IReadOnlyList<CodeResult> codes;
        try { codes = await attachment.Codes; }
        catch (Exception) { return; }
        if (codes.Count == 0 || !_attachments.Contains(attachment)) return;
        var first = codes[0];
        var actions = new List<(string, Action)>();
        if (first.IsLink) actions.Add((Loc.T("open_link"), () => OpenUrl(first.Text)));
        actions.Add((Loc.T("copy"), () => Clipboard2.SetText(first.Text)));
        actions.Add((Loc.T("details"), () => _ = ReadCodesAsync(showEmpty: false)));
        ShowBanner(Theme.Glyph.Qr, Loc.Format("code_found", Short(first.Text)), actions.ToArray());
    }

    private async Task ReadCodesAsync(bool showEmpty)
    {
        HideBanner();
        var results = new List<CodeResult>();
        foreach (var attachment in _attachments.Where(a => a.Image != null))
        {
            attachment.Codes ??= Task.Run(() => QrService.Decode(attachment.Image!));
            try { results.AddRange(await attachment.Codes); }
            catch (Exception ex) { Native.Log.Write("QR failed: " + ex.Message); }
        }
        if (results.Count == 0)
        {
            if (showEmpty) AddInfo(Theme.Glyph.Qr, Loc.T("no_code_found"), Loc.T("no_code_hint"));
            return;
        }
        foreach (var code in results.DistinctBy(r => r.Text))
        {
            var actions = new List<(string, string, Action)>();
            string title = Loc.Format("code_kind", code.Format);
            string body = code.Text;
            if (code.IsLink) actions.Add((Loc.T("open_link"), Theme.Glyph.Link, () => OpenUrl(code.Text)));
            if (QrService.ParseWifi(code.Text) is { } wifi)
            {
                title = Loc.Format("wifi_network", wifi.Ssid);
                body = Loc.Format("wifi_password", wifi.Password);
                actions.Add((Loc.T("copy_password"), Theme.Glyph.Wifi, () => Clipboard2.SetText(wifi.Password)));
            }
            actions.Add((Loc.T("copy"), Theme.Glyph.Copy, () => Clipboard2.SetText(code.Text)));
            AddInfo(Theme.Glyph.Qr, title, body, actions.ToArray());
        }
    }

    private async Task OpenFilesAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = FileImport.DialogFilter,
            Multiselect = true,
            Title = Loc.T("open_file"),
        };
        _host.SetModalOpen(true);
        bool? ok;
        try { ok = dialog.ShowDialog(_host); }
        finally { _host.SetModalOpen(false); }
        if (ok != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = ImportFilesAsync(files);
        e.Handled = true;
    }

    internal async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Take(6))
        {
            _file.Text = Loc.T("opening");
            try
            {
                var file = await FileImport.ImportAsync(path);
                switch (file.Kind)
                {
                    case ImportKind.Unsupported:
                        ShowBanner(Theme.Glyph.Warning, Loc.Format("file_unsupported", file.Name));
                        break;
                    case ImportKind.Document:
                        AddAttachment(new Attachment { Name = file.Name, Glyph = Theme.Glyph.Document, FileText = file.Text });
                        break;
                    default:
                        int page = 1;
                        foreach (var image in file.Images)
                        {
                            var name = file.Images.Count > 1 ? $"{file.Name} ({page++})" : file.Name;
                            AddAttachment(new Attachment { Name = name, Image = image });
                        }
                        if (file.Kind == ImportKind.Pdf && file.Note != null)
                            ShowBanner(Theme.Glyph.Info, Loc.Format("pdf_pages", file.Note));
                        else if (file.Kind == ImportKind.Video)
                            ShowBanner(Theme.Glyph.Video, Loc.Format("video_frames", file.Name, file.Note ?? ""));
                        break;
                }
            }
            catch (Exception ex)
            {
                Native.Log.Write($"Import {path}: {ex}");
                ShowBanner(Theme.Glyph.Warning, Loc.Format("file_failed", Path.GetFileName(path)));
            }
        }
        _file.Text = Loc.T("open_file");
    }

    // ---- voice ----

    private async Task DictateIntoPromptAsync()
    {
        var language = _settings.VoiceInputLanguage == "auto" ? SpeechService.SystemLanguageTag() : _settings.VoiceInputLanguage;
        _mic.SetIconBrush(Theme.AnimatedAurora(2));
        _mic.ToolTip = Loc.T("listening");
        try
        {
            var result = await SpeechService.ListenAsync(language, CancellationToken.None);
            if (result.Error == SpeechService.ListenError.None)
            {
                if (result.Text.Length > 0)
                {
                    _prompt.Text = (_prompt.Text.Trim() + " " + result.Text).Trim();
                    FocusPrompt();
                    _prompt.CaretIndex = _prompt.Text.Length;
                }
                return;
            }
            HandleListenError(result, () => { FocusPrompt(); Native.VoiceTyping.Start(); });
        }
        finally
        {
            _mic.SetIconBrush(Theme.Muted);
            _mic.ToolTip = Loc.T("dictate");
        }
    }

    internal void HandleListenError(SpeechService.ListenResult result, Action useVoiceTyping)
    {
        Native.Log.Write($"Listen failed: {result.Error} {result.Detail}");
        switch (result.Error)
        {
            case SpeechService.ListenError.PrivacyDeclined:
                ShowBanner(Theme.Glyph.Mic, Loc.T("speech_privacy"),
                    (Loc.T("open_privacy"), () => OpenUrl("ms-settings:privacy-speech")),
                    (Loc.T("use_voice_typing"), useVoiceTyping));
                break;
            case SpeechService.ListenError.NoMicrophone:
                ShowBanner(Theme.Glyph.Mic, Loc.T("speech_no_mic"),
                    (Loc.T("open_mic_settings"), () => OpenUrl("ms-settings:privacy-microphone")),
                    (Loc.T("use_voice_typing"), useVoiceTyping));
                break;
            default:
                ShowBanner(Theme.Glyph.Mic, Loc.T("speech_failed"), (Loc.T("use_voice_typing"), useVoiceTyping));
                break;
        }
    }

    internal async Task SpeakAsync(string text, string languageCode, AnswerView? from)
    {
        if (SpeechService.IsSpeaking)
        {
            SpeechService.Stop();
            return;
        }
        try
        {
            bool ok = await SpeechService.SpeakAsync(text, languageCode, _settings.VoiceRate, _settings.VoiceId);
            if (ok) from?.MarkSpeaking();
            if (!ok)
            {
                var language = Languages.Find(languageCode)?.Display(Loc.IsPortuguese).Split(" · ")[0] ?? languageCode;
                ShowBanner(Theme.Glyph.Volume, Loc.Format("no_voice", language),
                    (Loc.T("install_voices"), () => OpenUrl("ms-settings:regionlanguage")),
                    (Loc.T("speech_settings"), () => OpenUrl("ms-settings:speech")));
            }
        }
        catch (Exception ex)
        {
            Native.Log.Write("Speak failed: " + ex.Message);
            ShowBanner(Theme.Glyph.Warning, Loc.T("speak_failed"));
        }
    }

    /// <summary>A language for reading recognized text aloud: the OCR text's script, else the user's language.</summary>
    internal string DetectSpeechLanguage(string text)
    {
        foreach (var ch in text)
        {
            if (ch >= '぀' && ch <= 'ヿ') return "ja";
            if (ch >= '가' && ch <= '힯') return "ko";
            if (ch >= '一' && ch <= '鿿') return "zh-CN";
            if (ch >= 'Ѐ' && ch <= 'ӿ') return "ru";
            if (ch >= '؀' && ch <= 'ۿ') return "ar";
        }
        return UserLanguage.Code;
    }

    internal void OpenGoogleTranslate(string text, string targetCode)
    {
        var tl = targetCode switch { "zh-CN" or "zh-TW" => targetCode, "pt-BR" or "pt-PT" => "pt", "he" => "iw", _ => targetCode };
        if (text.Length > 4500) text = text[..4500];
        OpenUrl($"https://translate.google.com/?sl=auto&tl={Uri.EscapeDataString(tl)}&text={Uri.EscapeDataString(text)}&op=translate");
    }

    internal void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _host.CloseOverlay();
        }
        catch (Exception ex)
        {
            Native.Log.Write($"Open {url}: {ex.Message}");
        }
    }

    internal IEnumerable<Picker.Item> LanguageItems() =>
        Languages.All
            .OrderBy(l => Loc.IsPortuguese ? l.Portuguese : l.English, StringComparer.CurrentCulture)
            .Select(l => new Picker.Item(l.Code, Loc.IsPortuguese ? l.Portuguese : l.English, l.Native));

    internal void Retry(AnswerView view)
    {
        if (_busy) return;
        int index = _messages.Children.IndexOf(view);
        // Ask the last user question again.
        var lastUser = _history.LastOrDefault(t => t.FromUser);
        if (lastUser == null) return;
        if (_history.Count > 0 && !_history[^1].FromUser) _history.RemoveAt(_history.Count - 1);
        if (_history.Count > 0 && _history[^1].FromUser) _history.RemoveAt(_history.Count - 1);
        if (index >= 0) _messages.Children.RemoveAt(index);
        if (index > 0 && _messages.Children[index - 1] is Border) _messages.Children.RemoveAt(index - 1);
        Ask(lastUser.Text, lastUser.Text);
    }
}

/// <summary>One streamed answer, with copy / listen / retry.</summary>
internal sealed class AnswerView : StackPanel
{
    private readonly RichTextBox _body;
    private readonly StringBuilder _text = new();
    private readonly TextBlock _note;
    private readonly StackPanel _actions;
    private readonly FrameworkElement _typing;
    private readonly DispatcherTimer _render;
    private readonly IconButton _speak;
    private readonly TextBlock _status;
    private bool _dirty;

    public string Text => _text.ToString();

    public AnswerView(string speakLanguage, CardView card)
    {
        Margin = new Thickness(0, 0, 0, 12);
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(Ui.Sparkle(14));
        _typing = TypingDots();
        _typing.Margin = new Thickness(10, 0, 0, 0);
        head.Children.Add(_typing);
        _status = Ui.Label("", 12, Theme.Muted);
        _status.Margin = new Thickness(10, 0, 0, 0);
        _status.TextWrapping = TextWrapping.NoWrap;
        head.Children.Add(_status);
        Children.Add(head);

        _body = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.Text,
            SelectionBrush = Theme.Selection,
            Padding = new Thickness(0),
            Margin = new Thickness(-2, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            FocusVisualStyle = null,
            Document = MiniMarkdown.Render(""),
        };
        Children.Add(_body);

        _note = Ui.Label("", 12, Theme.Faint);
        _note.Visibility = Visibility.Collapsed;
        _note.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(_note);

        _actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 4, 0, 0), Visibility = Visibility.Collapsed };
        var copy = new IconButton(Theme.Glyph.Copy, Loc.T("copy"), 28, 12);
        copy.Click += () =>
        {
            Clipboard2.SetText(MiniMarkdown.ToPlain(Text));
            copy.Glyph = Theme.Glyph.Check;
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) => { copy.Glyph = Theme.Glyph.Copy; reset.Stop(); };
            reset.Start();
        };
        _speak = new IconButton(Theme.Glyph.Volume, Loc.T("listen"), 28, 12);
        _speak.Click += () => _ = card.SpeakAsync(MiniMarkdown.ToPlain(Text), speakLanguage, this);
        var retry = new IconButton(Theme.Glyph.Refresh, Loc.T("retry"), 28, 12);
        retry.Click += () => card.Retry(this);
        _actions.Children.Add(copy);
        _actions.Children.Add(_speak);
        _actions.Children.Add(retry);
        Children.Add(_actions);

        _render = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _render.Tick += (_, _) => Flush();
        _render.Start();
        SpeechService.SpeakingEnded += OnSpeakingEnded;
        Unloaded += (_, _) => SpeechService.SpeakingEnded -= OnSpeakingEnded;
    }

    private void OnSpeakingEnded() => Dispatcher.BeginInvoke(() => _speak.Glyph = Theme.Glyph.Volume);

    public void Append(string piece)
    {
        _text.Append(piece);
        _dirty = true;
        if (_body.Visibility != Visibility.Visible)
        {
            _body.Visibility = Visibility.Visible;
            _typing.Visibility = Visibility.Collapsed;
        }
    }

    private void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        _body.Document = MiniMarkdown.Render(_text.ToString());
    }

    public void Complete(string? note)
    {
        _render.Stop();
        _dirty = true;
        Flush();
        _typing.Visibility = Visibility.Collapsed;
        _status.Text = "";
        if (_text.Length > 0)
        {
            _body.Visibility = Visibility.Visible;
            _actions.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(note))
        {
            _note.Text = note;
            _note.Visibility = Visibility.Visible;
        }
    }

    public void Fail(string message)
    {
        _render.Stop();
        Flush();
        _typing.Visibility = Visibility.Collapsed;
        _status.Text = "";
        _note.Text = message;
        _note.Foreground = Theme.Danger;
        _note.Visibility = Visibility.Visible;
        _actions.Visibility = Visibility.Visible;
    }

    public void MarkSpeaking() => _speak.Glyph = Theme.Glyph.Stop;

    /// <summary>Shows what's happening before text arrives (e.g. the local model loading).</summary>
    public void SetStatus(string? status) => _status.Text = status ?? "";

    /// <summary>Clears the answer so it can stream again (used when a translation is retried).</summary>
    public void Reset()
    {
        _text.Clear();
        _dirty = true;
        Flush();
        _body.Visibility = Visibility.Collapsed;
        _actions.Visibility = Visibility.Collapsed;
        _note.Visibility = Visibility.Collapsed;
        _note.Foreground = Theme.Faint;
        _typing.Visibility = Visibility.Visible;
        _render.Start();
    }

    private static FrameworkElement TypingDots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++)
        {
            var dot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Theme.Accent,
                Margin = new Thickness(0, 0, 4, 0),
                Opacity = 0.3,
            };
            var pulse = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(480))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(i * 160),
            };
            dot.BeginAnimation(OpacityProperty, pulse);
            row.Children.Add(dot);
        }
        return row;
    }
}

/// <summary>Clipboard access that survives another app holding the clipboard for a moment.</summary>
internal static class Clipboard2
{
    public static void SetText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
    }
}
