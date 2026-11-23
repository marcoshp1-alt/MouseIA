using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MouseIA.UI;

/// <summary>Small factory helpers for text and icons.</summary>
internal static class Ui
{
    public static TextBlock Icon(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Theme.IconFont,
        FontSize = size,
        Foreground = brush ?? Theme.Text,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static TextBlock Label(string text, double size = 13, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        Foreground = brush ?? Theme.Text,
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>The ✦ mark, painted with the accent gradient.</summary>
    public static TextBlock Sparkle(double size = 15) => new()
    {
        Text = "✦",
        FontSize = size,
        Foreground = Theme.Accent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static void Fade(UIElement element, double to, double ms = 160, Action? done = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (done != null) animation.Completed += (_, _) => done();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Pops an element in with a small scale + fade.</summary>
    public static void PopIn(FrameworkElement element, double fromScale = 0.96)
    {
        var scale = new ScaleTransform(fromScale, fromScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        element.Opacity = 0;
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Fade(element, 1, 180);
    }
}

/// <summary>A rounded clickable surface with hover and press feedback.</summary>
internal class Clickable : Border
{
    private bool _pressed;
    private Brush _normal = Brushes.Transparent;
    private Brush _hover = Theme.Hover;

    public event Action? Click;

    public Clickable()
    {
        Cursor = Cursors.Hand;
        Background = _normal;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
    }

    public Brush NormalBrush
    {
        get => _normal;
        set { _normal = value; if (!IsMouseOver) Background = value; }
    }

    public Brush HoverBrush
    {
        get => _hover;
        set { _hover = value; if (IsMouseOver) Background = value; }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (IsEnabled) Background = _hover;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Background = _normal;
        _pressed = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        _pressed = true;
        Background = Theme.Pressed;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        Background = IsMouseOver ? _hover : _normal;
        e.Handled = true;
        if (IsEnabled && IsMouseOver) Click?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsEnabled && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            Click?.Invoke();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsEnabledProperty) Opacity = (bool)e.NewValue ? 1 : 0.4;
    }

    public void RaiseClick() => Click?.Invoke();
}

internal sealed class IconButton : Clickable
{
    private readonly TextBlock _icon;

    public IconButton(string glyph, string tooltip, double size = 32, double iconSize = 14)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        ToolTip = tooltip;
        _icon = Ui.Icon(glyph, iconSize, Theme.Muted);
        Child = _icon;
        MouseEnter += (_, _) => _icon.Foreground = Theme.Text;
        MouseLeave += (_, _) => _icon.Foreground = IconBrush ?? Theme.Muted;
    }

    public Brush? IconBrush { get; set; }

    public string Glyph
    {
        get => _icon.Text;
        set => _icon.Text = value;
    }

    public void SetIconBrush(Brush brush)
    {
        IconBrush = brush;
        _icon.Foreground = brush;
    }
}

/// <summary>Rounded pill with an icon and a label.</summary>
internal sealed class PillButton : Clickable
{
    private readonly TextBlock _label;
    private readonly TextBlock _icon;

    public PillButton(string glyph, string text, string? tooltip = null)
    {
        Height = 32;
        CornerRadius = new CornerRadius(16);
        Padding = new Thickness(11, 0, 13, 0);
        NormalBrush = Theme.Field;
        HoverBrush = Theme.Hover;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        if (tooltip != null) ToolTip = tooltip;
        _icon = Ui.Icon(glyph, 13, Theme.Muted);
        _label = Ui.Label(text, 12.5);
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.Margin = new Thickness(7, 0, 0, 1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_icon);
        row.Children.Add(_label);
        Child = row;
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    public string Glyph
    {
        get => _icon.Text;
        set => _icon.Text = value;
    }

    public void SetActive(bool active)
    {
        NormalBrush = active ? Theme.AccentSoft : Theme.Field;
        _icon.Foreground = active ? Theme.Text : Theme.Muted;
    }
}

/// <summary>Suggestion chip: ✦ + short prompt.</summary>
internal sealed class ChipButton : Clickable
{
    public string Prompt { get; }

    public ChipButton(string text, string? prompt = null)
    {
        Prompt = prompt ?? text;
        Height = 30;
        CornerRadius = new CornerRadius(15);
        Padding = new Thickness(10, 0, 12, 0);
        Margin = new Thickness(0, 0, 6, 6);
        NormalBrush = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Sparkle(12));
        var label = Ui.Label(text, 12.5);
        label.TextWrapping = TextWrapping.NoWrap;
        label.Margin = new Thickness(6, 0, 0, 1);
        row.Children.Add(label);
        Child = row;
    }
}

/// <summary>Filled accent button.</summary>
internal sealed class PrimaryButton : Clickable
{
    private readonly TextBlock _label;

    public PrimaryButton(string text, string? glyph = null)
    {
        Height = 36;
        CornerRadius = new CornerRadius(18);
        Padding = new Thickness(18, 0, 18, 0);
        NormalBrush = Theme.Accent;
        HoverBrush = Theme.Gradient(45, Theme.C("#9D74FF"), Theme.C("#45DDF2"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
        {
            var icon = Ui.Icon(glyph, 13, Brushes.White);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
        }
        _label = Ui.Label(text, 13, Brushes.White, FontWeights.SemiBold);
        _label.TextWrapping = TextWrapping.NoWrap;
        row.Children.Add(_label);
        Child = row;
        Effect = Theme.Glow(Theme.AccentA, 18, 0.35);
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}

/// <summary>Quiet outlined button.</summary>
internal sealed class SecondaryButton : Clickable
{
    private readonly TextBlock _label;

    public SecondaryButton(string text, string? glyph = null)
    {
        Height = 34;
        CornerRadius = new CornerRadius(17);
        Padding = new Thickness(14, 0, 14, 0);
        NormalBrush = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
        {
            var icon = Ui.Icon(glyph, 12.5, Theme.Muted);
            icon.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(icon);
        }
        _label = Ui.Label(text, 12.5);
        _label.TextWrapping = TextWrapping.NoWrap;
        row.Children.Add(_label);
        Child = row;
    }

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }
}

/// <summary>On/off switch.</summary>
internal sealed class ToggleSwitch : Clickable
{
    private readonly Border _knob;
    private readonly Border _track;
    private bool _isOn;

    public event Action<bool>? Changed;

    public ToggleSwitch(bool isOn = false)
    {
        Width = 42;
        Height = 24;
        CornerRadius = new CornerRadius(12);
        HoverBrush = Brushes.Transparent;
        _track = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        _knob = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
        };
        var grid = new Grid();
        grid.Children.Add(_track);
        grid.Children.Add(_knob);
        Child = grid;
        _isOn = isOn;
        Apply(animate: false);
        Click += () => IsOn = !IsOn;
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Apply(animate: true);
            Changed?.Invoke(value);
        }
    }

    private void Apply(bool animate)
    {
        _track.Background = _isOn ? Theme.Accent : Theme.Field;
        _track.BorderBrush = _isOn ? Brushes.Transparent : Theme.FieldStroke;
        _knob.Background = _isOn ? Brushes.White : Theme.Muted;
        var to = new Thickness(_isOn ? 22 : 4, 0, 0, 0);
        if (!animate)
        {
            _knob.Margin = to;
            return;
        }
        _knob.BeginAnimation(MarginProperty, new ThicknessAnimation(to, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}

/// <summary>A row of mutually exclusive options.</summary>
internal sealed class Segmented : Border
{
    private readonly List<(string Key, Clickable Button, TextBlock Label)> _items = new();
    private string _selected = "";

    public event Action<string>? Changed;

    public Segmented(IEnumerable<(string Key, string Label)> options, string selected)
    {
        CornerRadius = new CornerRadius(18);
        Background = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label) in options)
        {
            var text = Ui.Label(label, 12.5);
            text.TextWrapping = TextWrapping.NoWrap;
            var button = new Clickable
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(14, 6, 14, 7),
                Child = text,
            };
            var captured = key;
            button.Click += () => Selected = captured;
            _items.Add((key, button, text));
            row.Children.Add(button);
        }
        Child = row;
        _selected = selected;
        Refresh();
    }

    public string Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Refresh();
            Changed?.Invoke(value);
        }
    }

    private void Refresh()
    {
        foreach (var (key, button, label) in _items)
        {
            bool on = key == _selected;
            button.NormalBrush = on ? Theme.Accent : Brushes.Transparent;
            button.HoverBrush = on ? Theme.Accent : Theme.Hover;
            label.Foreground = on ? Brushes.White : Theme.Muted;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }
}

/// <summary>Rounded text field that lights up when focused.</summary>
internal sealed class GlassField : Border
{
    public TextBox Box { get; }

    public GlassField(string placeholder, string text = "", bool multiline = false)
    {
        CornerRadius = new CornerRadius(12);
        Background = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 7, 10, 7);
        Box = CreateBox(text, multiline);
        var hint = Ui.Label(placeholder, 13, Theme.Faint);
        hint.IsHitTestVisible = false;
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.Margin = new Thickness(3, 0, 0, 0);
        hint.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        var grid = new Grid();
        grid.Children.Add(hint);
        grid.Children.Add(Box);
        Child = grid;
        void UpdateHint() => hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Box.TextChanged += (_, _) => UpdateHint();
        Box.GotKeyboardFocus += (_, _) => BorderBrush = Theme.Accent;
        Box.LostKeyboardFocus += (_, _) => BorderBrush = Theme.FieldStroke;
        UpdateHint();
    }

    public static TextBox CreateBox(string text, bool multiline) => new()
    {
        Text = text,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.Text,
        CaretBrush = Theme.Text,
        SelectionBrush = Theme.Selection,
        FontFamily = Theme.Font,
        FontSize = 13,
        Padding = new Thickness(0),
        VerticalContentAlignment = VerticalAlignment.Center,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        FocusVisualStyle = null,
    };

    public string Text
    {
        get => Box.Text;
        set => Box.Text = value;
    }
}

/// <summary>Secret field with a show/hide button.</summary>
internal sealed class SecretField : Border
{
    private readonly PasswordBox _password;
    private readonly TextBox _plain;
    private bool _revealed;

    public event Action? Changed;

    public SecretField(string placeholder, string value)
    {
        CornerRadius = new CornerRadius(12);
        Background = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(10, 7, 4, 7);
        _password = new PasswordBox
        {
            Password = value,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.Text,
            CaretBrush = Theme.Text,
            SelectionBrush = Theme.Selection,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
        };
        _plain = GlassField.CreateBox(value, false);
        _plain.Visibility = Visibility.Collapsed;
        var hint = Ui.Label(placeholder, 13, Theme.Faint);
        hint.IsHitTestVisible = false;
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.Margin = new Thickness(3, 0, 0, 0);
        var eye = new IconButton("", Loc.T("show_hide"), 26, 12);
        eye.Click += () =>
        {
            _revealed = !_revealed;
            if (_revealed) _plain.Text = _password.Password; else _password.Password = _plain.Text;
            _plain.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
            _password.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(hint);
        grid.Children.Add(_password);
        grid.Children.Add(_plain);
        Grid.SetColumn(eye, 1);
        grid.Children.Add(eye);
        Child = grid;
        void UpdateHint() => hint.Visibility = Value.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _password.PasswordChanged += (_, _) => { UpdateHint(); Changed?.Invoke(); };
        _plain.TextChanged += (_, _) => { UpdateHint(); Changed?.Invoke(); };
        _password.GotKeyboardFocus += (_, _) => BorderBrush = Theme.Accent;
        _password.LostKeyboardFocus += (_, _) => BorderBrush = Theme.FieldStroke;
        UpdateHint();
    }

    public string Value
    {
        get => (_revealed ? _plain.Text : _password.Password).Trim();
        set { _password.Password = value; _plain.Text = value; }
    }
}

/// <summary>Dropdown with search, used for languages and models.</summary>
internal sealed class Picker : Clickable
{
    public sealed record Item(string Key, string Label, string? Detail = null);

    private readonly TextBlock _label;
    private readonly Popup _popup;
    private readonly StackPanel _list;
    private readonly TextBox _search;
    private IReadOnlyList<Item> _items = Array.Empty<Item>();
    private string _selected = "";

    public event Action<string>? Changed;

    /// <summary>Text shown before the selection, e.g. "→ ".</summary>
    public string Prefix { get; set; } = "";

    public Picker(IEnumerable<Item> items, string selected, bool compact = false, bool searchable = true)
    {
        Height = compact ? 32 : 36;
        CornerRadius = new CornerRadius(compact ? 16 : 12);
        Padding = new Thickness(12, 0, 8, 0);
        NormalBrush = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        _label = Ui.Label("", compact ? 12.5 : 13);
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        var chevron = Ui.Icon(Theme.Glyph.ChevronDown, 10, Theme.Muted);
        chevron.Margin = new Thickness(8, 1, 2, 0);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(_label);
        Child = row;

        _search = GlassField.CreateBox("", false);
        _search.Margin = new Thickness(6, 0, 0, 0);
        var searchRow = new DockPanel { Margin = new Thickness(10, 8, 10, 6) };
        var searchIcon = Ui.Icon(Theme.Glyph.Search, 12, Theme.Faint);
        DockPanel.SetDock(searchIcon, Dock.Left);
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(_search);
        _list = new StackPanel { Margin = new Thickness(4) };
        var scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var panel = new DockPanel();
        if (searchable)
        {
            var divider = new Border { Height = 1, Background = Theme.Divider };
            DockPanel.SetDock(searchRow, Dock.Top);
            DockPanel.SetDock(divider, Dock.Top);
            panel.Children.Add(searchRow);
            panel.Children.Add(divider);
        }
        panel.Children.Add(scroll);
        var surface = new Border
        {
            Background = Theme.B("#F7141825"),
            BorderBrush = Theme.GlassStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = panel,
            MinWidth = 220,
            Margin = new Thickness(12),
            Effect = Theme.Shadow(),
        };
        _popup = new Popup
        {
            Child = surface,
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PlacementTarget = this,
            VerticalOffset = -8,
            HorizontalOffset = -12,
            PopupAnimation = PopupAnimation.Fade,
        };
        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var first = _list.Children.OfType<Clickable>().FirstOrDefault();
                first?.RaiseClick();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _popup.IsOpen = false;
                e.Handled = true;
            }
        };
        _popup.Opened += (_, _) =>
        {
            _search.Text = "";
            Fill();
            surface.MinWidth = Math.Max(220, ActualWidth);
            if (searchable) _search.Focus();
        };
        Click += () => _popup.IsOpen = !_popup.IsOpen;
        SetItems(items, selected);
    }

    public bool IsOpen => _popup.IsOpen;

    public void SetItems(IEnumerable<Item> items, string selected)
    {
        _items = items.ToList();
        _selected = selected;
        UpdateLabel();
    }

    public string Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            UpdateLabel();
        }
    }

    private void UpdateLabel()
    {
        var item = _items.FirstOrDefault(i => i.Key == _selected);
        _label.Text = Prefix + (item?.Label ?? _selected);
        _label.Foreground = item == null && string.IsNullOrEmpty(_selected) ? Theme.Faint : Theme.Text;
    }

    public void SetPlaceholder(string text)
    {
        if (_items.Any(i => i.Key == _selected)) return;
        _label.Text = text;
        _label.Foreground = Theme.Faint;
    }

    private void Fill()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        foreach (var item in _items)
        {
            if (query.Length > 0 &&
                !item.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase) &&
                !(item.Detail?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) &&
                !item.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            bool on = item.Key == _selected;
            var text = new StackPanel();
            text.Children.Add(Ui.Label(item.Label, 13, Theme.Text, on ? FontWeights.SemiBold : FontWeights.Normal));
            if (!string.IsNullOrEmpty(item.Detail)) text.Children.Add(Ui.Label(item.Detail!, 11.5, Theme.Faint));
            var row = new DockPanel();
            var check = Ui.Icon(on ? Theme.Glyph.Check : "", 11, Theme.Accent);
            check.Width = 18;
            DockPanel.SetDock(check, Dock.Right);
            row.Children.Add(check);
            row.Children.Add(text);
            var button = new Clickable
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 6, 6),
                Child = row,
                NormalBrush = on ? Theme.Hover : Brushes.Transparent,
            };
            var key = item.Key;
            button.Click += () =>
            {
                _popup.IsOpen = false;
                if (key == _selected) return;
                Selected = key;
                Changed?.Invoke(key);
            };
            _list.Children.Add(button);
            if (on) button.Loaded += (_, _) => button.BringIntoView();
        }
        if (_list.Children.Count == 0)
            _list.Children.Add(new Border { Padding = new Thickness(10, 8, 10, 8), Child = Ui.Label(Loc.T("nothing_found"), 12.5, Theme.Faint) });
    }
}
