using System;
using System.Globalization;
using System.IO;
using System.Linq;
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
    private int _settingsHotkeyId;
    private readonly System.Collections.Generic.List<OverlayWindow> _overlays = new();
    private volatile bool _overlayOpen;
    private volatile bool _resetShake;
    private volatile bool _shakeEnabled;
    private SettingsWindow? _settingsWindow;
    private DateTime _lastActivation;

    public AppSettings Settings { get; private set; }

    /// <summary>A newer release found on GitHub, if any.</summary>
    public UpdateInfo? AvailableUpdate { get; private set; }
    public event Action? UpdateStateChanged;
    private DispatcherTimer? _updateTimer;
    private bool _installingUpdate;
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
        StartupRegistration.Apply(Settings.StartWithWindows, Settings.RunAsAdmin);
        StartElevationWatch();

        StartUpdateChecks();
        Services.Cleanup.RunPending();
        StartNaturalVoices();
        // Live translation is in beta and locked for now.

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            SaveSettings();
            OpenSettings();
            _tray.Notify("MouseIA", Loc.Format("welcome_toast", Settings.Hotkey));
        }
        else if (Environment.GetCommandLineArgs().Contains("--updated"))
        {
            _tray.Notify("MouseIA", Loc.Format("updated_toast", Services.Updater.Current));
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
        RegisterSettingsHotkey();
    }

    private void RegisterSettingsHotkey()
    {
        if (_messages == null) return;
        _messages.UnregisterHotkey(_settingsHotkeyId);
        _settingsHotkeyId = 0;
        if (Settings.SettingsHotkey.Length == 0) return;
        _settingsHotkeyId = _messages.RegisterHotkey(Settings.SettingsHotkey, () => _application.Dispatcher.BeginInvoke(() =>
        {
            CloseAllOverlays();
            OpenSettings();
        }));
        if (_settingsHotkeyId == 0)
        {
            Log.Write("Settings hotkey unavailable: " + Settings.SettingsHotkey);
            _tray?.Notify("MouseIA", Loc.Format("hotkey_taken", Settings.SettingsHotkey));
        }
    }

    public void SuspendHotkey(bool suspend)
    {
        if (_messages == null) return;
        if (suspend)
        {
            _messages.UnregisterHotkey(_hotkeyId);
            _hotkeyId = 0;
            _messages.UnregisterHotkey(_settingsHotkeyId);
            _settingsHotkeyId = 0;
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
        if (_overlays.Count > 0)
        {
            // A second press puts the pointer away.
            CloseAllOverlays();
            return;
        }
        if ((DateTime.UtcNow - _lastActivation).TotalMilliseconds < 400) return;
        _lastActivation = DateTime.UtcNow;
        try
        {
            // Capture every monitor first, so no overlay appears in another's picture.
            var shots = ScreenCapture.MonitorsFor(Settings.MonitorMode).Select(ScreenCapture.CaptureMonitor).ToList();
            if (shots.Count == 0) shots.Add(ScreenCapture.CaptureMonitorUnderCursor());
            var primary = shots.FirstOrDefault(s => s.HasCursor) ?? shots[0];
            _overlayOpen = true;
            foreach (var shot in shots.OrderBy(s => ReferenceEquals(s, primary)))
            {
                var overlay = new OverlayWindow(this, shot, primary: ReferenceEquals(shot, primary));
                _overlays.Add(overlay);
                overlay.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Show pointer failed: " + ex);
            CloseAllOverlays();
            _overlayOpen = _overlays.Count > 0;
        }
    }

    private bool _elevationHinted;

    /// <summary>
    /// When an app opened as administrator is in front, Windows hides the mouse from MouseIA and the
    /// shake can't work there. Say so once, with the fix.
    /// </summary>
    private void StartElevationWatch()
    {
        if (Elevation.IsSelfElevated) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            if (_elevationHinted || Settings.RunAsAdmin || !Settings.Shake || _overlayOpen) return;
            var foreground = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(foreground, out var pid);
            if (pid == Win32.CurrentProcessId || !Elevation.IsWindowElevated(foreground)) return;
            _elevationHinted = true;
            timer.Stop();
            _tray?.Notify("MouseIA", Loc.T("elevated_hint"));
        };
        timer.Start();
    }

    /// <summary>Restarts MouseIA as administrator right away (Windows asks first).</summary>
    public bool RestartAsAdmin()
    {
        if (!Elevation.Relaunch("--after", Environment.ProcessId.ToString())) return false;
        _application.Dispatcher.BeginInvoke(() => _application.Shutdown());
        return true;
    }

    /// <summary>Turns the always-on live translation on or off (and saves it).</summary>
    public void SetLiveTranslation(bool on)
    {
        var next = Settings.Clone();
        next.LiveTranslate = on;
        ApplySettings(next);
        if (!on) UI.LiveTranslation.Stop();
    }

    public bool AllOverlaysUntouched => _overlays.All(o => o.Untouched);

    public void CloseAllOverlays()
    {
        foreach (var overlay in _overlays.ToList()) overlay.CloseSelf();
    }

    public void CloseOtherOverlays(OverlayWindow keep)
    {
        foreach (var overlay in _overlays.Where(o => !ReferenceEquals(o, keep)).ToList()) overlay.CloseSelf();
    }

    public void OverlayClosed(OverlayWindow overlay)
    {
        _overlays.Remove(overlay);
        if (_overlays.Count > 0) return;
        _overlayOpen = false;
        _resetShake = true;
        // Screenshots are large; give the memory back promptly.
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
    }

    // ---- updates ----

    /// <summary>MouseIA's natural voices download quietly in the background, once.</summary>
    private void StartNaturalVoices()
    {
        Services.SpeechService.UseNeuralVoices = Settings.NaturalVoices;
        if (!Settings.NaturalVoices || Services.NeuralVoices.Installed) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (Settings.NaturalVoices) _ = Services.NeuralVoices.EnsureInstalledAsync();
        };
        timer.Start();
    }

    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();
        // A little after startup, so the PC isn't busy with it at logon.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            _ = CheckForUpdatesAsync(manual: false);
        };
        first.Start();
    }

    /// <summary>Asks GitHub for a newer version; installs it on its own when automatic updates are on.</summary>
    public async System.Threading.Tasks.Task<UpdateInfo?> CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !Settings.AutoUpdate) return null;
        UpdateInfo? info;
        try
        {
            info = await Services.Updater.CheckAsync(System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            if (manual) throw;
            return null;
        }
        Settings.LastUpdateCheck = DateTime.UtcNow;
        SaveSettings();
        AvailableUpdate = info;
        UpdateStateChanged?.Invoke();
        if (info == null || manual) return info;
        if (Settings.AutoUpdate && Services.Updater.CanInstall(info) && info.Tag != Settings.SkippedVersion)
            InstallUpdateWhenIdle(info);
        else
            _tray?.Notify("MouseIA", Loc.Format("update_available_toast", info.Version));
        return info;
    }

    private void InstallUpdateWhenIdle(UpdateInfo info)
    {
        if (_overlays.Count > 0 || _settingsWindow != null)
        {
            // Don't pull the rug while someone is using MouseIA; try again in a few minutes.
            var later = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                InstallUpdateWhenIdle(info);
            };
            later.Start();
            return;
        }
        _ = InstallUpdateAsync(info, null);
    }

    /// <summary>Downloads and applies an update, then closes so it can replace this copy.</summary>
    public async System.Threading.Tasks.Task InstallUpdateAsync(UpdateInfo info, IProgress<int>? progress)
    {
        if (_installingUpdate) return;
        _installingUpdate = true;
        try
        {
            _tray?.Notify("MouseIA", Loc.Format("update_installing_toast", info.Version));
            await Services.Updater.InstallAsync(info, progress, System.Threading.CancellationToken.None);
            Exit();
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex);
            _tray?.Notify("MouseIA", Loc.T("update_failed"));
            _installingUpdate = false;
            throw;
        }
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
        bool hotkeyChanged = next.Hotkey != Settings.Hotkey || next.SettingsHotkey != Settings.SettingsHotkey;
        bool languageChanged = next.UiLanguage != Settings.UiLanguage;
        bool modelChanged = next.LocalModelFile != Settings.LocalModelFile || next.LocalMmproj != Settings.LocalMmproj ||
                            next.LocalBackend != Settings.LocalBackend || next.Provider != Settings.Provider ||
                            next.LocalMode != Settings.LocalMode;
        Settings = next.Clone();
        Settings.Normalize();
        SaveSettings();
        if (hotkeyChanged || _hotkeyId == 0) RegisterHotkey();
        ApplyShake();
        StartupRegistration.Apply(Settings.StartWithWindows, Settings.RunAsAdmin);
        if (modelChanged) Services.LocalEngine.Stop(); // the next question starts the new model
        Services.SpeechService.UseNeuralVoices = Settings.NaturalVoices;
        if (languageChanged)
        {
            // The settings window rebuilds itself; menus and the pointer read the language when they open.
            Loc.Apply(Settings.UiLanguage);
            _tray?.SetTip(TrayTip());
        }
    }

    public void Exit()
    {
        UI.LiveTranslation.Stop();
        Services.LocalEngine.Stop();
        foreach (var overlay in _overlays.ToList()) overlay.Close();
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
