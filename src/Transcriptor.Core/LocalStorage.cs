using System.Text.Json;

namespace Transcriptor.Core;

public sealed class LocalStorage(string directory, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect) : IHistoryStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string HistoryPath => Path.Combine(directory, "history.dat");
    public string SettingsPath => Path.Combine(directory, "settings.json");

    public async Task<AppSettings> ReadSettingsAsync()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(await File.ReadAllBytesAsync(SettingsPath)) ?? new(); }
        catch (JsonException) { throw new InvalidDataException(UiText.Get("Los ajustes están dañados. Conserva settings.json y restablece su contenido para continuar.")); }
    }

    public Task WriteSettingsAsync(AppSettings settings) => AtomicWriteAsync(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings));

    public async Task<IReadOnlyList<HistoryEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadEntriesAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(HistoryEntry entry, int retentionDays, CancellationToken cancellationToken = default)
    {
        await MutateAsync(entries => entries.Where(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays)))
            .Prepend(entry).Take(500).ToList(), cancellationToken);
    }

    public Task UpdateTextAsync(Guid id, string text, CancellationToken cancellationToken = default) =>
        MutateAsync(entries => entries.Select(x => x.Id == id ? x with { Text = text } : x).ToList(), cancellationToken);

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { if (File.Exists(HistoryPath)) File.Delete(HistoryPath); }
        finally { _gate.Release(); }
    }

    public Task PruneAsync(int retentionDays) => MutateAsync(entries => entries
        .Where(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays))).ToList(), default);

    private async Task MutateAsync(Func<List<HistoryEntry>, List<HistoryEntry>> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = update(await ReadEntriesAsync(cancellationToken));
            await AtomicWriteAsync(HistoryPath, protect(JsonSerializer.SerializeToUtf8Bytes(entries)), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<HistoryEntry>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HistoryPath)) return [];
        return JsonSerializer.Deserialize<List<HistoryEntry>>(unprotect(await File.ReadAllBytesAsync(HistoryPath, cancellationToken))) ?? [];
    }

    public static async Task AtomicWriteAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
