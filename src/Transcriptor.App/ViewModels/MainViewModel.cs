using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using Transcriptor.Core;

namespace Transcriptor.App.ViewModels;

public sealed class Choice(string value, string label) : Bindable
{
    public string Value { get; } = value;
    private string _label = label;
    public string Label { get => _label; set => Set(ref _label, value); }
}
public sealed class MicrophoneChoice(int index, string name) : Bindable
{
    public int Index { get; } = index;
    private string _name = name;
    public string Name { get => _name; set => Set(ref _name, value); }
}
public sealed class HistoryItem(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;
    public string Text => Entry.Text;
    public string Date => Entry.CreatedAt.ToLocalTime().ToString(UiText.Language == "en" ? "MMM dd · HH:mm" : "dd MMM · HH:mm", UiText.Culture);
    public string Details => UiText.Get("{0:0.0} s de voz  ·  {1:0.0} s de proceso  ·  {2}", Entry.AudioSeconds, Entry.ProcessingSeconds, Entry.Engine);
}

public sealed class MainViewModel : Bindable, IDisposable
{
    private readonly LocalStorage _storage;
    private readonly string _directory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _importCancellation;
    private TaskCompletionSource? _preparationFinished;
    private AppSettings _settings = new();
    private string _status = UiText.Get("Elige un modelo para empezar.");
    private string _stateLabel = UiText.Get("Listo cuando tú lo estés");
    private string _lastText = "";
    private string _modelName = UiText.Get("Sin modelo");
    private string _modelPath = "";
    private bool _busy;
    private bool _downloading;
    private bool _preparingModel;
    private double _downloadProgress;
    private string _language = "auto";
    private string _hotkey = HotkeyGesture.Default.ToString();
    private string _hotkeyPreset = HotkeyGesture.Default.ToString();
    private bool _capturingHotkey;
    private string _shortcutError = "";
    private string _interfaceLanguage = "es";
    private Transcript? _lastRun;
    private TimeSpan _lastDelivery;
    private bool _saveHistory = true;
    private bool _launchAtLogin;
    private string _theme = "dark";
    private bool _useGlassEffects = true;
    private string _acceleration = "cpu";
    private bool _usePunctuationPrompt;
    private bool _pressEnterAfterPaste;
    private string _downloadModelFile = ModelDownload.FileName;
    private int _microphoneIndex = -1;
    private int _cpuThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private int _unloadMinutes = 5;
    private string _search = "";
    private HistoryItem? _selectedEntry;
    private List<HistoryEntry> _allHistory = [];

    public MainViewModel(string directory)
    {
        _directory = directory;
        _storage = new LocalStorage(directory,
            data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser),
            data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
        DownloadModelCommand = new(DownloadModelAsync, ReportError, () => !Busy && !PreparingModel);
        ImportModelCommand = new(ImportModelAsync, ReportError, () => !Busy && !PreparingModel);
        SaveSettingsCommand = new(SaveSettingsAsync, ReportError, () => !Busy && !PreparingModel && !CapturingHotkey);
        ClearHistoryCommand = new(ClearHistoryAsync, ReportError);
        CopyLastCommand = new Command(() => Copy(LastText));
        CopyHistoryCommand = new Command(() => Copy(SelectedEntry?.Text ?? ""));
        CancelDownloadCommand = new Command(() => _downloadCancellation?.Cancel());
        UiText.Changed += RefreshLanguage;
    }

    public AppSettings Settings => _settings;
    public string AppVersion => "Senda " + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
    public string ActiveHotkey => HotkeyGesture.TryParse(_settings.Hotkey, out var gesture) ? gesture.DisplayName : HotkeyGesture.Default.DisplayName;
    public event Action<AppSettings>? SettingsChanged;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string StateLabel { get => _stateLabel; set => Set(ref _stateLabel, value); }
    public string LastText { get => _lastText; set { if (Set(ref _lastText, value)) Raise(nameof(HasLastText)); } }
    public bool HasLastText => !string.IsNullOrWhiteSpace(LastText);
    public string ModelName { get => _modelName; private set => Set(ref _modelName, value); }
    public string ModelPath { get => _modelPath; private set => Set(ref _modelPath, value); }
    public bool ModelReady => File.Exists(_settings.ModelPath);
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { RefreshCommands(); Raise(nameof(CanCaptureHotkey)); } } }
    public bool Downloading { get => _downloading; private set { if (Set(ref _downloading, value)) RefreshCommands(); } }
    public bool PreparingModel { get => _preparingModel; private set { if (Set(ref _preparingModel, value)) { RefreshCommands(); Raise(nameof(CanCaptureHotkey)); } } }
    public double DownloadProgress { get => _downloadProgress; private set => Set(ref _downloadProgress, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
    public string Hotkey { get => _hotkey; set { if (Set(ref _hotkey, value)) Raise(nameof(HotkeyDisplay)); } }
    public string HotkeyPreset
    {
        get => _hotkeyPreset;
        set
        {
            if (!Set(ref _hotkeyPreset, value)) return;
            if (value != "custom" && HotkeyGesture.TryParse(value, out var gesture)) Hotkey = gesture.ToString();
            Raise(nameof(IsCustomHotkey));
        }
    }
    public bool IsCustomHotkey => HotkeyPreset == "custom";
    public bool CapturingHotkey
    {
        get => _capturingHotkey;
        set { if (Set(ref _capturingHotkey, value)) { Raise(nameof(HotkeyDisplay)); Raise(nameof(CaptureButtonText)); SaveSettingsCommand.Refresh(); } }
    }
    public bool CanCaptureHotkey => !Busy && !PreparingModel;
    public string CaptureButtonText => UiText.Get(CapturingHotkey ? "Pulsa la combinación…" : "Grabar atajo");
    public string HotkeyDisplay => CapturingHotkey ? UiText.Get("Pulsa la combinación…") : HotkeyGesture.TryParse(Hotkey, out var gesture) ? gesture.DisplayName : Hotkey;
    public string ShortcutError { get => UiText.Get(_shortcutError); set => Set(ref _shortcutError, value); }
    public string InterfaceLanguage { get => _interfaceLanguage; set => Set(ref _interfaceLanguage, value); }
    public bool SaveHistory { get => _saveHistory; set => Set(ref _saveHistory, value); }
    public bool LaunchAtLogin { get => _launchAtLogin; set => Set(ref _launchAtLogin, value); }
    public string Theme { get => _theme; set => Set(ref _theme, value); }
    public bool UseGlassEffects { get => _useGlassEffects; set => Set(ref _useGlassEffects, value); }
    public string Acceleration { get => _acceleration; set => Set(ref _acceleration, value); }
    public bool UsePunctuationPrompt { get => _usePunctuationPrompt; set => Set(ref _usePunctuationPrompt, value); }
    public bool PressEnterAfterPaste { get => _pressEnterAfterPaste; set => Set(ref _pressEnterAfterPaste, value); }
    public string LastRunDetails => _lastRun is null ? "" : UiText.Get("{0} · modelo {1:0.00} s · inferencia {2:0.00} s · entrega {3:0.00} s", _lastRun.Engine, _lastRun.ModelLoadDuration.TotalSeconds, (_lastRun.ProcessingDuration - _lastRun.ModelLoadDuration).TotalSeconds, _lastDelivery.TotalSeconds);
    public string DownloadModelFile { get => _downloadModelFile; set => Set(ref _downloadModelFile, value); }
    public int MicrophoneIndex { get => _microphoneIndex; set => Set(ref _microphoneIndex, value); }
    public int CpuThreads { get => _cpuThreads; set => Set(ref _cpuThreads, value); }
    public int UnloadMinutes { get => _unloadMinutes; set => Set(ref _unloadMinutes, value); }
    public string Search { get => _search; set { if (Set(ref _search, value)) FilterHistory(); } }
    public HistoryItem? SelectedEntry { get => _selectedEntry; set => Set(ref _selectedEntry, value); }
    public string TotalDictations => _allHistory.Count.ToString();
    public string TotalWords => _allHistory.Sum(x => x.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length).ToString("N0", UiText.Culture);
    public string TotalMinutes => (_allHistory.Sum(x => x.AudioSeconds) / 60).ToString("0.0", UiText.Culture);
    public ObservableCollection<HistoryItem> History { get; } = [];
    public ObservableCollection<MicrophoneChoice> Microphones { get; } = [];
    public IReadOnlyList<Choice> Languages { get; } = [new("auto", UiText.Get("Detectar idioma")), new("es", "Español"), new("en", "English")];
    public IReadOnlyList<Choice> InterfaceLanguages { get; } = [new("es", "Español"), new("en", "English")];
    public IReadOnlyList<Choice> HotkeyChoices { get; } = [new(HotkeyGesture.Default.ToString(), HotkeyGesture.Default.DisplayName), new(HotkeyGesture.Alternative.ToString(), HotkeyGesture.Alternative.DisplayName), new("custom", UiText.Get("Personalizado…"))];
    public IReadOnlyList<int> ThreadOptions { get; } = Enumerable.Range(1, Environment.ProcessorCount).ToArray();
    public IReadOnlyList<int> UnloadOptions { get; } = [1, 5, 10];
    public IReadOnlyList<Choice> Themes { get; } = [new("dark", UiText.Get("Oscuro")), new("light", UiText.Get("Claro")), new("system", UiText.Get("Usar tema de Windows"))];
    public IReadOnlyList<Choice> AccelerationModes { get; } = [new("cpu", UiText.Get("CPU · predeterminado")), new("vulkan", UiText.Get("GPU · Vulkan"))];
    public IReadOnlyList<Choice> DownloadModels { get; } = [new(ModelDownload.FileName, UiText.Get("Base · ligero (recomendado para CPU)")), new(ModelDownload.SmallFileName, UiText.Get("Small · más capacidad (más recursos)"))];
    public AsyncCommand DownloadModelCommand { get; }
    public AsyncCommand ImportModelCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand ClearHistoryCommand { get; }
    public System.Windows.Input.ICommand CopyLastCommand { get; }
    public System.Windows.Input.ICommand CopyHistoryCommand { get; }
    public System.Windows.Input.ICommand CancelDownloadCommand { get; }

    public async Task InitializeAsync()
    {
        _settings = await _storage.ReadSettingsAsync();
        InterfaceLanguage = _settings.InterfaceLanguage == "en" ? "en" : "es";
        UiText.SetLanguage(InterfaceLanguage);
        Language = Languages.Any(x => x.Value == _settings.Language) ? _settings.Language : "auto";
        Hotkey = HotkeyGesture.TryParse(_settings.Hotkey, out var savedGesture) ? savedGesture.ToString() : HotkeyGesture.Default.ToString();
        HotkeyPreset = Hotkey == HotkeyGesture.Default.ToString() || Hotkey == HotkeyGesture.Alternative.ToString() ? Hotkey : "custom";
        SaveHistory = _settings.SaveHistory;
        LaunchAtLogin = _settings.LaunchAtLogin;
        Theme = Themes.Any(x => x.Value == _settings.Theme) ? _settings.Theme : "dark";
        UseGlassEffects = _settings.UseGlassEffects;
        Acceleration = AccelerationModes.Any(x => x.Value == _settings.Acceleration) ? _settings.Acceleration : "cpu";
        UsePunctuationPrompt = _settings.UsePunctuationPrompt;
        PressEnterAfterPaste = _settings.PressEnterAfterPaste;
        CpuThreads = Math.Clamp(_settings.CpuThreads, 1, ThreadOptions.Count);
        UnloadMinutes = UnloadOptions.Contains(_settings.UnloadAfterMinutes) ? _settings.UnloadAfterMinutes : 5;
        Microphones.Add(new(-1, UiText.Get("Micrófono predeterminado de Windows")));
        for (var i = 0; i < WaveIn.DeviceCount; i++) Microphones.Add(new(i, WaveIn.GetCapabilities(i).ProductName));
        MicrophoneIndex = Microphones.Any(x => x.Index == _settings.MicrophoneIndex) ? _settings.MicrophoneIndex : -1;
        _settings = _settings with { Language = Language, Hotkey = Hotkey, CpuThreads = CpuThreads, UnloadAfterMinutes = UnloadMinutes, MicrophoneIndex = MicrophoneIndex, Theme = Theme, Acceleration = Acceleration, InterfaceLanguage = InterfaceLanguage };
        RefreshModel();
        try
        {
            if (_settings.SaveHistory) await _storage.PruneAsync(_settings.HistoryRetentionDays);
            else await _storage.ClearAsync();
            await ReloadHistoryAsync();
            Status = ModelReady ? UiText.Get("Todo se procesa en tu equipo. Mantén el atajo para dictar.") : UiText.Get("Abre Ajustes para descargar o importar un modelo local.");
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or System.Text.Json.JsonException)
        { Status = UiText.Get("No se pudo leer el historial. Se conservó el archivo original; puedes borrarlo desde Historial."); }
    }

    public async Task RecordAsync(Transcript transcript, Guid id)
    {
        LastText = transcript.Text;
        if (!_settings.SaveHistory) return;
        await _storage.AddAsync(new(id, DateTimeOffset.UtcNow, transcript.Text, transcript.AudioDuration.TotalSeconds,
            transcript.ProcessingDuration.TotalSeconds, transcript.Engine), _settings.HistoryRetentionDays);
        await ReloadHistoryAsync();
    }

    public async Task UpdateHistoryTextAsync(Guid id, string text)
    {
        LastText = text;
        if (!_settings.SaveHistory) return;
        await _storage.UpdateTextAsync(id, text);
        await ReloadHistoryAsync();
    }

    private async Task ReloadHistoryAsync()
    {
        _allHistory = (await _storage.ReadAsync()).ToList(); FilterHistory();
        Raise(nameof(TotalDictations)); Raise(nameof(TotalWords)); Raise(nameof(TotalMinutes));
    }

    private void FilterHistory()
    {
        History.Clear();
        foreach (var entry in _allHistory.Where(x => x.Text.Contains(Search, StringComparison.CurrentCultureIgnoreCase))) History.Add(new(entry));
    }

    private async Task SaveSettingsAsync()
    {
        if (!HotkeyGesture.TryParse(Hotkey, out var gesture)) throw new InvalidOperationException(UiText.Get("El atajo no es válido."));
        Hotkey = gesture.ToString();
        var next = _settings with { Language = Language, Hotkey = Hotkey, SaveHistory = SaveHistory,
            LaunchAtLogin = LaunchAtLogin, MicrophoneIndex = MicrophoneIndex, CpuThreads = CpuThreads, UnloadAfterMinutes = UnloadMinutes,
            Theme = Theme, UseGlassEffects = UseGlassEffects, Acceleration = Acceleration, UsePunctuationPrompt = UsePunctuationPrompt, PressEnterAfterPaste = PressEnterAfterPaste, InterfaceLanguage = InterfaceLanguage == "en" ? "en" : "es" };
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (LaunchAtLogin) key.SetValue("Senda", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Senda", throwOnMissingValue: false);
        await _storage.WriteSettingsAsync(next);
        _settings = next;
        UiText.SetLanguage(_settings.InterfaceLanguage);
        Raise(nameof(ActiveHotkey));
        if (!SaveHistory)
        {
            await _storage.ClearAsync();
            await ReloadHistoryAsync();
        }
        Status = UiText.Get("Ajustes guardados.");
        SettingsChanged?.Invoke(_settings);
    }

    private async Task ImportModelAsync()
    {
        PreparingModel = true;
        _preparationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _importCancellation = new();
        try
        {
        var dialog = new OpenFileDialog { Title = UiText.Get("Importar modelo Whisper GGML"), Filter = UiText.Get("Modelos Whisper GGML (*.bin)|*.bin"), CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        var source = dialog.FileName;
        await using (var stream = File.OpenRead(source))
        {
            var magic = new byte[4];
            await stream.ReadExactlyAsync(magic);
            if (BitConverter.ToUInt32(magic) != 0x67676d6c || stream.Length < 1_000_000)
                throw new InvalidDataException(UiText.Get("Este archivo no es un modelo Whisper GGML. Los modelos de Cactus requieren una integración diferente."));
        }
        var directory = Path.Combine(_directory, "models");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(source));
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            // Do not overwrite a currently loaded model or another imported file.
            if (File.Exists(target)) target = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
            var temporary = target + ".partial";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = File.Create(temporary))
                    await input.CopyToAsync(output, _importCancellation.Token);
                _importCancellation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await SetModelAsync(target);
        }
        finally
        {
            _importCancellation.Dispose(); _importCancellation = null;
            PreparingModel = false; _preparationFinished.TrySetResult();
        }
    }

    private async Task DownloadModelAsync()
    {
        PreparingModel = true; Downloading = true; DownloadProgress = 0;
        _preparationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _downloadCancellation = new();
        try
        {
            var fileName = DownloadModelFile;
            Status = UiText.Get("Descargando modelo multilingüe. Solo esta descarga necesita Internet.");
            var path = await new ModelDownload(_http).DownloadAsync(Path.Combine(_directory, "models"),
                new Progress<double>(p => DownloadProgress = p * 100), _downloadCancellation.Token, fileName);
            await SetModelAsync(path);
        }
        catch (OperationCanceledException) { Status = UiText.Get("Descarga cancelada. No se cambió el modelo activo."); }
        finally { _downloadCancellation.Dispose(); _downloadCancellation = null; Downloading = false; PreparingModel = false; _preparationFinished.TrySetResult(); }
    }

    private async Task SetModelAsync(string path)
    {
        var next = _settings with { ModelPath = path };
        await _storage.WriteSettingsAsync(next);
        _settings = next;
        RefreshModel();
        Status = UiText.Get("Modelo preparado. Mantén el atajo en cualquier campo de texto para dictar.");
        SettingsChanged?.Invoke(_settings);
    }

    private void RefreshModel()
    {
        ModelPath = _settings.ModelPath;
        ModelName = ModelReady ? Path.GetFileName(_settings.ModelPath) : UiText.Get("Sin modelo");
        Raise(nameof(ModelReady));
    }

    private async Task ClearHistoryAsync()
    {
        if (System.Windows.MessageBox.Show(UiText.Get("¿Borrar todas las transcripciones guardadas en este equipo?"), UiText.Get("Borrar historial"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _storage.ClearAsync(); await ReloadHistoryAsync(); Status = UiText.Get("Historial borrado.");
    }

    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); Status = UiText.Get("Texto copiado."); }
        catch (System.Runtime.InteropServices.ExternalException) { Status = UiText.Get("El portapapeles está ocupado. Vuelve a intentarlo."); }
    }

    public void ReportError(Exception error) => Status = error switch
    {
        HttpRequestException => UiText.Get("No se pudo descargar el modelo. Revisa Internet o importa un archivo GGML desde disco."),
        DllNotFoundException => UiText.Get("No se pudo cargar el motor local. Reinstala la edición x64 y el runtime de Visual C++."),
        _ => error.Message
    };

    private void RefreshCommands() { DownloadModelCommand.Refresh(); ImportModelCommand.Refresh(); SaveSettingsCommand.Refresh(); }
    public async Task StopModelPreparationAsync()
    {
        _downloadCancellation?.Cancel(); _importCancellation?.Cancel();
        if (_preparationFinished is not null) await _preparationFinished.Task;
    }
    public void SetCustomHotkey(HotkeyGesture gesture) { Hotkey = gesture.ToString(); HotkeyPreset = "custom"; ShortcutError = ""; }

    public void ReportRun(Transcript transcript, TimeSpan delivery)
    {
        _lastRun = transcript; _lastDelivery = delivery; Raise(nameof(LastRunDetails));
    }

    private void RefreshLanguage()
    {
        // Update labels in place: replacing ItemsSource can write a null SelectedValue back into settings.
        foreach (var choices in new[] { Languages, Themes, AccelerationModes, DownloadModels })
            foreach (var choice in choices) choice.Label = UiText.TranslateDisplayed(choice.Label);
        foreach (var choice in HotkeyChoices)
            choice.Label = HotkeyGesture.TryParse(choice.Value, out var gesture) ? gesture.DisplayName : UiText.Get("Personalizado…");
        var defaultMicrophone = Microphones.FirstOrDefault(x => x.Index == -1);
        if (defaultMicrophone is not null)
        {
            defaultMicrophone.Name = UiText.Get("Micrófono predeterminado de Windows");
        }
        StateLabel = UiText.Get(HasLastText ? "Tu texto está listo" : "Listo cuando tú lo estés");
        Status = UiText.TranslateDisplayed(Status);
        RefreshModel(); FilterHistory();
        foreach (var property in new[] { nameof(ActiveHotkey), nameof(HotkeyDisplay), nameof(CaptureButtonText), nameof(ShortcutError), nameof(LastRunDetails), nameof(TotalMinutes), nameof(TotalWords) }) Raise(property);
    }

    public void Dispose() { UiText.Changed -= RefreshLanguage; _downloadCancellation?.Cancel(); _importCancellation?.Cancel(); _http.Dispose(); }
}
