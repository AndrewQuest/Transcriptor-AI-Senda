using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Transcriptor.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Transcriptor.App.Services;

/// <summary>Event-driven theme resources and native DWM backdrops; no animation or polling loop.</summary>
public sealed class AppearanceService : IDisposable
{
    private readonly Application _application;
    private readonly HashSet<Window> _windows = [];
    private ResourceDictionary? _palette;
    private AppSettings _settings = new();
    private Forms.ContextMenuStrip? _menu;
    private bool _disposed;
    public bool IsDark { get; private set; } = true;

    public AppearanceService(Application application)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
    }

    public void Apply(AppSettings settings)
    {
        _application.Dispatcher.VerifyAccess();
        _settings = settings;
        foreach (var key in new[] { "Ink", "Muted", "Paper", "Input", "Surface", "Subtle", "Line", "Positive", "GlassEdge", "SurfaceRaised", "WindowTint", "OverlaySurface", "OverlayInk", "OverlayMuted", "Accent", "Selection", "Hover", "AccentInk", "GlassSurface", "HeroSurface", "Sidebar" })
            _application.Resources.Remove(key);
        IsDark = settings.Theme != "light" && (settings.Theme != "system" || !UsesLightSystemTheme());
        if (_palette is not null) _application.Resources.MergedDictionaries.Remove(_palette);
        _palette = new ResourceDictionary { Source = new Uri($"/Senda;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        _application.Resources.MergedDictionaries.Add(_palette);
        var glass = settings.UseGlassEffects && TransparencyEnabled() && !SystemParameters.HighContrast;
        foreach (var key in new[] { "GlassSurface", "HeroSurface", "Sidebar", "OverlaySurface" })
            _application.Resources[key] = _palette[glass ? key : "Surface"];
        if (SystemParameters.HighContrast)
        {
            _application.Resources["Ink"] = SystemColors.WindowTextBrush;
            _application.Resources["Muted"] = SystemColors.WindowTextBrush;
            _application.Resources["OverlayInk"] = SystemColors.WindowTextBrush;
            _application.Resources["OverlayMuted"] = SystemColors.WindowTextBrush;
            _application.Resources["Paper"] = SystemColors.WindowBrush;
            _application.Resources["Input"] = SystemColors.WindowBrush;
            _application.Resources["Surface"] = SystemColors.WindowBrush;
            _application.Resources["GlassSurface"] = SystemColors.WindowBrush;
            _application.Resources["HeroSurface"] = SystemColors.WindowBrush;
            _application.Resources["Sidebar"] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Subtle", "Line", "Positive", "GlassEdge" }) _application.Resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "SurfaceRaised", "WindowTint", "OverlaySurface" }) _application.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Accent", "Selection", "Hover" }) _application.Resources[key] = SystemColors.HighlightBrush;
            _application.Resources["AccentInk"] = SystemColors.HighlightTextBrush;
        }
        foreach (var window in _windows) ApplyWindow(window);
        UpdateTray();
    }

    public void Register(Window window)
    {
        if (!_windows.Add(window)) return;
        window.SourceInitialized += WindowInitialized;
        window.Closed += WindowClosed;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyWindow(window);
    }

    public void RegisterTray(Forms.ContextMenuStrip menu) { _menu = menu; UpdateTray(); }

    private void WindowInitialized(object? sender, EventArgs e) => ApplyWindow((Window)sender!);
    private void WindowClosed(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= WindowInitialized;
        window.Closed -= WindowClosed;
        _windows.Remove(window);
    }

    private void ApplyWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || window.AllowsTransparency) return;
        var dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var corners = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        var glass = _settings.UseGlassEffects && TransparencyEnabled() && !SystemParameters.HighContrast;
        // Mica is the low-cost material intended for long-lived windows. Windows 10 falls back to solid.
        var backdrop = glass ? 2 : 1;
        var supported = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) >= 0;
        var margins = new Margins { Left = glass && supported ? -1 : 0 };
        var extended = DwmExtendFrameIntoClientArea(handle, ref margins) >= 0;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is not null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
        window.SetResourceReference(Window.BackgroundProperty, glass && supported && extended ? "WindowTint" : "Paper");
    }

    private void PreferencesChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || _application.Dispatcher.HasShutdownStarted) return;
        _ = _application.Dispatcher.BeginInvoke(() => { if (!_disposed) Apply(_settings); });
    }

    private static bool UsesLightSystemTheme() => ReadPersonalization("AppsUseLightTheme", 0) != 0;
    private static bool TransparencyEnabled() => ReadPersonalization("EnableTransparency", 1) != 0;
    private static int ReadPersonalization(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return fallback; }
    }

    private void UpdateTray()
    {
        if (_menu is null || _menu.IsDisposed) return;
        Drawing.Color ColorFor(string key)
        {
            var color = ((SolidColorBrush)_application.Resources[key]).Color;
            return Drawing.Color.FromArgb(color.R, color.G, color.B);
        }
        var background = ColorFor("Surface"); var foreground = ColorFor("Ink"); var hover = ColorFor("Hover");
        _menu.Renderer = new Forms.ToolStripProfessionalRenderer(new TrayColors(background, hover));
        _menu.BackColor = background; _menu.ForeColor = foreground;
        foreach (Forms.ToolStripItem item in _menu.Items) { item.BackColor = background; item.ForeColor = foreground; }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= PreferencesChanged;
        foreach (var window in _windows) { window.SourceInitialized -= WindowInitialized; window.Closed -= WindowClosed; }
        _windows.Clear();
    }

    private sealed class TrayColors(Drawing.Color background, Drawing.Color hover) : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => background;
        public override Drawing.Color ImageMarginGradientBegin => background;
        public override Drawing.Color ImageMarginGradientMiddle => background;
        public override Drawing.Color ImageMarginGradientEnd => background;
        public override Drawing.Color MenuItemSelected => hover;
        public override Drawing.Color MenuItemBorder => hover;
        public override Drawing.Color MenuBorder => hover;
        public override Drawing.Color SeparatorDark => hover;
        public override Drawing.Color SeparatorLight => background;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
}
