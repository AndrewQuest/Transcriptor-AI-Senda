namespace Transcriptor.Core;

public enum DictationState { Idle, Recording, Transcribing, Completed, Cancelled, Error, Starting }

public sealed record AudioClip(byte[] WaveData, TimeSpan Duration, float Peak);
public sealed record Transcript(string Text, TimeSpan AudioDuration, TimeSpan ProcessingDuration, string Engine)
{
    public TimeSpan ModelLoadDuration { get; init; }
}
public sealed record HistoryEntry(Guid Id, DateTimeOffset CreatedAt, string Text, double AudioSeconds, double ProcessingSeconds, string Engine);

public interface IAudioCapture : IDisposable
{
    event Action<float>? LevelChanged;
    event Action<Exception>? Failed;
    Task StartAsync(CancellationToken cancellationToken);
    Task<AudioClip> StopAsync(CancellationToken cancellationToken);
    void Cancel();
}

public interface ITranscriptionEngine : IAsyncDisposable
{
    Task<Transcript> TranscribeAsync(AudioClip audio, string language, CancellationToken cancellationToken);
}

public sealed record AppSettings
{
    public string Language { get; init; } = "auto";
    public string ModelPath { get; init; } = "";
    public string DownloadModelFile { get; init; } = "";
    public string Hotkey { get; init; } = HotkeyGesture.Default.ToString();
    public bool SaveHistory { get; init; } = true;
    public int HistoryRetentionDays { get; init; } = 30;
    public int UnloadAfterMinutes { get; init; } = 5;
    public int CpuThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    public int MicrophoneIndex { get; init; } = -1;
    public string MicrophoneName { get; init; } = "";
    public bool LaunchAtLogin { get; init; }
    public string Theme { get; init; } = "dark";
    public bool UseGlassEffects { get; init; } = true;
    public string Acceleration { get; init; } = "cpu";
    public bool UsePunctuationPrompt { get; init; }
    public bool PressEnterAfterPaste { get; init; }
    public string InterfaceLanguage { get; init; } = "es";
}

public interface IHistoryStore
{
    Task<IReadOnlyList<HistoryEntry>> ReadAsync(CancellationToken cancellationToken = default);
    Task AddAsync(HistoryEntry entry, int retentionDays, CancellationToken cancellationToken = default);
    Task UpdateTextAsync(Guid id, string text, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
