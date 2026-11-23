using System;
using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Input;

namespace MouseIA.Native;

/// <summary>
/// A hidden top-level window that receives hotkeys, tray icon callbacks and shell broadcasts.
/// (Message-only windows miss broadcasts such as "TaskbarCreated".)
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _hotkeys = new();
    private int _nextHotkeyId = 0x4D49; // "MI"

    public IntPtr Handle => _source.Handle;

    /// <summary>Raised for every message; return true to mark it handled.</summary>
    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("MouseIA.MessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)Win32.WS_EX_TOOLWINDOW,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _hotkeys.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
            return IntPtr.Zero;
        }
        if (Message != null && Message(msg, wParam, lParam)) handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Registers a global hotkey such as "Alt+Shift+M". Returns its id, or 0 on failure.</summary>
    public int RegisterHotkey(string text, Action action)
    {
        if (!HotkeyText.TryParse(text, out var modifiers, out var key)) return 0;
        uint mods = Win32.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Win32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= Win32.MOD_WIN;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        int id = _nextHotkeyId++;
        if (!Win32.RegisterHotKey(Handle, id, mods, (uint)vk)) return 0;
        _hotkeys[id] = action;
        return id;
    }

    public void UnregisterHotkey(int id)
    {
        if (id == 0) return;
        Win32.UnregisterHotKey(Handle, id);
        _hotkeys.Remove(id);
    }

    public void Dispose()
    {
        foreach (var id in _hotkeys.Keys) Win32.UnregisterHotKey(Handle, id);
        _hotkeys.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

/// <summary>Converts between "Ctrl+Shift+K" style text and WPF keys.</summary>
public static class HotkeyText
{
    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": case "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, true, out key)) return false;
                    break;
            }
        }
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
