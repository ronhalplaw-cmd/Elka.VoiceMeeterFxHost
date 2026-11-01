using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Elka.VoiceMeeterFxHost.App;

internal readonly record struct ToggleViewHotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out ToggleViewHotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    continue;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    continue;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    continue;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    continue;
            }

            if (key is not null || !TryParseKey(rawPart, out var parsed))
            {
                return false;
            }

            key = parsed;
        }

        if (key is null || !IsUsable(modifiers, key.Value))
        {
            return false;
        }

        gesture = new ToggleViewHotkeyGesture(modifiers, key.Value);
        return true;
    }

    // A global shortcut without Ctrl/Alt/Win would steal normal typing from every other app.
    public static bool IsUsable(ModifierKeys modifiers, Key key)
    {
        return (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 &&
            !IsModifierKey(key) &&
            key != Key.None &&
            KeyInterop.VirtualKeyFromKey(key) != 0;
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
    }

    private static string KeyName(Key key)
    {
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString()
        };
    }

    private static bool TryParseKey(string text, out Key key)
    {
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            key = Key.D0 + (text[0] - '0');
            return true;
        }

        return Enum.TryParse(text, ignoreCase: true, out key) && Enum.IsDefined(key);
    }
}

// Registers one system-wide hotkey on the main window's HWND (it exists even while hidden to tray).
internal sealed class ToggleViewHotkeyRegistration : IDisposable
{
    public const int WindowsMessageHotkey = 0x0312;
    public const int HotkeyId = 0x5456;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private IntPtr _windowHandle;
    private bool _registered;

    public ToggleViewHotkeyGesture? Current { get; private set; }

    public bool Register(IntPtr windowHandle, ToggleViewHotkeyGesture gesture, out string error)
    {
        Unregister();
        error = string.Empty;
        if (windowHandle == IntPtr.Zero)
        {
            error = "the main window handle is not ready";
            return false;
        }

        var modifiers = ModNoRepeat;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= ModAlt;
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= ModControl;
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= ModShift;
        }

        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= ModWin;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!RegisterHotKey(windowHandle, HotkeyId, modifiers, virtualKey))
        {
            error = $"{gesture} is already used by another program or by Windows";
            return false;
        }

        _windowHandle = windowHandle;
        _registered = true;
        Current = gesture;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_windowHandle, HotkeyId);
        }

        _registered = false;
        _windowHandle = IntPtr.Zero;
        Current = null;
    }

    public void Dispose()
    {
        Unregister();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
