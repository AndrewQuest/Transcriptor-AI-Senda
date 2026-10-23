using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Transcriptor.App.Services;
using Transcriptor.Core;

namespace Transcriptor.App.Views;

public partial class OverlayWindow : Window
{
    private int _transitionVersion;
    private bool _dismissing;
    private bool _closed;
    private readonly OverlayPlacement _placement;
    public bool AnimateTransitions { get; set; } = true;
    private bool CanAnimate => AnimateTransitions && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public OverlayWindow() { InitializeComponent(); _placement = new(this); UiText.Changed += RefreshLanguage; }
    public void BeginSession(IntPtr target) => _placement.BeginSession(target);
    private void RefreshLanguage()
    {
        StateText.Text = UiText.TranslateDisplayed(StateText.Text);
        HintText.Text = UiText.TranslateDisplayed(HintText.Text);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtrW(handle, -20).ToInt64();
        NativeMethods.SetWindowLongPtrW(handle, -20, (IntPtr)(style | 0x08000000 | 0x80 | 0x20)); // no activation; tool window; click through
    }
    public void Display(string state, string hint)
    {
        if (_closed) return;
        var wasDismissing = _dismissing;
        _dismissing = false;
        var animate = CanAnimate;
        StateText.Text = state; HintText.Text = hint;
        _placement.Position();
        if (!IsVisible)
        {
            var version = ++_transitionVersion;
            ResetMotion(animate ? 0 : 1, animate ? 8 : 0);
            Show();
            if (animate) TransitionTo(1, 0, 170, version, false);
        }
        else if (wasDismissing)
        {
            var version = ++_transitionVersion;
            // A new dictation can interrupt the exit; its old completion must not hide this one.
            if (animate) TransitionTo(1, 0, 150, version, false);
            else ResetMotion(1, 0);
        }
        else if (!animate) { ++_transitionVersion; ResetMotion(1, 0); }
    }

    public void Dismiss()
    {
        if (_closed || !IsVisible || _dismissing) return;
        var version = ++_transitionVersion;
        _dismissing = true;
        if (CanAnimate) TransitionTo(0, 6, 120, version, true);
        else FinishDismissal();
    }

    private void TransitionTo(double opacity, double offset, int milliseconds, int version, bool hide)
    {
        var fromOpacity = Bubble.Opacity;
        var fromOffset = BubbleOffset.Y;
        ResetMotion(opacity, offset);
        var ease = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut };
        var fade = new DoubleAnimation(fromOpacity, opacity, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(fromOffset, offset, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (_closed || version != _transitionVersion) return;
            // Remove clocks after each short transition; hidden overlays retain no animation loop.
            if (hide) FinishDismissal();
            else ResetMotion(1, 0);
        };
        BubbleOffset.BeginAnimation(TranslateTransform.YProperty, slide);
        Bubble.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void FinishDismissal()
    {
        Hide();
        _dismissing = false;
        ResetMotion(1, 0);
    }

    private void ResetMotion(double opacity, double offset)
    {
        Bubble.BeginAnimation(UIElement.OpacityProperty, null);
        BubbleOffset.BeginAnimation(TranslateTransform.YProperty, null);
        Bubble.Opacity = opacity;
        BubbleOffset.Y = offset;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true; ++_transitionVersion;
        UiText.Changed -= RefreshLanguage;
        _placement.Dispose();
        ResetMotion(1, 0);
        base.OnClosed(e);
    }
    public void SetLevel(float level)
    {
        var scale = Math.Clamp(level * 5, 0.08f, 1);
        var weights = new[] { 0.45, 0.8, 1.0, 0.75, 0.5 };
        for (var i = 0; i < Bars.Children.Count; i++) ((Border)Bars.Children[i]).Height = 4 + 23 * scale * weights[i];
    }
}
