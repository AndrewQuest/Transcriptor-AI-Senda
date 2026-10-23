namespace Transcriptor.Core;

public readonly record struct DesktopRect(int Left, int Top, int Right, int Bottom);
public readonly record struct OverlayPosition(int Left, int Top);

public static class OverlayGeometry
{
    // All inputs are physical desktop pixels, including negative monitor origins.
    public static OverlayPosition BottomCenter(DesktopRect workArea, int width, int height, int bottomMargin)
    {
        var x = workArea.Left + Math.Max(0, (workArea.Right - workArea.Left - width) / 2);
        var y = Math.Max(workArea.Top, workArea.Bottom - height - bottomMargin);
        return new(x, y);
    }
}
