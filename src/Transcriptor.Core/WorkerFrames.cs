using System.Buffers.Binary;
using System.Text.Json;

namespace Transcriptor.Core;

public sealed record WorkerCommand(int Version, string Id, string Operation, int AudioBytes = 0,
    AppSettings? Settings = null, string? Model = null);
public sealed record WorkerFrame(WorkerCommand Command, byte[] Audio);

public static class WorkerFrames
{
    public const int MaxHeaderBytes = 65536;
    public const int MaxAudioBytes = 3_840_044;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<WorkerFrame?> ReadAsync(Stream input, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        if (await input.ReadAsync(prefix.AsMemory(0, 1), cancellationToken) == 0) return null;
        await input.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 or > MaxHeaderBytes) throw new InvalidDataException("Invalid IPC header length.");
        var header = new byte[length];
        await input.ReadExactlyAsync(header, cancellationToken);
        var command = JsonSerializer.Deserialize<WorkerCommand>(header, JsonOptions) ?? throw new InvalidDataException("Missing IPC command.");
        if (command.Version != 1 || command.Id is null || command.Id.Length is < 1 or > 80 || command.Operation is null || command.Operation.Length > 32 ||
            command.AudioBytes is < 0 or > MaxAudioBytes || (command.AudioBytes != 0 && command.Operation != "transcribe"))
            throw new InvalidDataException("Invalid IPC command.");
        var audio = new byte[command.AudioBytes];
        await input.ReadExactlyAsync(audio, cancellationToken);
        return new(command, audio);
    }

    public static async Task WriteAsync<T>(Stream output, T message, CancellationToken cancellationToken = default)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (header.Length > MaxHeaderBytes) throw new InvalidDataException("IPC response exceeds limit.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, header.Length);
        await output.WriteAsync(prefix, cancellationToken);
        await output.WriteAsync(header, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
