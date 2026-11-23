using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Native;
using MouseIA.UI;

namespace MouseIA;

/// <summary>Owns the app's lifetime: tray icon, hotkey, shake detection, overlay and settings.</summary>
internal sealed class AppController : IDisposable
{
    private readonly Application _application;
    private readonly ISecretProtector _protector = new DpapiProtector();
    private readonly string _settingsPath = Path.Combine(AppSettings.DefaultFolder, "settings.json");
    private readonly ShakeDetector _shake = new();
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private MouseHook? _hook;
    private IntPtr _trayIcon;
    private int _hotkeyId;
    private OverlayWindow? _overlay;
    private volatile bool _overlayOpen;
    private volatile bool _resetShake;
    private volatile bool _shakeEnabled;
    private SettingsWindow? _settingsWindow;
    private DateTime _lastActivation;

    public AppSettings Settings { get; private set; }
    public ImageSource? WindowIcon { get; private set; }

    public AppController(Application application)
    {
        _application = application;
        Settings = AppSettings.Load(_settingsPath, _protector);
        Loc.Apply(Settings.UiLanguage);
    }

    public void Start(bool fromStartup)
    {
        Theme.LoadGlobalStyles(_application);
        LoadIcons();
        _messages = new MessageWindow();
        _tray = new TrayIcon(_messages, _trayIcon, TrayTip());
        _tray.LeftClick += () => _application.Dispatcher.BeginInvoke(() => ShowPointer());
        _tray.RightClick += () => _application.Dispatcher.BeginInvoke(ShowTrayMenu);
        _tray.Show();
        RegisterHotkey();
        ApplyShake();
        StartupRegistration.Apply(Settings.StartWithWindows);

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            SaveSettings();
            OpenSettings();
            _tray.Notify("MouseIA", Loc.Format("welcome_toast", Settings.Hotkey));
        }
        else if (!fromStartup)
        {
            _tray.Notify("MouseIA", Loc.Format("running_toast", Settings.Hotkey));
        }
    }

    private string TrayTip() => "MouseIA · " + Settings.Hotkey;

    private void LoadIcons()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MouseIA.Assets.MouseIA.ico");
            if (stream == null) return;
            var folder = AppSettings.DefaultFolder;
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "icon.ico");
            using (var file = File.Create(path)) stream.CopyTo(file);
            int size = Math.Max(16, Win32.GetSystemMetrics(Win32.SM_CXSMICON));
            _trayIcon = Win32.LoadImage(IntPtr.Zero, path, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
            using var read = File.OpenRead(path);
            var decoder = new IconBitmapDecoder(read, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var frame in decoder.Frames)
                if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;
            WindowIcon = best;
        }
        catch (Exception ex)
        {
            Log.Write("Icon load failed: " + ex.Message);
        }
    }

    // ---- activation ----

    private void RegisterHotkey()
    {
        if (_messages == null) return;
        _messages.UnregisterHotkey(_hotkeyId);
        _hotkeyId = _messages.RegisterHotkey(Settings.Hotkey, () => ShowPointer());
        if (_hotkeyId == 0)
        {
            Log.Write("Hotkey unavailable: " + Settings.Hotkey);
            _tray?.Notify("MouseIA", Loc.Format("hotkey_taken", Settings.Hotkey));
        }
        _tray?.SetTip(TrayTip());
    }

    public void SuspendHotkey(bool suspend)
    {
        if (_messages == null) return;
        if (suspend)
        {
            _messages.UnregisterHotkey(_hotkeyId);
            _hotkeyId = 0;
        }
        else if (_hotkeyId == 0)
        {
            RegisterHotkey();
        }
    }

    private void ApplyShake()
    {
        _hook?.Uninstall(); // the detector belongs to the hook thread; reconfigure while it's stopped
        _shake.Sensitivity = Settings.ShakeSensitivity;
        _shake.Scale = Math.Max(1.0, VisualScale());
        _shake.Reset();
        _shakeEnabled = Settings.Shake;
        if (Settings.Shake)
        {
            if (_hook == null)
            {
                _hook = new MouseHook();
                _hook.Moved += OnMouseMoved;
            }
            if (!_hook.Install()) Log.Write("Mouse hook failed.");
        }
        else
        {
            _hook?.Uninstall();
        }
    }

    private static double VisualScale()
    {
        Win32.GetCursorPos(out var point);
        Win32.MonitorAt(point, out var monitor);
        return Win32.MonitorScale(monitor);
    }

    // Runs on the hook thread.
    private void OnMouseMoved(int x, int y, long time)
    {
        if (_resetShake)
        {
            _resetShake = false;
            _shake.Reset();
        }
        if (_overlayOpen || !_shakeEnabled) return;
        if (!_shake.Add(x, y, time)) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            if (Settings.DisableInFullscreen && Win32.ForegroundIsFullscreen()) return;
            ShowPointer();
        });
    }

    public void ShowPointer()
    {
        if (_overlay != null)
        {
            // A second press puts the pointer away.
            _overlay.CloseOverlay();
            return;
        }
        if ((DateTime.UtcNow - _lastActivation).TotalMilliseconds < 400) return;
        _lastActivation = DateTime.UtcNow;
        try
        {
            var shot = ScreenCapture.CaptureMonitorUnderCursor();
            _overlay = new OverlayWindow(this, shot);
            _overlayOpen = true;
            _overlay.Show();
        }
        catch (Exception ex)
        {
            Log.Write("Show pointer failed: " + ex);
            _overlay = null;
            _overlayOpen = false;
        }
    }

    public void OverlayClosed(OverlayWindow overlay)
    {
        if (ReferenceEquals(_overlay, overlay))
        {
            _overlay = null;
            _overlayOpen = false;
        }
        _resetShake = true;
        // Screenshots are large; give the memory back promptly.
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
    }

    // ---- tray menu ----

    private void ShowTrayMenu()
    {
        if (_messages != null) Win32.SetForegroundWindow(_messages.Handle);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuItem(Loc.Format("menu_open", Settings.Hotkey), () => ShowPointer()));
        menu.Items.Add(MenuItem(Loc.T("menu_settings"), () => OpenSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Settings.Shake ? Loc.T("menu_shake_off") : Loc.T("menu_shake_on"), () =>
        {
            var next = Settings.Clone();
            next.Shake = !next.Shake;
            ApplySettings(next);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Loc.T("menu_quit"), Exit));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- settings ----

    public void OpenSettings(string? page = null)
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, page);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void SaveSettings()
    {
        try { Settings.Save(_settingsPath, _protector); }
        catch (Exception ex) { Log.Write("Save failed: " + ex.Message); }
    }

    public void ApplySettings(AppSettings next)
    {
        bool hotkeyChanged = next.Hotkey != Settings.Hotkey;
        bool languageChanged = next.UiLanguage != Settings.UiLanguage;
        bool modelChanged = next.LocalModelFile != Settings.LocalModelFile || next.LocalMmproj != Settings.LocalMmproj ||
                            next.LocalBackend != Settings.LocalBackend || next.Provider != Settings.Provider ||
                            next.LocalMode != Settings.LocalMode;
        Settings = next.Clone();
        Settings.Normalize();
        SaveSettings();
        if (hotkeyChanged || _hotkeyId == 0) RegisterHotkey();
        ApplyShake();
        StartupRegistration.Apply(Settings.StartWithWindows);
        if (modelChanged) Services.LocalEngine.Stop(); // the next question starts the new model
        if (languageChanged)
        {
            // The settings window rebuilds itself; menus and the pointer read the language when they open.
            Loc.Apply(Settings.UiLanguage);
            _tray?.SetTip(TrayTip());
        }
    }

    public void Exit()
    {
        Services.LocalEngine.Stop();
        _overlay?.Close();
        _settingsWindow?.Close();
        Dispose();
        _application.Shutdown();
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
        _tray?.Dispose();
        _tray = null;
        _messages?.Dispose();
        _messages = null;
        if (_trayIcon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_trayIcon);
            _trayIcon = IntPtr.Zero;
        }
    }
}
