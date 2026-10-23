using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Transcriptor.Core;

namespace Transcriptor.App.Services;

public sealed class GlobalHotkey : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _hook;
    private readonly HoldHotkey _state = new();
    private readonly HashSet<int> _modifiers = [];
    public string Shortcut
    {
        get => _state.Gesture.ToString();
        set => _state.Gesture = HotkeyGesture.TryParse(value, out var gesture) ? gesture : throw new ArgumentException(UiText.Get("El atajo no es válido."));
    }
    public bool Enabled { get; set; } = true;
    public bool CanCancel { get; set; }
    public bool IsHeld => _state.IsHeld;
    public event Action<IntPtr>? Pressed;
    public event Action? Released;
    public event Action? Cancelled;

    public GlobalHotkey(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnKeyboard;
        foreach (var key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0) _modifiers.Add(key);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), UiText.Get("No se pudo registrar el atajo global."));
    }

    private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
            var down = message.ToInt64() is 0x100 or 0x104;
            var up = message.ToInt64() is 0x101 or 0x105;
            if ((key.Flags & 0x10) == 0 && (down || up))
            {
                var codeKey = (int)key.Key;
                // Track physical modifier events: the async key state has not yet been updated inside this hook.
                if (codeKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
                {
                    if (down) _modifiers.Add(codeKey); else _modifiers.Remove(codeKey);
                }
                var action = _state.Process(codeKey, down, CurrentModifiers(), Enabled, CanCancel);
                switch (action)
                {
                    case HotkeyAction.Pressed:
                        var window = NativeMethods.GetForegroundWindow();
                        _dispatcher.BeginInvoke(() => Pressed?.Invoke(window)); break;
                    case HotkeyAction.Released: _dispatcher.BeginInvoke(() => Released?.Invoke()); break;
                    case HotkeyAction.Cancelled: _dispatcher.BeginInvoke(() => Cancelled?.Invoke()); break;
                }
                if (action != HotkeyAction.None) return (IntPtr)1;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, message, data);
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var result = HotkeyModifiers.None;
        if (_modifiers.Contains(0x11) || _modifiers.Contains(0xA2) || _modifiers.Contains(0xA3)) result |= HotkeyModifiers.Control;
        if (_modifiers.Contains(0x12) || _modifiers.Contains(0xA4) || _modifiers.Contains(0xA5)) result |= HotkeyModifiers.Alt;
        if (_modifiers.Contains(0x10) || _modifiers.Contains(0xA0) || _modifiers.Contains(0xA1)) result |= HotkeyModifiers.Shift;
        if (_modifiers.Contains(0x5B) || _modifiers.Contains(0x5C)) result |= HotkeyModifiers.Windows;
        return result;
    }

    public void Dispose() => NativeMethods.UnhookWindowsHookEx(_hook);
}
