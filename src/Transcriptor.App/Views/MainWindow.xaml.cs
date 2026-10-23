using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Transcriptor.App.ViewModels;
using Transcriptor.Core;

namespace Transcriptor.App.Views;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public event Action? CorrectionRequested;
    public event Action<bool>? HotkeyCaptureChanged;
    private HotkeyGesture? _pendingGesture;
    public MainWindow() { InitializeComponent(); }
    private void HomeClicked(object sender, RoutedEventArgs e) => Navigate(0);
    private void HistoryClicked(object sender, RoutedEventArgs e) => Navigate(1);
    private void SettingsClicked(object sender, RoutedEventArgs e) => Navigate(2);
    private void CorrectClicked(object sender, RoutedEventArgs e) => CorrectionRequested?.Invoke();
    private void Navigate(int index)
    {
        if (HomePanel is null) return;
        HomePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageScroll?.ScrollToTop();
    }
    public void ShowSettings() { SettingsNav.IsChecked = true; Show(); WindowState = WindowState.Normal; Activate(); }
    private void RecordHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.CanCaptureHotkey) return;
        _pendingGesture = null;
        viewModel.ShortcutError = "";
        viewModel.CapturingHotkey = true;
        HotkeyCaptureChanged?.Invoke(true);
        ShortcutCaptureBox.Focus();
    }
    private void ShortcutKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.CapturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndHotkeyCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (e.IsRepeat || _pendingGesture is not null) return;
        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        if (HotkeyGesture.TryCreate(modifiers, KeyInterop.VirtualKeyFromKey(key), out var gesture))
        { _pendingGesture = gesture; viewModel.ShortcutError = ""; }
        else
        {
            _pendingGesture = null;
            viewModel.ShortcutError = "Esa combinación no se admite. Usa Ctrl, Alt o Mayús con una tecla, o F1–F24. Evita combinaciones reservadas por Windows.";
        }
    }
    private void ShortcutKeyUp(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.CapturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_pendingGesture is not { } gesture || KeyInterop.VirtualKeyFromKey(key) != gesture.Key) return;
        // Resume the global hook only after the main key has physically been released.
        EndHotkeyCapture();
        viewModel.SetCustomHotkey(gesture);
    }
    private void ShortcutLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndHotkeyCapture();
    private void EndHotkeyCapture()
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.CapturingHotkey) return;
        _pendingGesture = null; viewModel.CapturingHotkey = false;
        HotkeyCaptureChanged?.Invoke(false);
    }
    protected override void OnDeactivated(EventArgs e) { EndHotkeyCapture(); base.OnDeactivated(e); }
    protected override void OnClosing(CancelEventArgs e)
    {
        EndHotkeyCapture();
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
