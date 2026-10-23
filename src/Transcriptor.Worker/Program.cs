using System.Buffers.Binary;
using System.Security.Cryptography;
using Transcriptor.Core;
using Transcriptor.Inference;

if (args.Length != 2 || args[0] != "--data-directory")
{
    Console.Error.WriteLine("Usage: Senda.Worker --data-directory PATH (private framed stdin/stdout only)");
    return 2;
}
var directory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.Combine(directory, "models"));
var settings = new AppSettings();
await using var engine = new WhisperEngine(() => settings);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
using var outputGate = new SemaphoreSlim(1, 1);
CancellationTokenSource? activeCancellation = null;
Task? active = null;

async Task ReplyAsync(string id, string kind, object? data = null, string? error = null)
{
    await outputGate.WaitAsync();
    try { await WorkerFrames.WriteAsync(output, new { version = 1, id, kind, data, error }); }
    finally { outputGate.Release(); }
}
async Task ExecuteAsync(WorkerFrame frame, CancellationToken cancellation)
{
    try
    {
        if (frame.Command.Operation == "download")
        {
            var path = await new ModelDownload(http).DownloadAsync(Path.Combine(directory, "models"), null, cancellation, frame.Command.Model ?? ModelDownload.FileName);
            await ReplyAsync(frame.Command.Id, "downloaded", new { model = Path.GetFileName(path) });
        }
        else
        {
            var audio = frame.Audio;
            if (audio.Length < 44 || !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
                !audio.AsSpan(12, 4).SequenceEqual("fmt "u8) || BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(16)) != 16 ||
                BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(20)) != 1 || BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(22)) != 1 ||
                BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(24)) != 16000 || BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(34)) != 16 ||
                !audio.AsSpan(36, 4).SequenceEqual("data"u8) || BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(40)) != audio.Length - 44)
                throw new InvalidDataException("Expected bounded mono 16 kHz PCM16 WAV in RAM.");
            var duration = TimeSpan.FromSeconds((audio.Length - 44) / 32000d);
            float peak = 0;
            for (var i = 44; i + 1 < audio.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i))) / 32768f);
            if (duration.TotalMilliseconds < 250 || peak < 0.003) throw new InvalidDataException("No speech detected.");
            var transcript = await engine.TranscribeAsync(new(audio, duration, peak), settings.Language, cancellation);
            cancellation.ThrowIfCancellationRequested();
            await ReplyAsync(frame.Command.Id, "transcript", new { transcript.Text, audioSeconds = duration.TotalSeconds,
                processingSeconds = transcript.ProcessingDuration.TotalSeconds, loadSeconds = transcript.ModelLoadDuration.TotalSeconds, transcript.Engine,
                model = Path.GetFileName(settings.ModelPath) });
        }
    }
    catch (OperationCanceledException) { await ReplyAsync(frame.Command.Id, "cancelled"); }
    catch (Exception ex) { await ReplyAsync(frame.Command.Id, "error", error: ex.Message); }
    finally { CryptographicOperations.ZeroMemory(frame.Audio); }
}
try
{
    await ReplyAsync("hello", "ready", new { protocol = 1, maxAudioBytes = WorkerFrames.MaxAudioBytes, backend = "cpu" });
    while (await WorkerFrames.ReadAsync(input, default) is { } frame)
    {
        var command = frame.Command;
        if (command.Operation == "shutdown") break;
        if (command.Operation == "cancel") { activeCancellation?.Cancel(); await ReplyAsync(command.Id, "cancel-requested"); continue; }
        if (active is { IsCompleted: false }) { CryptographicOperations.ZeroMemory(frame.Audio); await ReplyAsync(command.Id, "error", error: "Worker is busy."); continue; }
        if (command.Operation == "configure")
        {
            var next = command.Settings ?? new();
            var model = command.Model is ModelDownload.FileName or ModelDownload.SmallFileName ? command.Model : ModelDownload.FileName;
            settings = next with { ModelPath = Path.Combine(directory, "models", model), Acceleration = "cpu",
                Language = next.Language is "es" or "en" ? next.Language : "auto", CpuThreads = Math.Clamp(next.CpuThreads, 1, Environment.ProcessorCount) };
            UiText.SetLanguage(settings.InterfaceLanguage);
            await ReplyAsync(command.Id, "configured"); continue;
        }
        if (command.Operation is not ("transcribe" or "download")) { await ReplyAsync(command.Id, "error", error: "Unknown operation."); continue; }
        activeCancellation?.Dispose(); activeCancellation = new();
        active = ExecuteAsync(frame, activeCancellation.Token);
    }
}
catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine("IPC connection closed or invalid frame.");
    return 1;
}
finally
{
    activeCancellation?.Cancel();
    if (active is not null) { try { await active; } catch (IOException) { } }
    activeCancellation?.Dispose();
}
return 0;
