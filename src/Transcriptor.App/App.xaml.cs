using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Transcriptor.App.Services;
using Transcriptor.App.ViewModels;
using Transcriptor.App.Views;
using Transcriptor.Core;
using Transcriptor.Inference;
using Forms = System.Windows.Forms;

namespace Transcriptor.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private MainViewModel? _viewModel;
    private Views.MainWindow? _main;
    private OverlayWindow? _overlay;
    private CorrectionWindow? _correction;
    private MicrophoneCapture? _capture;
    private WhisperEngine? _engine;
    private DictationSession? _session;
    private GlobalHotkey? _hotkey;
    private Forms.NotifyIcon? _tray;
    private AppearanceService? _appearance;
    private LocalizationService? _localization;
    private readonly TextInsertion _insertion = new();
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private DeliveryTarget? _target;
    private bool _pressEnterForSession;
    private CancellationTokenSource? _deliveryCancellation;
    private Guid _lastHistoryId;
    private bool _delivering;
    private bool _exiting;
    private readonly Stopwatch _deliveryWatch = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Senda");
        try
        {
            var path = Path.Combine(directory, "settings.json");
            if (File.Exists(path)) UiText.SetLanguage(JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path))?.InterfaceLanguage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* Normal initialization reports damaged settings. */ }
        _instance = new Mutex(false, @"Local\Senda.Dictation.Desktop", out var isNew);
        if (!isNew) { System.Windows.MessageBox.Show(UiText.Get("Senda ya está activa. Ábrela desde su icono en la bandeja de Windows."), "Senda"); _instance.Dispose(); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _viewModel?.ReportError(args.Exception);
            CancelOperations();
        };
        try
        {
            _localization = new LocalizationService(this);
            _viewModel = new MainViewModel(directory);
            await _viewModel.InitializeAsync();
            _appearance = new AppearanceService(this);
            _appearance.Apply(_viewModel.Settings);
            _main = new Views.MainWindow { DataContext = _viewModel };
            _appearance.Register(_main);
            MainWindow = _main;
            _main.CorrectionRequested += ShowCorrection;
            _overlay = new OverlayWindow();
            _appearance.Register(_overlay);
            _capture = new MicrophoneCapture(() => _viewModel.Settings.MicrophoneIndex);
            _engine = new WhisperEngine(() => _viewModel.Settings);
            _session = new DictationSession(_capture, _engine);
            _session.StateChanged += OnStateChanged;
            _session.Failed += message => _viewModel.Status = message;
            _session.Completed += transcript => Run(() => AcceptTranscriptAsync(transcript));
            _capture.LevelChanged += level => Dispatcher.BeginInvoke(() => _overlay.SetLevel(level));
            _capture.Failed += error => Dispatcher.BeginInvoke(() => _session.CaptureFailed(error));
            _hotkey = new GlobalHotkey(Dispatcher) { Shortcut = _viewModel.Settings.Hotkey };
            _main.HotkeyCaptureChanged += capturing => { if (_hotkey is not null) _hotkey.Enabled = !capturing; };
            _hotkey.Pressed += target => Run(() => StartAsync(target));
            _hotkey.Released += () => Run(() => _session.StopAsync(_viewModel.Settings.Language));
            _hotkey.Cancelled += CancelOperations;
            _viewModel.SettingsChanged += settings =>
            {
                _hotkey.Shortcut = settings.Hotkey; _appearance.Apply(settings);
                if (WhisperEngine.RequiresRestart(settings))
                    _viewModel.Status = UiText.Get("Ajustes guardados. Sal de Senda desde la bandeja y vuelve a abrirla para cambiar la aceleración.");
            };
            _overlayTimer.Tick += (_, _) => { _overlayTimer.Stop(); if (!_viewModel.Busy) _overlay.Dismiss(); };
            ConfigureTray();
            UiText.Changed += RefreshTrayLanguage;
            if (!e.Args.Contains("--background"))
            {
                if (_viewModel.ModelReady) _main.Show(); else _main.ShowSettings();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(UiText.Get("No se pudo iniciar Senda.\n\n") + ex.Message, "Senda", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private void ConfigureTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(UiText.Get("Abrir Senda"), null, (_, _) => ShowMain());
        menu.Items.Add(UiText.Get("Ajustes"), null, (_, _) => _main?.ShowSettings());
        menu.Items.Add(UiText.Get("Corregir último dictado"), null, (_, _) => ShowCorrection());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(UiText.Get("Salir"), null, (_, _) => Run(ExitAsync));
        // Generated from our own vector-like mark, without an external icon dependency.
        _tray = new Forms.NotifyIcon { Text = UiText.Get("Senda · Dictado local"), ContextMenuStrip = menu, Visible = true, Icon = CreateTrayIcon() };
        _tray.DoubleClick += (_, _) => ShowMain();
        _appearance?.RegisterTray(menu);
    }

    private void RefreshTrayLanguage()
    {
        if (_tray?.ContextMenuStrip is not { } menu) return;
        var keys = new[] { "Abrir Senda", "Ajustes", "Corregir último dictado", "", "Salir" };
        for (var i = 0; i < keys.Length; i++) if (keys[i].Length > 0) menu.Items[i].Text = UiText.Get(keys[i]);
        _tray.Text = UiText.Get("Senda · Dictado local");
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var image = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.Transparent);
        using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(214, 92, 66));
        graphics.FillEllipse(background, 1, 1, 30, 30);
        using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        graphics.DrawLine(pen, 10, 13, 10, 19); graphics.DrawLine(pen, 16, 8, 16, 24); graphics.DrawLine(pen, 22, 12, 22, 20);
        var handle = image.GetHicon();
        try { using var icon = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private void ShowMain()
    {
        if (_main is null) return;
        _main.Show(); _main.WindowState = WindowState.Normal; _main.Activate();
    }

    private async Task StartAsync(IntPtr target)
    {
        if (_viewModel is null || _session is null || _viewModel.Busy || _viewModel.PreparingModel || _viewModel.CapturingHotkey) return;
        if (!_viewModel.ModelReady) { _main?.ShowSettings(); _viewModel.Status = UiText.Get("Descarga o importa un modelo para empezar."); return; }
        if (await Task.Run(TextInsertion.IsPasswordField)) { _viewModel.Status = UiText.Get("El dictado está desactivado en campos de contraseña."); return; }
        // The key can be released or shortcut capture can begin while accessibility inspection is pending.
        if (_hotkey?.IsHeld != true || !_hotkey.Enabled || _viewModel.Busy || _viewModel.PreparingModel || _viewModel.CapturingHotkey || NativeMethods.GetForegroundWindow() != target) return;
        _correction?.Close(); _correction = null;
        var deliveryTarget = await DeliveryTarget.CaptureAsync(target);
        if (_hotkey?.IsHeld != true || !_hotkey.Enabled || !deliveryTarget.MatchesNative()) return;
        _target = deliveryTarget;
        _pressEnterForSession = _viewModel.Settings.PressEnterAfterPaste;
        _deliveryCancellation?.Dispose();
        _deliveryCancellation = new();
        _overlay?.BeginSession(target);
        _insertion.Reset();
        await _session.StartAsync();
    }

    private void OnStateChanged(DictationState state)
    {
        if (_viewModel is null || _overlay is null) return;
        _overlayTimer.Stop();
        _viewModel.Busy = state is DictationState.Recording or DictationState.Transcribing || _delivering;
        if (_hotkey is not null) _hotkey.CanCancel = _viewModel.Busy;
        switch (state)
        {
            case DictationState.Recording:
                _viewModel.StateLabel = UiText.Get("Te escucho…");
                _overlay.Display(UiText.Get("Te escucho…"), UiText.Get(_pressEnterForSession ? "Al terminar: pegar + Enter · Esc cancela" : "Suelta el atajo para insertar · Esc cancela")); break;
            case DictationState.Transcribing:
                _deliveryWatch.Restart();
                _viewModel.StateLabel = UiText.Get("Transcribiendo en tu equipo…");
                _overlay.Display(UiText.Get("Preparando tus palabras…"), UiText.Get("Procesamiento local · Esc cancela")); break;
            case DictationState.Cancelled:
                _viewModel.StateLabel = UiText.Get("Dictado cancelado"); _viewModel.Status = UiText.Get("Grabación descartada."); _overlay.Dismiss(); break;
            case DictationState.Error:
                _viewModel.StateLabel = UiText.Get("Revisa el micrófono o el modelo");
                _overlay.Display(UiText.Get("No se pudo completar"), UiText.Get("Abre Senda para ver los detalles")); _overlayTimer.Start(); break;
            case DictationState.Idle:
                _viewModel.StateLabel = UiText.Get("Listo cuando tú lo estés"); _overlay.Dismiss(); break;
        }
    }

    private async Task AcceptTranscriptAsync(Transcript transcript)
    {
        if (_viewModel is null || _target is null || _exiting) return;
        _delivering = true;
        _viewModel.Busy = true;
        _lastHistoryId = Guid.NewGuid();
        _viewModel.LastText = transcript.Text;
        try
        {
            // Paste before file I/O so a history failure cannot lose a valid transcription.
            var result = await _insertion.InsertAsync(_target, transcript.Text, _pressEnterForSession, _deliveryCancellation?.Token ?? default);
            _deliveryWatch.Stop();
            _viewModel.ReportRun(transcript, _deliveryWatch.Elapsed);
            _viewModel.Status = result.Message;
            _viewModel.StateLabel = result.Inserted ? UiText.Get("Tu texto está listo") : UiText.Get("Texto listo para copiar");
            _overlay?.Display(result.EnterDispatched ? UiText.Get("Pegado + Enter despachados") : result.Inserted ? UiText.Get("Listo. Sigue con tu idea.") : UiText.Get("Tu texto está listo"),
                result.EnterAttempted ? UiText.Get("Consulta el último texto en Senda") : result.Inserted ? UiText.Get("Puedes corregirlo desde la bandeja de Senda") : UiText.Get("Abre Senda para copiar el resultado"));
            try { await _viewModel.RecordAsync(transcript, _lastHistoryId); }
            catch (Exception ex) { _viewModel.Status = result.Message + UiText.Get(" No se pudo guardar el historial: ") + ex.Message; }
        }
        finally { _delivering = false; _viewModel.Busy = false; if (_hotkey is not null) _hotkey.CanCancel = false; _overlayTimer.Start(); }
    }

    private void ShowCorrection()
    {
        if (_viewModel is null || !_viewModel.HasLastText || _viewModel.Busy) return;
        if (_correction?.IsVisible == true) { _correction.Activate(); return; }
        _correction = new CorrectionWindow(_viewModel.LastText, async text =>
        {
            _viewModel.Busy = true;
            try
            {
                var result = await _insertion.CorrectAsync(text);
                if (result.Inserted)
                {
                    try { await _viewModel.UpdateHistoryTextAsync(_lastHistoryId, text); }
                    catch (Exception ex) { _viewModel.Status = UiText.Get("Texto reemplazado; no se pudo actualizar el historial: ") + ex.Message; }
                }
                return result;
            }
            finally { _viewModel.Busy = false; }
        });
        _appearance?.Register(_correction);
        _correction.Show();
    }

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _viewModel?.ReportError(ex); }
    }

    private void CancelOperations()
    {
        _deliveryCancellation?.Cancel();
        _session?.Cancel();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _hotkey?.Dispose(); _hotkey = null;
        CancelOperations();
        _overlayTimer.Stop();
        _tray?.Dispose();
        if (_viewModel is not null) await _viewModel.StopModelPreparationAsync();
        if (_engine is not null) await _engine.DisposeAsync();
        _capture?.Dispose(); _viewModel?.Dispose();
        _deliveryCancellation?.Dispose();
        _appearance?.Dispose();
        UiText.Changed -= RefreshTrayLanguage;
        _localization?.Dispose();
        _correction?.Close(); _overlay?.Close();
        if (_main is not null) { _main.AllowClose = true; _main.Close(); }
        _instance?.Dispose();
        Shutdown();
    }
}
