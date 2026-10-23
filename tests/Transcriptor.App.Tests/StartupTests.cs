using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using Transcriptor.App.Services;
using Transcriptor.App.ViewModels;
using Transcriptor.App.Views;
using Transcriptor.Core;

namespace Transcriptor.App.Tests;

public sealed class StartupTests
{
    [Fact]
    public async Task WindowsOpenAndUpdateTheirControlsAcrossDarkLightAndSolidThemes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            App? app = null;
            MainWindow? window = null;
            CorrectionWindow? correction = null;
            OverlayWindow? overlay = null;
            AppearanceService? appearance = null;
            LocalizationService? localization = null;
            Exception? failure = null;
            try
            {
                // Load the real resources and view without starting hooks, tray or microphone.
                app = new App();
                app.InitializeComponent();
                UiText.SetLanguage("es");
                localization = new LocalizationService(app);
                appearance = new AppearanceService(app);
                appearance.Apply(new AppSettings());
                using var viewModel = new MainViewModel(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "Senda-UiTest-" + Guid.NewGuid().ToString("N")));
                window = new MainWindow
                {
                    DataContext = viewModel,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    AllowClose = true
                };
                appearance.Register(window);
                window.Show();
                // Show/UpdateLayout attach bindings synchronously. Do not pump Startup,
                // which would start the real tray, keyboard hook and microphone services.
                window.UpdateLayout();

                Assert.True(window.IsVisible);
                var progress = FindProgressBar(window);
                Assert.NotNull(progress);
                var binding = BindingOperations.GetBindingExpression(progress, ProgressBar.ValueProperty);
                Assert.NotNull(binding);
                Assert.Equal(BindingStatus.Active, binding.Status);
                Assert.Equal(viewModel.DownloadProgress, progress.Value);
                Assert.True(appearance.IsDark);
                var darkInk = ((SolidColorBrush)window.Foreground).Color;
                Snapshot(window, "dark-home", app);

                ((RadioButton)window.FindName("SettingsNav")).IsChecked = true;
                window.UpdateLayout();
                var themePicker = (ComboBox)window.FindName("ThemePicker");
                Assert.Equal(3, themePicker.Items.Count);
                var accelerationPicker = (ComboBox)window.FindName("AccelerationPicker");
                Assert.Equal("cpu", accelerationPicker.SelectedValue);
                accelerationPicker.SelectedValue = "vulkan";
                window.UpdateLayout();
                Assert.Equal("vulkan", viewModel.Acceleration);
                Assert.Contains(Descendants(accelerationPicker).OfType<TextBlock>(), text => text.Text == "GPU · Vulkan");
                var downloadPicker = (ComboBox)window.FindName("DownloadModelPicker");
                downloadPicker.SelectedValue = ModelDownload.SmallFileName;
                Assert.Equal(ModelDownload.SmallFileName, viewModel.DownloadModelFile);
                Snapshot(window, "dark-settings", app);
                themePicker.SelectedValue = "light";
                window.UpdateLayout();
                Assert.Equal("light", viewModel.Theme);
                Assert.Contains(Descendants(themePicker).OfType<TextBlock>(), text => text.Text == "Claro");
                themePicker.SelectedValue = "dark";
                window.UpdateLayout();
                ((ScrollViewer)window.FindName("PageScroll")).ScrollToBottom();
                window.UpdateLayout();
                Snapshot(window, "dark-settings-bottom", app);

                ((RadioButton)window.FindName("HistoryNav")).IsChecked = true;
                window.UpdateLayout();
                Snapshot(window, "dark-history", app);
                ((RadioButton)window.FindName("HomeNav")).IsChecked = true;
                window.UpdateLayout();
                viewModel.LastText = "¿Lo revisamos mañana? Sí, por supuesto. Esta frase es solo un ejemplo de la interfaz.";
                viewModel.ReportRun(new Transcript("Example", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(0.37), "Whisper · GPU Vulkan")
                    { ModelLoadDuration = TimeSpan.FromSeconds(0.20) }, TimeSpan.FromSeconds(0.61));
                window.UpdateLayout();
                Snapshot(window, "dark-last-dictation", app);

                correction = new CorrectionWindow("Una frase de prueba para revisar y corregir.",
                    _ => Task.FromResult(new InsertResult(false, "Test")))
                    { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                appearance.Register(correction);
                correction.Show(); correction.UpdateLayout();
                Snapshot(correction, "dark-correction", app);
                overlay = new OverlayWindow { Opacity = 0, AnimateTransitions = false };
                appearance.Register(overlay);
                overlay.Display("Te escucho…", "Suelta el atajo para insertar · Esc cancela");
                overlay.UpdateLayout();
                Snapshot(overlay, "dark-overlay", app);
                Snapshot(overlay, "dark-overlay-on-white", app, Colors.White);
                var bubble = (Grid)overlay.FindName("Bubble");
                Assert.Equal(new Thickness(0), ((Border)overlay.FindName("Chrome")).BorderThickness);
                Assert.Equal(1, bubble.Opacity);
                Assert.False(bubble.HasAnimatedProperties);
                overlay.Dismiss();
                Assert.False(overlay.IsVisible);
                overlay.Display("Te escucho…", "Nuevo dictado");
                Assert.True(overlay.IsVisible);
                overlay.AnimateTransitions = true;
                overlay.Dismiss();
                overlay.Display("Te escucho de nuevo…", "La entrada interrumpe una salida pendiente");
                Assert.True(overlay.IsVisible);
                Assert.Equal("Te escucho de nuevo…", ((TextBlock)overlay.FindName("StateText")).Text);
                overlay.AnimateTransitions = false;
                overlay.Dismiss();
                Assert.False(overlay.IsVisible);
                Assert.False(bubble.HasAnimatedProperties);
                overlay.Display("Te escucho…", "Suelta el atajo para insertar · Esc cancela");

                appearance.Apply(new AppSettings { Theme = "light" });
                viewModel.Theme = "light";
                window.UpdateLayout(); correction.UpdateLayout(); overlay.UpdateLayout();
                Assert.False(appearance.IsDark);
                Assert.NotEqual(darkInk, ((SolidColorBrush)window.Foreground).Color);
                Assert.Equal(window.Foreground, ((TextBox)correction.FindName("Editor")).Foreground);
                Assert.Equal(app.Resources["OverlayInk"], ((TextBlock)overlay.FindName("StateText")).Foreground);
                Snapshot(window, "light-home", app);
                Snapshot(overlay, "light-overlay", app);
                Snapshot(overlay, "light-overlay-on-black", app, Colors.Black);
                ((RadioButton)window.FindName("SettingsNav")).IsChecked = true;
                window.UpdateLayout();
                Snapshot(window, "light-settings", app);

                appearance.Apply(new AppSettings { Theme = "dark", UseGlassEffects = false });
                viewModel.Theme = "dark";
                viewModel.UseGlassEffects = false;
                window.UpdateLayout();
                Assert.True(appearance.IsDark);
                Assert.Equal(255, ((SolidColorBrush)window.Background).Color.A);
                Assert.Equal(255, ((SolidColorBrush)app.Resources["GlassSurface"]).Color.A);
                Assert.Equal(255, ((SolidColorBrush)app.Resources["HeroSurface"]).Color.A);
                Assert.Equal(255, ((SolidColorBrush)((Border)overlay.FindName("Chrome")).Background).Color.A);
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                Snapshot(window, "dark-small-settings", app);
                appearance.Apply(new AppSettings { Theme = "system", UseGlassEffects = false });
                window.UpdateLayout();

                // Custom keys must remain selected when translating option labels in place.
                window.Width = 1120; window.Height = 820;
                appearance.Apply(new AppSettings { Theme = "dark" });
                viewModel.SetCustomHotkey(new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x44));
                window.UpdateLayout();
                Assert.Equal("custom", ((ComboBox)window.FindName("HotkeyPicker")).SelectedValue);
                Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("CustomHotkeyPanel")).Visibility);
                Assert.Equal("Ctrl + Mayús + D", ((TextBox)window.FindName("ShortcutCaptureBox")).Text);
                var captureEvents = new List<bool>();
                window.HotkeyCaptureChanged += captureEvents.Add;
                ((Button)window.FindName("RecordHotkeyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(viewModel.CapturingHotkey);
                Assert.False(viewModel.SaveSettingsCommand.CanExecute(null));
                var captureBox = (TextBox)window.FindName("ShortcutCaptureBox");
                captureBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(viewModel.CapturingHotkey);
                Assert.True(viewModel.SaveSettingsCommand.CanExecute(null));
                Assert.Equal(new[] { true, false }, captureEvents);
                Assert.Equal("Ctrl + Shift + D", viewModel.Hotkey);

                var speechLanguage = viewModel.Language;
                var savedText = viewModel.LastText;
                var modelChoice = viewModel.DownloadModelFile;
                ((ComboBox)window.FindName("InterfaceLanguagePicker")).SelectedValue = "en";
                Assert.Equal("es", UiText.Language); // Selection is a draft until settings are applied.
                UiText.SetLanguage(viewModel.InterfaceLanguage);
                window.UpdateLayout(); correction.UpdateLayout(); overlay.UpdateLayout();
                Assert.Equal("Senda — Local dictation", window.Title);
                Assert.Contains(Descendants(window).OfType<TextBlock>(), text => text.Text == "Settings");
                Assert.Equal("Correct dictation — Senda", correction.Title);
                Assert.Equal("Listening…", ((TextBlock)overlay.FindName("StateText")).Text);
                Assert.Equal("Release to insert · Esc to cancel", ((TextBlock)overlay.FindName("HintText")).Text);
                Assert.Equal("Ctrl + Shift + D", captureBox.Text);
                Assert.Equal(speechLanguage, viewModel.Language);
                Assert.Equal(savedText, viewModel.LastText);
                Assert.Equal(modelChoice, viewModel.DownloadModelFile);
                Assert.Equal("custom", viewModel.HotkeyPreset);
                ((ScrollViewer)window.FindName("PageScroll")).ScrollToVerticalOffset(((ScrollViewer)window.FindName("PageScroll")).VerticalOffset + 180);
                window.UpdateLayout();
                Snapshot(window, "english-custom-shortcut", app);
                Snapshot(correction, "english-correction", app);
                Snapshot(overlay, "english-overlay", app);
                ((ScrollViewer)window.FindName("PageScroll")).ScrollToTop(); window.UpdateLayout();
                Snapshot(window, "english-settings", app);
                ((RadioButton)window.FindName("HomeNav")).IsChecked = true; window.UpdateLayout();
                Snapshot(window, "english-home", app);
                UiText.SetLanguage("es"); window.UpdateLayout();
                Assert.Equal("Senda — Dictado local", window.Title);
                Assert.Equal("Ctrl + Shift + D", viewModel.Hotkey);
                Assert.Equal(savedText, viewModel.LastText);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { UiText.SetLanguage("es"); localization?.Dispose(); appearance?.Dispose(); overlay?.Close(); correction?.Close(); window?.Close(); app?.Shutdown(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
                catch (Exception error) { failure ??= error; }
            }
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static ProgressBar? FindProgressBar(DependencyObject parent)
    {
        if (parent is ProgressBar progress) return progress;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var match = FindProgressBar(VisualTreeHelper.GetChild(parent, i));
            if (match is not null) return match;
        }
        return null;
    }

    private static void Snapshot(Window window, string name, Application app, Color? background = null)
    {
        var directory = Environment.GetEnvironmentVariable("SENDA_UI_SNAPSHOT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            drawing.DrawRectangle(background is { } color ? new SolidColorBrush(color) : (Brush)app.Resources["Paper"], null, area);
            var brush = new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(VisualTreeHelper.GetOffset(content).X, VisualTreeHelper.GetOffset(content).Y, content.ActualWidth, content.ActualHeight)
            };
            drawing.DrawRectangle(brush, null, new Rect(content.Margin.Left, content.Margin.Top, content.ActualWidth, content.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(directory, name + ".png"));
        encoder.Save(file);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
