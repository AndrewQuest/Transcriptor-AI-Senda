using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Transcriptor.App.Services;

public sealed record DeliveryTarget(IntPtr Window, uint Process, IntPtr FocusWindow, int[]? ElementId)
{
    public static Task<DeliveryTarget> CaptureAsync(IntPtr window) => Task.Run(() =>
    {
        NativeMethods.GetWindowThreadProcessId(window, out var process);
        var info = FocusInfo();
        int[]? id = null;
        try { id = AutomationElement.FocusedElement?.GetRuntimeId(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException) { }
        return new DeliveryTarget(window, process, info.Focus, id);
    });

    public async Task<bool> IsCurrentAsync()
    {
        if (!MatchesNative()) return false;
        if (ElementId is null) return FocusWindow != IntPtr.Zero && FocusWindow != Window;
        var sameElement = await Task.Run(() =>
        {
            try { return AutomationElement.FocusedElement?.GetRuntimeId().SequenceEqual(ElementId) == true; }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException) { return false; }
        });
        return sameElement && MatchesNative();
    }

    public bool MatchesNative()
    {
        if (Window == IntPtr.Zero || NativeMethods.GetForegroundWindow() != Window) return false;
        NativeMethods.GetWindowThreadProcessId(Window, out var process);
        return process == Process && FocusInfo().Focus == FocusWindow;
    }
    private static NativeMethods.GUITHREADINFO FocusInfo()
    {
        var info = new NativeMethods.GUITHREADINFO { Size = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
        NativeMethods.GetGUIThreadInfo(0, ref info);
        return info;
    }
}
