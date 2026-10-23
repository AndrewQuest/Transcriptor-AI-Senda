using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Transcriptor.Core;

namespace Transcriptor.App.Services;

public sealed class OverlayPlacement : IDisposable
{
    private readonly Window _window;
    private IntPtr _monitor, _target;
    private bool _disposed;
    public OverlayPlacement(Window window) { _window = window; SystemEvents.DisplaySettingsChanged += DisplaysChanged; }

    public void BeginSession(IntPtr target)
    {
        _target = target;
        _monitor = target != IntPtr.Zero ? NativeMethods.MonitorFromWindow(target, 0) : IntPtr.Zero;
        if (_monitor == IntPtr.Zero) _monitor = PointerMonitor();
    }

    public void Position()
    {
        if (_disposed) return;
        var info = new NativeMethods.MONITORINFO { Size = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (_monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(_monitor, ref info))
        {
            _monitor = _target != IntPtr.Zero ? NativeMethods.MonitorFromWindow(_target, 2) : PointerMonitor();
            if (!NativeMethods.GetMonitorInfoW(_monitor, ref info)) return;
        }
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        const uint noSize = 1, noZOrder = 4, noActivate = 0x10;
        // Move the HWND to the destination first so WPF handles WM_DPICHANGED there.
        // Desktop origins must never be divided by one global DPI scale.
        if (NativeMethods.MonitorFromWindow(handle, 2) != _monitor)
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, info.Work.Left, info.Work.Top, 0, 0, noSize | noZOrder | noActivate);
        var scale = Math.Max(96, NativeMethods.GetDpiForWindow(handle)) / 96d;
        var width = (int)Math.Round(_window.Width * scale);
        var height = (int)Math.Round(_window.Height * scale);
        var position = OverlayGeometry.BottomCenter(new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), width, height, (int)Math.Round(20 * scale));
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, position.Left, position.Top, width, height, noZOrder | noActivate);
    }

    private static IntPtr PointerMonitor()
    {
        NativeMethods.GetCursorPos(out var pointer);
        return NativeMethods.MonitorFromPoint(pointer, 2);
    }
    private void DisplaysChanged(object? sender, EventArgs e)
    {
        if (!_disposed && !_window.Dispatcher.HasShutdownStarted)
            _window.Dispatcher.BeginInvoke(() => { if (!_disposed && _window.IsVisible) Position(); });
    }
    public void Dispose() { _disposed = true; SystemEvents.DisplaySettingsChanged -= DisplaysChanged; }
}
