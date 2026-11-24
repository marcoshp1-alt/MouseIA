using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MouseIA.UI;

/// <summary>A small dark confirmation window, with an optional extra choice ("also delete the cache").</summary>
internal sealed class ConfirmDialog : Window
{
    private bool _confirmed;
    private bool _option;

    private ConfirmDialog(Window owner, string title, string message, string confirmText, string? optionText, bool optionDefault,
        Func<bool, string>? summary)
    {
        Owner = owner;
        Title = title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = Theme.Font;
        _option = optionDefault;

        var stack = new StackPanel();
        var heading = Ui.Label(title, 17, Theme.Text, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(heading);
        var body = Ui.Label(message, 13, Theme.Muted);
        body.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(body);

        var total = Ui.Label("", 13, Theme.Text, FontWeights.SemiBold);
        total.TextWrapping = TextWrapping.Wrap;
        total.Margin = new Thickness(0, 12, 0, 0);
        void UpdateSummary() => total.Text = summary?.Invoke(_option) ?? "";

        if (optionText != null)
        {
            var toggle = new ToggleSwitch(optionDefault) { VerticalAlignment = VerticalAlignment.Center };
            toggle.Changed += v => { _option = v; UpdateSummary(); };
            var label = Ui.Label(optionText, 13, Theme.Text);
            label.TextWrapping = TextWrapping.Wrap;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(10, 0, 0, 0);
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(toggle, Dock.Left);
            row.Children.Add(toggle);
            row.Children.Add(label);
            stack.Children.Add(row);
        }
        if (summary != null) stack.Children.Add(total);
        UpdateSummary();

        var cancel = new SecondaryButton(Loc.T("cancel"));
        cancel.Click += Close;
        var confirm = new PrimaryButton(confirmText) { Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += () => { _confirmed = true; Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(18),
            Background = Theme.SolidSurface,
            BorderBrush = Theme.FieldStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 20, 22, 18),
            Child = stack,
            Effect = Theme.Shadow(),
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
    }

    /// <returns>Confirmed, and the state of the extra choice.</returns>
    public static (bool Confirmed, bool Option) Ask(Window owner, string title, string message, string confirmText,
        string? optionText = null, bool optionDefault = false, Func<bool, string>? summary = null)
    {
        var dialog = new ConfirmDialog(owner, title, message, confirmText, optionText, optionDefault, summary);
        dialog.ShowDialog();
        return (dialog._confirmed, dialog._option);
    }
}
