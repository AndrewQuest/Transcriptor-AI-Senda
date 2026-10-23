namespace Transcriptor.Core;

[Flags]
public enum HotkeyModifiers { None = 0, Control = 1, Alt = 2, Shift = 4, Windows = 8 }

/// <summary>Layout-independent Windows virtual key and canonical, language-independent storage.</summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20);
    public static HotkeyGesture Alternative { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x20);
    private static readonly Dictionary<int, string> NamedKeys = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x20] = "Space",
        [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2D] = "Insert", [0x2E] = "Delete"
    };

    public static bool TryCreate(HotkeyModifiers modifiers, int key, out HotkeyGesture gesture)
    {
        gesture = Default;
        var functionKey = key is >= 0x70 and <= 0x87;
        if ((modifiers & ~(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift)) != 0 ||
            (modifiers == HotkeyModifiers.None && !functionKey) ||
            !(functionKey || key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 || NamedKeys.ContainsKey(key))) return false;
        // These combinations are handled by Windows rather than the dictation application.
        if (((modifiers & HotkeyModifiers.Alt) != 0 && key is 0x09 or 0x73) ||
            ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt)) == (HotkeyModifiers.Control | HotkeyModifiers.Alt) && key == 0x2E)) return false;
        gesture = new(modifiers, key);
        return true;
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = Default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var modifier = tokens[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" or "MAYÚS" or "MAYUS" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None
            };
            if (modifier == HotkeyModifiers.None || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        var name = tokens[^1].ToUpperInvariant();
        var key = name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]) ? name[0] : 0;
        if (name == "ESPACIO") key = 0x20;
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var number) && number is >= 1 and <= 24) key = 0x6F + number;
        if (name.StartsWith("NUMPAD", StringComparison.Ordinal) && int.TryParse(name.AsSpan(6), out var pad) && pad is >= 0 and <= 9) key = 0x60 + pad;
        if (key == 0) key = NamedKeys.FirstOrDefault(x => x.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        return TryCreate(modifiers, key, out gesture);
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key switch
        {
            >= 0x70 and <= 0x87 => "F" + (Key - 0x6F),
            >= 0x60 and <= 0x69 => "NumPad" + (Key - 0x60),
            _ => NamedKeys.TryGetValue(Key, out var name) ? name : ((char)Key).ToString()
        });
        return string.Join(" + ", parts);
    }

    public string DisplayName => ToString().Replace("Shift", UiText.Get("Mayús"), StringComparison.Ordinal)
        .Replace("Space", UiText.Get("Espacio"), StringComparison.Ordinal);
}

public enum HotkeyAction { None, Suppress, Pressed, Released, Cancelled }

/// <summary>Pure hold/release logic shared by the native hook and its regression tests.</summary>
public sealed class HoldHotkey
{
    public HotkeyGesture Gesture { get; set; } = HotkeyGesture.Default;
    public bool IsHeld { get; private set; }
    private int _activeKey;
    private int _suppressedKey;

    public HotkeyAction Process(int key, bool down, HotkeyModifiers modifiers, bool enabled, bool canCancel)
    {
        if (key == 0x1B && down && enabled && (IsHeld || canCancel))
        {
            if (IsHeld) _suppressedKey = _activeKey;
            IsHeld = false;
            return HotkeyAction.Cancelled;
        }
        if (_suppressedKey == key)
        {
            if (!down) _suppressedKey = 0;
            return HotkeyAction.Suppress;
        }
        if (IsHeld && key == _activeKey)
        {
            if (down) return HotkeyAction.Suppress;
            IsHeld = false;
            return HotkeyAction.Released;
        }
        if (enabled && down && key == Gesture.Key && modifiers == Gesture.Modifiers)
        {
            IsHeld = true; _activeKey = key;
            return HotkeyAction.Pressed;
        }
        return HotkeyAction.None;
    }
}
