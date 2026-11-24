using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MouseIA.Core;
using MouseIA.Native;
using MouseIA.Services;

namespace MouseIA.UI;

internal sealed class SettingsWindow : Window
{
    private readonly AppController _app;
    private AppSettings _draft;
    private readonly Border _surface;
    private ContentControl _page = new();
    private StackPanel _nav = new();
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer;
    private TextBlock _savedNote = new();
    private IconButton? _maximize;
    private Dictionary<string, Func<FrameworkElement>> _pages = new();
    private readonly Dictionary<string, Clickable> _navButtons = new();
    private string _current = "ai";
    private bool _acrylic;
    private WindowState _lastState = WindowState.Normal;

    public SettingsWindow(AppController app, string? startPage = null)
    {
        _app = app;
        _draft = app.Settings.Clone();
        Width = 900;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // A normal window style (its frame hidden by WindowChrome) keeps Windows' own
        // open, close, minimize and maximize animations.
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        FontFamily = Theme.Font;
        Foreground = Theme.Text;
        UseLayoutRounding = true;
        Icon = app.WindowIcon;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // Changes apply on their own, a moment after the last edit.
        _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Apply();
        };

        _surface = new Border { BorderBrush = Theme.GlassStroke, BorderThickness = new Thickness(1) };
        Content = _surface;
        _current = startPage ?? "ai";
        BuildUi();

        SourceInitialized += (_, _) => ApplyBackdrop();
        Loaded += (_, _) => AnimateIn();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized && _lastState == WindowState.Minimized) AnimateIn();
            _lastState = WindowState;
            if (_maximize != null) _maximize.Glyph = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            // A maximized borderless window spills past the screen edge by the resize border.
            _surface.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closing += (_, _) =>
        {
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                Apply();
            }
        };
    }

    /// <summary>The content glides up and fades in, on top of Windows' own window animation.</summary>
    private void AnimateIn()
    {
        if (_surface.Child is not FrameworkElement content) return;
        var shift = new TranslateTransform(0, 14);
        content.RenderTransform = shift;
        content.Opacity = 0;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        shift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        Ui.Fade(content, 1, 260);
    }

    /// <summary>Builds every text again, e.g. after the app language changes.</summary>
    public void Rebuild()
    {
        _draft = _app.Settings.Clone();
        BuildUi();
    }

    private void BuildUi()
    {
        Title = Loc.T("settings_title");
        _pages = new()
        {
            ["ai"] = AiPage,
            ["activation"] = ActivationPage,
            ["voice"] = VoicePage,
            ["general"] = GeneralPage,
            ["updates"] = UpdatesPage,
            ["about"] = AboutPage,
        };
        // Fresh containers: the old ones still belong to the previous layout.
        _page = new ContentControl();
        _nav = new StackPanel();
        _navButtons.Clear();

        // ---- sidebar ----
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 18, 18, 22) };
        var mark = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = Theme.Accent,
            Child = Ui.Icon(Theme.Glyph.Mouse, 15, Brushes.White),
            Effect = Theme.Glow(Theme.AccentA, 16, 0.5),
        };
        logo.Children.Add(mark);
        var name = Ui.Label("MouseIA", 17, Theme.Text, FontWeights.SemiBold);
        name.FontFamily = Theme.DisplayFont;
        name.Margin = new Thickness(10, 0, 0, 2);
        logo.Children.Add(name);
        logo.MouseLeftButtonDown += (_, e) => DragFromTitle(e);

        AddNav("ai", Theme.Glyph.Robot, Loc.T("nav_ai"));
        AddNav("activation", Theme.Glyph.Mouse, Loc.T("nav_activation"));
        AddNav("voice", Theme.Glyph.Globe, Loc.T("nav_voice"));
        AddNav("general", Theme.Glyph.Settings, Loc.T("nav_general"));
        AddNav("updates", "\uE895", Loc.T("nav_updates"));
        AddNav("about", Theme.Glyph.Heart, Loc.T("nav_about"));

        var sidebar = new DockPanel { Width = 220, Background = Theme.B("#0AFFFFFF") };
        DockPanel.SetDock(logo, Dock.Top);
        sidebar.Children.Add(logo);
        var shortcut = Ui.Label(Loc.Format("sidebar_hint", _app.Settings.Hotkey), 11.5, Theme.Faint);
        shortcut.Margin = new Thickness(20, 0, 16, 18);
        DockPanel.SetDock(shortcut, Dock.Bottom);
        sidebar.Children.Add(shortcut);
        sidebar.Children.Add(_nav);

        // ---- title bar: minimize, maximize, close in the top-right corner ----
        var minimize = CaptionButton("\uE921", Loc.T("minimize"), danger: false);
        minimize.Click += () => WindowState = WindowState.Minimized;
        _maximize = CaptionButton(WindowState == WindowState.Maximized ? "\uE923" : "\uE922", Loc.T("maximize"), danger: false);
        _maximize.Click += () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var close = CaptionButton("\uE8BB", Loc.T("close"), danger: true);
        close.Click += Close;
        var captions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _savedNote = Ui.Label("", 12, Theme.Success);
        _savedNote.Margin = new Thickness(0, 0, 14, 0);
        captions.Children.Add(_savedNote);
        captions.Children.Add(minimize);
        captions.Children.Add(_maximize);
        captions.Children.Add(close);
        var titleBar = new DockPanel { Height = 40, Background = Brushes.Transparent, LastChildFill = true };
        DockPanel.SetDock(captions, Dock.Right);
        titleBar.Children.Add(captions);
        titleBar.Children.Add(new Border { Background = Brushes.Transparent });
        titleBar.MouseLeftButtonDown += (_, e) => DragFromTitle(e);

        var scroll = new ScrollViewer
        {
            Content = _page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(32, 0, 32, 28),
        };
        var main = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        main.Children.Add(titleBar);
        main.Children.Add(scroll);

        var layout = new DockPanel();
        DockPanel.SetDock(sidebar, Dock.Left);
        layout.Children.Add(sidebar);
        var divider = new Border { Width = 1, Background = Theme.Divider };
        DockPanel.SetDock(divider, Dock.Left);
        layout.Children.Add(divider);
        layout.Children.Add(main);
        _surface.Child = layout;
        ApplySurface();
        ShowPage(_current);
    }

    private IconButton CaptionButton(string glyph, string tooltip, bool danger)
    {
        var button = new IconButton(glyph, tooltip, 46, 10)
        {
            Width = 46,
            Height = 40,
            CornerRadius = new CornerRadius(danger ? 0 : 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (danger) button.HoverBrush = Theme.B("#FFC42B1C");
        return button;
    }

    private void DragFromTitle(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Background of the window, following the glass settings.</summary>
    private void ApplySurface()
    {
        double opacity = _draft.GlassEffect ? _draft.GlassOpacity : 1.0;
        if (_acrylic && _draft.GlassEffect)
        {
            var top = Theme.C("#141826");
            var bottom = Theme.C("#0B0E17");
            byte a = (byte)Math.Round(255 * Math.Clamp(opacity * 0.85, 0.25, 0.97));
            _surface.Background = Theme.Vertical(Color.FromArgb(a, top.R, top.G, top.B), Color.FromArgb((byte)Math.Min(255, a + 20), bottom.R, bottom.G, bottom.B));
            _surface.BorderThickness = new Thickness(0);
        }
        else
        {
            _surface.Background = Theme.SolidSurface;
            _surface.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>Marks the draft as changed; it is applied and saved shortly after.</summary>
    private void Changed()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Apply()
    {
        bool languageChanged = _draft.UiLanguage != _app.Settings.UiLanguage;
        _app.ApplySettings(_draft);
        _draft = _app.Settings.Clone();
        if (languageChanged)
        {
            BuildUi();
        }
        ApplySurface();
        _savedNote.Text = Loc.T("saved");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { _savedNote.Text = ""; timer.Stop(); };
        timer.Start();
    }

    /// <summary>Real acrylic on Windows 11; a solid dark surface elsewhere.</summary>
    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        if (Environment.OSVersion.Version.Build >= 22621)
        {
            int acrylic = 3; // DWMSBT_TRANSIENTWINDOW
            if (Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int)) == 0)
            {
                if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                    target.BackgroundColor = Colors.Transparent;
                _acrylic = true;
                ApplySurface();
            }
        }
    }

    private void AddNav(string key, string glyph, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon(glyph, 14, Theme.Muted);
        icon.Width = 20;
        row.Children.Add(icon);
        var text = Ui.Label(label, 13.5);
        text.Margin = new Thickness(10, 0, 0, 1);
        row.Children.Add(text);
        var button = new Clickable
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(10, 0, 10, 2),
            Child = row,
        };
        button.Click += () => ShowPage(key);
        _navButtons[key] = button;
        _nav.Children.Add(button);
    }

    private void ShowPage(string key)
    {
        _current = key;
        foreach (var (k, button) in _navButtons)
        {
            bool on = k == key;
            button.NormalBrush = on ? Theme.Hover : Brushes.Transparent;
            if (button.Child is StackPanel row && row.Children[0] is TextBlock icon)
                icon.Foreground = on ? Theme.Accent : Theme.Muted;
        }
        _page.Content = _pages[key]();
        if (_page.Content is FrameworkElement element) Ui.PopIn(element, 0.99);
    }

    // ---- building blocks ----

    private static StackPanel Page(string title, string subtitle)
    {
        var page = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        var heading = Ui.Label(title, 24, Theme.Text, FontWeights.SemiBold);
        heading.FontFamily = Theme.DisplayFont;
        page.Children.Add(heading);
        var sub = Ui.Label(subtitle, 13, Theme.Muted);
        sub.Margin = new Thickness(0, 4, 0, 18);
        page.Children.Add(sub);
        return page;
    }

    private static Border Section(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return new Border
        {
            Background = Theme.B("#0DFFFFFF"),
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18, 14, 18, 16),
            Margin = new Thickness(0, 0, 0, 14),
            Child = stack,
        };
    }

    private static FrameworkElement Field(string label, string? help, UIElement control)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };
        stack.Children.Add(Ui.Label(label, 13, Theme.Text, FontWeights.SemiBold));
        if (help != null)
        {
            var h = Ui.Label(help, 12, Theme.Muted);
            h.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(h);
        }
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(control);
        return stack;
    }

    private static FrameworkElement ToggleRow(string label, string? help, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch(value) { VerticalAlignment = VerticalAlignment.Center };
        toggle.Changed += changed;
        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Ui.Label(label, 13, Theme.Text, FontWeights.SemiBold));
        if (help != null)
        {
            var h = Ui.Label(help, 12, Theme.Muted);
            h.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(h);
        }
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 8) };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(text);
        return row;
    }

    private static Slider DarkSlider(double min, double max, double value, double step) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        TickFrequency = step,
        IsSnapToTickEnabled = true,
        Width = 260,
        HorizontalAlignment = HorizontalAlignment.Left,
        Foreground = Theme.Accent,
    };

    private static TextBlock Link(string text, string url)
    {
        var link = Ui.Label(text, 12.5, Theme.B("#7DE6F5"));
        link.Cursor = Cursors.Hand;
        link.TextDecorations = TextDecorations.Underline;
        link.MouseLeftButtonUp += (_, _) => OpenUrl(url);
        return link;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Open {url}: {ex.Message}"); }
    }

    // ---- pages ----

    private FrameworkElement AiPage()
    {
        var page = Page(Loc.T("nav_ai"), Loc.T("ai_subtitle"));
        var details = new ContentControl();

        var cards = new UniformGrid2(2);
        var providerButtons = new Dictionary<string, Clickable>();
        void SelectProvider(string id)
        {
            if (_draft.Provider != id) { _draft.Provider = id; Changed(); }
            foreach (var (key, button) in providerButtons)
            {
                bool on = key == id;
                button.BorderBrush = on ? Theme.Accent : Theme.FieldStroke;
                button.BorderThickness = new Thickness(on ? 1.5 : 1);
                button.NormalBrush = on ? Theme.B("#1A8B5CF6") : Theme.Field;
            }
            details.Content = id == ProviderIds.Local ? LocalPanel() : CloudDetails(id);
        }
        foreach (var (id, glyph, title, help) in new[]
                 {
                     (ProviderIds.Gemini, Theme.Glyph.Cloud, "Gemini", Loc.T("gemini_help")),
                     (ProviderIds.OpenAi, Theme.Glyph.Cloud, "ChatGPT", Loc.T("openai_help")),
                     (ProviderIds.Claude, Theme.Glyph.Cloud, "Claude", Loc.T("claude_help")),
                     (ProviderIds.Grok, Theme.Glyph.Cloud, "Grok", Loc.T("grok_help")),
                     (ProviderIds.Local, Theme.Glyph.Pc, Loc.T("local_ai"), Loc.T("local_help")),
                 })
        {
            var text = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Ui.Icon(glyph, 14, Theme.Accent);
            icon.Margin = new Thickness(0, 0, 8, 0);
            head.Children.Add(icon);
            head.Children.Add(Ui.Label(title, 14, Theme.Text, FontWeights.SemiBold));
            text.Children.Add(head);
            var helpText = Ui.Label(help, 12, Theme.Muted);
            helpText.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(helpText);
            var card = new Clickable
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 10, 10),
                BorderThickness = new Thickness(1),
                Child = text,
            };
            var captured = id;
            card.Click += () => SelectProvider(captured);
            providerButtons[id] = card;
            cards.Add(card);
        }
        page.Children.Add(Section(Ui.Label(Loc.T("choose_ai"), 13, Theme.Text, FontWeights.SemiBold), Spacer(10), cards));
        page.Children.Add(details);
        SelectProvider(_draft.Provider);
        return page;
    }

    private static FrameworkElement Spacer(double height) => new Border { Height = height };

    private FrameworkElement CloudDetails(string provider)
    {
        var (keyUrl, keyHint) = provider switch
        {
            ProviderIds.OpenAi => ("https://platform.openai.com/api-keys", "sk-..."),
            ProviderIds.Claude => ("https://platform.claude.com/settings/keys", "sk-ant-..."),
            ProviderIds.Grok => ("https://console.x.ai", "xai-..."),
            _ => ("https://aistudio.google.com/apikey", "AIza..."),
        };
        var key = new SecretField(keyHint, _draft.KeyFor(provider));
        void StoreKey()
        {
            switch (provider)
            {
                case ProviderIds.OpenAi: _draft.OpenAiKey = key.Value; break;
                case ProviderIds.Claude: _draft.ClaudeKey = key.Value; break;
                case ProviderIds.Grok: _draft.GrokKey = key.Value; break;
                default: _draft.GeminiKey = key.Value; break;
            }
            Changed();
        }
        key.Changed += StoreKey;

        var keyLink = Link(Loc.T("get_key"), keyUrl);
        keyLink.Margin = new Thickness(0, 6, 0, 0);
        var quality = new Segmented(new[]
        {
            ("fast", Loc.T("quality_fast")),
            ("balanced", Loc.T("quality_balanced")),
            ("smart", Loc.T("quality_smart")),
        }, _draft.Quality);
        quality.Changed += q => { _draft.Quality = q; Changed(); };

        var status = Ui.Label("", 12.5, Theme.Muted);
        status.Margin = new Thickness(12, 0, 0, 0);
        var test = new SecondaryButton(Loc.T("test_connection"), Theme.Glyph.Play);
        test.Click += () =>
        {
            StoreKey();
            _ = TestAsync(status);
        };
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        testRow.Children.Add(test);
        testRow.Children.Add(status);

        var keyBlock = new StackPanel();
        keyBlock.Children.Add(key);
        keyBlock.Children.Add(keyLink);
        return Section(
            Field(Loc.Format("api_key_for", ModelCatalog.DisplayName(provider)), Loc.T("api_key_help"), keyBlock),
            Field(Loc.T("quality"), Loc.T("quality_help"), quality),
            testRow);
    }

    private FrameworkElement ServerDetails()
    {
        var url = new GlassField("http://127.0.0.1:11434/v1", _draft.LocalUrl);
        url.Box.TextChanged += (_, _) => { _draft.LocalUrl = url.Text.Trim(); Changed(); };
        var model = new Picker(Array.Empty<Picker.Item>(), _draft.LocalModel);
        if (string.IsNullOrEmpty(_draft.LocalModel)) model.SetPlaceholder(Loc.T("pick_model"));
        model.Changed += m => { _draft.LocalModel = m; Changed(); };
        var found = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var status = Ui.Label("", 12.5, Theme.Muted);
        status.Margin = new Thickness(12, 0, 0, 0);

        async Task LoadModels(bool scanAll)
        {
            status.Foreground = Theme.Muted;
            status.Text = Loc.T("searching");
            found.Children.Clear();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var servers = scanAll
                ? await LocalAiDiscovery.DetectAsync(_draft.LocalUrl, cts.Token)
                : new List<LocalServer>();
            if (!scanAll)
            {
                var models = await LocalAiDiscovery.ListModelsAsync(_draft.LocalUrl, _draft.LocalKey, cts.Token);
                if (models != null) servers = new[] { new LocalServer(Loc.T("this_server"), _draft.LocalUrl, models) };
            }
            if (servers.Count == 0)
            {
                status.Foreground = Theme.Danger;
                status.Text = Loc.T("no_local_server");
                return;
            }
            // Prefer the server already chosen; otherwise the first one found.
            var current = servers.FirstOrDefault(s => LocalAiDiscovery.Normalize(s.BaseUrl) == LocalAiDiscovery.Normalize(_draft.LocalUrl))
                          ?? servers[0];
            void UseServer(LocalServer server)
            {
                _draft.LocalUrl = server.BaseUrl;
                Changed();
                if (url.Text != server.BaseUrl) url.Text = server.BaseUrl;
                var items = server.Models.Select(m => new Picker.Item(m, m)).ToList();
                if (!server.Models.Contains(_draft.LocalModel))
                    _draft.LocalModel = server.Models.FirstOrDefault(m => LooksMultimodal(m)) ?? server.Models.FirstOrDefault() ?? "";
                model.SetItems(items, _draft.LocalModel);
                status.Foreground = Theme.Success;
                status.Text = Loc.Format("server_found", server.Name, server.Models.Count);
            }
            foreach (var server in servers)
            {
                var chip = new SecondaryButton($"{server.Name} · {server.Models.Count}", Theme.Glyph.Pc) { Height = 30, Margin = new Thickness(0, 0, 8, 6) };
                var captured = server;
                chip.Click += () => UseServer(captured);
                found.Children.Add(chip);
            }
            UseServer(current);
        }

        var detect = new SecondaryButton(Loc.T("detect"), Theme.Glyph.Search);
        detect.Click += () => _ = LoadModels(scanAll: true);
        var refresh = new IconButton(Theme.Glyph.Refresh, Loc.T("refresh_models"), 34, 12) { Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += () => _ = LoadModels(scanAll: false);
        var detectRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        detectRow.Children.Add(detect);
        detectRow.Children.Add(status);
        var modelRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        modelRow.Children.Add(refresh);
        modelRow.Children.Add(model);

        var key = new SecretField(Loc.T("optional"), _draft.LocalKey);
        key.Changed += () => { _draft.LocalKey = key.Value; Changed(); };

        var testStatus = Ui.Label("", 12.5, Theme.Muted);
        testStatus.Margin = new Thickness(12, 0, 0, 0);
        var test = new SecondaryButton(Loc.T("test_connection"), Theme.Glyph.Play);
        test.Click += () => _ = TestAsync(testStatus);
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        testRow.Children.Add(test);
        testRow.Children.Add(testStatus);

        var serverBlock = new StackPanel();
        serverBlock.Children.Add(url);
        serverBlock.Children.Add(detectRow);
        serverBlock.Children.Add(found);

        var help = new StackPanel();
        help.Children.Add(Ui.Label(Loc.T("local_howto"), 12.5, Theme.Muted));
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, link) in new[] { ("Ollama", "https://ollama.com/download"), ("LM Studio", "https://lmstudio.ai"), ("llama.cpp", "https://github.com/ggml-org/llama.cpp") })
        {
            var l = Link(label, link);
            l.Margin = new Thickness(0, 0, 16, 0);
            links.Children.Add(l);
        }
        help.Children.Add(links);

        // Detect right away so the model list is ready.
        Dispatcher.BeginInvoke(() => _ = LoadModels(scanAll: true));
        return new StackPanel
        {
            Children =
            {
                Section(
                    Field(Loc.T("local_server"), Loc.T("local_server_help"), serverBlock),
                    Field(Loc.T("local_model"), Loc.T("local_model_help"), modelRow),
                    Field(Loc.T("local_key"), Loc.T("local_key_help"), key),
                    testRow),
                Section(help),
            },
        };
    }

    /// <summary>Local AI: a model file MouseIA runs itself, or an external server.</summary>
    private FrameworkElement LocalPanel()
    {
        var holder = new StackPanel();
        var content = new ContentControl();
        var mode = new Segmented(new[]
        {
            ("file", Loc.T("local_mode_file")),
            ("server", Loc.T("local_mode_server")),
        }, _draft.LocalMode);
        mode.Margin = new Thickness(0, 0, 0, 14);
        mode.Changed += m =>
        {
            _draft.LocalMode = m;
            Changed();
            content.Content = m == "file" ? FileDetails() : ServerDetails();
        };
        holder.Children.Add(mode);
        holder.Children.Add(content);
        content.Content = _draft.LocalMode == "file" ? FileDetails() : ServerDetails();
        return holder;
    }

    private static string SizeText(string path)
    {
        try
        {
            double gb = new System.IO.FileInfo(path).Length / 1073741824.0;
            return gb >= 1 ? $"{gb:0.0} GB" : $"{gb * 1024:0} MB";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Pick a .gguf / .litertlm file downloaded from Hugging Face; MouseIA runs it.</summary>
    private FrameworkElement FileDetails()
    {
        var fileName = Ui.Label("", 13, Theme.Text, FontWeights.SemiBold);
        var fileInfo = Ui.Label("", 12, Theme.Muted);
        var vision = Ui.Label("", 12.5, Theme.Muted);
        var engine = Ui.Label("", 12.5, Theme.Muted);
        var visionButton = new SecondaryButton(Loc.T("pick_mmproj"), Theme.Glyph.Picture) { Height = 30, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var engineActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

        void Refresh()
        {
            var file = _draft.LocalModelFile;
            bool has = !string.IsNullOrEmpty(file) && System.IO.File.Exists(file);
            fileName.Text = has ? System.IO.Path.GetFileName(file) : Loc.T("no_model_file");
            fileName.Foreground = has ? Theme.Text : Theme.Faint;
            fileInfo.Text = has ? $"{SizeText(file)} · {System.IO.Path.GetDirectoryName(file)}" : Loc.T("model_file_hint");
            engineActions.Children.Clear();
            if (!has)
            {
                vision.Text = "";
                engine.Text = "";
                visionButton.Visibility = Visibility.Collapsed;
                return;
            }
            if (LocalEngine.IsLiteRt(file))
            {
                visionButton.Visibility = Visibility.Collapsed;
                vision.Text = Loc.T("vision_litert");
                vision.Foreground = Theme.Success;
                var cli = LocalEngine.FindLiteRtCli();
                engine.Text = cli != null ? Loc.T("engine_litert_found") : Loc.T("engine_litert_missing");
                engine.Foreground = cli != null ? Theme.Success : Theme.Danger;
                if (cli == null)
                {
                    engine.Foreground = Theme.Muted;
                    var install = new SecondaryButton(Loc.T("install_now"), Theme.Glyph.Add) { Height = 30 };
                    install.Click += async () =>
                    {
                        install.IsEnabled = false;
                        var progress = new Progress<string>(p => { engine.Foreground = Theme.Muted; engine.Text = p; });
                        try
                        {
                            await Task.Run(() => LocalEngine.InstallLiteRtAsync(progress, CancellationToken.None));
                            Refresh();
                        }
                        catch (Exception ex)
                        {
                            engine.Foreground = Theme.Danger;
                            engine.Text = ex.Message;
                            install.IsEnabled = true;
                        }
                    };
                    engineActions.Children.Add(install);
                }
                return;
            }
            var mmproj = System.IO.File.Exists(_draft.LocalMmproj) ? _draft.LocalMmproj : LocalEngine.FindMmproj(file);
            visionButton.Visibility = Visibility.Visible;
            if (mmproj != null)
            {
                vision.Text = Loc.Format("vision_on", System.IO.Path.GetFileName(mmproj));
                vision.Foreground = Theme.Success;
            }
            else
            {
                vision.Text = Loc.T("vision_off");
                vision.Foreground = Theme.Muted;
            }
            bool installed = LocalEngine.InstalledLlamaServer("vulkan") != null || LocalEngine.InstalledLlamaServer("cpu") != null;
            engine.Text = installed ? Loc.T("engine_llama_ready") : Loc.T("engine_llama_missing");
            engine.Foreground = installed ? Theme.Success : Theme.Muted;
        }

        var pick = new PrimaryButton(Loc.T("pick_model_file"), Theme.Glyph.Folder) { HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("pick_model_file"),
                Filter = Loc.T("model_files") + "|*.gguf;*.litertlm|GGUF|*.gguf|LiteRT-LM|*.litertlm",
            };
            if (System.IO.File.Exists(_draft.LocalModelFile))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(_draft.LocalModelFile);
            if (dialog.ShowDialog(this) != true) return;
            if (System.IO.Path.GetFileName(dialog.FileName).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            {
                // That's the vision part, not the model itself.
                _draft.LocalMmproj = dialog.FileName;
                fileInfo.Text = Loc.T("picked_mmproj_as_model");
                Changed();
                return;
            }
            _draft.LocalModelFile = dialog.FileName;
            if (!string.IsNullOrEmpty(_draft.LocalMmproj) &&
                System.IO.Path.GetDirectoryName(_draft.LocalMmproj) != System.IO.Path.GetDirectoryName(dialog.FileName))
                _draft.LocalMmproj = "";
            Changed();
            Refresh();
        };
        visionButton.Click += () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("pick_mmproj"), Filter = "mmproj|*.gguf" };
            if (System.IO.File.Exists(_draft.LocalModelFile))
                dialog.InitialDirectory = System.IO.Path.GetDirectoryName(_draft.LocalModelFile);
            if (dialog.ShowDialog(this) != true) return;
            _draft.LocalMmproj = dialog.FileName;
            Changed();
            Refresh();
        };

        var backend = new Segmented(new[] { ("auto", Loc.T("backend_gpu")), ("cpu", Loc.T("backend_cpu")) }, _draft.LocalBackend);
        backend.Changed += b => { _draft.LocalBackend = b; Changed(); };

        var testStatus = Ui.Label("", 12.5, Theme.Muted);
        testStatus.Margin = new Thickness(12, 0, 0, 0);
        var test = new SecondaryButton(Loc.T("start_and_test"), Theme.Glyph.Play);
        test.Click += async () =>
        {
            Apply();
            await TestAsync(testStatus);
            Refresh();
        };
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        testRow.Children.Add(test);
        testRow.Children.Add(testStatus);

        var fileBlock = new StackPanel();
        fileBlock.Children.Add(pick);
        var chosen = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        chosen.Children.Add(fileName);
        chosen.Children.Add(fileInfo);
        fileBlock.Children.Add(chosen);

        var visionBlock = new StackPanel();
        visionBlock.Children.Add(vision);
        visionBlock.Children.Add(visionButton);

        var engineBlock = new StackPanel();
        engineBlock.Children.Add(engine);
        engineBlock.Children.Add(engineActions);

        var help = new StackPanel();
        help.Children.Add(Ui.Label(Loc.T("file_howto"), 12.5, Theme.Muted));
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, link) in new[]
                 {
                     (Loc.T("hf_gguf_models"), "https://huggingface.co/models?library=gguf&sort=trending"),
                     ("LiteRT-LM", "https://huggingface.co/litert-community"),
                 })
        {
            var l = Link(label, link);
            l.Margin = new Thickness(0, 0, 16, 0);
            links.Children.Add(l);
        }
        help.Children.Add(links);

        Refresh();
        return new StackPanel
        {
            Children =
            {
                Section(
                    Field(Loc.T("model_file"), Loc.T("model_file_help"), fileBlock),
                    Field(Loc.T("vision"), null, visionBlock),
                    Field(Loc.T("engine"), null, engineBlock),
                    Field(Loc.T("backend"), Loc.T("backend_help"), backend),
                    testRow),
                Section(help),
            },
        };
    }

    private static string LanguageName(string code)
    {
        if (string.IsNullOrEmpty(code)) return "?";
        var info = Languages.Find(code) ?? Languages.Find(code.Split('-')[0]);
        if (info != null) return Loc.IsPortuguese ? info.Portuguese : info.English;
        try { return new System.Globalization.CultureInfo(code).DisplayName; }
        catch (Exception) { return code; }
    }

    private static bool LooksMultimodal(string model)
    {
        var m = model.ToLowerInvariant();
        return m.Contains("gemma3") || m.Contains("gemma4") || m.Contains("vl") || m.Contains("llava") || m.Contains("vision") ||
               m.Contains("minicpm-v") || m.Contains("moondream") || m.Contains("pixtral") || m.Contains("llama4") || m.Contains("mistral-small");
    }

    private async Task TestAsync(TextBlock status)
    {
        status.Foreground = Theme.Muted;
        status.Text = Loc.T("testing");
        if (!_draft.HasAi)
        {
            status.Foreground = Theme.Danger;
            status.Text = Loc.T(_draft.Provider != ProviderIds.Local ? "missing_key" : _draft.LocalMode == "file" ? "engine_no_file" : "no_local_server");
            return;
        }
        var settings = _draft.Clone();
        var watch = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var progress = new Progress<string>(p => { status.Foreground = Theme.Muted; status.Text = p; });
            var prepared = await Task.Run(() => AiSetup.PrepareAsync(settings, "Reply with exactly: OK",
                new[] { new ChatTurn(true, "Say OK.") }, Array.Empty<string>(), 64, progress, cts.Token));
            status.Text = Loc.T("testing");
            watch.Restart();
            var result = await Task.Run(() => AiClient.StreamAsync(prepared.Request, _ => { }, cts.Token));
            status.Foreground = Theme.Success;
            var name = settings.UsesModelFile ? System.IO.Path.GetFileNameWithoutExtension(settings.LocalModelFile) : result.Model;
            status.Text = Loc.Format("test_ok", name, watch.Elapsed.TotalSeconds.ToString("0.0"));
        }
        catch (LocalEngineException ex)
        {
            status.Foreground = Theme.Danger;
            status.Text = ex.Message;
        }
        catch (AiException ex)
        {
            status.Foreground = Theme.Danger;
            status.Text = ex.Kind switch
            {
                AiErrorKind.InvalidKey => Loc.T("test_bad_key"),
                AiErrorKind.Network when _draft.Provider == ProviderIds.Local => Loc.T("no_local_server"),
                AiErrorKind.Network => Loc.T("test_no_internet"),
                AiErrorKind.ModelMissing => Loc.T("test_no_model"),
                AiErrorKind.NoCredit => Loc.T("test_no_credit"),
                AiErrorKind.RateLimit => Loc.T("test_rate"),
                _ => ex.Detail.Length > 140 ? ex.Detail[..140] + "…" : ex.Detail,
            };
        }
        catch (Exception ex)
        {
            status.Foreground = Theme.Danger;
            status.Text = ex.Message;
        }
    }

    private FrameworkElement ActivationPage()
    {
        var page = Page(Loc.T("nav_activation"), Loc.T("activation_subtitle"));
        var settingsKeys = new HotkeyBox(_draft.SettingsHotkey, h => { _draft.SettingsHotkey = h; Changed(); }, _app);
        var clearKeys = new IconButton(Theme.Glyph.Close, Loc.T("hotkey_remove"), 34, 11) { Margin = new Thickness(8, 0, 0, 0) };
        clearKeys.Click += () => { _draft.SettingsHotkey = ""; settingsKeys.SetValue(""); Changed(); };
        var settingsRow = new StackPanel { Orientation = Orientation.Horizontal };
        settingsRow.Children.Add(settingsKeys);
        settingsRow.Children.Add(clearKeys);
        page.Children.Add(Section(
            Field(Loc.T("hotkey"), Loc.T("hotkey_help"), new HotkeyBox(_draft.Hotkey, h => { _draft.Hotkey = h; Changed(); }, _app)),
            Field(Loc.T("settings_hotkey"), Loc.T("settings_hotkey_help"), settingsRow)));

        var sensitivity = DarkSlider(1, 10, _draft.ShakeSensitivity, 1);
        sensitivity.ValueChanged += (_, e) => { _draft.ShakeSensitivity = (int)e.NewValue; Changed(); };
        var scale = new DockPanel { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var low = Ui.Label(Loc.T("strong_shake"), 11.5, Theme.Faint);
        var high = Ui.Label(Loc.T("light_shake"), 11.5, Theme.Faint);
        DockPanel.SetDock(high, Dock.Right);
        scale.Children.Add(high);
        scale.Children.Add(low);
        var sensitivityBlock = new StackPanel();
        sensitivityBlock.Children.Add(sensitivity);
        sensitivityBlock.Children.Add(scale);
        page.Children.Add(Section(
            ToggleRow(Loc.T("shake"), Loc.T("shake_help"), _draft.Shake, v => { _draft.Shake = v; Changed(); }),
            Field(Loc.T("sensitivity"), null, sensitivityBlock),
            ToggleRow(Loc.T("fullscreen_off"), Loc.T("fullscreen_off_help"), _draft.DisableInFullscreen, v => { _draft.DisableInFullscreen = v; Changed(); })));
        page.Children.Add(Section(
            ToggleRow(Loc.T("type_to_ask"), Loc.T("type_to_ask_help"), _draft.TypeToAsk, v => { _draft.TypeToAsk = v; Changed(); })));

        // What each mouse gesture does while the pointer is open.
        Picker ActionPicker(string current, Action<string> set)
        {
            var items = AppSettings.Actions.Select(a => new Picker.Item(a, Loc.T("action_" + a), Loc.T("action_" + a + "_detail")));
            var picker = new Picker(items, current, searchable: false) { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            picker.Changed += v => { set(v); Changed(); };
            return picker;
        }
        page.Children.Add(Section(
            Ui.Label(Loc.T("mouse_actions"), 13, Theme.Text, FontWeights.SemiBold),
            Spacer(2),
            Ui.Label(Loc.T("mouse_actions_help"), 12, Theme.Muted),
            Spacer(6),
            Field(Loc.T("left_drag"), Loc.T("left_drag_help"), ActionPicker(_draft.LeftDragAction, v => _draft.LeftDragAction = v)),
            Field(Loc.T("right_drag"), Loc.T("right_drag_help"), ActionPicker(_draft.RightDragAction, v => _draft.RightDragAction = v)),
            Field(Loc.T("scroll_action"), Loc.T("scroll_action_help"), ActionPicker(_draft.ScrollAction, v => _draft.ScrollAction = v)),
            ToggleRow(Loc.T("speak_overlay"), Loc.T("speak_overlay_help"), _draft.SpeakOverlayTranslation,
                v => { _draft.SpeakOverlayTranslation = v; Changed(); })));
        return page;
    }

    private FrameworkElement VoicePage()
    {
        var page = Page(Loc.T("nav_voice"), Loc.T("voice_subtitle"));
        var languageItems = new List<Picker.Item> { new("auto", Loc.T("windows_language")) };
        languageItems.AddRange(Languages.All
            .OrderBy(l => Loc.IsPortuguese ? l.Portuguese : l.English, StringComparer.CurrentCulture)
            .Select(l => new Picker.Item(l.Code, Loc.IsPortuguese ? l.Portuguese : l.English, l.Native)));
        var target = new Picker(languageItems, _draft.TranslateTo) { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        target.Changed += c => { _draft.TranslateTo = c; Changed(); };
        var mic = new Picker(languageItems, _draft.VoiceInputLanguage) { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        mic.Changed += c => { _draft.VoiceInputLanguage = c; Changed(); };
        var rate = DarkSlider(0.5, 2.0, _draft.VoiceRate, 0.1);
        rate.ValueChanged += (_, e) => { _draft.VoiceRate = Math.Round(e.NewValue, 1); Changed(); };

        // Voices are read fresh whenever this page opens, so newly installed ones appear by themselves.
        var voicePicker = new Picker(Array.Empty<Picker.Item>(), _draft.VoiceId) { Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var voiceInfo = Ui.Label("", 12, Theme.Muted);
        voiceInfo.Margin = new Thickness(0, 8, 0, 0);
        void LoadVoices()
        {
            var voices = SpeechService.AllVoices();
            var items = new List<Picker.Item> { new("auto", Loc.T("voice_auto"), Loc.T("voice_auto_detail")) };
            items.AddRange(voices
                .OrderBy(v => LanguageName(v.Language), StringComparer.CurrentCulture)
                .ThenByDescending(v => v.Natural)
                .ThenBy(v => v.Name)
                .Select(v => new Picker.Item(v.Id, v.Label, v.Language == "mul" ? Loc.T("voice_neural_detail") :
                    LanguageName(v.Language) + (v.Natural ? " · " + Loc.T("voice_natural") : "") + (v.Sapi ? " · SAPI" : ""))));
            if (_draft.VoiceId != "auto" && voices.All(v => v.Id != _draft.VoiceId)) _draft.VoiceId = "auto";
            voicePicker.SetItems(items, _draft.VoiceId);
            int natural = voices.Count(v => v.Natural);
            voiceInfo.Text = voices.Count == 0 ? Loc.T("no_voices")
                : Loc.Format("voices_found", voices.Count, natural, voices.Select(v => v.Language.Split('-')[0]).Distinct().Count());
        }
        LoadVoices();
        voicePicker.Changed += id => { _draft.VoiceId = id; Changed(); };
        var refresh = new IconButton(Theme.Glyph.Refresh, Loc.T("refresh_voices"), 34, 12) { Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += LoadVoices;
        var voiceRow = new StackPanel { Orientation = Orientation.Horizontal };
        voiceRow.Children.Add(voicePicker);
        voiceRow.Children.Add(refresh);
        var voiceBlock = new StackPanel();
        voiceBlock.Children.Add(voiceRow);
        voiceBlock.Children.Add(voiceInfo);

        var install = new SecondaryButton(Loc.T("install_voices"), Theme.Glyph.Add);
        install.Click += () => OpenUrl("ms-settings:regionlanguage");
        var speech = new SecondaryButton(Loc.T("speech_settings"), Theme.Glyph.Mic) { Margin = new Thickness(8, 0, 0, 0) };
        speech.Click += () => OpenUrl("ms-settings:speech");
        var testVoice = new SecondaryButton(Loc.T("test_voice"), Theme.Glyph.Volume) { Margin = new Thickness(8, 0, 0, 0) };
        testVoice.Click += async () =>
        {
            var chosen = SpeechService.AllVoices().FirstOrDefault(v => v.Id == _draft.VoiceId);
            var lang = chosen?.Language is { Length: > 0 } l && l != "mul" ? l : (Loc.IsPortuguese ? "pt-BR" : "en-US");
            var sample = lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? "Olá! Eu sou o MouseIA." :
                lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Hi! I'm MouseIA." : Loc.T("voice_sample");
            try
            {
                if (!await SpeechService.SpeakAsync(sample, lang, _draft.VoiceRate, _draft.VoiceId))
                    voiceInfo.Text = Loc.Format("no_voice", LanguageName(lang));
            }
            catch (Exception ex) { voiceInfo.Text = ex.Message; }
        };
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(install);
        buttons.Children.Add(speech);
        buttons.Children.Add(testVoice);
        var naturalHelp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        naturalHelp.Children.Add(Ui.Label(Loc.T("natural_voices_help"), 12, Theme.Muted));
        var adapter = Link(Loc.T("natural_voices_link"), "https://github.com/gexgd0419/NaturalVoiceSAPIAdapter");
        adapter.Margin = new Thickness(0, 4, 0, 0);
        naturalHelp.Children.Add(adapter);

        // MouseIA's own natural voices (female and male), downloaded once.
        var neuralStatus = Ui.Label("", 12, Theme.Muted);
        neuralStatus.Margin = new Thickness(0, 0, 0, 8);
        var neuralDownload = new SecondaryButton(Loc.T("neural_download"), Theme.Glyph.Add);
        neuralDownload.Click += async () =>
        {
            var ok = await NeuralVoices.EnsureInstalledAsync();
            if (ok) LoadVoices();
            else neuralStatus.Text = Loc.T("neural_failed");
        };
        bool wasInstalled = NeuralVoices.Installed;
        void ShowNeural()
        {
            if (NeuralVoices.Installing)
                neuralStatus.Text = Loc.Format("neural_downloading", (int)Math.Round(Math.Max(0, NeuralVoices.Progress) * 100));
            else if (NeuralVoices.Installed)
                neuralStatus.Text = Loc.T("neural_ready");
            else if (!neuralStatus.Text.Equals(Loc.T("neural_failed")))
                neuralStatus.Text = Loc.T("neural_missing");
            neuralDownload.Visibility = NeuralVoices.Installed || NeuralVoices.Installing ? Visibility.Collapsed : Visibility.Visible;
            if (NeuralVoices.Installed && !wasInstalled)
            {
                wasInstalled = true;
                LoadVoices();
            }
        }
        ShowNeural();
        var neuralTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        neuralTimer.Tick += (_, _) => ShowNeural();
        page.Loaded += (_, _) => neuralTimer.Start();
        page.Unloaded += (_, _) => neuralTimer.Stop();
        var neuralBlock = new StackPanel();
        neuralBlock.Children.Add(neuralStatus);
        neuralBlock.Children.Add(neuralDownload);
        page.Children.Add(Section(
            ToggleRow(Loc.T("neural_voices"), Loc.T("neural_voices_help"), _draft.NaturalVoices, v => { _draft.NaturalVoices = v; Changed(); }),
            neuralBlock));

        page.Children.Add(Section(
            Field(Loc.T("translate_to"), Loc.T("translate_to_help"), target),
            ToggleRow(Loc.T("speak_translation"), Loc.T("speak_translation_help"), _draft.SpeakTranslation, v => { _draft.SpeakTranslation = v; Changed(); })));
        page.Children.Add(Section(
            Field(Loc.T("voice"), Loc.T("voice_help"), voiceBlock),
            Field(Loc.T("voice_rate"), null, rate),
            buttons,
            naturalHelp));
        page.Children.Add(Section(
            Field(Loc.T("mic_language"), Loc.T("mic_language_help"), mic)));
        return page;
    }

    private FrameworkElement GeneralPage()
    {
        var page = Page(Loc.T("nav_general"), Loc.T("general_subtitle"));
        var ui = new Segmented(new[] { ("auto", Loc.T("automatic")), ("pt", "Português"), ("en", "English") }, _draft.UiLanguage);
        // The language switches right away: the whole window is rebuilt in it.
        ui.Changed += v =>
        {
            _draft.UiLanguage = v;
            _saveTimer.Stop();
            Apply();
        };
        page.Children.Add(Section(
            Field(Loc.T("ui_language"), Loc.T("ui_language_help"), ui),
            ToggleRow(Loc.T("start_with_windows"), null, _draft.StartWithWindows, v => { _draft.StartWithWindows = v; Changed(); })));

        var opacity = DarkSlider(30, 100, Math.Round(_draft.GlassOpacity * 100), 5);
        var opacityValue = Ui.Label($"{Math.Round(_draft.GlassOpacity * 100)}%", 12.5, Theme.Muted);
        opacityValue.Margin = new Thickness(12, 0, 0, 0);
        opacity.ValueChanged += (_, e) =>
        {
            _draft.GlassOpacity = e.NewValue / 100.0;
            opacityValue.Text = $"{e.NewValue:0}%";
            ApplySurface();
            Changed();
        };
        var opacityRow = new StackPanel { Orientation = Orientation.Horizontal };
        opacityRow.Children.Add(opacity);
        opacityRow.Children.Add(opacityValue);
        var scale = new DockPanel { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var clear = Ui.Label(Loc.T("more_transparent"), 11.5, Theme.Faint);
        var solid = Ui.Label(Loc.T("more_solid"), 11.5, Theme.Faint);
        DockPanel.SetDock(solid, Dock.Right);
        scale.Children.Add(solid);
        scale.Children.Add(clear);
        var opacityBlock = new StackPanel();
        opacityBlock.Children.Add(opacityRow);
        opacityBlock.Children.Add(scale);
        var opacityField = Field(Loc.T("glass_opacity"), Loc.T("glass_opacity_help"), opacityBlock);
        opacityField.IsEnabled = _draft.GlassEffect;
        page.Children.Add(Section(
            ToggleRow(Loc.T("glass_effect"), Loc.T("glass_effect_help"), _draft.GlassEffect, v =>
            {
                _draft.GlassEffect = v;
                opacityField.IsEnabled = v;
                opacityField.Opacity = v ? 1 : 0.45;
                ApplySurface();
                Changed();
            }),
            opacityField));
        if (!_draft.GlassEffect) opacityField.Opacity = 0.45;
        // Monitors the pointer covers.
        var monitors = ScreenCapture.Monitors();
        var monitorItems = new List<Picker.Item>
        {
            new("all", Loc.T("monitors_all"), Loc.Format("monitors_all_detail", monitors.Count)),
            new("cursor", Loc.T("monitors_cursor"), Loc.T("monitors_cursor_detail")),
        };
        monitorItems.AddRange(monitors.Select(m => new Picker.Item(m.Device, Loc.Format("monitor_n", m.Number) + (m.Primary ? " · " + Loc.T("primary") : ""),
            $"{m.Bounds.Width}×{m.Bounds.Height}")));
        var monitorPicker = new Picker(monitorItems, _draft.MonitorMode, searchable: false) { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        monitorPicker.Changed += v => { _draft.MonitorMode = v; Changed(); };
        page.Children.Add(Section(Field(Loc.T("monitors"), Loc.T("monitors_help"), monitorPicker)));

        page.Children.Add(Section(
            ToggleRow(Loc.T("auto_ocr"), Loc.T("auto_ocr_help"), _draft.AutoOcr, v => { _draft.AutoOcr = v; Changed(); }),
            ToggleRow(Loc.T("auto_qr"), Loc.T("auto_qr_help"), _draft.AutoQr, v => { _draft.AutoQr = v; Changed(); })));
        var folder = new SecondaryButton(Loc.T("open_data_folder"), Theme.Glyph.Folder);
        folder.Click += () => OpenUrl(AppSettings.DefaultFolder);
        var ocr = Ui.Label(Loc.Format("ocr_languages", SafeOcrLanguages()), 12, Theme.Muted);
        ocr.Margin = new Thickness(0, 0, 0, 10);
        var cache = new SecondaryButton(Loc.T("open_cache_folder"), Theme.Glyph.Folder) { Margin = new Thickness(0, 8, 0, 0) };
        cache.Click += () => { try { System.IO.Directory.CreateDirectory(LocalEngine.CacheFolder); } catch (Exception) { } OpenUrl(LocalEngine.CacheFolder); };
        page.Children.Add(Section(ocr, folder, cache));
        return page;
    }

    private static string SafeOcrLanguages()
    {
        try { return OcrService.LanguagesSummary(); }
        catch (Exception) { return "—"; }
    }

    private FrameworkElement UpdatesPage()
    {
        var page = Page(Loc.T("nav_updates"), Loc.Format("updates_subtitle", Updater.Current));
        var status = Ui.Label("", 13, Theme.Muted);
        var notes = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.Muted,
            FontFamily = Theme.Font,
            FontSize = 12.5,
            MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var install = new PrimaryButton(Loc.T("update_install"), "\uE896") { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0) };
        var check = new SecondaryButton(Loc.T("update_check"), Theme.Glyph.Refresh);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(install);
        buttons.Children.Add(check);

        void Show(UpdateInfo? info, bool checkedNow)
        {
            if (info == null)
            {
                status.Foreground = checkedNow ? Theme.Success : Theme.Muted;
                status.Text = checkedNow ? Loc.T("update_none") : (_app.Settings.LastUpdateCheck == default
                    ? Loc.T("update_never")
                    : Loc.Format("update_last_check", _app.Settings.LastUpdateCheck.ToLocalTime().ToString("g")));
                install.Visibility = Visibility.Collapsed;
                notes.Visibility = Visibility.Collapsed;
                return;
            }
            status.Foreground = Theme.Text;
            status.Text = Loc.Format("update_found", info.Version);
            install.Visibility = Updater.CanInstall(info) ? Visibility.Visible : Visibility.Collapsed;
            notes.Text = info.Notes;
            notes.Visibility = info.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        Show(_app.AvailableUpdate, false);

        check.Click += async () =>
        {
            check.IsEnabled = false;
            status.Foreground = Theme.Muted;
            status.Text = Loc.T("update_checking");
            try { Show(await _app.CheckForUpdatesAsync(manual: true), true); }
            catch (Exception)
            {
                status.Foreground = Theme.Danger;
                status.Text = Loc.T("update_check_failed");
            }
            check.IsEnabled = true;
        };
        install.Click += async () =>
        {
            var info = _app.AvailableUpdate;
            if (info == null) return;
            install.IsEnabled = false;
            check.IsEnabled = false;
            var progress = new Progress<int>(p => status.Text = Loc.Format("update_downloading", p));
            try { await _app.InstallUpdateAsync(info, progress); }
            catch (Exception)
            {
                status.Foreground = Theme.Danger;
                status.Text = Loc.T("update_failed");
                install.IsEnabled = true;
                check.IsEnabled = true;
            }
        };

        var releases = Link(Loc.T("update_all_versions"), UpdateInfo.ReleasesPage);
        releases.Margin = new Thickness(0, 12, 0, 0);
        page.Children.Add(Section(status, notes, buttons, releases));
        page.Children.Add(Section(
            ToggleRow(Loc.T("auto_update"), Loc.T(Updater.IsInstalled ? "auto_update_help" : "auto_update_help_portable"),
                _draft.AutoUpdate, v => { _draft.AutoUpdate = v; Changed(); })));
        return page;
    }

    private FrameworkElement AboutPage()
    {
        var page = Page("MouseIA", Loc.Format("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"));
        page.Children.Add(Section(
            Ui.Label(Loc.T("about_text"), 13, Theme.Text),
            Spacer(10),
            Ui.Label(Loc.T("made_by"), 13, Theme.Text, FontWeights.SemiBold),
            Spacer(6),
            Links(("YouTube", "https://www.youtube.com/@odorizzioficial"),
                  ("GitHub", "https://github.com/odorizzioficial"),
                  ("Instagram", "https://www.instagram.com/odorizzidicas"),
                  ("TikTok", "https://www.tiktok.com/@odorizzioficial"))));
        var quit = new SecondaryButton(Loc.T("quit"), Theme.Glyph.Power);
        quit.Click += () => _app.Exit();
        page.Children.Add(quit);
        return page;
    }

    private static WrapPanel Links(params (string Label, string Url)[] links)
    {
        var panel = new WrapPanel();
        foreach (var (label, url) in links)
        {
            var link = Link(label, url);
            link.Margin = new Thickness(0, 0, 16, 4);
            panel.Children.Add(link);
        }
        return panel;
    }

}

/// <summary>Simple grid that flows children into N columns.</summary>
internal sealed class UniformGrid2 : Grid
{
    private readonly int _columns;

    public UniformGrid2(int columns)
    {
        _columns = columns;
        for (int i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition());
    }

    public void Add(UIElement element)
    {
        int index = Children.Count;
        int row = index / _columns;
        while (RowDefinitions.Count <= row) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(element, row);
        SetColumn(element, index % _columns);
        Children.Add(element);
    }
}

/// <summary>Click, then press the new shortcut.</summary>
internal sealed class HotkeyBox : Clickable
{
    private readonly TextBlock _label;
    private readonly Action<string> _changed;
    private readonly AppController _app;
    private string _value;
    private bool _capturing;

    public HotkeyBox(string value, Action<string> changed, AppController app)
    {
        _value = value;
        _changed = changed;
        _app = app;
        Height = 38;
        Width = 260;
        HorizontalAlignment = HorizontalAlignment.Left;
        CornerRadius = new CornerRadius(12);
        NormalBrush = Theme.Field;
        BorderBrush = Theme.FieldStroke;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(12, 0, 12, 0);
        var icon = Ui.Icon(Theme.Glyph.Keyboard, 14, Theme.Muted);
        icon.Margin = new Thickness(0, 0, 10, 0);
        _label = Ui.Label(Shown(value), 13.5, Theme.Text, FontWeights.SemiBold);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(_label);
        Child = row;
        Click += Begin;
        LostKeyboardFocus += (_, _) => End();
    }

    private void Begin()
    {
        _capturing = true;
        _app.SuspendHotkey(true);
        BorderBrush = Theme.Accent;
        _label.Text = Loc.T("press_keys");
        _label.Foreground = Theme.Muted;
        Focus();
        Keyboard.Focus(this);
    }

    private void End()
    {
        if (!_capturing) return;
        _capturing = false;
        _app.SuspendHotkey(false);
        BorderBrush = Theme.FieldStroke;
        _label.Text = Shown(_value);
        _label.Foreground = Theme.Text;
    }

    private static string Shown(string value) => value.Length == 0 ? Loc.T("hotkey_none") : value;

    public void SetValue(string value)
    {
        _value = value;
        _label.Text = Shown(value);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { End(); return; }
        if (HotkeyText.IsModifierKey(key)) return;
        var modifiers = Keyboard.Modifiers;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= ModifierKeys.Windows;
        if (modifiers == ModifierKeys.None || modifiers == ModifierKeys.Shift)
        {
            _label.Text = Loc.T("needs_modifier");
            return;
        }
        _value = HotkeyText.Format(modifiers, key);
        _changed(_value);
        End();
    }
}
