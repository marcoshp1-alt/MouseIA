using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MouseIA.Core;
using MouseIA.Services;

namespace MouseIA.UI;

/// <summary>Speak in one language, hear it in another.</summary>
internal sealed class VoicePanel : Border
{
    private readonly AppSettings _settings;
    private readonly CardView _card;
    private readonly Picker _from;
    private readonly Picker _to;
    private readonly Clickable _micButton;
    private readonly TextBlock _micIcon;
    private readonly Border _pulse;
    private readonly TextBlock _status;
    private readonly GlassField _typed;
    private readonly StackPanel _result;
    private CancellationTokenSource? _cts;
    private bool _listening;

    public VoicePanel(AppSettings settings, CardView card)
    {
        _settings = settings;
        _card = card;
        CornerRadius = new CornerRadius(18);
        Background = Theme.B("#10FFFFFF");
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 12, 14, 14);
        Margin = new Thickness(0, 0, 0, 8);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        title.Children.Add(Ui.Sparkle(13));
        var titleText = Ui.Label(Loc.T("voice_translator"), 13.5, Theme.Text, FontWeights.SemiBold);
        titleText.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(titleText);

        var fromItems = new List<Picker.Item> { new("auto", Loc.T("windows_language")) };
        fromItems.AddRange(card.LanguageItems());
        _from = new Picker(fromItems, settings.VoiceInputLanguage, compact: true);
        var target = Languages.Resolve(settings.TranslateTo, CultureInfo.CurrentUICulture);
        var fromLanguage = FromCode();
        if (target.Code.Split('-')[0] == fromLanguage.Split('-')[0]) target = Languages.Find("en")!;
        _to = new Picker(card.LanguageItems(), target.Code, compact: true);
        var swap = new IconButton("", Loc.T("swap_languages"), 30, 12) { Margin = new Thickness(6, 0, 6, 0) };
        swap.Click += () =>
        {
            var from = _from.Selected == "auto" ? Languages.FromCulture(CultureInfo.CurrentUICulture).Code : _from.Selected;
            var to = _to.Selected;
            _from.Selected = to;
            _to.Selected = from;
            _settings.VoiceInputLanguage = to;
        };
        _from.Changed += code => _settings.VoiceInputLanguage = code;
        var languages = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        languages.ColumnDefinitions.Add(new ColumnDefinition());
        languages.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        languages.ColumnDefinitions.Add(new ColumnDefinition());
        languages.Children.Add(_from);
        Grid.SetColumn(swap, 1);
        languages.Children.Add(swap);
        Grid.SetColumn(_to, 2);
        languages.Children.Add(_to);

        _pulse = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = Theme.AccentSoft,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        _micIcon = Ui.Icon(Theme.Glyph.Mic, 22, Brushes.White);
        _micButton = new Clickable
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            NormalBrush = Theme.Accent,
            HoverBrush = Theme.Gradient(45, Theme.C("#9D74FF"), Theme.C("#45DDF2")),
            Child = _micIcon,
            ToolTip = Loc.T("tap_and_speak"),
            Effect = Theme.Glow(Theme.AccentA, 28, 0.5),
        };
        _micButton.Click += () => _ = MicAsync();
        var micHolder = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        micHolder.Children.Add(_pulse);
        micHolder.Children.Add(_micButton);

        _status = Ui.Label(Loc.T("tap_and_speak"), 12.5, Theme.Muted);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.TextAlignment = TextAlignment.Center;
        _status.Margin = new Thickness(0, 8, 0, 10);

        _typed = new GlassField(Loc.T("or_type_here"));
        _typed.Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _typed.Text.Trim().Length > 0)
            {
                e.Handled = true;
                _ = TranslateAsync(_typed.Text.Trim());
            }
        };

        _result = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(languages);
        stack.Children.Add(micHolder);
        stack.Children.Add(_status);
        stack.Children.Add(_typed);
        stack.Children.Add(_result);
        Child = stack;
    }

    public bool HasPopupOpen => _from.IsOpen || _to.IsOpen;

    private string FromCode() =>
        _from.Selected == "auto" || string.IsNullOrEmpty(_from.Selected) ? SpeechService.SystemLanguageTag() : _from.Selected;

    public void Cancel()
    {
        _cts?.Cancel();
        SpeechService.Stop();
        SetListening(false);
    }

    private void SetListening(bool on)
    {
        _listening = on;
        _micIcon.Text = on ? Theme.Glyph.Stop : Theme.Glyph.Mic;
        var scale = (ScaleTransform)_pulse.RenderTransform;
        if (on)
        {
            _status.Text = Loc.T("listening");
            _pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
            var grow = new DoubleAnimation(1, 1.7, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        else
        {
            _pulse.BeginAnimation(OpacityProperty, null);
            _pulse.Opacity = 0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
    }

    private async Task MicAsync()
    {
        if (_listening)
        {
            Cancel();
            _status.Text = Loc.T("tap_and_speak");
            return;
        }
        SpeechService.Stop();
        _cts = new CancellationTokenSource();
        SetListening(true);
        SpeechService.ListenResult result;
        try
        {
            result = await SpeechService.ListenAsync(FromCode(), _cts.Token);
        }
        finally
        {
            SetListening(false);
        }
        if (result.Error != SpeechService.ListenError.None)
        {
            _status.Text = Loc.T("tap_and_speak");
            _card.HandleListenError(result, () =>
            {
                _typed.Box.Focus();
                Native.VoiceTyping.Start();
            });
            return;
        }
        if (result.Text.Trim().Length == 0)
        {
            _status.Text = Loc.T("didnt_hear");
            return;
        }
        _typed.Text = result.Text.Trim();
        await TranslateAsync(result.Text.Trim());
    }

    private async Task TranslateAsync(string text)
    {
        var target = Languages.Find(_to.Selected) ?? Languages.Find("en")!;
        _result.Children.Clear();
        if (!_card.HasAi)
        {
            _status.Text = Loc.T("tap_and_speak");
            var google = new SecondaryButton(Loc.T("translate_google"), Theme.Glyph.Globe);
            google.Click += () => _card.OpenGoogleTranslate(text, target.Code);
            _result.Children.Add(Ui.Label(Loc.T("needs_ai_voice"), 12.5, Theme.Muted));
            google.Margin = new Thickness(0, 8, 0, 0);
            google.HorizontalAlignment = HorizontalAlignment.Left;
            _result.Children.Add(google);
            return;
        }
        _status.Text = Loc.T("translating");
        _cts = new CancellationTokenSource();
        string? translation;
        try
        {
            translation = await _card.TranslateTextAsync(text, target, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = Loc.T("tap_and_speak");
            return;
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = Loc.T("tap_and_speak");
            _result.Children.Add(Ui.Label(ex.Message, 12.5, Theme.Danger));
            return;
        }
        _status.Text = Loc.T("tap_to_continue");
        if (string.IsNullOrWhiteSpace(translation)) return;

        var original = Ui.Label(text, 12.5, Theme.Faint);
        var translated = new TextBox
        {
            Text = translation,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.Text,
            SelectionBrush = Theme.Selection,
            FontFamily = Theme.DisplayFont,
            FontSize = 18,
            Margin = new Thickness(-2, 4, 0, 4),
            FocusVisualStyle = null,
        };
        var listen = new IconButton(Theme.Glyph.Volume, Loc.T("listen"), 30, 13);
        listen.Click += () => _ = _card.SpeakAsync(translation!, target.Code, null);
        var copy = new IconButton(Theme.Glyph.Copy, Loc.T("copy"), 30, 13);
        copy.Click += () => Clipboard2.SetText(translation!);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-6, 0, 0, 0) };
        actions.Children.Add(listen);
        actions.Children.Add(copy);
        _result.Children.Add(original);
        _result.Children.Add(translated);
        _result.Children.Add(actions);
        Ui.PopIn(_result, 0.98);
        await _card.SpeakAsync(translation, target.Code, null);
    }
}
