using Transcriptor.Core;

namespace Transcriptor.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl + Alt + Espacio", "Ctrl + Alt + Space")]
    [InlineData("Ctrl + Mayús + Espacio", "Ctrl + Shift + Space")]
    [InlineData("control+shift+d", "Ctrl + Shift + D")]
    [InlineData("Alt + F8", "Alt + F8")]
    [InlineData("F9", "F9")]
    [InlineData("Shift + F24", "Shift + F24")]
    [InlineData("Ctrl + NumPad5", "Ctrl + NumPad5")]
    [InlineData("Ctrl + Backspace", "Ctrl + Backspace")]
    public void ValidShortcutsHaveStableStorageAcrossLanguages(string value, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(value, out var gesture));
        Assert.Equal(canonical, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(canonical, out var restored));
        Assert.Equal(gesture, restored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("Ctrl + Shift")]
    [InlineData("Ctrl + Ctrl + X")]
    [InlineData("Win + H")]
    [InlineData("Alt + Tab")]
    [InlineData("Alt + F4")]
    [InlineData("Ctrl + Alt + Delete")]
    [InlineData("Esc")]
    [InlineData("Ctrl + Esc")]
    [InlineData("Ctrl + F25")]
    public void IncompleteUnsupportedAndSystemShortcutsAreRejected(string value) => Assert.False(HotkeyGesture.TryParse(value, out _));

    [Fact]
    public void HoldingCustomKeyStartsOnceAndReleasingItStopsEvenWithoutModifiers()
    {
        var state = new HoldHotkey { Gesture = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x44) };
        Assert.Equal(HotkeyAction.None, state.Process(0x20, true, state.Gesture.Modifiers, true, false));
        Assert.Equal(HotkeyAction.None, state.Process(0x44, true, HotkeyModifiers.Control, true, false));
        Assert.Equal(HotkeyAction.Pressed, state.Process(0x44, true, state.Gesture.Modifiers, true, false));
        Assert.True(state.IsHeld);
        Assert.Equal(HotkeyAction.Suppress, state.Process(0x44, true, HotkeyModifiers.None, true, false));
        Assert.Equal(HotkeyAction.Released, state.Process(0x44, false, HotkeyModifiers.None, true, false));
        Assert.False(state.IsHeld);
        Assert.Equal(HotkeyAction.None, state.Process(0x44, false, HotkeyModifiers.None, true, false));
    }

    [Fact]
    public void EscapeCancelsAndSuppressesTheRemainderOfTheHeldKey()
    {
        var state = new HoldHotkey();
        var modifiers = state.Gesture.Modifiers;
        Assert.Equal(HotkeyAction.Pressed, state.Process(0x20, true, modifiers, true, false));
        Assert.Equal(HotkeyAction.Cancelled, state.Process(0x1B, true, modifiers, true, false));
        Assert.False(state.IsHeld);
        Assert.Equal(HotkeyAction.Suppress, state.Process(0x20, true, modifiers, true, false));
        Assert.Equal(HotkeyAction.Suppress, state.Process(0x20, false, modifiers, true, false));
        Assert.Equal(HotkeyAction.Pressed, state.Process(0x20, true, modifiers, true, false));
    }

    [Fact]
    public void CaptureModeDoesNotStartDictationOrConsumeEscape()
    {
        var state = new HoldHotkey();
        Assert.Equal(HotkeyAction.None, state.Process(0x20, true, state.Gesture.Modifiers, false, true));
        Assert.Equal(HotkeyAction.None, state.Process(0x1B, true, HotkeyModifiers.None, false, true));
        Assert.False(state.IsHeld);
        Assert.Equal(HotkeyAction.Cancelled, state.Process(0x1B, true, HotkeyModifiers.None, true, true));
    }

    [Fact]
    public void ExtraModifiersDoNotActivateAndChangingGestureCannotLoseARelease()
    {
        var state = new HoldHotkey();
        Assert.Equal(HotkeyAction.None, state.Process(0x20, true, state.Gesture.Modifiers | HotkeyModifiers.Windows, true, false));
        Assert.Equal(HotkeyAction.Pressed, state.Process(0x20, true, state.Gesture.Modifiers, true, false));
        state.Gesture = new(HotkeyModifiers.Alt, 0x79);
        Assert.Equal(HotkeyAction.Released, state.Process(0x20, false, HotkeyModifiers.None, true, false));
        Assert.Equal(HotkeyAction.Pressed, state.Process(0x79, true, HotkeyModifiers.Alt, true, false));
    }
}
